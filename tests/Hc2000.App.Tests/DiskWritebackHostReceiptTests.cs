using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

// Callback-boundary unit evidence only: neither installed lifecycle coverage nor held-read identity.
public sealed class DiskWritebackHostReceiptTests : IDisposable
{
    private readonly HddColdAdmissionTests.PrivateScopedDomain nativeDomain = new();
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "hc2000-receipts-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { Directory.Delete(root, true); nativeDomain.Dispose(); }
    private static ImmutableArray<byte> Disk(byte fill = 0xe5) =>
        ImmutableArray.CreateRange(Dsk.Create(new(40, 1, 1, 128), fill: fill).Save());
    private string FileFor(string name, ImmutableArray<byte> bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    [Fact]
    public void OffRecordsBothRealRepliesWithoutWriterAndSnapshotsStayImmutable()
    {
        var f = new Owner(false);
        var bytes = Disk(); var a = FileFor("a.dsk", bytes); var b = FileFor("b.dsk", bytes);
        var replyA = f.Insert(DiskSlot.A, a, bytes);
        f.Note(DiskSlot.A, a, bytes, replyA);
        var first = f.Host.CaptureMountObservations();
        var observedA = Assert.Single(first);
        AssertReceipt(observedA, f, DiskSlot.A, a, bytes, replyA);
        Assert.Equal(DiskWritebackHost.MountBindingState.Off, observedA.BindingState);
        var replyB = f.Insert(DiskSlot.B, b, bytes);
        f.Note(DiskSlot.B, b, bytes, replyB);
        var both = f.Host.CaptureMountObservations();
        Assert.Equal(new[] { DiskSlot.A, DiskSlot.B }, both.Select(x => x.Slot));
        AssertReceipt(both[1], f, DiskSlot.B, b, bytes, replyB);
        Assert.Equal(DiskWritebackHost.MountBindingState.Off, both[1].BindingState);
        Assert.Single(first); Assert.Equal(observedA, both[0]);
        Assert.False(f.Host.ReportsTarget); Assert.False(f.Host.Frozen);
        Assert.False(f.Finish(f.Host.TryEjectAsync(DiskSlot.A)));
        Assert.True(f.Request(new(MediaAction.Inspect)).A.Present);
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(a));
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(b));
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulReplacementWithMissingOrInvalidSourceRetainsNewMountAsUnboundError(bool corrupt)
    {
        var f = new Owner(true); var original = Disk(); var replacement = Disk(0x72);
        var oldPath = FileFor("old.dsk", original); var newPath = FileFor("new.dsk", replacement);
        var initial = f.Insert(DiskSlot.A, oldPath, original);
        f.Note(DiskSlot.A, oldPath, original, initial);
        var before = Assert.Single(f.Host.CaptureMountObservations());
        var actual = f.Insert(DiskSlot.A, newPath, replacement);
        Assert.Null(actual.Error); Assert.True(actual.A.Generation > initial.A.Generation);
        // Mutation AFTER the owner read proves callback facts do not identify what was read.
        if (corrupt) File.WriteAllBytes(newPath, [1, 2, 3]); else File.Delete(newPath);
        f.Note(DiskSlot.A, newPath, replacement, actual);
        var after = Assert.Single(f.Host.CaptureMountObservations());
        AssertReceipt(after, f, DiskSlot.A, newPath, replacement, actual);
        Assert.Equal(DiskWritebackHost.MountBindingState.UnboundError, after.BindingState);
        Assert.NotNull(after.Error); Assert.NotEqual(before.Generation, after.Generation);
        Assert.True(f.Request(new(MediaAction.Inspect)).A.Present);
        var exported = f.Request(new(MediaAction.ExportDisk));
        Assert.Equal(0x72, Dsk.Load(exported.Export.AsSpan()).Tracks[0].Sectors[0].Data[0]);
        if (!corrupt) { Assert.False(after.PathFactsAtReceipt.Known); Assert.Null(after.PathFactsAtReceipt.FileId); }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("dirty")]
    [InlineData("busy")]
    public void ActualOwnerRefusalsPreservePredecessorAndOtherSlot(string refusal)
    {
        var f = new Owner(true); var bytes = Disk(); var path = FileFor("a.dsk", bytes);
        var other = FileFor("b.dsk", bytes);
        f.Note(DiskSlot.A, path, bytes, f.Insert(DiskSlot.A, path, bytes));
        f.Note(DiskSlot.B, other, bytes, f.Insert(DiskSlot.B, other, bytes));
        var before = f.Host.CaptureMountObservations();
        if (refusal == "dirty") f.DirtyA();
        if (refusal == "busy") f.Board.WriteLatch(0x1a);
        var candidate = refusal == "malformed" ? ImmutableArray.Create<byte>(1, 2, 3) : Disk(0x72);
        var rejected = f.Request(new(MediaAction.InsertDisk, DiskSlot.A, candidate, "refused.dsk"));
        Assert.NotNull(rejected.Error);
        if (refusal == "dirty") Assert.True(rejected.A.DirtySectors > 0);
        if (refusal == "busy") Assert.True(rejected.A.Busy);
        Assert.Equal(before[0].Generation, rejected.A.Generation);
        f.Note(DiskSlot.A, path, candidate, rejected);
        Assert.Equal(before.ToArray(), f.Host.CaptureMountObservations().ToArray());
        Assert.True(f.Host.ReportsTarget);
    }

