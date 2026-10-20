using Hc2000.App.Audio;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class BeeperResamplerTests
{
    private const long Hz = FrameTiming.MasterHz;

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void RationalCountsAndResidualPhaseOverFrames(int rate)
    {
        var sampler = new BeeperResampler(Hz, rate);
        for (long frame = 1; frame <= 2000; frame++)
        {
            var tick = frame * FrameTiming.FrameTicks;
            sampler.Render(tick, []);
            Assert.Equal((long)((Int128)tick * rate / Hz), sampler.SampleCount);
            Assert.Equal((long)((Int128)tick * rate % Hz), sampler.Phase);
        }
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void ArbitraryChunkingAndSameTickEdgesAreBitIdentical(int rate)
    {
        var random = new Random(116);
        var edges = new List<BeeperEdge>();
        long tick = 0;
        for (var i = 0; i < 300; i++)
        {
            tick += random.Next(0, 10000);
            edges.Add(new(tick, random.Next(2) != 0, random.Next(2) != 0));
            if (i % 11 == 0) edges.Add(new(tick, false, true));
        }
        var end = tick + Hz / 50;
        var whole = new BeeperResampler(Hz, rate);
        var expected = whole.Render(end, edges.ToArray());
        var split = new BeeperResampler(Hz, rate);
        var actual = new List<float>();
        long cursor = 0;
        var index = 0;
        while (cursor < end)
        {
            var next = Math.Min(end, cursor + random.Next(1, 14000));
            var start = index;
            while (index < edges.Count && edges[index].MasterTick <= next) index++;
            actual.AddRange(split.Render(next, edges.Skip(start).Take(index - start).ToArray()));
            cursor = next;
        }
        Assert.Equal(expected, actual.ToArray());
        Assert.Equal(whole.SampleCount, split.SampleCount);
        Assert.Equal(whole.Phase, split.Phase);
        Assert.Equal(whole.Pins, split.Pins);
    }

    [Fact]
    public void ZeroDurationLevelsContributeNoAreaIncludingSampleBoundary()
    {
        // 1000 master ticks/sample: edge at a completed sample boundary.
        var first = new BeeperResampler(48_000_000, 48000);
        var second = new BeeperResampler(48_000_000, 48000);
        var a = first.Render(200_000, [new(1000, true, false), new(1000, false, true)]);
        var b = second.Render(200_000, [new(1000, false, true)]);
        for (var i = 0; i < a.Length; i++) Assert.InRange(Math.Abs(a[i] - b[i]), 0, 1e-7);
        var split = new BeeperResampler(48_000_000, 48000);
        var before = split.Render(1000, []);
        var after = split.Render(200_000, [new(1000, false, true)]);
        Assert.Equal(b, before.Concat(after).ToArray());
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void ConstantPinsDecayAndResetErasesFilterAndPhase(int rate)
    {
        var sampler = new BeeperResampler(Hz, rate);
        var pcm = sampler.Render(Hz, []);
        Assert.All(pcm.TakeLast(1000), x => Assert.InRange(Math.Abs(x), 0, 1e-6));
        sampler.Render(Hz + 13331, [new(Hz, true, true)]);
        sampler.Reset(123, true, false);
        var reference = new BeeperResampler(Hz, rate, 123, true, false);
        Assert.Equal(reference.Render(Hz + 123, []), sampler.Render(Hz + 123, []));
        Assert.Equal(reference.Phase, sampler.Phase);
    }

    [Fact]
    public void SpeakerAndMicHaveLogicalTwoToOneWeight()
    {
        var speaker = new BeeperResampler(Hz, 48000);
        var mic = new BeeperResampler(Hz, 48000);
        var baseline = new BeeperResampler(Hz, 48000).Render(Hz / 10, []);
        var a = speaker.Render(Hz / 10, [new(0, true, false)]);
        var b = mic.Render(Hz / 10, [new(0, false, true)]);
        for (var i = 0; i < a.Length; i++) Assert.InRange(Math.Abs((a[i] - baseline[i]) - 2 * (b[i] - baseline[i])), 0, 2e-7);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void LowpassPassesToneAndRejectsNearNyquistAndAboveRateAliasing(int rate)
    {
        var pass = ToneRms(rate, 1000);
        var nearNyquist = ToneRms(rate, (int)(rate * .48));
        var aboveRate = ToneRms(rate, (int)(rate * .9));
        Assert.InRange(pass, .18, .26);
        Assert.True(nearNyquist < pass / 50, $"{rate}: pass={pass}, nearNyquist={nearNyquist}");
        Assert.True(aboveRate < pass / 50, $"{rate}: pass={pass}, aboveRate={aboveRate}");
    }

    private static double ToneRms(int rate, int frequency)
    {
        var sampler = new BeeperResampler(Hz, rate);
        var edges = Enumerable.Range(0, frequency).Select(i =>
            new BeeperEdge((long)i * Hz / (frequency * 2L), i % 2 == 0, false)).ToArray();
        var output = sampler.Render(Hz / 2, edges);
        var settled = output.Skip(rate / 20).ToArray();
        return Math.Sqrt(settled.Average(x => (double)x * x));
    }

    [Fact]
    public void CausalKernelHasDocumentedDelayAndFiniteClampedGain()
    {
        var sampler = new BeeperResampler(48_000_000, 48000);
        var baseline = new BeeperResampler(48_000_000, 48000).Render(100_000, []);
        var step = sampler.Render(100_000, [new(0, true, false)]);
        Assert.InRange(step[0] - baseline[0], -1e-5, 1e-5);
        Assert.InRange(step[BeeperResampler.GroupDelaySamples - 1] - baseline[BeeperResampler.GroupDelaySamples - 1], .24, .26);
        sampler.Reset(0);
        Assert.All(sampler.Render(100_000, [new(0, true, true)], 4), x => Assert.True(float.IsFinite(x) && x is >= -1 and <= 1));
    }

    [Fact]
    public void RegressionsBadOrderAndOverflowAreRejectedBeforeMutation()
    {
        var sampler = new BeeperResampler(Hz, 48000);
        sampler.Render(10000, []);
        var phase = sampler.Phase;
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Render(9999, []));
        Assert.Throws<ArgumentException>(() => sampler.Render(20000, [new(11000, true, false), new(10001, false, false)]));
        Assert.Throws<ArgumentException>(() => sampler.Render(20000, [new(20001, true, false)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Render(20000, [], double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Render(Hz * 11, []));
        Assert.Equal(phase, sampler.Phase);
        Assert.Equal(10000, sampler.EndMasterTick);
        Assert.Throws<OverflowException>(() => new BeeperResampler(1, 48000).Render(long.MaxValue, []));
        var large = new BeeperResampler(long.MaxValue, 48000);
        Assert.Equal(23999, large.Render(long.MaxValue / 2, []).Length); // floor of slightly less than half
    }
}
