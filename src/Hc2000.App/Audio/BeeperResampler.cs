using Hc2000.Machine.Video;

namespace Hc2000.App.Audio;

public enum BeeperChannel { LegacyMix, Bel, EarMonitor }

/// <summary>
/// Master-time, causal band-limited edge renderer. A 64-sample Blackman-windowed
/// sinc (cutoff .40 * sample rate, delay 32 samples) is integrated into a step
/// response. Corrections retain fractional edge phase; no invented future edges.
/// The 20Hz DC blocker and logical MIC/speaker mix are not calibrated hardware.
/// </summary>
public sealed class BeeperResampler
{
    public const int KernelSamples = 64;
    public const int GroupDelaySamples = 32;
    private const int TableResolution = 1024;
    private static readonly double[] Step = CreateStep();
    private readonly double[] corrections = new double[KernelSamples + 2];
    private readonly double dcPole;
    private readonly BeeperChannel channel;
    private double level, previousInput, previousOutput;
    private long tick, samples;
    private int ring;
    public int SampleRate { get; }
    public long MasterHz { get; }
    public long SampleCount => samples;
    public long Phase { get; private set; }
    public long EndMasterTick => tick;
    public BeeperEdge Pins { get; private set; }

    public BeeperResampler(long masterHz, int sampleRate, long epochTick = 0,
        bool speaker = false, bool microphone = false, BeeperChannel channel = BeeperChannel.LegacyMix)
    {
        this.channel = channel;
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        if (masterHz <= 0) throw new ArgumentOutOfRangeException(nameof(masterHz));
        if (sampleRate is not (44100 or 48000)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        MasterHz = masterHz;
        SampleRate = sampleRate;
        dcPole = Math.Exp(-2 * Math.PI * 20 / sampleRate);
        Reset(epochTick, speaker, microphone);
    }

    public void Reset(long epochTick, bool speaker = false, bool microphone = false)
    {
        if (epochTick < 0) throw new ArgumentOutOfRangeException(nameof(epochTick));
        tick = epochTick;
        samples = Phase = 0;
        ring = 0;
        Array.Clear(corrections);
        Pins = new(epochTick, speaker, microphone);
        level = Signal(Pins);
        previousInput = previousOutput = 0;
    }

    /// <summary>Intervals must be contiguous; same-tick producer order is retained.
    /// All supplied edges must be in [previous end, end], including boundary edges.
    /// Validation precedes mutation. Calls are bounded to ten seconds of PCM.</summary>
    public float[] Render(long endMasterTick, ReadOnlySpan<BeeperEdge> edges, double gain = 1)
    {
        if (!double.IsFinite(gain) || gain < 0 || gain > 4) throw new ArgumentOutOfRangeException(nameof(gain));
        var last = tick;
        if (endMasterTick < tick) throw new ArgumentOutOfRangeException(nameof(endMasterTick));
        foreach (var edge in edges)
        {
            if (edge.MasterTick < last || edge.MasterTick > endMasterTick)
                throw new ArgumentException("Edges must be ordered within the known interval.", nameof(edges));
            last = edge.MasterTick;
        }
        var (count, _) = Advance(endMasterTick - tick);
        _ = checked(samples + count);
        if (count > SampleRate * 10L) throw new ArgumentOutOfRangeException(nameof(endMasterTick), "Split long intervals.");
        var output = new float[(int)count];
        var position = 0;
        foreach (var edge in edges)
        {
            EmitTo(edge.MasterTick, output, ref position, gain);
            var next = Signal(edge);
            var delta = next - level;
            // First future sample is 1 - Phase/MasterHz samples after this edge.
            // The raw level changes now; finite corrections implement the delayed LPF.
            var fraction = (double)Phase / MasterHz;
            for (var j = 1; j <= KernelSamples; j++)
                corrections[(ring + j - 1) % corrections.Length] += delta * (StepAt(j - fraction) - 1);
            level = next;
            Pins = edge;
        }
        EmitTo(endMasterTick, output, ref position, gain);
        return output;
    }

    private (long Count, long Remainder) Advance(long delta)
    {
        // Incremental quotient/remainder; Int128 handles a large ratio safely.
        var partial = (Int128)(delta % MasterHz) * SampleRate + Phase;
        var count = checked((long)((Int128)(delta / MasterHz) * SampleRate + partial / MasterHz));
        return (count, (long)(partial % MasterHz));
    }

    private void EmitTo(long end, float[] output, ref int position, double gain)
    {
        var (count, remainder) = Advance(end - tick);
        for (long i = 0; i < count; i++)
        {
            var input = level + corrections[ring];
            corrections[ring] = 0;
            ring = (ring + 1) % corrections.Length;
            var filtered = input - previousInput + dcPole * previousOutput;
            previousInput = input;
            previousOutput = filtered;
            output[position++] = (float)Math.Clamp(filtered * gain, -1, 1);
        }
        samples = checked(samples + count);
        Phase = remainder;
        tick = end;
    }

    private double Signal(BeeperEdge edge) => channel switch
    {
        BeeperChannel.Bel => edge.Speaker ? .25 : -.25,
        // Microphone is the host-only PLAY gate on this separately supplied EAR stream,
        // never the ULA MIC pin. Comparator-to-speaker path is inverted.
        BeeperChannel.EarMonitor => edge.Microphone ? (edge.Speaker ? -.25 : .25) : 0,
        _ => .25 * (edge.MixLevel - 1.5)
    };
    private static double StepAt(double time)
    {
        if (time <= 0) return 0;
        if (time >= KernelSamples) return 1;
        var index = time * TableResolution;
        var whole = (int)index;
        return Step[whole] + (Step[whole + 1] - Step[whole]) * (index - whole);
    }

    private static double[] CreateStep()
    {
        var result = new double[KernelSamples * TableResolution + 1];
        double Kernel(double t)
        {
            var x = t - GroupDelaySamples;
            var sinc = x == 0 ? .8 : Math.Sin(.8 * Math.PI * x) / (Math.PI * x);
            var window = .42 - .5 * Math.Cos(2 * Math.PI * t / KernelSamples)
                + .08 * Math.Cos(4 * Math.PI * t / KernelSamples);
            return sinc * window;
        }
        var previous = Kernel(0);
        for (var i = 1; i < result.Length; i++)
        {
            var current = Kernel((double)i / TableResolution);
            result[i] = result[i - 1] + (previous + current) / (2 * TableResolution);
            previous = current;
        }
        var area = result[^1];
        for (var i = 0; i < result.Length; i++) result[i] /= area;
        result[^1] = 1;
        return result;
    }
}