    [Fact]
    public void StaleRepliesCannotReplaceNewerReceiptAndInvalidationRetainsProvenance()
    {
        var f = new Owner(true); var bytes = Disk(); var next = Disk(0x72);
        var path = FileFor("a.dsk", bytes); var nextPath = FileFor("next.dsk", next);
        var old = f.Insert(DiskSlot.A, path, bytes); f.Note(DiskSlot.A, path, bytes, old);
        var latest = f.Insert(DiskSlot.A, nextPath, next); f.Note(DiskSlot.A, nextPath, next, latest);
        var snapshot = f.Host.CaptureMountObservations();
        f.Note(DiskSlot.A, path, bytes, old);
        f.Note(DiskSlot.A, path, bytes, latest); // conflicting replay of same request
        Assert.Equal(snapshot.ToArray(), f.Host.CaptureMountObservations().ToArray());
        f.Finish(f.Host.InvalidateAsync());
        var invalidated = Assert.Single(f.Host.CaptureMountObservations());
        Assert.Equal(snapshot[0] with { Invalidated = true }, invalidated);
        Assert.False(snapshot[0].Invalidated); Assert.False(f.Host.ReportsTarget);
        f.Note(DiskSlot.A, nextPath, next, latest);
        Assert.Equal(invalidated, Assert.Single(f.Host.CaptureMountObservations()));
    }

