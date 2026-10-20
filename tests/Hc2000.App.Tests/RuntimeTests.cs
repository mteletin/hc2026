using Hc2000.App.Runtime;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Tests;

public sealed class RuntimeTests
{
    internal sealed class Fixture
    {
        public long Cpu;
        public int Steps;
        public readonly DeviceScheduler Scheduler = new();
        public readonly HcMemory Memory = new(new MemoryProfile(), new byte[16384], new byte[16384], new byte[16384]);
        public readonly LiveMachine Machine;
        public Fixture(Action? onStep = null)
        {
            var clock = new FrameClock(Scheduler, 1);
            var ula = new Ula(Memory, new KeyboardMatrix(), Scheduler, clock, 2);
            var adapter = new CpuDeviceTimeAdapter(Scheduler, new CpuTimeMapper());
            Machine = new(Scheduler, clock, adapter, ula, () =>
            {
                onStep?.Invoke();
                ula.Write(0xfe, (byte)((Steps++ % 2) * 16), Scheduler.Now);
                Cpu += 11; // synthetic instruction: 55 master ticks, deliberately overshooting
            }, () => Cpu);
        }
    }
    private sealed class Clock : IHostClock
    {
        public long Frequency => 1_000_000_000;
        public long Now { get; set; }
        public List<long> Deadlines { get; } = [];
        public void WaitUntil(long deadline, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Deadlines.Add(deadline); Now = Math.Max(Now, deadline);
        }
    }

