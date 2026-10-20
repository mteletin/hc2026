using Hc2000.Machine.Memory;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeLegacyMigrationTests
{
    private static (TapeTransport Tape, DeviceScheduler Scheduler, Ula Ula) Fixture()
    {
        var s = new DeviceScheduler(); var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var u = new Ula(memory, new(), s, new FrameClock(s, 1), 2); s.InvalidateDevice(1); s.InvalidateDevice(2);
        return (new(s, u, 42), s, u);
    }
    private static Tape1MigrationInput Input(TapeTransport t, DeviceScheduler s, byte[] raw)
    {
        var cursor = t.Cursor; var pending = s.PendingEvents.SingleOrDefault(e => e.DeviceId == 42);
        // Authored tape1 wire fixture: the old event kind was its raw stage. The new
        // single transport owns native kinds100..102, never a second live old device.
        if (pending is not null) pending = pending with { Kind = (int)cursor.Stage,
            DueTick = checked((long)((cursor.IdealTargetNumerator + TapeTransport.ReferenceHz - 1) / TapeTransport.ReferenceHz)) };
        return new(cursor, [new("tape-source-0", TapeFormat.Tap, raw)], s.Now, 42, s.Generation(42), pending);
    }

    [Theory]
    [InlineData(100, false, TapeStage.Pilot)]
    [InlineData(11085, true, TapeStage.Pilot)]
    [InlineData(88573995, false, TapeStage.Sync1)]
    [InlineData(88577374, false, TapeStage.Sync2)]
    [InlineData(88581100, false, TapeStage.Data)]
    [InlineData(88719732, false, TapeStage.TerminalHold)]
    [InlineData(88737466, false, TapeStage.Pause)]
    [InlineData(106454207, false, TapeStage.AtEnd)]
    public void EveryLegacyPhaseAndStoppedResidualConvertsAndContinuesIdentically(long now, bool stop, TapeStage stage)
    {
        byte[] raw = [2, 0, 0, 0]; var (old, clock, oldUla) = Fixture();
        old.LoadTap(raw); old.Play(); clock.AdvanceTo(now); if (stop) old.Stop();
        var cursor = old.Cursor; Assert.Equal(stage, cursor.Stage);
        var state = old.StateTransfer.Migrate(Input(old, clock, raw), new()).Value;
        var (next, nextClock, nextUla) = Fixture(); Assert.True(next.StateTransfer.Restore(state).Success);
        Assert.Equal(cursor.Stage, next.Cursor.Stage);
        Assert.Equal(cursor.PilotConsumed, next.Cursor.PilotConsumed);
        Assert.Equal(cursor.NextBit, next.Cursor.NextBit);
        Assert.Equal(cursor.Half, next.Cursor.Half);
        Assert.Equal(cursor.RemainingNumerator, next.Cursor.RemainingNumerator);
        Assert.Equal(oldUla.TapeIn, nextUla.TapeIn);
        if (stop) { old.Play(); next.Play(); }
        for (int i = 0; i < 12; i++)
        {
            var pending = clock.PendingEvents.SingleOrDefault(e => e.DeviceId == 42);
            if (pending is null) break;
            Assert.Equal(pending.DueTick - now, Assert.Single(nextClock.PendingEvents, e => e.DeviceId == 42).DueTick);
            clock.AdvanceTo(pending.DueTick); nextClock.AdvanceTo(pending.DueTick - now);
            Assert.Equal(oldUla.TapeIn, nextUla.TapeIn);
            Assert.Equal(old.Cursor.Stage, next.Cursor.Stage);
            Assert.Equal(old.Cursor.PilotConsumed, next.Cursor.PilotConsumed);
        }
    }

    [Fact]
    public void UnloadedAndEmptyLoadedRemainDistinctAndLegacyEofHoldsOnlyMigratedMedia()
    {
        var (t, s, _) = Fixture();
        var empty = new Tape1MigrationInput(t.Cursor, [], 0, 42, s.Generation(42), null);
        Assert.Null(t.StateTransfer.Migrate(empty, new()).Value.Cassette);
        t.LoadTap([]);
        var loaded = t.StateTransfer.Migrate(Input(t, s, []), new()).Value;
        Assert.NotNull(loaded.Cassette); Assert.Single(loaded.Cassette!.A.Markers);
        var imported = new TapCodec().Read([], new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
        Assert.Empty(imported.Image.Markers);
    }

    [Fact]
    public void InvalidCursorEventOrCapacityRefusesWithoutTouchingLiveTape()
    {
        byte[] raw = [2, 0, 0, 0]; var (t, s, _) = Fixture(); t.LoadTap(raw); t.Play(); s.AdvanceTo(100);
        var before = t.Cursor; var events = s.PendingEvents.ToArray(); var input = Input(t, s, raw); var e = input.PendingEvent!;
        Tape1MigrationInput[] bad = [
            input with { Cursor = before with { PilotConsumed = 9999 } },
            input with { Cursor = before with { RemainingNumerator = 0 } },
            input with { Cursor = before with { WaveformLevel = true } },
            input with { PendingEvent = e with { DueTick = e.DueTick + 1 } },
            input with { PendingEvent = e with { Kind = 100 } },
            input with { PendingEvent = e with { Payload = 1 } },
            input with { PendingEvent = e with { Generation = e.Generation + 1 } }
        ];
        foreach (var candidate in bad) Assert.False(t.StateTransfer.Migrate(candidate, new()).Success);
        var tooLong = Enumerable.Range(0, 600).SelectMany(_ => raw).ToArray();
        var overflow = input with { RawTapAssets = [new("tape-source-0", TapeFormat.Tap, tooLong)] };
        Assert.Equal(TapeError.CapacityExceeded, t.StateTransfer.Migrate(overflow, new()).Failure!.Code);
        Assert.Equal(before, t.Cursor); Assert.Equal(events, s.PendingEvents);
    }
}
