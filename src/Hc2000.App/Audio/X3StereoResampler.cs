namespace Hc2000.App.Audio;

internal readonly record struct X3PcmEdge(int Channel, long Tick, sbyte Sample, byte Volume);

/// <summary>Projects X3's clocked sample edges onto the worker's PCM frame clock.</summary>
internal sealed class X3StereoResampler
{
    private readonly long masterHz;
    private readonly int sampleRate;
    private readonly float[] levels = new float[4];
    private long tick, phase;

    internal X3StereoResampler(long masterHz, int sampleRate)
    {
        if (masterHz <= 0) throw new ArgumentOutOfRangeException(nameof(masterHz));
        if (sampleRate is not (44100 or 48000)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.masterHz = masterHz; this.sampleRate = sampleRate;
    }

    internal void Reset(long epochTick, bool preserveLevels = false)
    {
        if (epochTick < 0) throw new ArgumentOutOfRangeException(nameof(epochTick));
        tick = epochTick; phase = 0;
        if (!preserveLevels) Array.Clear(levels);
    }

    internal float[] Render(long endTick, ReadOnlySpan<X3PcmEdge> edges)
    {
        Validate(endTick, edges);
        var (count, _) = Advance(endTick - tick);
        var output = new float[checked((int)count * 2)];
        var position = 0;
        foreach (var edge in edges)
        {
            Emit(edge.Tick, output, ref position);
            SetLevel(edge);
        }
        Emit(endTick, output, ref position);
        return output;
    }

    internal void Skip(long endTick, ReadOnlySpan<X3PcmEdge> edges)
    {
        Validate(endTick, edges);
        foreach (var edge in edges) SetLevel(edge);
        Reset(endTick, preserveLevels: true);
    }

    internal void Validate(long endTick, ReadOnlySpan<X3PcmEdge> edges)
    {
        var previous = tick;
        if (endTick < tick) throw new ArgumentOutOfRangeException(nameof(endTick));
        foreach (var edge in edges)
        {
            if (edge.Tick < previous || edge.Tick > endTick || (uint)edge.Channel >= 4 || edge.Volume > 64)
                throw new ArgumentException("Unordered or invalid X3 edge.", nameof(edges));
            previous = edge.Tick;
        }
        var (count, _) = Advance(endTick - tick);
        if (count > sampleRate * 10L) throw new ArgumentOutOfRangeException(nameof(endTick));
    }

    private void SetLevel(X3PcmEdge edge) => levels[edge.Channel] = edge.Sample * edge.Volume / (128f * 64f * 2f);

    private (long Count, long Remainder) Advance(long delta)
    {
        var partial = (Int128)(delta % masterHz) * sampleRate + phase;
        return (checked((long)((Int128)(delta / masterHz) * sampleRate + partial / masterHz)), (long)(partial % masterHz));
    }

    private void Emit(long endTick, float[] output, ref int position)
    {
        var (count, remainder) = Advance(endTick - tick);
        var left = levels[0] + levels[3];
        var right = levels[1] + levels[2];
        for (long index = 0; index < count; index++)
        {
            output[position++] = left;
            output[position++] = right;
        }
        phase = remainder; tick = endTick;
    }
}
