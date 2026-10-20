using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class CpuTurboTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void OptInUntimedWorkloadCompletesInFractionOfDeviceTime(int multiplier)
    {
        var profile = MachineProfile.Default;
        // Baseline-compatible opt-in probe: before cpu.turbo exists, the profile stays faithful.
        typeof(MachineProfile).GetProperty("CpuTurboMultiplier")?.SetValue(profile, multiplier);
        profile.Validate();
        var mapper = new CpuTimeMapper(profile.CpuMasterTicksNumerator, profile.CpuMasterTicksDenominator);
        var scheduler = new DeviceScheduler();
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        long states = 0;
        // A deterministic untimed worker; no elapsed-time reads or host-clock pacing.
        for (var work = 0; work < 1000; work++) adapter.SyncCpuTime(states += 8);
        Assert.Equal(40_000 / multiplier, scheduler.Now);
        Assert.Equal(8000, mapper.CpuTStates);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ProfileChoiceClonesAndMapsFractionalTimeWithoutDrift(int multiplier)
    {
        var original = MachineProfile.Default with { CpuTurboMultiplier = multiplier };
        var profile = original with { };
        Assert.Equal(multiplier, profile.CpuTurboMultiplier);
        var mapper = CpuTimeMapper.FromProfile(profile);
        Assert.Equal(5, mapper.Numerator);
        Assert.Equal(multiplier, mapper.Denominator);
        for (long states = 0; states < 10_000; states++)
        {
            var expected = (5 * states + multiplier - 1) / multiplier;
            Assert.Equal(expected, mapper.Map(states));
            Assert.Equal(expected, mapper.Map(states));
            Assert.Equal(5 * states / multiplier, mapper.WholeTicks);
            Assert.Equal(5 * states % multiplier, mapper.Remainder);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Map(0));
    }

    [Theory]
    [InlineData("hc2000-v1", 1)]
    [InlineData("hc2000-v1", 2)]
    [InlineData("hc2000-v1", 4)]
    [InlineData("hc2000-v1", 8)]
    [InlineData("hc2000-v2", 1)]
    [InlineData("hc2000-v2", 2)]
    [InlineData("hc2000-v2", 4)]
    [InlineData("hc2000-v2", 8)]
    public void CpuWorkPerFrameScalesButVideoAndInterruptCadenceStayFixed(string name, int multiplier)
    {
        var faithful = name == "hc2000-v1" ? MachineProfile.Hc2000V1 : MachineProfile.Hc2000V2;
        Assert.Equal(1, faithful.CpuTurboMultiplier);
        var profile = faithful with { CpuTurboMultiplier = multiplier };
        Assert.Equal(FrameTiming.MasterHz, profile.MasterHz);
        Assert.Equal(faithful.Video.FlashPeriodNumerator, profile.Video.FlashPeriodNumerator);
        Assert.Equal(faithful.Video.FlashPeriodDenominator, profile.Video.FlashPeriodDenominator);
        Assert.Equal(faithful.Video.FullRaster, profile.Video.FullRaster);
        Assert.Equal(faithful.Video.Palette.ToArray(), profile.Video.Palette.ToArray());
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var adapter = new CpuDeviceTimeAdapter(scheduler, CpuTimeMapper.FromProfile(profile));
        // Synthetic zero ROM stores, explicitly NOT a hardware/boot fixture.
        var memory = new HcMemory(profile.Memory with { RomProvenance = "synthetic turbo timing fixture" },
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ula = new Ula(memory, new KeyboardMatrix(), scheduler, clock, 2, profile.Video);
        var interrupts = new List<(long, bool)>();
        clock.InterruptChanged += (tick, asserted) => interrupts.Add((tick, asserted));
        long states = 0, work = 0;
        var machine = new Machine(scheduler, clock, adapter, ula, () => { states += 4; work++; }, () => states);
        for (var frame = 1; frame <= 3; frame++)
        {
            var video = machine.RunFrame();
            Assert.Equal(frame * FrameTiming.FrameTicks, video.MasterTick);
            Assert.Equal(frame * FrameTiming.FrameTicks, machine.MasterTick);
            Assert.Equal(frame * 17_784L * multiplier, work);
            Assert.Equal(frame * 71_136L * multiplier, states);
            Assert.Equal(352, video.Width);
            Assert.Equal(296, video.Height);
        }
        Assert.Equal(3, clock.CompletedFrames);
        Assert.Equal(Enumerable.Range(0, 3).SelectMany(frame => new[] {
            (frame * FrameTiming.FrameTicks + 180, true),
            (frame * FrameTiming.FrameTicks + 380, false)
        }), interrupts);
        Assert.Equal(1, faithful.CpuTurboMultiplier); // catalog is not modified
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void InstructionOvershootAndDeviceDeadlinesRemainOnMasterClock(int multiplier)
    {
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var adapter = new CpuDeviceTimeAdapter(scheduler,
            CpuTimeMapper.FromProfile(MachineProfile.Default with { CpuTurboMultiplier = multiplier }));
        var events = new List<long>();
        scheduler.RegisterDevice(2, e => events.Add(e.DueTick));
        scheduler.Schedule(new(1001, EventPhase.Controller, 2, 0));
        var frames = new List<long>();
        clock.FrameCompleted += frames.Add;
        var runner = new FrameRunner(clock, adapter);
        long states = 0;
        for (var frame = 1; frame <= 20; frame++)
        {
            runner.RunFrame(() => states += 7, () => states);
            // Shared contract drains at ceil(5 * CPU states / multiplier).
            // At x8, a fractional boundary may reach the frame one CPU T-state early.
            var minimumStates = ((frame * FrameTiming.FrameTicks - 1) * multiplier) / 5 + 1;
            var expectedStates = (minimumStates + 6) / 7 * 7;
            Assert.Equal(expectedStates, states);
            Assert.Equal((5 * states + multiplier - 1) / multiplier, scheduler.Now);
        }
        Assert.Equal(new long[] { 1001 }, events);
        Assert.Equal(Enumerable.Range(1, 20).Select(n => n * FrameTiming.FrameTicks), frames);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(int.MaxValue)]
    public void UnsupportedTurboChoiceRejectsBeforeClockConstruction(int multiplier)
    {
        var profile = MachineProfile.Default with { CpuTurboMultiplier = multiplier };
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuTimeMapper.FromProfile(profile));
    }

    [Fact]
    public void MapperFactoryRequiresAValidNonNullProfile()
    {
        Assert.Throws<ArgumentNullException>(() => CpuTimeMapper.FromProfile(null!));
        Assert.Throws<ArgumentException>(() => CpuTimeMapper.FromProfile(MachineProfile.Default with { Name = "unknown" }));
        Assert.Equal(5, new CpuTimeMapper().Map(1)); // existing callers remain faithful
    }
}
