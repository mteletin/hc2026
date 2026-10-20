using System.Collections.Immutable;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeStateTransferTests
{
    private static (TapeTransport Tape, DeviceScheduler Scheduler, Ula Ula) Fixture()
    {
        var s = new DeviceScheduler(); var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var u = new Ula(memory, new(), s, new FrameClock(s, 1), 2);
        s.InvalidateDevice(1); s.InvalidateDevice(2);
        return (new(s, u, 42), s, u);
    }
    private static ITapeDeckStateTransfer Transfer(TapeTransport tape) => Assert.IsAssignableFrom<ITapeDeckStateTransfer>(tape);
    private static TapeSideImage Side(TapeTime sourceDuration, TapeFormat format = TapeFormat.Blank) =>
        new(new(1800, 1), sourceDuration, [new(1, [new(false, new(1, 7)), new(true, new TapeTime(1800, 1) - new TapeTime(1, 7))])], [], [], [], new(format, null, true, [], null));

    [Fact]
    public void CaptureRestorePreservesFractionalPositionAndFailedRestoreIsTransactional()
    {
        var (t, s, _) = Fixture(); var transfer = Transfer(t);
        t.Deck.Insert(new(TapeCapacity.C60, Side(new(5, 7)), Side(default), []), false); t.Deck.Play(); s.AdvanceTo(17);
        var state = transfer.Capture().Value;
        Assert.Equal(new TapeTime(17, FrameTiming.MasterHz), TapeNativeCodec.Rational(state.Component.Position));
        var before = t.Deck.Observe(); var events = s.PendingEvents.ToArray();
        var bad = state with { Component = state.Component with { Position = new("01", "1") } };
        Assert.False(transfer.Restore(bad).Success);
        Assert.Equal(before, t.Deck.Observe()); Assert.Equal(events, s.PendingEvents);
        var (fresh, freshScheduler, _) = Fixture();
        Assert.True(Transfer(fresh).Restore(state).Success);
        Assert.Equal(before.Position, fresh.Deck.Observe().Position);
        Assert.Equal(Assert.Single(events, e => e.DeviceId == 42).DueTick - 17,
            Assert.Single(freshScheduler.PendingEvents, e => e.DeviceId == 42).DueTick);
    }

    [Fact]
    public void NativeSourceExtentIsDetachedExactAndCanonical()
    {
        var (t, _, _) = Fixture(); Transfer(t);
        var image = Side(new(5, 7), TapeFormat.Tap);
        var cassette = new TapeCassetteImage(TapeCapacity.C60, image, Side(default), []);
        var bytes = TapeNativeCodec.Write(cassette, new()).Value;
        Assert.Empty(image.Annotations);
        var restored = TapeNativeCodec.Read(bytes, [], new()).Value;
        Assert.Equal(new TapeTime(5, 7), restored.A.SourceDuration);
        Assert.Single(restored.A.Annotations, a => a.Kind == TapeAnnotationKind.SourceEof);
        Assert.Equal(bytes, TapeNativeCodec.Write(restored, new()).Value);
        Assert.Equal(default, restored.B.SourceDuration);
    }

    [Fact]
    public void NativeRejectsDuplicateFieldsBadRationalsAndMissingNonblankExtent()
    {
        var (t, _, _) = Fixture(); Transfer(t);
        var cassette = new TapeCassetteImage(TapeCapacity.C60, Side(new(5, 7), TapeFormat.Tap), Side(default), []);
        var bytes = TapeNativeCodec.Write(cassette, new()).Value;
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.False(TapeNativeCodec.Read(System.Text.Encoding.UTF8.GetBytes(text.Replace("\"version\":1", "\"version\":1,\"version\":1")), [], new()).Success);
        Assert.False(TapeNativeCodec.Read(System.Text.Encoding.UTF8.GetBytes(text.Replace("\"num\":\"5\"", "\"num\":\"05\"")), [], new()).Success);
        var dto = System.Text.Json.JsonSerializer.Deserialize<TapeCassetteDto>(bytes)!;
        dto = dto with { Sides = [dto.Sides[0] with { Annotations = [] }, dto.Sides[1]] };
        Assert.False(TapeNativeCodec.Read(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(dto), [], new()).Success);
    }
}
