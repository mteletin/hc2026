using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

// Accepted device-time model, not measured hardware or CPU interrupt servicing.
// Literal deadlines deliberately do not derive expected results from FrameTiming.
public sealed class FrameIntAcceptanceTests
{
    private const long Period = 355_680;

    private static (DeviceScheduler Scheduler, FrameClock Clock, Ula Ula) Machine()
    {
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        return (scheduler, clock, new Ula(memory, new(), scheduler, clock, 2));
    }

    [Fact]
    public void IntAndPublishedFramesChangeOnlyAtExactRepeatedDeadlines()
    {
        var (scheduler, clock, ula) = Machine();
        var edges = new List<(long Tick, bool Asserted)>();
        var frames = new List<long>();
        clock.InterruptChanged += (tick, asserted) =>
        {
            Assert.Equal(tick, scheduler.Now);
            Assert.Equal(asserted, clock.IntAsserted);
            edges.Add((tick, asserted));
        };
        ula.FrameReady += frame =>
        {
            Assert.Equal(frame.MasterTick, scheduler.Now);
            Assert.Same(frame, ula.LastFrame);
            frames.Add(frame.MasterTick);
        };
        Assert.False(clock.IntAsserted);
        Assert.Null(ula.LastFrame);
        for (var n = 0; n < 4; n++)
        {
            var start = n * Period;
            scheduler.AdvanceTo(start + 179);
            Assert.False(clock.IntAsserted);
            Assert.Equal(2 * n, edges.Count);
            scheduler.AdvanceTo(start + 180);
            Assert.True(clock.IntAsserted);
            Assert.Equal((start + 180, true), edges[^1]);
            scheduler.AdvanceTo(start + 379);
            Assert.True(clock.IntAsserted);
            Assert.Equal(2 * n + 1, edges.Count);
            scheduler.AdvanceTo(start + 380);
            Assert.False(clock.IntAsserted);
            Assert.Equal((start + 380, false), edges[^1]);
            Assert.Equal(2 * n + 2, edges.Count);
            scheduler.AdvanceTo(start + Period - 1);
            Assert.Equal(n, frames.Count);
            Assert.Equal((long)n, clock.CompletedFrames);
            scheduler.AdvanceTo(start + Period);
            Assert.Equal(n + 1, frames.Count);
            Assert.Equal((long)n + 1, clock.CompletedFrames);
            Assert.Equal(start + Period, ula.LastFrame!.MasterTick);
            // Re-draining the same deadline cannot duplicate a signal or publication.
            scheduler.AdvanceTo(start + Period);
            Assert.Equal(n + 1, frames.Count);
            Assert.Equal(2 * n + 2, edges.Count);
        }
        Assert.Equal(new[] { Period, 2 * Period, 3 * Period, 4 * Period }, frames);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void BulkCpuTimeMappingPublishesEveryCrossedFrameAndUnstretchedIntPulse(int denominator)
    {
        var (scheduler, clock, ula) = Machine();
        var trace = new List<(long Tick, string Output)>();
        clock.InterruptChanged += (tick, asserted) =>
        {
            Assert.Equal(tick, scheduler.Now);
            trace.Add((tick, asserted ? "INT on" : "INT off"));
        };
        ula.FrameReady += frame =>
        {
            Assert.Equal(frame.MasterTick, scheduler.Now);
            trace.Add((frame.MasterTick, "frame"));
        };
        // Synthetic cumulative CPU time only: no CPU execution or interrupt acknowledgement.
        new CpuDeviceTimeAdapter(scheduler, new CpuTimeMapper(5, denominator))
            .SyncCpuTime((3 * 71_136L + 100) * denominator);
        Assert.Equal(new[]
        {
            (180L, "INT on"), (380L, "INT off"), (Period, "frame"),
            (Period + 180, "INT on"), (Period + 380, "INT off"), (2 * Period, "frame"),
            (2 * Period + 180, "INT on"), (2 * Period + 380, "INT off"), (3 * Period, "frame"),
            (3 * Period + 180, "INT on"), (3 * Period + 380, "INT off")
        }, trace);
        Assert.Equal(3 * Period + 500, scheduler.Now);
        Assert.Equal(3, clock.CompletedFrames);
        Assert.Equal(3 * Period, ula.LastFrame!.MasterTick);
        Assert.False(clock.IntAsserted);
    }
}
