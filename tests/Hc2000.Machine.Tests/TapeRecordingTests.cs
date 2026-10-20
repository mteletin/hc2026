using Hc2000.Machine.Memory;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeRecordingTests
{
    private static (TapeTransport Tape, DeviceScheduler Clock, Ula Ula) Fixture()
    {
        var clock = new DeviceScheduler();
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ula = new Ula(memory, new(), clock, new FrameClock(clock, 1), 2);
        clock.InvalidateDevice(1); clock.InvalidateDevice(2);
        var tape = new TapeTransport(clock, ula, 42);
        ula.BeeperChanged += edge => tape.OnMicChanged(edge.MasterTick, edge.Microphone);
        return (tape, clock, ula);
    }
    private static ITapeSignalIndex Signal(TapeSideImage image) => new TapeSignalIndexFactory().Build(image, new()).Value;
    private static TapeTime Tick(long value) => new(value, FrameTiming.MasterHz);

    [Fact]
    public void ActualMicIngressWritesInvertedLevelsAtExactMasterCoordinatesNotBelOrReferenceClock()
    {
        var (t, clock, ula) = Fixture(); var d = t.Deck;
        Assert.True(d.Blank(TapeCapacity.C60, false).Success);
        Assert.True(d.Observe().Capabilities.CanRecord);
        Assert.True(d.RecordPlay().Success);
        clock.AdvanceTo(105); ula.Write(0x00fe, 8, clock.Now);
        clock.AdvanceTo(211); ula.Write(0x00fe, 24, clock.Now); // BEL alone, MIC unchanged
        clock.AdvanceTo(317); ula.Write(0x00fe, 16, clock.Now);
        clock.AdvanceTo(509); Assert.True(d.StopEject(false).Success);
        var signal = Signal(d.Export(TapeSide.A).Value.Image);
        Assert.True(signal.Sample(default)); // actual MIC low -> EAR high
        Assert.Equal(Tick(105), signal.NextEdge(default));
        Assert.False(signal.Sample(Tick(105)));
        Assert.Equal(Tick(317), signal.NextEdge(Tick(105)));
        Assert.True(signal.Sample(Tick(317)));
        Assert.False(signal.Sample(Tick(509))); // untouched blank beyond half-open edit
        Assert.True(d.Observe().A.Dirty); Assert.False(d.Observe().B.Dirty);
        Assert.False(ula.TapeIn); Assert.Equal(Tick(509), d.Observe().Position);
        Assert.DoesNotContain(clock.PendingEvents, e => e.DeviceId == 42);
    }

    [Fact]
    public void ConstantMicStillErasesAndZeroTimeChangesHaveNoInventedPulse()
    {
        var (t, clock, _) = Fixture(); var d = t.Deck;
        var side = new TapeSideImage(new(1800, 1), new(1, 1),
            [new(1, [new(true, new(1800, 1))])],
            [new(Tick(100), 0, "erased", null, null), new(Tick(600), 1, "retained", null, null)],
            [new(new(1, 1), 0, TapeAnnotationKind.SourceEof, "source-eof", null, null)],
            [], new(TapeFormat.Blank, null, true, [], null));
        Assert.True(d.Insert(new(TapeCapacity.C60, side, side, []), false).Success);
        d.OnMicChanged(clock.Now, true); d.RecordPlay();
        clock.AdvanceTo(200); d.OnMicChanged(clock.Now, false); d.OnMicChanged(clock.Now, true);
        clock.AdvanceTo(400); d.StopEject(false);
        var image = d.Export(TapeSide.A).Value.Image; var signal = Signal(image);
        Assert.False(signal.Sample(default)); Assert.Equal(Tick(400), signal.NextEdge(default));
        Assert.True(signal.Sample(Tick(400)));
        Assert.Equal("retained", Assert.Single(image.Markers).Label);
        Assert.Equal(new TapeTime(1, 1), image.SourceDuration);
        Assert.Single(image.Annotations, a => a.Kind == TapeAnnotationKind.SourceEof);
        Assert.Contains(image.Annotations, a => a.Kind == TapeAnnotationKind.Overwrite);
    }

    [Fact]
    public void PauseCommitsButDoesNotWriteOrReplayPausedMicAndPlayDoesNotDisarmRec()
    {
        var (t, clock, ula) = Fixture(); var d = t.Deck; d.Blank(TapeCapacity.C60, false); d.RecordPlay();
        Assert.True(d.Play().Success); Assert.Equal(TapeDeckMode.Recording, d.Observe().Mode);
        clock.AdvanceTo(100); Assert.True(d.Pause().Success);
        var paused = d.Observe(); Assert.True(paused.A.Dirty);
        clock.AdvanceTo(200); ula.Write(0x00fe, 8, clock.Now);
        clock.AdvanceTo(400); Assert.Equal(Tick(100), d.Observe().Position);
        Assert.Equal(paused.A.EditRevision, d.Observe().A.EditRevision);
        Assert.True(d.Pause().Success);
        clock.AdvanceTo(500); d.StopEject(false);
        var signal = Signal(d.Export(TapeSide.A).Value.Image);
        Assert.True(signal.Sample(default)); Assert.False(signal.Sample(Tick(100)));
        Assert.Equal(Tick(200), d.Observe().Position);
    }

    [Fact]
    public void TabAndMotionInterlocksAndDirtyEjectRefusalDoNotDiscardRecordedSide()
    {
        var (t, clock, _) = Fixture(); var d = t.Deck; d.Blank(TapeCapacity.C60, false); d.SetTab(TapeSide.A, false);
        var before = d.Observe(); Assert.Equal(TapeError.WriteProtected, d.RecordPlay().Failure!.Code); Assert.Equal(before, d.Observe());
        d.SetTab(TapeSide.A, true); d.RecordPlay();
        Assert.Equal(TapeError.Interlock, d.SetTab(TapeSide.A, false).Failure!.Code);
        Assert.Equal(TapeError.Interlock, d.Flip().Failure!.Code);
        Assert.Equal(TapeError.Interlock, d.FastForward().Failure!.Code);
        Assert.Equal(TapeError.Interlock, d.Export(TapeSide.A).Failure!.Code);
        clock.AdvanceTo(100); d.StopEject(false); before = d.Observe();
        Assert.Equal(TapeError.DirtyConfirmationRequired, d.StopEject(false).Failure!.Code); Assert.Equal(before, d.Observe());
        d.AcknowledgeSave(new(before.MediaIdentity, TapeSide.A, before.A.EditRevision,
            TapeSignalFidelity.DataOnly, TapeByteFidelity.Transformed, TapeFormat.Tap));
        Assert.True(d.Observe().A.Dirty);
    }

    [Fact]
    public void CaptureFlushesActiveIntervalAndContinuationUsesExistingNativeFieldsAndOneEndpoint()
    {
        var (t, clock, ula) = Fixture(); var d = t.Deck; d.Blank(TapeCapacity.C60, false); d.RecordPlay();
        clock.AdvanceTo(123); ula.Write(0x00fe, 8, clock.Now); clock.AdvanceTo(250);
        var state = t.StateTransfer.Capture().Value;
        Assert.NotNull(state.Component.RecordStart); Assert.NotNull(state.Component.RecordHead);
        Assert.True(state.Component.Dirty[0]); Assert.Equal("recording", state.Component.Mode);
        var (copy, otherClock, otherUla) = Fixture(); otherUla.Write(0x00fe, 8, otherClock.Now);
        Assert.True(copy.StateTransfer.Restore(state).Success);
        Assert.Single(otherClock.PendingEvents, e => e.DeviceId == 42);
        clock.AdvanceTo(400); otherClock.AdvanceTo(150);
        d.StopEject(false); copy.Deck.StopEject(false);
        var a = Signal(d.Export(TapeSide.A).Value.Image); var b = Signal(copy.Deck.Export(TapeSide.A).Value.Image);
        Assert.Equal(d.Observe().Position, copy.Deck.Observe().Position);
        Assert.Equal(a.NextEdge(default), b.NextEdge(default));
        Assert.Equal(a.Sample(Tick(399)), b.Sample(Tick(399)));
    }

    [Fact]
    public void PhysicalEndClosesRecordingIgnoresSourceMarkersAndNeverWritesPastCapacity()
    {
        var (t, clock, _) = Fixture(); var d = t.Deck; d.Blank(TapeCapacity.C60, false);
        d.SetFastWind(true); d.FastForward(); clock.AdvanceTo(FrameTiming.MasterHz * 15 - 1); d.StopEject(false);
        var start = d.Observe().Position; Assert.True(d.RecordPlay().Success);
        clock.AdvanceTo(clock.Now + 120);
        var end = d.Observe(); Assert.Equal(TapeDeckMode.Stopped, end.Mode); Assert.Equal(TapeHoldReason.PhysicalEnd, end.HoldReason);
        Assert.Equal(new TapeTime(1800, 1), end.Position); Assert.True(end.A.Dirty);
        Assert.False(Signal(d.Export(TapeSide.A).Value.Image).Sample(start - Tick(1)));
        Assert.True(Signal(d.Export(TapeSide.A).Value.Image).Sample(start));
        Assert.Equal(TapeError.Interlock, d.RecordPlay().Failure!.Code);
    }

    [Fact]
    public void RevisionExhaustionStopsAtLastCommittedCoordinateAndFaultedDirtyStateCanBeCaptured()
    {
        var (t, clock, _) = Fixture(); var d = t.Deck; d.Blank(TapeCapacity.C60, false);
        var original = t.StateTransfer.Capture().Value;
        var full = original with { Component = original.Component with
        {
            Dirty = [true, false], EditRevision = [ulong.MaxValue.ToString(), "0"],
            SavedRevision = [(ulong.MaxValue - 1).ToString(), "0"]
        } };
        Assert.True(t.StateTransfer.Restore(full).Success);
        d.RecordPlay(); clock.AdvanceTo(100);
        Assert.Equal(TapeError.BoundsExceeded, d.StopEject(false).Failure!.Code);
        var fault = d.Observe(); Assert.Equal(TapeDeckMode.Stopped, fault.Mode);
        Assert.Equal(TapeHoldReason.Fault, fault.HoldReason); Assert.Equal(default(TapeTime), fault.Position);
        Assert.True(fault.A.Dirty); Assert.Equal(ulong.MaxValue, fault.A.EditRevision);
        Assert.False(Signal(d.Export(TapeSide.A).Value.Image).Sample(default));
        Assert.DoesNotContain(clock.PendingEvents, e => e.DeviceId == 42);
        Assert.True(t.StateTransfer.Capture().Success); // recovery must not be poisoned by latched failure
    }

    [Fact]
    public void MalformedActiveRecorderRestoreCannotMutateLiveHeadOrEdits()
    {
        var (t, clock, _) = Fixture(); var d = t.Deck; d.Blank(TapeCapacity.C60, false); d.RecordPlay();
        clock.AdvanceTo(100); var good = t.StateTransfer.Capture().Value; var before = d.Observe();
        var bad = good with { Component = good.Component with { RecordHead = null } };
        Assert.Equal(TapeError.SnapshotUnsupported, t.StateTransfer.Restore(bad).Failure!.Code);
        Assert.Equal(before, d.Observe()); Assert.Single(clock.PendingEvents, e => e.DeviceId == 42);
    }

    [Fact]
    public void ResetRetainsRecordingLatchHeadAndDirtySignalAndNewPinCreatesOrdinaryBoundary()
    {
        var (t, clock, ula) = Fixture(); var d = t.Deck; d.Blank(TapeCapacity.C60, false); d.RecordPlay();
        clock.AdvanceTo(100); ula.Write(0x00fe, 8, clock.Now); clock.AdvanceTo(200);
        var before = d.Observe(); t.Reset();
        Assert.Equal(before.Position, d.Observe().Position); Assert.Equal(before.MediaIdentity, d.Observe().MediaIdentity);
        Assert.Equal(TapeDeckMode.Recording, d.Observe().Mode); Assert.True(d.Observe().A.Dirty);
        ula.Write(0x00fe, 0, clock.Now); clock.AdvanceTo(300); d.StopEject(false);
        var signal = Signal(d.Export(TapeSide.A).Value.Image);
        Assert.True(signal.Sample(default)); Assert.False(signal.Sample(Tick(100))); Assert.True(signal.Sample(Tick(200)));
        Assert.False(signal.Sample(Tick(300)));
    }
}
