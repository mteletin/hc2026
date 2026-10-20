using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Hc2000.App.Input;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;
using IOFile = System.IO.File;

namespace Hc2000.App.Tests;

// One-action A/B mount of a registered working copy through the existing App media path. The witnesses
// are the real catalog/copy/snapshot surface, the real MediaSession and the real FDC drives, wired the
// production way: the session lives on its own owner thread, which pumps the InputQueue through the
// InputController and answers the MediaBridge; the mount's dispatch seam is that same bridge. The
// accepted dirty/writeback discipline applies unchanged. Nothing is faked: no fake reply, no fake
// reserver, no second mount or session engine.
public sealed class DiskLibraryMountTests
{
    private sealed class SessionMachine : IDisposable
    {
        public DeviceScheduler Scheduler { get; } = new();
        public FloppyController Board { get; }
        public MediaBridge Bridge { get; }
        private readonly InputQueue _queue;
        private readonly InputController _controller;
        private readonly KeyboardMatrix _matrix = new();
        private readonly Thread _owner;
        private readonly MediaSession[] _session = [null!];
        private volatile bool _stop;

        public SessionMachine()
        {
            var a = new FloppyDrive(new());
            Board = new FloppyController(Scheduler, 10, a, new FloppyDrive(new()));
            _queue = new InputQueue();
            Bridge = new MediaBridge(_queue);
            _controller = new InputController(_queue, (_, _) => { }, (command, _) => Bridge.Publish(_session[0].Apply(command)));
            _owner = new Thread(() =>
            {
                _session[0] = new MediaSession(Board.DriveA, Board.DriveB);
                while (!_stop)
                {
                    try { _controller.ApplyPending(0, _matrix); }
                    catch { /* the pump only forwards media commands; the tests own every outcome */ }
                    Thread.Sleep(1);
                }
            }) { IsBackground = true, Name = "disk-library-mount-owner" };
            _owner.Start();
        }

