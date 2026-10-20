using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Input;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class TapeOwnerTests
{
    internal static string PathFor(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tape", name);
    private static ImmutableArray<byte> Fixture(string name, string hash)
    {
        var bytes = File.ReadAllBytes(PathFor(name));
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return ImmutableArray.CreateRange(bytes);
    }
    internal static ImmutableArray<byte> Valid() => Fixture("raw-data.tap", "d66bd032afb7e13e88d099f0e906872d0627fd53f51e4ada1990af0607fc238a");
    private static ImmutableArray<byte> Invalid() => Fixture("bad-checksum.tap", "c20755b787a2833db785252f034fd31ee89e71b5678a002bd2a1570b73e6de01");

    [Fact]
    public void OrderedBoundaryControlsUseScheduledDeviceAndPreserveMediaOnRejection()
    {
        var fixture = new RuntimeTests.Fixture();
        var tape = fixture.Machine.EnableTape(3);
        var session = new MediaSession(new(new()), new(new()), fixture.Machine.Tape);
        var queue = new InputQueue(); var bridge = new MediaBridge(queue);
        var controller = new InputController(queue, media: (command, _) => bridge.Publish(session.Apply(command)));
        MediaReply Apply(MediaCommand command)
        {
            var reply = bridge.RequestAsync(command);
            Assert.False(reply.IsCompleted); // No UI-thread/device mutation before owner drains FIFO.
            controller.ApplyPending(fixture.Scheduler.Now, new KeyboardMatrix());
            return reply.GetAwaiter().GetResult();
        }
        var queued = Apply(new(MediaAction.QueueTape, Bytes: Valid(), Name: "raw-data.tap"));
        Assert.Null(queued.Error); Assert.Equal(TapeState.Stopped, queued.Tape!.Cursor.State);
        Assert.DoesNotContain(fixture.Scheduler.PendingEvents, e => e.DeviceId == 3);
        Assert.Equal(TapeState.Playing, Apply(new(MediaAction.PlayTape)).Tape!.Cursor.State);
        var edge = Assert.Single(fixture.Scheduler.PendingEvents, e => e.DeviceId == 3);
        fixture.Scheduler.AdvanceTo(edge.DueTick);
        Assert.True(tape.Cursor.WaveformLevel);
        var before = tape.Cursor;
        var rejected = Apply(new(MediaAction.QueueTape, Bytes: Invalid(), Name: "bad-checksum.tap"));
        Assert.Contains("checksum", rejected.Error);
        Assert.Equal(before, rejected.Tape!.Cursor); Assert.Equal("raw-data.tap", rejected.Tape.Name);
        var stopped = Apply(new(MediaAction.StopTape)).Tape!.Cursor;
        Assert.Equal(TapeState.Stopped, stopped.State);
        Assert.DoesNotContain(fixture.Scheduler.PendingEvents, e => e.DeviceId == 3);
        fixture.Scheduler.AdvanceTo(fixture.Scheduler.Now + 100_000);
        Assert.Equal(stopped, Apply(new(MediaAction.Inspect)).Tape!.Cursor);
        Apply(new(MediaAction.PlayTape));
        Assert.Single(fixture.Scheduler.PendingEvents, e => e.DeviceId == 3);
        var ejected = Apply(new(MediaAction.EjectTape)).Tape!;
        Assert.Equal(TapeState.Unloaded, ejected.Cursor.State); Assert.Null(ejected.Name);
        fixture.Scheduler.AdvanceTo(fixture.Scheduler.Now + 100_000);
        Assert.Equal(ejected, Apply(new(MediaAction.Inspect)).Tape);
        Assert.DoesNotContain(fixture.Scheduler.PendingEvents, e => e.DeviceId == 3);
    }

    [Fact]
    public void RefreshCopiesActualCompletionAndNewQueueDoesNotAutoPlay()
    {
        var fixture = new RuntimeTests.Fixture(); var tape = fixture.Machine.EnableTape(3);
        var session = new MediaSession(new(new()), new(new()), tape);
        session.Apply(new(MediaAction.QueueTape, Bytes: Valid(), Name: "raw-data.tap"));
        session.Apply(new(MediaAction.PlayTape));
        // Pinned end tick of the existing raw-data transport fixture, not host time.
        fixture.Scheduler.AdvanceTo(53_424_275);
        Assert.Equal(TapeState.AtEnd, session.Apply(new(MediaAction.Inspect)).Tape!.Cursor.State);
        var next = session.Apply(new(MediaAction.QueueTape, Bytes: Valid(), Name: "again.tap")).Tape!;
        Assert.Equal(TapeState.Stopped, next.Cursor.State); Assert.Equal(2, next.Cursor.MediaIdentity);
        Assert.DoesNotContain(fixture.Scheduler.PendingEvents, e => e.DeviceId == 3);
    }

    [Fact]
    public async Task OwnerGuardRejectsForeignThreadAndUnavailableCommandsStayUnavailable()
    {
        var session = new MediaSession(new(new()), new(new()));
        Assert.Null(session.Apply(new(MediaAction.Inspect)).Tape);
        Assert.Contains("unavailable", session.Apply(new(MediaAction.PlayTape)).Error);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                Assert.Throws<InvalidOperationException>(() => session.Apply(new(MediaAction.Inspect)));
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true }.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
