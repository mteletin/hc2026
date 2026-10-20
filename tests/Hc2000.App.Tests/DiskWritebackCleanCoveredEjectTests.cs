using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class DiskWritebackCleanCoveredEjectTests
{
    [Theory]
    [InlineData(DiskSlot.A)]
    [InlineData(DiskSlot.B)]
    public void CleanPresentCompletionEjectsOnlyTargetAndAbsentRepeatIsIdempotent(DiskSlot slot)
    {
        var f = new Owner();
        var mounted = f.Request(new(MediaAction.InsertDisk, slot, Disk()));
        var other = slot == DiskSlot.A ? DiskSlot.B : DiskSlot.A;
        var both = f.Request(new(MediaAction.InsertDisk, other, Disk()));
        var completed = f.Finish(f.Fifo.CompleteAsync(WritebackIntent.Eject, slot.ToString(), []));
        Assert.True(completed.Accepted); Assert.False(completed.SessionFrozen);
        var after = f.Request(new(MediaAction.Inspect));
        Assert.Null(after.Error);
        Assert.False((slot == DiskSlot.A ? after.A : after.B).Present);
        Assert.Equal((slot == DiskSlot.A ? mounted.A : mounted.B).Generation + 1,
            (slot == DiskSlot.A ? after.A : after.B).Generation);
        Assert.Equal(slot == DiskSlot.A ? both.B : both.A, slot == DiskSlot.A ? after.B : after.A);
        Assert.True(f.Finish(f.Fifo.CompleteAsync(WritebackIntent.Eject, slot.ToString(), [])).Accepted);
        var repeat = f.Request(new(MediaAction.Inspect));
        Assert.Equal(after.A, repeat.A); Assert.Equal(after.B, repeat.B);
        Assert.False(f.Ledger.Frozen);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("lease")]
    [InlineData("invalid")]
    public void RefusalRetainsActualMediaAndFence(string reason)
    {
        var f = new Owner();
        f.Request(new(MediaAction.InsertDisk, DiskSlot.A, Disk()));
        if (reason == "busy") f.Board.WriteLatch(0x1a);
        FrozenWriteback? lease = null;
        if (reason == "lease")
        {
            lease = f.Finish(f.Fifo.BeginAsync("A", WritebackIntent.Eject)).Snapshot;
            Assert.NotNull(lease);
        }
        var before = f.Request(new(MediaAction.Inspect));
        var result = f.Finish(f.Fifo.CompleteAsync(WritebackIntent.Eject, reason == "invalid" ? "C" : "A", []));
        Assert.False(result.Accepted); Assert.NotNull(result.Error);
        Assert.Equal(reason == "lease", result.SessionFrozen);
        var after = f.Request(new(MediaAction.Inspect));
        Assert.Equal(before.A, after.A); Assert.Equal(before.B, after.B);
        Assert.True(after.A.Present);
        if (reason == "lease")
            Assert.True(f.Finish(f.Fifo.AbortAsync(lease!.Token)).Accepted);
        Assert.False(f.Ledger.Frozen);
    }

    private static ImmutableArray<byte> Disk() => ImmutableArray.CreateRange(Dsk.Create(new(40, 1, 1, 128)).Save());

    private sealed class Owner
    {
        private readonly InputQueue queue = new();
        private readonly MediaBridge bridge;
        private readonly InputController input;
        public FloppyController Board { get; } = new(new DeviceScheduler(), 10, new(new()), new(new()));
        public DiskWritebackOwnerLedger Ledger { get; } = new();
        public FifoDiskWritebackOwner Fifo { get; }
        public Owner()
        {
            var session = new MediaSession(Board.DriveA, Board.DriveB);
            bridge = new(queue); Fifo = new(bridge);
            input = new(queue, media: (command, _) =>
            {
                if (command.Action == MediaAction.Writeback)
                {
                    var result = Ledger.Apply(command.Writeback, session, null, queue);
                    bridge.Publish(session.Apply(new(MediaAction.Inspect)) with
                        { RequestId = command.RequestId, Error = result.Error, Writeback = result });
                }
                else bridge.Publish(session.Apply(command));
            });
        }
        public MediaReply Request(MediaCommand command) => Finish(bridge.RequestAsync(command));
        public T Finish<T>(Task<T> task)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Clean eject FIFO did not settle.");
                input.ApplyPending(0, new KeyboardMatrix()); Thread.Yield();
            }
            return task.GetAwaiter().GetResult();
        }
    }
}
