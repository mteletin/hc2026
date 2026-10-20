using System.Numerics;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TimingTests
{
    [Fact]
    public void FaithfulConstantsAndCpuCounterAreExact()
    {
        var mapper = new CpuTimeMapper();
        Assert.Equal(500, mapper.Map(100));
        Assert.Equal(500, mapper.Map(100));
        Assert.Equal(17_734_475, FrameTiming.MasterHz);
        Assert.Equal(228 * 5, FrameTiming.LineTicks);
        Assert.Equal(71_136 * 5, FrameTiming.FrameTicks);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Map(99));
        Assert.Equal(100, mapper.CpuTStates);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void FractionalCpuMappingMatchesUnchunkedExactArithmetic(int denominator)
    {
        var chunked = new CpuTimeMapper(5, denominator);
        for (long counter = 1; counter <= 40_004; counter++)
            Assert.Equal((counter * 5 + denominator - 1) / denominator, chunked.Map(counter));
        var bulk = new CpuTimeMapper(5, denominator);
        Assert.Equal(bulk.Map(40_004), chunked.Map(40_004));
        Assert.Equal(bulk.WholeTicks, chunked.WholeTicks);
        Assert.Equal(bulk.Remainder, chunked.Remainder);
    }

    [Fact]
    public void MapperUsesWideIntermediatesAndOverflowLeavesPhaseUnchanged()
    {
        var wide = new CpuTimeMapper(long.MaxValue, long.MaxValue - 1);
        var expected = (BigInteger)3 * long.MaxValue / (long.MaxValue - 1) + 1;
        Assert.Equal((long)expected, wide.Map(3));
        var mapper = new CpuTimeMapper();
        Assert.Equal(5, mapper.Map(1));
        Assert.Throws<OverflowException>(() => mapper.Map(long.MaxValue));
        Assert.Equal(1, mapper.CpuTStates);
        Assert.Equal(5, mapper.WholeTicks);
        Assert.Equal(10, mapper.Map(2));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 2)]
    public void InvalidDividersAndPeriodsAreRejected(long numerator, long denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CpuTimeMapper(numerator, denominator));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RationalClock(0, numerator, denominator));
    }

    [Fact]
    public void DeviceByteDeadlinesDoNotAccumulateRoundedPeriods()
    {
        // 250kbit/s MFM bytes, 32us: 709379/1250 master ticks. No CPU clock involved.
        var bytes = new RationalClock(0, 709_379, 1_250);
        for (long n = 1; n <= 100_000; n++)
        {
            var actual = bytes.Advance();
            var exactNumerator = n * 709_379;
            Assert.Equal((exactNumerator + 1_249) / 1_250, actual);
            Assert.InRange(actual * 1_250 - exactNumerator, 0, 1_249);
        }
        var bulk = new RationalClock(0, 709_379, 1_250);
        Assert.Equal(bulk.Advance(100_000), bytes.WholeTicks + (bytes.Remainder == 0 ? 0 : 1));
        Assert.Equal(bulk.Remainder, bytes.Remainder);
        Assert.Equal(30_148_608, new RationalClock(0, 60_297_215, 2).Advance()); // Motor 1.7s.
        Assert.Equal(3_546_895, new RationalClock(0, FrameTiming.MasterHz, 5).Advance()); // 300rpm.
    }

    [Fact]
    public void SerialAndFlashPhasesAreIndependentOfFrameAndCpuRates()
    {
        var serial = new RationalClock(17, FrameTiming.MasterHz, 9_600);
        for (long bit = 1; bit <= 100_000; bit++)
            Assert.Equal(17 + (bit * FrameTiming.MasterHz + 9_599) / 9_600, serial.Advance());
        // Selected ADR provisional RC model: 0.5s/toggle; not a measured frame divider.
        var flash = new RationalClock(0, FrameTiming.MasterHz, 2);
        Assert.Equal(8_867_238, flash.Advance());
        Assert.Equal(17_734_475, flash.Advance());
        Assert.NotEqual(0, flash.WholeTicks % FrameTiming.FrameTicks);
    }

    [Fact]
    public void RationalClockOverflowAndBadAdvanceAreAtomic()
    {
        var clock = new RationalClock(long.MaxValue - 2, 3, 2);
        Assert.Equal(long.MaxValue, clock.Advance());
        var state = (clock.WholeTicks, clock.Remainder);
        Assert.Throws<OverflowException>(() => clock.Advance());
        Assert.Equal(state, (clock.WholeTicks, clock.Remainder));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(0));
    }

    [Fact]
    public void EqualTimeEventsOrderByPhaseDeviceThenInsertionAndObserveDueTime()
    {
        var scheduler = new DeviceScheduler();
        var trace = new List<(long Tick, int Device, int Kind)>();
        scheduler.RegisterDevice(9, e => trace.Add((scheduler.Now, e.DeviceId, e.Kind)));
        scheduler.RegisterDevice(2, e => trace.Add((scheduler.Now, e.DeviceId, e.Kind)));
        scheduler.Schedule(new(20, EventPhase.Frame, 2, 4));
        scheduler.Schedule(new(20, EventPhase.Video, 9, 3));
        scheduler.Schedule(new(20, EventPhase.Video, 2, 1));
        scheduler.Schedule(new(20, EventPhase.Video, 2, 2));
        scheduler.Schedule(new(20, EventPhase.ExternalInput, 9, 0));
        scheduler.Schedule(new(10, EventPhase.Controller, 9, 5));
        scheduler.AdvanceTo(100);
        Assert.Equal(new[] { (10L, 9, 5), (20L, 9, 0), (20L, 2, 1), (20L, 2, 2), (20L, 9, 3), (20L, 2, 4) }, trace);
        Assert.Equal(100, scheduler.Now);
        scheduler.AdvanceTo(100);
        Assert.Equal(6, trace.Count);
    }

    [Fact]
    public void CancellationAndGenerationRetriggerSilenceOnlyStaleEvents()
    {
        var scheduler = new DeviceScheduler();
        var trace = new List<int>();
        scheduler.RegisterDevice(0, e => trace.Add(e.Kind));
        scheduler.RegisterDevice(1, e => trace.Add(e.Kind));
        var token = scheduler.Schedule(new(10, EventPhase.Controller, 0, 0));
        Assert.True(scheduler.Cancel(token));
        Assert.False(scheduler.Cancel(token));
        scheduler.Schedule(new(20, EventPhase.Controller, 0, 1));
        scheduler.Schedule(new(20, EventPhase.Controller, 1, 2));
        scheduler.InvalidateDevice(0);
        scheduler.Schedule(new(20, EventPhase.Controller, 0, 3, Generation: scheduler.Generation(0)));
        Assert.Equal(2, scheduler.PendingEvents.Count);
        Assert.Throws<ArgumentException>(() => scheduler.Schedule(new(30, EventPhase.Controller, 0, 4)));
        scheduler.AdvanceTo(40);
        Assert.Equal(new[] { 3, 2 }, trace);
        Assert.Empty(scheduler.PendingEvents);
    }

    [Fact]
    public void SameTickEnqueueReentrancyAndTimeRegressionFailExplicitly()
    {
        var scheduler = new DeviceScheduler();
        scheduler.RegisterDevice(0, _ =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Schedule(new(scheduler.Now, EventPhase.Video, 0, 0)));
            Assert.Throws<InvalidOperationException>(() => scheduler.AdvanceTo(scheduler.Now));
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Schedule(new(0, EventPhase.Video, 0, 0)));
        Assert.Throws<ArgumentException>(() => scheduler.Schedule(new(1, (EventPhase)int.MaxValue, 0, 0)));
        scheduler.Schedule(new(1, EventPhase.Video, 0, 0));
        scheduler.AdvanceTo(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.AdvanceTo(9));
    }

    [Fact]
    public void MappedAccessDrainsSamplesBeforeCpuWriteAndObserverEffect()
    {
        var scheduler = new DeviceScheduler();
        var adapter = new CpuDeviceTimeAdapter(scheduler, new());
        var trace = new List<int>();
        var ram = 1;
        scheduler.RegisterDevice(0, _ => trace.Add(ram));
        scheduler.Schedule(new(50, EventPhase.Video, 0, 0));
        scheduler.Schedule(new(55, EventPhase.Video, 0, 0));
        adapter.SyncCpuTime(10); // Read/Write/In/Out/IntVector or pre-fetch observer seam.
        ram = 2; // CPU effect after all due events; faithful Contend adds no clocks.
        trace.Add(ram); // Observer after mapper-before-fetch sync.
        adapter.SyncCpuTime(11);
        Assert.Equal(new[] { 1, 2, 2 }, trace);
    }

    [Theory]
    [InlineData(19, true)]
    [InlineData(20, false)]
    public void ControllerReadWindowIsHalfOpenAtExactDeadline(int cpuT, bool expectedReady)
    {
        var scheduler = new DeviceScheduler();
        var adapter = new CpuDeviceTimeAdapter(scheduler, new());
        var ready = false;
        // Synthetic controller proves scheduler policy, not an implemented 8272 protocol.
        scheduler.RegisterDevice(0, e => ready = e.Kind == 0);
        scheduler.Schedule(new(50, EventPhase.Controller, 0, 0));
        scheduler.Schedule(new(100, EventPhase.Controller, 0, 1));
        adapter.SyncCpuTime(cpuT);
        Assert.Equal(expectedReady, ready);
    }

    [Fact]
    public void SelectedIntPulseAndFirstPixelUseDocumentedRelativePhase()
    {
        var scheduler = new DeviceScheduler();
        var frame = new FrameClock(scheduler, 0);
        var edges = new List<(long, bool)>();
        frame.InterruptChanged += (tick, asserted) => edges.Add((tick, asserted));
        Assert.Equal(64_340, frame.Timing.FirstPixelTick); // (36 + 12832) nominal T.
        scheduler.AdvanceTo(179);
        Assert.False(frame.IntAsserted);
        scheduler.AdvanceTo(180);
        Assert.True(frame.IntAsserted);
        scheduler.AdvanceTo(379); // T75 inclusive; T76 ends the 40T pulse.
        Assert.True(frame.IntAsserted);
        scheduler.AdvanceTo(380);
        Assert.False(frame.IntAsserted);
        Assert.Equal(new[] { (180L, true), (380L, false) }, edges);
        Assert.False(frame.PositionAt(64_339).Display);
        Assert.True(frame.PositionAt(64_340).Display);
        Assert.True(frame.PositionAt(64_979).Display);
        Assert.False(frame.PositionAt(64_980).Display);
    }

    [Fact]
    public void EveryLineHasExpectedBlankBorderAndDisplayWindow()
    {
        var clock = new FrameClock(new(), 0);
        for (var line = 0; line < 312; line++)
        for (var character = 0; character < 57; character++)
        {
            var position = clock.PositionAt(line * 1_140L + character * 20);
            var display = line is >= 56 and <= 247 && character >= 25;
            Assert.Equal(line < 24, position.Blanked);
            Assert.Equal(display, position.Display);
            Assert.Equal(line >= 24 && !display, position.Border);
            Assert.Equal(line, position.Line);
            Assert.Equal(character, position.Character);
        }
        Assert.Equal(clock.PositionAt(0), clock.PositionAt(FrameTiming.FrameTicks));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DeviceDeadlinesStayFixedAcrossCpuDividerMappings(int dividerDenominator)
    {
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 0);
        var mapper = new CpuTimeMapper(5, dividerDenominator);
        var trace = new List<long>();
        clock.FrameCompleted += trace.Add;
        new CpuDeviceTimeAdapter(scheduler, mapper).SyncCpuTime(71_136L * dividerDenominator);
        Assert.Equal(new[] { 355_680L }, trace);
        Assert.Equal(1, clock.CompletedFrames);
        Assert.False(clock.IntAsserted);
    }

    [Fact]
    public void SamplingPrecedesPublicationAtExactFrameBoundary()
    {
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 0);
        var trace = new List<string>();
        clock.VideoSample += (tick, _) => { if (tick == FrameTiming.FrameTicks) trace.Add("sample"); };
        clock.FrameCompleted += tick => { Assert.Equal(FrameTiming.FrameTicks, tick); trace.Add("publish"); };
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        Assert.Equal(new[] { "sample", "publish" }, trace);
    }

    [Fact]
    public void FrameRunnerCarriesInstructionOvershootAcrossCalls()
    {
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 0);
        var runner = new FrameRunner(clock, new(scheduler, new()));
        long cpu = 0;
        runner.RunFrame(() => cpu += 7, () => cpu);
        Assert.Equal(71_141, cpu);
        Assert.Equal(cpu * 5, scheduler.Now);
        runner.RunFrame(() => cpu += 7, () => cpu);
        Assert.Equal(142_275, cpu);
        Assert.Equal(2, clock.CompletedFrames);
    }

    [Fact]
    public void IdleTailDrainsAllCrossedFramesAndDoesNotStretchInt()
    {
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 0);
        var edges = new List<(long, bool)>();
        clock.InterruptChanged += (tick, level) => edges.Add((tick, level));
        var mapper = new CpuTimeMapper();
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        var runner = new FrameRunner(clock, adapter);
        long cpu = 0;
        runner.RunFrame(() => cpu = 2 * 71_136 + 100, () => cpu);
        Assert.Equal(2, clock.CompletedFrames);
        Assert.Equal(cpu * 5, scheduler.Now);
        Assert.False(clock.IntAsserted);
        Assert.Equal(6, edges.Count); // Three complete pulses, including overshoot's third frame.
        Assert.Equal((2 * FrameTiming.FrameTicks + 380, false), edges[^1]);
        Assert.Throws<InvalidOperationException>(() => runner.RunFrame(() => { }, () => cpu));
    }

    [Fact]
    public void TwoMachinesNeverShareClocksPhasesOrQueues()
    {
        var a = new DeviceScheduler();
        var b = new DeviceScheduler();
        var first = new FrameClock(a, 0);
        var second = new FrameClock(b, 0);
        a.AdvanceTo(180);
        Assert.True(first.IntAsserted);
        Assert.False(second.IntAsserted);
        Assert.Equal(0, b.Now);
        Assert.Equal(4, b.PendingEvents.Count);
    }
}
