using Hc2000.Machine.Memory;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeDeckCoreTests
{
    private static (ITapeDeck Deck, DeviceScheduler Scheduler, Ula Ula) Fixture()
    {
        var scheduler = new DeviceScheduler();
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ula = new Ula(memory, new(), scheduler, new FrameClock(scheduler, 1), 2);
        // Mechanics run many virtual minutes; unrelated video raster work is outside this fixture.
        scheduler.InvalidateDevice(1); scheduler.InvalidateDevice(2);
        return (Assert.IsAssignableFrom<ITapeDeck>(new TapeTransport(scheduler, ula, 42)), scheduler, ula);
    }
    private static TapeSideImage Side(params TapeRun[] runs) => new(new(1800, 1), new(1800, 1),
        [new(1, runs)], [], [], [], new(TapeFormat.Blank, null, true, [], null));

    [Fact]
    public void BlankInterlocksPauseStopAndPhysicalFlip()
    {
        var (deck, scheduler, _) = Fixture();
        Assert.True(deck.Blank(TapeCapacity.C60, false).Success);
        Assert.Equal(TapeDeckMode.Stopped, deck.Observe().Mode);
        Assert.True(deck.Play().Success);
        Assert.Equal(TapeError.Interlock, deck.Rewind().Failure!.Code);
        scheduler.AdvanceTo(FrameTiming.MasterHz * 7);
        Assert.Equal(new TapeTime(7, 1), deck.Pause().Value.Position);
        scheduler.AdvanceTo(FrameTiming.MasterHz * 10);
        Assert.Equal(new TapeTime(7, 1), deck.Observe().Position);
        Assert.True(deck.Pause().Success);
        scheduler.AdvanceTo(FrameTiming.MasterHz * 12);
        Assert.Equal(new TapeTime(9, 1), deck.StopEject(false).Value.Position);
        var flipped = deck.Flip().Value;
        Assert.Equal(TapeSide.B, flipped.ActiveSide);
        Assert.Equal(new TapeTime(1791, 1), flipped.Position);
        Assert.True(deck.StopEject(false).Success);
        Assert.False(deck.Observe().Present);
    }

    [Fact]
    public void WindHasRealEndpointAndReelCounterIsNotSeconds()
    {
        var (deck, scheduler, _) = Fixture(); deck.Blank(TapeCapacity.C60, false);
        deck.FastForward(); Assert.Single(scheduler.PendingEvents, e => e.DeviceId == 42);
        scheduler.AdvanceTo(FrameTiming.MasterHz * 179);
        Assert.Equal(new TapeTime(1790, 1), deck.Observe().Position);
        Assert.NotEqual((uint)790, deck.Observe().Counter);
        scheduler.AdvanceTo(FrameTiming.MasterHz * 180);
        Assert.Equal(TapeDeckMode.Stopped, deck.Observe().Mode);
        Assert.Equal(TapeHoldReason.PhysicalEnd, deck.Observe().HoldReason);
        Assert.DoesNotContain(scheduler.PendingEvents, e => e.DeviceId == 42);
        Assert.Equal(TapeError.Interlock, deck.Play().Failure!.Code);
        deck.ResetCounter(); Assert.Equal(0U, deck.Observe().Counter);
        deck.SetFastWind(true); deck.Rewind();
        scheduler.AdvanceTo(FrameTiming.MasterHz * 195);
        Assert.Equal(default, deck.Observe().Position);
        Assert.Equal(TapeHoldReason.PhysicalEnd, deck.Observe().HoldReason);
    }

    [Fact]
    public void CumulativeProjectionBatchesSubTickEdgesWithoutDrift()
    {
        var (deck, scheduler, ula) = Fixture();
        var unit = new TapeTime(1, FrameTiming.MasterHz * 3);
        var image = Side(new(false, unit), new(true, unit), new(false, unit),
            new(true, new TapeTime(1800, 1) - unit * 3));
        deck.Insert(new(TapeCapacity.C60, image, Side(new TapeRun(false, new(1800, 1))), []), false);
        deck.Play(); Assert.Equal(1, Assert.Single(scheduler.PendingEvents, e => e.DeviceId == 42).DueTick);
        scheduler.AdvanceTo(1); Assert.True(ula.TapeIn);
        Assert.Equal(FrameTiming.MasterHz * 1800, Assert.Single(scheduler.PendingEvents, e => e.DeviceId == 42).DueTick);
        deck.Pause(); Assert.False(ula.TapeIn); Assert.DoesNotContain(scheduler.PendingEvents, e => e.DeviceId == 42);
        scheduler.AdvanceTo(100); deck.Pause(); Assert.True(ula.TapeIn);
        Assert.Equal(FrameTiming.MasterHz * 1800 + 99, Assert.Single(scheduler.PendingEvents, e => e.DeviceId == 42).DueTick);
    }

    [Fact]
    public void InvalidInsertPreservesOldDeckAndRecorderUsesTheSameFacade()
    {
        var (deck, _, _) = Fixture(); deck.Blank(TapeCapacity.C60, false); var before = deck.Observe();
        var wrong = new TapeSideImage(new(1800, 1), default, [new(1, [new(false, new(1, 1))])], [], [], [], new(TapeFormat.Blank, null, true, [], null));
        Assert.False(deck.Insert(new(TapeCapacity.C60, wrong, wrong, []), false).Success);
        Assert.Equal(before, deck.Observe());
        Assert.True(deck.RecordPlay().Success);
        Assert.True(deck.Observe().Capabilities.CanRecord);
        deck.OnMicChanged(0, true); Assert.True(deck.Observe().MicLevel);
    }
}
