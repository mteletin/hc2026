using System.Text.Json;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeCoreAdditionalTests
{
    private static (TapeTransport Tape, DeviceScheduler Clock, Ula Ula) Fixture()
    {
        var s = new DeviceScheduler(); var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var u = new Ula(memory, new(), s, new FrameClock(s, 1), 2); s.InvalidateDevice(1); s.InvalidateDevice(2);
        return (new(s, u, 42), s, u);
    }
    private static TapeSideImage Side(TapeTime source, IEnumerable<TapeStopMarker>? markers = null, IEnumerable<TapeAnnotation>? annotations = null) =>
        new(new(1800, 1), source, [new(1, [new(true, new(1800, 1))])], markers ?? [], annotations ?? [], [], new(TapeFormat.Blank, null, true, [], null));

    [Fact]
    public void SourceStopsConsumeInOrdinalOrderAtSameSubTickWithoutRecursiveEvents()
    {
        var (t, clock, u) = Fixture(); var p = new TapeTime(1, FrameTiming.MasterHz * 3);
        t.Deck.Insert(new(TapeCapacity.C60, Side(default, [new(p, 0, "first", null, null), new(p, 1, "second", null, null)]), Side(default), []), false);
        t.Deck.Play(); Assert.True(u.TapeIn); clock.AdvanceTo(1);
        Assert.Equal(p, t.Deck.Observe().Position); Assert.Equal(TapeHoldReason.FormatStop, t.Deck.Observe().HoldReason);
        Assert.False(u.TapeIn); Assert.DoesNotContain(clock.PendingEvents, e => e.DeviceId == 42);
        t.Deck.Play(); Assert.Equal(TapeHoldReason.FormatStop, t.Deck.Observe().HoldReason);
        t.Deck.Play(); Assert.Equal(TapeHoldReason.None, t.Deck.Observe().HoldReason); Assert.True(u.TapeIn);
        Assert.Single(clock.PendingEvents, e => e.DeviceId == 42);
    }

    [Fact]
    public void CounterIsPinnedReelGeometryAndZeroIsIndependentOnEachSide()
    {
        var (t, clock, _) = Fixture(); t.Deck.Blank(TapeCapacity.C60, false); t.Deck.FastForward(); clock.AdvanceTo(FrameTiming.MasterHz * 180);
        Assert.Equal(763U, t.Deck.Observe().Counter); Assert.Equal(1800UL, t.Deck.Observe().ElapsedSeconds);
        t.Deck.ResetCounter(); Assert.Equal(0U, t.Deck.Observe().Counter);
        t.Deck.Flip(); Assert.Equal(0U, t.Deck.Observe().Counter); Assert.Equal(0UL, t.Deck.Observe().ElapsedSeconds);
        t.Deck.Flip(); Assert.Equal(0U, t.Deck.Observe().Counter); t.Deck.SetFastWind(true); t.Deck.Rewind();
        clock.AdvanceTo(FrameTiming.MasterHz * 195); Assert.Equal(237U, t.Deck.Observe().Counter);
        Assert.Equal(0UL, t.Deck.Observe().ElapsedSeconds);
    }

    [Fact]
    public void SaveAcknowledgementCorrelatesIdentityRevisionFidelityAndOnlyOneSide()
    {
        var (t, _, _) = Fixture(); t.Deck.Blank(TapeCapacity.C60, false);
        var state = t.StateTransfer.Capture().Value;
        var edited = state with { Component = state.Component with { EditRevision = ["1", "2"], SavedRevision = ["0", "1"], Dirty = [true, true] } };
        Assert.True(t.StateTransfer.Restore(edited).Success);
        var before = t.Deck.Observe(); Assert.Equal(TapeError.DirtyConfirmationRequired, t.Deck.StopEject(false).Failure!.Code);
        Assert.Equal(before, t.Deck.Observe());
        t.Deck.AcknowledgeSave(new(before.MediaIdentity + 1, TapeSide.A, 1, TapeSignalFidelity.ExactSignal, TapeByteFidelity.Transformed, TapeFormat.Tzx));
        Assert.True(t.Deck.Observe().A.Dirty);
        t.Deck.AcknowledgeSave(new(before.MediaIdentity, TapeSide.A, 1, TapeSignalFidelity.Quantized, TapeByteFidelity.Transformed, TapeFormat.Tzx));
        Assert.True(t.Deck.Observe().A.Dirty);
        t.Deck.AcknowledgeSave(new(before.MediaIdentity, TapeSide.A, 1, TapeSignalFidelity.ExactSignal, TapeByteFidelity.Transformed, TapeFormat.Wav));
        Assert.True(t.Deck.Observe().A.Dirty);
        t.Deck.AcknowledgeSave(new(before.MediaIdentity, TapeSide.A, 1, TapeSignalFidelity.ExactSignal, TapeByteFidelity.Transformed, TapeFormat.Tzx));
        Assert.False(t.Deck.Observe().A.Dirty); Assert.True(t.Deck.Observe().B.Dirty);
        Assert.Equal(1UL, t.Deck.Export(TapeSide.A).Value.EditRevision);
    }

    [Theory]
    [InlineData(1, 0, 0, "missing-xor")]
    [InlineData(2, 0, 1, "bad-xor")]
    public void RawTapWarnsWithoutRepairingAndLegacyApiStillRejects(byte length, byte flag, byte checksum, string warning)
    {
        byte[] bytes = length == 1 ? [1, 0, flag] : [2, 0, flag, checksum];
        var read = new TapCodec().Read(bytes, new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
        Assert.Equal(warning, Assert.Single(read.Warnings).Code); Assert.Equal(bytes, Assert.Single(read.SourceAssets).Bytes.ToArray());
        Assert.Empty(read.Image.Markers);
        var (t, _, _) = Fixture(); Assert.Throws<InvalidDataException>(() => t.LoadTap(bytes));
        Assert.False(new TapCodec().Write(read.Image, new(TapeFormat.Tap, TapeSide.A)).Success);
        Assert.False(new TapeCodecRegistry().Find(TapeFormat.Wav).Success);
    }

    [Fact]
    public void RawTapPhaseUsesSamePinnedWaveformAndSourceEofIsNotPhysicalEnd()
    {
        var (t, clock, u) = Fixture();
        var read = new TapCodec().Read([2, 0, 0, 0], new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
        t.Deck.Insert(new(TapeCapacity.C60, read.Image, Side(default), read.SourceAssets), false); t.Deck.Play();
        clock.AdvanceTo(88573995); Assert.True(u.TapeIn);
        clock.AdvanceTo(88577374); Assert.False(u.TapeIn);
        clock.AdvanceTo(88581099); Assert.True(u.TapeIn);
        clock.AdvanceTo(88737466); Assert.False(u.TapeIn);
        clock.AdvanceTo(106454207); Assert.Equal(TapeDeckMode.Playing, t.Deck.Observe().Mode);
        Assert.Equal(TapeHoldReason.None, t.Deck.Observe().HoldReason);
        Assert.Equal(FrameTiming.MasterHz * 1800, Assert.Single(clock.PendingEvents, e => e.DeviceId == 42).DueTick);
    }

    [Fact]
    public void ResetOfNativePausedAndWindingStatePreservesPositionLatchesAndResidual()
    {
        var (t, clock, _) = Fixture(); t.Deck.Blank(TapeCapacity.C90, false); t.Deck.FastForward(); clock.AdvanceTo(12345);
        var before = t.Deck.Observe(); var due = Assert.Single(clock.PendingEvents, e => e.DeviceId == 42).DueTick;
        t.Reset(); Assert.Equal(before, t.Deck.Observe()); Assert.Equal(due, Assert.Single(clock.PendingEvents, e => e.DeviceId == 42).DueTick);
        t.Stop(); t.Play(); t.Deck.Pause(); before = t.Deck.Observe();
        t.Reset(); Assert.Equal(before, t.Deck.Observe()); Assert.DoesNotContain(clock.PendingEvents, e => e.DeviceId == 42);
    }

    [Fact]
    public void SourceExtentSurvivesOverwriteAndConflictsCannotBeSerialized()
    {
        var eof = new TapeAnnotation(new(1, 7), 0, TapeAnnotationKind.SourceEof, "source-eof", null, null);
        var side = Side(new(1, 7), annotations: [eof]);
        var index = new TapeSignalIndexFactory().Build(side, new()).Value;
        var replacement = new TapeSideImage(new(1, 1), default, [new(1, [new(false, new(1, 1))])], [], [], [], new(TapeFormat.Blank, null, true, [], null));
        var edited = index.Replace(default, new(1, 1), replacement, new()).Value;
        Assert.Equal(new TapeTime(1, 7), edited.SourceDuration); Assert.Equal(eof, Assert.Single(edited.Annotations));
        var conflict = Side(new(1, 7), annotations: [eof, eof with { Ordinal = 1 }]);
        Assert.False(TapeNativeCodec.Write(new(TapeCapacity.C60, conflict, Side(default), []), new()).Success);
        var blankBytes = TapeNativeCodec.Write(new(TapeCapacity.C60, Side(default), Side(default), []), new()).Value;
        var blank = JsonSerializer.Deserialize<TapeCassetteDto>(blankBytes)!;
        blank = blank with { Sides = [blank.Sides[0] with { Annotations = [] }, blank.Sides[1]] };
        Assert.Equal(default, TapeNativeCodec.Read(JsonSerializer.SerializeToUtf8Bytes(blank), [], new()).Value.A.SourceDuration);
    }

    [Fact]
    public void FutureDeadlineOverflowIsNamedAndLeavesTheLiveCassetteAndClockUnchanged()
    {
        var (t, clock, _) = Fixture(); clock.AdvanceTo(long.MaxValue - 1);
        Assert.True(t.Deck.Blank(TapeCapacity.C60, false).Success);
        var before = t.Deck.Observe(); var generation = clock.Generation(42); var sequence = clock.NextSequence;
        var failed = t.Deck.Play();
        Assert.False(failed.Success); Assert.Equal(TapeError.BoundsExceeded, failed.Failure!.Code);
        Assert.Equal(before, t.Deck.Observe()); Assert.Equal(generation, clock.Generation(42));
        Assert.Equal(sequence, clock.NextSequence); Assert.Empty(clock.PendingEvents);
    }

    [Fact]
    public void ActualUlaBusMicIngressIsObservedWithoutArmingRecordingOrDrivingEar()
    {
        var s = new DeviceScheduler(); var clock = new FrameClock(s, 1);
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var u = new Ula(memory, new(), s, clock, 2); s.InvalidateDevice(1); s.InvalidateDevice(2);
        var machine = new Machine(s, clock, new(s, new CpuTimeMapper()), u, () => { }, () => 0);
        var t = machine.EnableTape(42); t.Deck.Blank(TapeCapacity.C60, false);
        s.AdvanceTo(3); u.Write(0xfffe, 8, 3);
        Assert.True(t.Deck.Observe().MicLevel); Assert.False(t.Deck.Observe().EarLevel);
        Assert.False(t.Deck.Observe().A.Dirty); Assert.Equal(TapeDeckMode.Stopped, t.Deck.Observe().Mode);
        Assert.True(t.StateTransfer.Capture().Value.Component.MicLevel);
        s.AdvanceTo(4); u.Write(0xfffe, 24, 4); // b4-only change is not another MIC transition.
        Assert.True(t.Deck.Observe().MicLevel); Assert.True(t.Deck.Observe().Capabilities.CanRecord);
        Assert.False(u.TapeIn);
    }

    [Fact]
    public void LegacyFacadeKeepsEofPlayAndImmediateEjectAfterNativeCapture()
    {
        var (t, clock, _) = Fixture(); t.LoadTap([]); Assert.True(t.StateTransfer.Capture().Success);
        t.Play(); Assert.Equal(TapeState.AtEnd, t.Cursor.State);
        Assert.DoesNotContain(clock.PendingEvents, e => e.DeviceId == 42);
        Assert.True(t.Deck.Play().Success); // explicit deck button can traverse blank beyond source EOF.
        Assert.Single(clock.PendingEvents, e => e.DeviceId == 42);
        t.Eject(); Assert.Equal(TapeState.Unloaded, t.Cursor.State);
        Assert.DoesNotContain(clock.PendingEvents, e => e.DeviceId == 42);
    }
}