        public async Task<MediaReply> InspectAsync() => await Bridge.RequestAsync(new MediaCommand(MediaAction.Inspect));
        public async Task<MediaReply> InsertAsync(DiskSlot slot, ImmutableArray<byte> bytes, string name)
            => await Bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, slot, bytes, name));

        public void Dispose()
        {
            _stop = true;
            _owner.Join(TimeSpan.FromSeconds(2));
            Bridge.Disconnect();
        }
    }

    private sealed class Counts { public int Inspects; public int Dispatches; }

    private static DiskLibraryMount Mount(DiskLibraryCatalog catalog, string root, SessionMachine? machine, Counts counts)
    {
        return new(catalog, root)
        {
            Inspect = machine is null ? null : () =>
            {
                counts.Inspects++;
                return machine.InspectAsync();
            },
            DispatchInsert = machine is null ? null : (slot, bytes, name) =>
            {
                counts.Dispatches++;
                return machine.InsertAsync(slot, bytes, name);
            }
        };
    }

    private static async Task<DiskLibraryCopy> OneCopyAsync(DiskLibraryWorkingCopyFixtures fixtures)
    {
        await fixtures.RegisterAsync();
        var result = await fixtures.Copy();
        Assert.Equal(DiskLibraryCopyStatus.Created, result.Status);
        return Assert.IsType<DiskLibraryCopy>(result.Copy);
    }

    private static string CopyPath(DiskLibraryWorkingCopyFixtures fixtures, DiskLibraryCopy copy)
        => Path.GetFullPath(Path.Combine(fixtures.Root, copy.RelativePath));

    // (a) Selection: an unknown copy id is a closed refusal; nothing is inspected, prepared or
    // dispatched, and the session is untouched.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task SelectionRefusesAnUnknownCopyIdWithoutDispatching()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        var copy = await OneCopyAsync(fixtures);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var mount = Mount(fixtures.Catalog, fixtures.Root, machine, counts);

        var refused = await mount.MountAsync("no-such-copy", DiskSlot.A);
        Assert.Equal(DiskLibraryMountOutcome.UnknownCopy, refused.Outcome);
        Assert.Contains("no registered working copy", refused.Message);
        Assert.Null(refused.Prepared); Assert.Null(refused.Status);
        Assert.Equal(0, counts.Inspects); Assert.Equal(0, counts.Dispatches);
        var view = await machine.InspectAsync();
        Assert.False(view.A.Present); Assert.Equal(0, view.A.Generation);
        fixtures.Preserved(priorCatalog: false);
    }

    // (b) Fail-closed: with the existing App media path not wired the mount refuses ownership-unknown
    // before preparing or dispatching anything; the copy and the session are untouched.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ASeamlessMountRefusesAsOwnershipUnknownBeforeAnyPreparation()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        var copy = await OneCopyAsync(fixtures);
        var copyBytes = IOFile.ReadAllBytes(CopyPath(fixtures, copy));
        using var machine = new SessionMachine();
        var counts = new Counts();
        var mount = Mount(fixtures.Catalog, fixtures.Root, null, counts);

        var refused = await mount.MountAsync(copy.Id, DiskSlot.B);
        Assert.Equal(DiskLibraryMountOutcome.OwnershipUnknown, refused.Outcome);
        Assert.Contains("not wired", refused.Message);
        Assert.Null(refused.Prepared);
        Assert.Equal(0, counts.Inspects); Assert.Equal(0, counts.Dispatches);
        Assert.Equal(copyBytes, IOFile.ReadAllBytes(CopyPath(fixtures, copy)));
        var view = await machine.InspectAsync();
        Assert.False(view.B.Present); Assert.Equal(0, view.B.Generation);
        fixtures.Preserved(priorCatalog: false);
    }

    // (c) Refused snapshot, actual session: the copy file became a link, so the admitted preparation
    // refuses before dispatch; the active session is preserved exactly.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ALinkedCopyRefusesBeforeDispatchAndPreservesTheSession()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        var copy = await OneCopyAsync(fixtures);
        var path = CopyPath(fixtures, copy);
        IOFile.Delete(path);
        IOFile.CreateSymbolicLink(path, fixtures.Source);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var mount = Mount(fixtures.Catalog, fixtures.Root, machine, counts);

        var refused = await mount.MountAsync(copy.Id, DiskSlot.A);
        Assert.Equal(DiskLibraryMountOutcome.Refused, refused.Outcome);
        Assert.Equal(DiskLibraryRefusal.FinalLink, refused.Refusal);
        Assert.Equal(0, counts.Dispatches);
        var view = await machine.InspectAsync();
        Assert.False(view.A.Present); Assert.Equal(0, view.A.Generation);
        Assert.False(view.B.Present); Assert.Equal(0, view.B.Generation);
        fixtures.Preserved(priorCatalog: false);
    }

    // (d) The actual attach dispatch: the prepared copy bytes go through the real MediaSession and the
    // target reports the mount at both boundaries; the mounted sectors are exactly the current copy
    // content, and the original is never a dispatch target.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ThePreparedCopyDispatchesThroughTheActualMediaPathAndTheTargetReportsTheMount()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        var copy = await OneCopyAsync(fixtures);
        var copyBytes = IOFile.ReadAllBytes(CopyPath(fixtures, copy));
        var name = Path.GetFileName(copy.RelativePath);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var mount = Mount(fixtures.Catalog, fixtures.Root, machine, counts);

        var result = await mount.MountAsync(copy.Id, DiskSlot.A);
        Assert.Equal(DiskLibraryMountOutcome.Mounted, result.Outcome);
        var prepared = Assert.IsType<DiskLibraryCopyContentSnapshot>(result.Prepared);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(copyBytes)), prepared.CurrentSha256);
        Assert.Equal(copyBytes, prepared.Bytes.AsSpan().ToArray());
        var status = Assert.IsType<DiskLibraryMountStatus>(result.Status);
        Assert.Equal(copy.Id, status.CopyId);
        Assert.Equal(prepared.CurrentSha256, status.CopyCurrentSha256);
        Assert.Equal(copyBytes.Length, status.CopyLength);
        Assert.Equal(fixtures.Entry.Id, status.OriginalId);
        Assert.Equal(fixtures.Entry.Sha256, status.OriginalSha256);
        Assert.False(status.OriginalDispatchedAsTarget);
        Assert.False(status.TargetBefore.Present); Assert.Equal(0, status.TargetBefore.Generation);
        Assert.True(status.TargetAfter.Present);
        Assert.Equal(name, status.TargetAfter.Name);
        Assert.Equal(1, status.TargetAfter.Generation);
        Assert.False(status.TargetAfter.WriteProtected);
        Assert.Equal(1, counts.Dispatches); Assert.Equal(1, counts.Inspects);
        AssertSameSectors(copyBytes, BoardExport(machine, DiskSlot.A));
        var after = await machine.InspectAsync();
        Assert.True(after.A.Present);
        Assert.Equal(name, after.A.Name);
        Assert.Equal(1, after.A.Generation);
        Assert.False(after.B.Present); Assert.Equal(0, after.B.Generation);
        fixtures.Preserved(priorCatalog: false);
    }

    // (e) A legitimately modified working copy mounts its CURRENT content, not the original and not the
    // initial copy: the snapshot's current hash and the mounted sectors are the modified bytes.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task AModifiedWorkingCopyMountsItsCurrentContentNotTheOriginal()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        var copy = await OneCopyAsync(fixtures);
        var modified = ImmutableArray.CreateRange(Dsk.Create(new(40, 1, 1, 128), fill: 0x77).Save());
        var path = CopyPath(fixtures, copy);
        IOFile.WriteAllBytes(path, modified.AsSpan().ToArray()); // same inode: a legitimately edited working copy
        using var machine = new SessionMachine();
        var counts = new Counts();
        var mount = Mount(fixtures.Catalog, fixtures.Root, machine, counts);

        var result = await mount.MountAsync(copy.Id, DiskSlot.B);
        Assert.Equal(DiskLibraryMountOutcome.Mounted, result.Outcome);
        var prepared = Assert.IsType<DiskLibraryCopyContentSnapshot>(result.Prepared);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(modified.AsSpan())), prepared.CurrentSha256);
        Assert.NotEqual(fixtures.Hash, prepared.CurrentSha256);
        AssertSameSectors(modified.AsSpan().ToArray(), BoardExport(machine, DiskSlot.B));
        var status = Assert.IsType<DiskLibraryMountStatus>(result.Status);
        Assert.True(status.TargetAfter.Present);
        Assert.Equal(1, status.TargetAfter.Generation);
        var after = await machine.InspectAsync();
        Assert.False(after.A.Present); Assert.Equal(0, after.A.Generation);
        fixtures.Preserved(priorCatalog: false);
    }

    // (f) Accepted dirty discipline, actual session: a dirty unsaved target refuses the mount through the
    // machine's own replacement gate; the active session is preserved exactly (same medium, generation
    // and dirty count), and the copy was never mounted.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ADirtyTargetRefusesTheMountAndPreservesTheActiveSession()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        var copy = await OneCopyAsync(fixtures);
        using var machine = new SessionMachine();
        Assert.Null((await machine.InsertAsync(DiskSlot.A, MediaSessionTests.Disk(), "first.dsk")).Error);
        DirtySector(machine);
        Assert.Single(DriveA(machine).DirtySectors);
        var imageBefore = DriveA(machine).Image;
        var counts = new Counts();
        var mount = Mount(fixtures.Catalog, fixtures.Root, machine, counts);

        var refused = await mount.MountAsync(copy.Id, DiskSlot.A);
        Assert.Equal(DiskLibraryMountOutcome.DispatchFailed, refused.Outcome);
        Assert.Contains("Unsaved changes", refused.Message);
        Assert.Equal(1, counts.Dispatches);
        var view = await machine.InspectAsync();
        Assert.True(view.A.Present);
        Assert.Equal("first.dsk", view.A.Name);
        Assert.Equal(1, view.A.Generation);
        Assert.Equal(1, view.A.DirtySectors);
        Assert.Same(imageBefore, DriveA(machine).Image);
        Assert.False(view.B.Present);
        fixtures.Preserved(priorCatalog: false);
    }

    // (g) Cancellation: a cancelled token refuses before dispatch; the active session is preserved.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ACancelledMountRefusesBeforeDispatch()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        var copy = await OneCopyAsync(fixtures);
        using var machine = new SessionMachine();
        var counts = new Counts();
        var mount = Mount(fixtures.Catalog, fixtures.Root, machine, counts);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var cancelled = await mount.MountAsync(copy.Id, DiskSlot.A, source.Token);
        Assert.Equal(DiskLibraryMountOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal(0, counts.Dispatches);
        var view = await machine.InspectAsync();
        Assert.False(view.A.Present); Assert.Equal(0, view.A.Generation);
        fixtures.Preserved(priorCatalog: false);
    }

    // (h) Slot selection: both slots are selectable through the same action; mounting B leaves the
    // already-mounted A session exactly as it was.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task BothSlotsAreSelectableThroughTheSameAction()
    {
        using var fixtures = new DiskLibraryWorkingCopyFixtures();
        var copy = await OneCopyAsync(fixtures);
        using var machine = new SessionMachine();
        Assert.Null((await machine.InsertAsync(DiskSlot.A, MediaSessionTests.Disk(0x2b), "boot.dsk")).Error);
        var counts = new Counts();
        var mount = Mount(fixtures.Catalog, fixtures.Root, machine, counts);

        var result = await mount.MountAsync(copy.Id, DiskSlot.B);
        Assert.Equal(DiskLibraryMountOutcome.Mounted, result.Outcome);
        var after = await machine.InspectAsync();
        Assert.True(after.A.Present);
        Assert.Equal("boot.dsk", after.A.Name);
        Assert.Equal(1, after.A.Generation);
        Assert.True(after.B.Present);
        Assert.Equal(Path.GetFileName(copy.RelativePath), after.B.Name);
        Assert.Equal(1, after.B.Generation);
        fixtures.Preserved(priorCatalog: false);
    }

    private static FloppyDrive DriveA(SessionMachine machine) => machine.Board.DriveA;

    // ExportMedia is a drive read; run it on the owner thread is impossible (it is busy), so a one-shot
    // thread reads the drive directly between test steps, when the pump is idle.
    private static byte[] BoardExport(SessionMachine machine, DiskSlot slot)
    {
        using var done = new ManualResetEventSlim();
        byte[] exported = null!;
        var thread = new Thread(() =>
        {
            try { exported = (slot == DiskSlot.A ? machine.Board.DriveA : machine.Board.DriveB).ExportMedia(); }
            finally { done.Set(); }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        return exported;
    }

    // ExportMedia always emits an EXTENDED container, so equality is the parsed sector content, not
    // the file bytes: the mounted medium must carry exactly the dispatched container's sectors.
    private static void AssertSameSectors(byte[] source, byte[] exported)
    {
        var s = Dsk.Load(source.AsSpan()); var m = Dsk.Load(exported.AsSpan());
        Assert.Equal(s.Tracks.Count, m.Tracks.Count);
        for (var i = 0; i < s.Tracks.Count; i++)
            for (var j = 0; j < s.Tracks[i].Sectors.Count; j++)
                Assert.Equal(s.Tracks[i].Sectors[j].Data.ToArray(), m.Tracks[i].Sectors[j].Data.ToArray());
    }

    // The FDC write that makes the pre-mounted target dirty: the same machine discipline the
    // host-read admission tests prove (DiskLibraryHostReadAdmissionTests (c)). Runs on the test thread
    // between awaits, when the owner pump is idle: the FDC and the scheduler are deterministic.
    private static void DirtySector(SessionMachine machine)
    {
        var board = machine.Board; var scheduler = machine.Scheduler;
        var a = machine.Board.DriveA;
        board.WriteLatch(0x1a);
        foreach (var value in new byte[] { 3, 0xdf, 1, 0x45, 0, 0, 0, 1, 0, 1, 0x2a, 0x80 }) board.Fdc.WriteData(value);
        Ready(); board.Fdc.WriteData(0x72);
        board.WriteLatch(0x1b); Ready();
        while (board.Fdc.Phase == FdcPhase.Result) board.Fdc.ReadData();
        board.WriteLatch(0x10);
        int events = 0;
        while (a.HeadLoaded && ++events < 1000) scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
        Assert.Single(a.DirtySectors);

        void Ready()
        {
            int count = 0;
            while ((board.Fdc.ReadStatus() & 0x80) == 0 && ++count < 1000)
                scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
            Assert.NotEqual(0, board.Fdc.ReadStatus() & 0x80);
        }
    }
}