    [Theory]
    [InlineData(SpeedMode.Normal,100)]
    [InlineData(SpeedMode.Double,200)]
    [InlineData(SpeedMode.Quadruple,400)]
    public void Nominal50HzUsesHardwareRationalPeriodWithoutDrift(SpeedMode mode, int percent)
    {
        var clock = new Clock(); var pacer = new FramePacer(clock);
        for (int i = 0; i < 500; i++) pacer.Pace(mode, false, default);
        long expected = (long)((Int128)500 * FrameTiming.FrameTicks * clock.Frequency * 100 / (FrameTiming.MasterHz * percent));
        Assert.Equal(expected, clock.Now);
        Assert.Equal(500, clock.Deadlines.Count);
        Assert.InRange(FrameTiming.MasterHz / (double)FrameTiming.FrameTicks, 49.8, 50.0);
        Assert.NotEqual(500L * 20_000_000 * 100 / percent, clock.Now);
    }
    [Fact]
    public void TurboNeverWaitsAndAudioNeverDoublePacesFallback()
    {
        var clock = new Clock(); var pacer = new FramePacer(clock);
        pacer.Pace(SpeedMode.Turbo, false, default);
        pacer.Pace(SpeedMode.Normal, true, default);
        Assert.Empty(clock.Deadlines);
        clock.Now = 5_000_000_000;
        pacer.Pace(SpeedMode.Normal, false, default);
        Assert.InRange(clock.Now, 5_020_000_000, 5_021_000_000);
    }
    [Fact]
    public void SpeedChangesAndHostStallsRebaseWithoutCatchupStorm()
    {
        var clock = new Clock(); var pacer = new FramePacer(clock);
        pacer.Pace(SpeedMode.Normal, false, default);
        long before = clock.Now;
        pacer.Pace(SpeedMode.Quadruple, false, default);
        Assert.InRange(clock.Now - before, 5_000_000, 5_100_000);
        clock.Now += 10_000_000_000;
        before = clock.Now;
        pacer.Pace(SpeedMode.Quadruple, false, default);
        Assert.Equal(before, clock.Now);
        Assert.Throws<ArgumentOutOfRangeException>(() => pacer.Pace((SpeedMode)123, false, default));
    }
    [Theory]
    [InlineData(SpeedMode.Normal)] [InlineData(SpeedMode.Double)]
    [InlineData(SpeedMode.Quadruple)] [InlineData(SpeedMode.Turbo)]
    public void SpeedModesProduceIdenticalMachineTimePixelsAndAudio(SpeedMode mode)
    {
        var reference = new Fixture(); var actual = new Fixture();
        var pacer = new FramePacer(new Clock());
        for (int i = 1; i <= 3; i++)
        {
            Assert.True(reference.Machine.TryRunFrame(() => true, out var expected));
            Assert.True(actual.Machine.TryRunFrame(() => true, out var result));
            pacer.Pace(mode, false, default);
            Assert.Equal(i * FrameTiming.FrameTicks, result!.Video.MasterTick);
            Assert.Equal(expected!.EndMasterTick, result.EndMasterTick);
            Assert.Equal(expected.BeeperEdges, result.BeeperEdges);
            Assert.Equal(expected.Video.Pixels.ToArray(), result.Video.Pixels.ToArray());
            Assert.Equal(reference.Cpu, actual.Cpu);
        }
    }
    [Fact]
    public void FrameReturnsAfterInstructionOvershootAndRetainedPixelsNeverChange()
    {
        var f = new Fixture();
        var frame = f.Machine.RunFrame(); var old = frame.Pixels.ToArray();
        Assert.Equal(355_680, frame.MasterTick);
        Assert.Equal(355_685, f.Machine.MasterTick);
        Assert.Equal(352, frame.Width); Assert.Equal(296, frame.Height);
        Assert.Equal(352 * 296, frame.Pixels.Length);
        f.Memory.Ram.Span[0x5800] = 0x38; // white paper, zero bitmap
        var second = f.Machine.RunFrame();
        Assert.Equal(711_360, second.MasterTick);
        Assert.Equal(old, frame.Pixels.ToArray());
        Assert.NotEqual(old, second.Pixels.ToArray());
    }
    [Fact]
    public void PauseRetainsPartialFrameAndEdgesAtCompletedInstructionBoundary()
    {
        var f = new Fixture(); int boundaries = 0;
        Assert.False(f.Machine.TryRunFrame(() => ++boundaries <= 2, out var none));
        Assert.Null(none); Assert.Equal(22, f.Cpu); Assert.Equal(110, f.Machine.MasterTick);
        Assert.True(f.Machine.TryRunFrame(() => true, out var frame));
        Assert.Equal(0, frame!.StartMasterTick);
        Assert.Equal(55, frame.BeeperEdges[0].MasterTick);
        Assert.Equal(355_680, frame.Video.MasterTick);
        Assert.True(f.Machine.TryRunFrame(() => true, out var next));
        Assert.Equal(frame.EndMasterTick, next!.StartMasterTick);
        Assert.All(next.BeeperEdges, e => Assert.True(e.MasterTick >= next.StartMasterTick));
    }
    [Fact]
    public async Task LiveMachineRejectsOtherThreads()
    {
        var f = new Fixture();
        // Task.Run may be inlined onto the current test worker when awaited; use a distinct owner.
        var checkedOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { Assert.Throws<InvalidOperationException>(() => f.Machine.RunFrame()); checkedOwner.SetResult(); }
            catch (Exception ex) { checkedOwner.SetException(ex); }
        }).Start();
        await checkedOwner.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    [Fact]
    public async Task WorkerOwnsConstructionCommandsStepsCleanupAndLatestMailbox()
    {
        int uiThread = Environment.CurrentManagedThreadId, owner = 0, cleanupOwner = 0, frames = 0;
        var worker = new EmulationWorker(() =>
        {
            owner = Environment.CurrentManagedThreadId;
            return new Fixture(() => Assert.Equal(owner, Environment.CurrentManagedThreadId)).Machine;
        }, ctx =>
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            ctx.SetSpeed(SpeedMode.Turbo);
        }, (frame, speed, token) =>
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            Assert.Equal(0, speed);
            if (++frames == 8) throw new StopFixtureException();
            return false;
        }, _ => cleanupOwner = Environment.CurrentManagedThreadId);
        await Assert.ThrowsAsync<StopFixtureException>(() => worker.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(uiThread, owner); Assert.Equal(owner, cleanupOwner);
        var last = worker.TakeLatestFrame(); Assert.NotNull(last);
        Assert.Equal(8 * FrameTiming.FrameTicks, last.MasterTick);
        Assert.Null(worker.TakeLatestFrame()); worker.Dispose();
    }
    private sealed class StopFixtureException : Exception;
    [Fact]
    public async Task PausedWorkerPumpsControlsWithoutCpuAndSingleStepsThenShutsDown()
    {
        int boundaries = 0; Fixture? fixture = null;
        var worker = new EmulationWorker(() => (fixture = new Fixture()).Machine, ctx =>
        {
            if (++boundaries == 1) ctx.Pause();
            if (boundaries == 3) { Assert.Equal(0, fixture!.Cpu); ctx.Step(); }
            if (boundaries == 4) { Assert.Equal(11, fixture!.Cpu); ctx.Shutdown(); }
        });
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(11, fixture!.Cpu); Assert.Null(worker.TakeLatestFrame()); worker.Dispose();
    }
}
