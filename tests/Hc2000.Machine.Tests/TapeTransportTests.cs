using Hc2000.Machine.Memory;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeTransportTests
{
    private static (DeviceScheduler Scheduler, Ula Ula, TapeTransport Tape) Create()
    {
        var scheduler = new DeviceScheduler();
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var clock = new FrameClock(scheduler, 1);
        var ula = new Ula(memory, new(), scheduler, clock, 2);
        return (scheduler, ula, new TapeTransport(scheduler, ula, 3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedRawBlockVectorsIncludeChecksumAndFullPause(bool data)
    {
        var (s, u, t) = Create();
        t.LoadTap(data ? [2, 0, 255, 255] : [2, 0, 0, 0]); t.Play();
        long[] ticks = data
            ? [35405431, 35408810, 35412535, 35421199, 35429864, 35689800, 35707535, 53424275]
            : [88573995, 88577374, 88581099, 88585431, 88589763, 88719732, 88737466, 106454207];
        bool[] levels = [true, false, true, false, true, true, false, false];
        TapeStage[] stages = [TapeStage.Sync1, TapeStage.Sync2, TapeStage.Data, TapeStage.Data,
            TapeStage.Data, TapeStage.TerminalHold, TapeStage.Pause, TapeStage.AtEnd];
        for (var i = 0; i < ticks.Length; i++)
        {
            s.AdvanceTo(ticks[i] - 1);
            if (i != 7) Assert.Equal(!levels[i], u.TapeIn);
            s.AdvanceTo(ticks[i]);
            Assert.Equal(levels[i] ? (byte)0xff : (byte)0xbf, u.Read(0xfffe, s.Now));
            Assert.Equal(stages[i], t.Cursor.Stage);
            if (i == 2) Assert.Equal((0, 0), (t.Cursor.NextBit, t.Cursor.Half));
            if (i == 3) Assert.Equal((0, 1), (t.Cursor.NextBit, t.Cursor.Half));
            if (i == 4) Assert.Equal((1, 0), (t.Cursor.NextBit, t.Cursor.Half));
            if (i == 5) Assert.Equal(16, t.Cursor.NextBit);
        }
        Assert.Equal(1, t.Cursor.BlockIndex);
        Assert.Equal(TapeState.AtEnd, t.Cursor.State);
        t.Play(); Assert.Equal(TapeState.AtEnd, t.Cursor.State);
        Assert.DoesNotContain(s.PendingEvents, e => e.DeviceId == 3);
    }

    [Fact]
    public void FractionCarriesAcrossBlocksAndDataIsMsbFirst()
    {
        var (s, u, t) = Create();
        var bytes = new byte[] { 2, 0, 0, 0, 2, 0, 255, 255 };
        t.LoadTap(bytes); Array.Fill(bytes, (byte)9); t.Play();
        s.AdvanceTo(106454207);
        Assert.Equal((1, TapeStage.Pilot, false), (t.Cursor.BlockIndex, t.Cursor.Stage, u.TapeIn));
        s.AdvanceTo(106465191); Assert.False(u.TapeIn);
        s.AdvanceTo(106465192); Assert.True(u.TapeIn);
        t.LoadTap([2, 0, 128, 128]); t.Play();
        var anchor = s.Now;
        long[] ticks = [35421199, 35429864, 35434196, 35438528, 35442861, 35447193];
        for (var i = 0; i < ticks.Length; i++)
        {
            s.AdvanceTo(anchor + ticks[i]);
            Assert.Equal(i % 2 == 1, u.TapeIn);
            Assert.Equal(((i + 1) / 2, (i + 1) % 2), (t.Cursor.NextBit, t.Cursor.Half));
        }
        t.LoadTap([2, 0, 127, 127]); t.Play();
        anchor = s.Now;
        s.AdvanceTo(anchor + 88573995);
        Assert.Equal(8063, t.Cursor.PilotConsumed);
        Assert.Equal(TapeStage.Sync1, t.Cursor.Stage);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("eject")]
    [InlineData("load")]
    public void CancellationCannotChangeCursorOrEar(string command)
    {
        var (s, u, t) = Create();
        t.LoadTap([2, 0, 0, 0]); t.Play(); s.AdvanceTo(11085);
        switch (command)
        {
            case "stop": t.Stop(); break;
            case "eject": t.Eject(); break;
            case "load": t.LoadTap([2, 0, 0, 0]); break;
        }
        var frozen = t.Cursor;
        s.AdvanceTo(100000);
        Assert.Equal(frozen, t.Cursor); Assert.False(u.TapeIn);
        Assert.DoesNotContain(s.PendingEvents, e => e.DeviceId == 3);
        if (command == "load")
        {
            Assert.Equal((TapeState.Stopped, TapeStage.Pilot, 0), (t.Cursor.State, t.Cursor.Stage, t.Cursor.PilotConsumed));
            t.Play(); s.AdvanceTo(110985); Assert.False(u.TapeIn);
            s.AdvanceTo(110986); Assert.True(u.TapeIn);
        }
        if (command == "eject") Assert.Equal(TapeState.Unloaded, t.Cursor.State);
    }

    [Fact]
    public void ResetPreservesExternalDeckAndExactHalfPulseResidual()
    {
        var (s, u, t) = Create();
        t.LoadTap([2, 0, 0, 0]); t.Play(); s.AdvanceTo(11085);
        var before = t.Cursor; var pending = Assert.Single(s.PendingEvents, e => e.DeviceId == 3);
        t.Reset();
        Assert.Equal(before.State, t.Cursor.State);
        Assert.Equal(before.PilotConsumed, t.Cursor.PilotConsumed);
        Assert.Equal(before.RemainingNumerator, t.Cursor.RemainingNumerator);
        Assert.Equal(pending.DueTick, Assert.Single(s.PendingEvents, e => e.DeviceId == 3).DueTick);
        Assert.True(t.Cursor.Generation > before.Generation); Assert.True(u.TapeIn);
        s.AdvanceTo(pending.DueTick); Assert.False(u.TapeIn);
    }

    [Fact]
    public void StopAtClosingBoundaryConsumesDueEdgeBeforeResume()
    {
        var (s, u, t) = Create();
        t.LoadTap([2, 0, 0, 0]); t.Play();
        s.AdvanceTo(10986); t.Stop(); t.Play();
        Assert.Equal(1, t.Cursor.PilotConsumed); Assert.True(u.TapeIn);
        s.AdvanceTo(21970); Assert.True(u.TapeIn);
        s.AdvanceTo(21971); Assert.False(u.TapeIn);
        s.AdvanceTo(32956); Assert.True(u.TapeIn);
        s.AdvanceTo(43941); Assert.False(u.TapeIn);
    }

    [Fact]
    public void FailedLoadsPreserveMountedPlayingStateAndEmptyIsAtEnd()
    {
        var (s, u, t) = Create();
        t.LoadTap([2, 0, 0, 0]); t.Play(); s.AdvanceTo(10986);
        var old = t.Cursor;
        byte[][] malformed = [[1, 0, 0], [2, 0, 0], [2, 0, 0, 1], [0], [0, 0]];
        foreach (var bytes in malformed)
        {
            Assert.Throws<InvalidDataException>(() => t.LoadTap(bytes));
            Assert.Equal(old, t.Cursor); Assert.True(u.TapeIn);
        }
        Assert.Throws<InvalidDataException>(() => t.LoadTap([2, 0, 0, 0], maxBytes: 3));
        Assert.Throws<InvalidDataException>(() => t.LoadTap([2, 0, 0, 0], maxBlocks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.LoadTap([2, 0, 0, 0], maxBlocks: 4097));
        Assert.Equal(old, t.Cursor);
        t.LoadTap([]); t.Play();
        Assert.Equal(TapeState.AtEnd, t.Cursor.State); Assert.False(u.TapeIn);
        Assert.DoesNotContain(s.PendingEvents, e => e.DeviceId == 3);
    }

    [Fact]
    public void MachineBoundaryDrainsTapeBeforeOrderedStopPlayCommands()
    {
        var scheduler = new DeviceScheduler();
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var clock = new FrameClock(scheduler, 1);
        var ula = new Ula(memory, new(), scheduler, clock, 2);
        var adapter = new CpuDeviceTimeAdapter(scheduler, new CpuTimeMapper());
        long cpu = 0;
        var machine = new Machine(scheduler, clock, adapter, ula, () => cpu += 100, () => cpu);
        var tape = machine.EnableTape(3);
        var started = false;
        Assert.False(machine.TryRunFrame(() =>
        {
            if (!started) { tape.LoadTap([2, 0, 0, 0]); tape.Play(); started = true; }
            if (scheduler.Now < 11000) return true;
            Assert.Equal(1, tape.Cursor.PilotConsumed);
            Assert.Equal((byte)0xff, ula.Read(0xfffe, scheduler.Now));
            tape.Stop(); tape.Play(); tape.Stop(); // host queue ordering at the same boundary
            return false;
        }, out _));
        Assert.Equal(TapeState.Stopped, tape.Cursor.State);
        Assert.Equal(1, tape.Cursor.PilotConsumed);
        Assert.False(ula.TapeIn);
        Assert.DoesNotContain(scheduler.PendingEvents, e => e.DeviceId == 3);
    }

    [Fact]
    public async Task MachineOwnsOptionalTransportAndRejectsForeignThreadCommands()
    {
        var (s, u, _) = Create();
        var clock = new FrameClock(s, 4);
        var adapter = new CpuDeviceTimeAdapter(s, new CpuTimeMapper());
        var machine = new Machine(s, clock, adapter, u, () => {}, () => 0);
        Assert.Null(machine.Tape);
        var tape = machine.EnableTape(5);
        Assert.Same(tape, machine.Tape);
        Assert.Throws<InvalidOperationException>(() => machine.EnableTape(6));
        Assert.Throws<ArgumentException>(() => s.RegisterDevice(5, _ => {}));
        Assert.Equal(TapeState.Unloaded, tape.Cursor.State);
        var result = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => result.SetResult(Record.Exception(() => tape.Play())));
        thread.Start();
        Assert.IsType<InvalidOperationException>(await result.Task);
    }

    [Fact]
    public void HeaderVectorFirstEdgesAndExactStoppedResidual()
    {
        var (s, u, t) = Create();
        t.LoadTap([2, 0, 0, 0]); t.Play();
        s.AdvanceTo(10985); Assert.False(u.TapeIn);
        s.AdvanceTo(10986); Assert.True(u.TapeIn);
        Assert.Equal(1, t.Cursor.PilotConsumed);
        s.AdvanceTo(11085); t.Stop();
        Assert.False(u.TapeIn);
        Assert.True(t.Cursor.WaveformLevel);
        Assert.Equal((Int128)38_099_183_600, t.Cursor.RemainingNumerator);
        s.AdvanceTo(30000); Assert.False(u.TapeIn); t.Play(); t.Play();
        Assert.True(u.TapeIn);
        Assert.Single(s.PendingEvents, e => e.DeviceId == 3);
        s.AdvanceTo(40885); Assert.True(u.TapeIn);
        s.AdvanceTo(40886); Assert.False(u.TapeIn);
        s.AdvanceTo(51870); Assert.False(u.TapeIn);
        s.AdvanceTo(51871); Assert.True(u.TapeIn);
    }
}
