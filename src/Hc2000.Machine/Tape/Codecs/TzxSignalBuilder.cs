using System.Numerics;
using System.Collections.Immutable;

namespace Hc2000.Machine.Tape.Codecs;

/// <summary>Codec output builder over TapeSignalChunk; no transport, index or scheduler.</summary>
internal sealed class TzxSignalBuilder(TapeLimits limits)
{
    public bool Level { get; set; }
    public TapeTime Position { get; private set; }
    public List<TapeSignalChunk> Chunks { get; } = [];
    private long virtualRuns;
    private long storedBytes;
    private bool combineHolds = true;
    public void Reserve(long bytes)
    {
        if (bytes < 0 || storedBytes > limits.DecodedBytes - bytes)
            throw new TzxException(TapeError.BoundsExceeded, "TZX decoded/provenance storage budget exceeded.");
        storedBytes += bytes;
    }

    public void Hold(bool level, TapeTime duration)
    {
        Level = level;
        if (duration.Numerator == 0) return;
        if (combineHolds && Chunks.Count > 0 && Chunks[^1].Repeat == 1 && Chunks[^1].Runs.Length == 1 && Chunks[^1].Runs[0].Level == level)
        {
            var end = TzxTime.Add(Position, duration);
            if (TzxTime.Compare(end, limits.MaxDuration) > 0)
                throw new TzxException(TapeError.BoundsExceeded, "TZX duration budget exceeded.");
            Chunks[^1] = new(1, [new(level, TzxTime.Add(Chunks[^1].Runs[0].Duration, duration))]);
            Position = end;
        }
        else Add(new(1, [new(level, duration)]));
    }
    public void Pulse(uint ticks)
    {
        if (ticks == 0) CountZero(1);
        else Hold(Level, new(ticks, 3_500_000));
        Level = !Level;
    }
    private void CountZero(long count)
    {
        if (virtualRuns > limits.VirtualTransitions - count)
            throw new TzxException(TapeError.BoundsExceeded, "TZX zero-time transition budget exceeded.");
        virtualRuns += count;
    }
    public void Tone(uint ticks, uint pulses)
    {
        if (pulses == 0) return;
        if (ticks == 0) { CountZero(pulses); if ((pulses & 1) != 0) Level = !Level; return; }
        if (pulses / 2 != 0)
            Add(new(pulses / 2, [new(Level, new(ticks, 3_500_000)), new(!Level, new(ticks, 3_500_000))]));
        if ((pulses & 1) != 0) Pulse(ticks);
    }
    public void Pause(uint milliseconds)
    {
        if (milliseconds == 0) return;
        Hold(Level, new(1, 1000));
        Hold(false, new(milliseconds - 1, 1000));
        Level = false;
    }
    public void Data(byte[] data, int bits, uint zero, uint one, bool msb = true)
    {
        if (bits is < 1 or > 8) throw new TzxException(TapeError.InvalidFormat, "Used bits must be 1..8.");
        if ((data.Length == 0 ? 0 : ((long)data.Length - 1) * 8 + bits) * 2 > limits.VirtualTransitions)
            throw new TzxException(TapeError.BoundsExceeded, "TZX data expansion work exceeds budget.");
        for (int i = 0; i < data.Length; i++)
            for (int j = 0; j < (i == data.Length - 1 ? bits : 8); j++)
            {
                bool bit = (data[i] & 1 << (msb ? 7 - j : j)) != 0;
                Tone(bit ? one : zero, 2);
            }
    }
    public void Add(TapeSignalChunk chunk)
    {
        long count;
        try { count = checked((long)chunk.Repeat * chunk.Runs.Length); }
        catch (OverflowException) { throw new TzxException(TapeError.BoundsExceeded, "TZX run expansion overflow."); }
        if (virtualRuns > limits.VirtualTransitions - count)
            throw new TzxException(TapeError.BoundsExceeded, "TZX virtual-run/storage budget exceeded.");
        TapeTime length = default;
        foreach (var run in chunk.Runs) length = TzxTime.Add(length, run.Duration);
        length = TzxTime.Multiply(length, chunk.Repeat);
        var end = TzxTime.Add(Position, length);
        if (TzxTime.Compare(end, limits.MaxDuration) > 0)
            throw new TzxException(TapeError.BoundsExceeded, "TZX duration budget exceeded.");
        Reserve(48L + 48L * chunk.Runs.Length);
        virtualRuns += count;
        Position = end;
        Chunks.Add(chunk);
    }
    // A source pattern's level dependence has period at most two. Build at most two
    // occurrences then reuse the actual explicit-level chunks, never expand a pilot.
    public void Repeat(uint count, Action emit)
    {
        if (count == 0) return;
        if (count == 1) { emit(); return; }
        int start = Chunks.Count;
        var position = Position;
        long runs = virtualRuns, storage = storedBytes;
        bool combine = combineHolds;
        combineHolds = false;
        emit(); emit();
        combineHolds = combine;
        var pair = Chunks.Skip(start).ToArray();
        long pairVirtual = virtualRuns - runs;
        var endLevel = Level;
        Chunks.RemoveRange(start, Chunks.Count - start);
        Position = position; virtualRuns = runs; storedBytes = storage;
        // Most pairs have many chunks; combine only their small fixed pattern.
        var flat = pair.SelectMany(c => Enumerable.Range(0, checked((int)c.Repeat)).SelectMany(_ => c.Runs)).ToArray();
        if (flat.Length != 0) Add(new(count / 2, flat));
        long positive = pair.Sum(c => checked((long)c.Repeat * c.Runs.Length));
        CountZero(checked((pairVirtual - positive) * (count / 2)));
        Level = endLevel;
        if ((count & 1) != 0) emit();
    }
}

internal static class TzxTime
{
    public static TapeTime Add(TapeTime a, TapeTime b) => Reduce(
        (BigInteger)a.Numerator * (BigInteger)b.Denominator + (BigInteger)b.Numerator * (BigInteger)a.Denominator,
        (BigInteger)a.Denominator * (BigInteger)b.Denominator);
    public static TapeTime Multiply(TapeTime a, ulong count) => Reduce((BigInteger)a.Numerator * count, (BigInteger)a.Denominator);
    public static int Compare(TapeTime a, TapeTime b) =>
        ((BigInteger)a.Numerator * (BigInteger)b.Denominator).CompareTo((BigInteger)b.Numerator * (BigInteger)a.Denominator);
    public static TapeTime Subtract(TapeTime a, TapeTime b) => Reduce(
        (BigInteger)a.Numerator * (BigInteger)b.Denominator - (BigInteger)b.Numerator * (BigInteger)a.Denominator,
        (BigInteger)a.Denominator * (BigInteger)b.Denominator);
    private static TapeTime Reduce(BigInteger n, BigInteger d)
    {
        var gcd = BigInteger.GreatestCommonDivisor(n, d); n /= gcd; d /= gcd;
        if (n < 0 || n > (BigInteger)Int128.MaxValue || d > (BigInteger)Int128.MaxValue)
            throw new TzxException(TapeError.BoundsExceeded, "TZX rational range exceeded.");
        return new((Int128)n, (Int128)d);
    }
}