    [Fact]
    public void AbsentActualViewDoesNotEraseLastObservation()
    {
        var f = new Owner(true); var bytes = Disk(); var path = FileFor("a.dsk", bytes);
        f.Note(DiskSlot.A, path, bytes, f.Insert(DiskSlot.A, path, bytes));
        var before = Assert.Single(f.Host.CaptureMountObservations());
        var ejected = f.Request(new(MediaAction.EjectDisk));
        Assert.Null(ejected.Error); Assert.False(ejected.A.Present);
        f.Note(DiskSlot.A, path, bytes, ejected);
        Assert.Equal(before, Assert.Single(f.Host.CaptureMountObservations()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyConfirmedWritebackEjectRemovesItsMatchingObservation(bool busy)
    {
        if (!OperatingSystem.IsLinux()) return; // Native writer unsupported elsewhere; never invent identity.
        var f = new Owner(true); var bytes = Disk(); var path = FileFor("a.dsk", bytes);
        var other = FileFor("b.dsk", bytes);
        f.Note(DiskSlot.A, path, bytes, f.Insert(DiskSlot.A, path, bytes));
        f.Note(DiskSlot.B, other, bytes, f.Insert(DiskSlot.B, other, bytes));
        var before = f.Host.CaptureMountObservations();
        Assert.All(before, x => Assert.Equal(DiskWritebackHost.MountBindingState.Bound, x.BindingState));
        f.DirtyA(); // Exercise a covered actual eject, not the separate clean-completion ledger path.
        if (busy) f.Board.WriteLatch(0x1a);
        Assert.True(f.Finish(f.Host.TryEjectAsync(DiskSlot.A)));
        var viewed = f.Request(new(MediaAction.Inspect));
        Assert.True(busy == viewed.A.Present, f.Host.StatusLine()); Assert.True(viewed.B.Present);
        var after = f.Host.CaptureMountObservations();
        if (busy) Assert.Equal(before.ToArray(), after.ToArray());
        else Assert.Equal(before[1], Assert.Single(after));
        Assert.False(f.Host.Frozen);
        Assert.Equal(busy ? 0xe5 : 0x72, Dsk.Load(File.ReadAllBytes(path)).Tracks[0].Sectors[0].Data[0]);
    }

    // Historical Accepted/Present/same-G witness remains at b51cd0d, blob
    // 0895e6a3e0d28586189ebf4e507582f71996db97; d505212 recorded 10P/3F.
    // The repaired normal path must eject, while a real remount still exercises retention.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanAcceptedCompletionRequiresActualAbsenceBeforeForgettingObservation(bool remount)
    {
        if (!OperatingSystem.IsLinux()) return;
        var f = new Owner(true); var bytes = Disk(); var path = FileFor("clean.dsk", bytes);
        var mounted = f.Insert(DiskSlot.A, path, bytes);
        f.Note(DiskSlot.A, path, bytes, mounted);
        var other = FileFor("b.dsk", bytes);
        f.Note(DiskSlot.B, other, bytes, f.Insert(DiskSlot.B, other, bytes));
        var before = f.Host.CaptureMountObservations();
        var expectedPublished = f.Request(new(MediaAction.ExportDisk, DiskSlot.A));
        Assert.Null(expectedPublished.Error);
        using var context = new HeldContext();
        f.Continuations = context;
        var replacementBytes = Disk(0x72); var replacement = FileFor("replacement.dsk", replacementBytes);
        if (remount) f.RemountAfterCompletion = new(MediaAction.InsertDisk, DiskSlot.A, replacementBytes, replacement);
        var handled = f.Finish(f.Host.TryEjectAsync(DiskSlot.A));
        // Observe the queued real request even when a later old-ledger assertion fails.
        var inserted = f.ReplacementReply;
        if (f.ReplacementTask is { } task) Assert.Equal(f.Finish(task), inserted);
        Assert.True(handled);
        var completion = Assert.Single(f.OwnerReplies, x => x.Command.Writeback?.Op == WritebackOwnerOp.Complete);
        Assert.Equal(WritebackIntent.Eject, completion.Command.Writeback!.Intent);
        Assert.True(completion.Reply.Accepted);
        var atCompletion = Assert.Single(f.CompletionViews);
        Assert.Null(atCompletion.Error); Assert.False(atCompletion.A.Present);
        Assert.Equal(mounted.A.Generation + 1, atCompletion.A.Generation);
        var readback = Assert.Single(f.InspectReplies);
        Assert.Null(readback.Error); Assert.Equal(remount, readback.A.Present);
        Assert.Equal(mounted.A.Generation + (remount ? 2 : 1), readback.A.Generation);
        Assert.Equal(0, readback.A.DirtySectors); Assert.True(readback.B.Present);
        if (remount)
        {
            Assert.NotNull(inserted);
            Assert.Null(inserted.Error); Assert.True(inserted.RequestId > 0);
            Assert.True(inserted.RequestId < readback.RequestId);
            Assert.True(inserted.A.Present); Assert.Equal(readback.A.Generation, inserted.A.Generation);
            Assert.Equal(before.ToArray(), f.Host.CaptureMountObservations().ToArray());
            Assert.Contains("owner still present; mount retained", f.Host.StatusLine());
            var exported = f.Request(new(MediaAction.ExportDisk, DiskSlot.A));
            Assert.Null(exported.Error);
            // MediaSession export stamps its creator header; compare every real sector byte.
            var expectedDisk = Dsk.Load(replacementBytes.ToArray());
            var actualDisk = Dsk.Load(exported.Export.ToArray());
            Assert.Equal(expectedDisk.Tracks.Count, actualDisk.Tracks.Count);
            Assert.Equal(expectedDisk.Tracks.SelectMany(t => t.Sectors).SelectMany(s => s.Data),
                actualDisk.Tracks.SelectMany(t => t.Sectors).SelectMany(s => s.Data));
        }
        else Assert.Equal(before[1], Assert.Single(f.Host.CaptureMountObservations()));
        Assert.False(f.Host.Frozen);
        Assert.Equal(expectedPublished.Export.ToArray(), File.ReadAllBytes(path));
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(other));
        Assert.Equal(replacementBytes.ToArray(), File.ReadAllBytes(replacement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateRealBindCompletionCannotUndoInvalidationOrNewerReceipt(bool replace)
    {
        if (!OperatingSystem.IsLinux()) return;
        var f = new Owner(true);
        var bytes = ImmutableArray.CreateRange(Dsk.Create(new(80, 2, 18, 512)).Save());
        var path = FileFor("large.dsk", bytes);
        var actual = f.Insert(DiskSlot.A, path, bytes);
        using var context = new HeldContext();
        var bind = f.Host.NoteMounted(DiskSlot.A, path, bytes, actual);
        Assert.False(bind.IsCompleted); // A real asynchronous file read, not a fake coordinator.
        var pending = Assert.Single(f.Host.CaptureMountObservations());
        Assert.Equal(DiskWritebackHost.MountBindingState.Pending, pending.BindingState);
        DiskWritebackHost.MountObservation expected;
        if (replace)
        {
            var next = Disk(0x72); var nextPath = FileFor("new.dsk", next);
            var nextReply = f.Insert(DiskSlot.A, nextPath, next);
            f.Note(DiskSlot.A, nextPath, next, nextReply);
            expected = Assert.Single(f.Host.CaptureMountObservations());
            Assert.Equal(nextReply.A.Generation, expected.Generation);
            Assert.Equal(DiskWritebackHost.MountBindingState.UnboundError, expected.BindingState);
        }
        else
        {
            f.Finish(f.Host.InvalidateAsync());
            expected = Assert.Single(f.Host.CaptureMountObservations());
            Assert.True(expected.Invalidated);
        }
        context.DrainUntil(bind);
        Assert.Equal(expected, Assert.Single(f.Host.CaptureMountObservations()));
    }

    private sealed class HeldContext : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? previous = Current;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> callbacks = new();
        public HeldContext() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue(() => callback(state));
        public void PumpOne() { if (callbacks.TryDequeue(out var callback)) callback(); }
        public void DrainUntil(Task task)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Real bind did not settle.");
                if (callbacks.TryDequeue(out var callback)) callback(); else Thread.Yield();
            }
            task.GetAwaiter().GetResult();
        }
        public void Dispose() => SetSynchronizationContext(previous);
    }

    private static void AssertReceipt(DiskWritebackHost.MountObservation observation, Owner f,
        DiskSlot slot, string path, ImmutableArray<byte> bytes, MediaReply reply)
    {
        Assert.Equal(slot, observation.Slot); Assert.Equal(f.Ledger.SessionId, observation.SessionId);
        Assert.True(reply.RequestId > 0); Assert.Equal(reply.RequestId, observation.RequestId);
        Assert.Equal(slot == DiskSlot.A ? reply.A.Generation : reply.B.Generation, observation.Generation);
        Assert.Equal(Path.GetFullPath(path), observation.CanonicalSourcePath);
        Assert.Equal(DiskWritebackCoordinator.Hash(bytes), observation.MountedHash);
        Assert.Equal(LinuxFileFacts.Inspect(path), observation.PathFactsAtReceipt);
        if (!OperatingSystem.IsLinux()) Assert.False(observation.PathFactsAtReceipt.Known);
    }

    // Test owns the same FIFO/bridge/session boundary and drains it on ONE owner thread.
    // No manufactured MediaReply success, request id, generation or native file identity.
    private sealed class Owner
    {
        private readonly InputQueue queue = new();
        private readonly MediaBridge bridge;
        private readonly InputController input;
        private readonly DeviceScheduler scheduler = new();
        private readonly FloppyDrive a = new(new());
        public FloppyController Board { get; }
        public DiskWritebackOwnerLedger Ledger { get; } = new();
        public DiskWritebackHost Host { get; }
        public List<(MediaCommand Command, WritebackOwnerReply Reply)> OwnerReplies { get; } = new();
        public List<MediaReply> CompletionViews { get; } = new();
        public List<MediaReply> InspectReplies { get; } = new();
        public MediaCommand? RemountAfterCompletion { get; set; }
        public Task<MediaReply>? ReplacementTask { get; private set; }
        public MediaReply? ReplacementReply { get; private set; }
        public HeldContext? Continuations { get; set; }
        public Owner(bool enabled)
        {
            Board = new(scheduler, 10, a, new(new()));
            var session = new MediaSession(a, Board.DriveB);
            bridge = new(queue); Host = new(Ledger, bridge, enabled);
            input = new(queue, media: (command, _) =>
            {
                if (command.Action == MediaAction.Writeback)
                {
                    var owner = Ledger.Apply(command.Writeback, session, null, queue);
                    OwnerReplies.Add((command, owner));
                    var actual = session.Apply(new(MediaAction.Inspect));
                    if (command.Writeback?.Op == WritebackOwnerOp.Complete) CompletionViews.Add(actual);
                    bridge.Publish(actual with
                        { RequestId = command.RequestId, Error = owner.Error, Writeback = owner });
                    // Host continuations are held: Publish must clear the bridge's one-pending
                    // slot before the replacement can enter this same bridge/FIFO.
                    if (command.Writeback?.Op == WritebackOwnerOp.Complete && owner.Accepted
                        && RemountAfterCompletion is { } replacement)
                    {
                        Assert.NotNull(Continuations);
                        RemountAfterCompletion = null;
                        ReplacementTask = bridge.RequestAsync(replacement);
                    }
                }
                else
                {
                    var actual = session.Apply(command);
                    if (command.Action == MediaAction.Inspect) InspectReplies.Add(actual);
                    bridge.Publish(actual);
                }
            });
        }
        public MediaReply Request(MediaCommand command) => Finish(bridge.RequestAsync(command));
        public MediaReply Insert(DiskSlot slot, string path, ImmutableArray<byte> bytes)
        {
            var reply = Request(new(MediaAction.InsertDisk, slot, bytes, path));
            Assert.Null(reply.Error); return reply;
        }
        public void Note(DiskSlot slot, string path, ImmutableArray<byte> bytes, MediaReply reply) =>
            Finish(Host.NoteMounted(slot, path, bytes, reply));
        public void Finish(Task task)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Receipt fixture FIFO did not settle.");
                input.ApplyPending(0, new KeyboardMatrix());
                // Complete's continuation cannot request Inspect until the one-pending
                // bridge has published the queued replacement's actual reply.
                if (ReplacementTask is { IsCompleted: true } replacement)
                    ReplacementReply ??= replacement.GetAwaiter().GetResult();
                if (ReplacementTask is null || ReplacementReply is not null) Continuations?.PumpOne();
                Thread.Yield();
            }
            task.GetAwaiter().GetResult();
        }
        public T Finish<T>(Task<T> task) { Finish((Task)task); return task.GetAwaiter().GetResult(); }
        public void DirtyA()
        {
            Board.WriteLatch(0x1a);
            foreach (var value in new byte[] { 3, 0xdf, 1, 0x45, 0, 0, 0, 1, 0, 1, 0x2a, 0x80 }) Board.Fdc.WriteData(value);
            Ready(); Board.Fdc.WriteData(0x72); Board.WriteLatch(0x1b); Ready();
            while (Board.Fdc.Phase == FdcPhase.Result) Board.Fdc.ReadData();
            Board.WriteLatch(0x10);
            var count = 0;
            while (a.HeadLoaded && ++count < 1000) scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
            Assert.False(a.HeadLoaded);
        }
        private void Ready()
        {
            var count = 0;
            while ((Board.Fdc.ReadStatus() & 0x80) == 0 && ++count < 1000)
                scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
            Assert.NotEqual(0, Board.Fdc.ReadStatus() & 0x80);
        }
    }
}
