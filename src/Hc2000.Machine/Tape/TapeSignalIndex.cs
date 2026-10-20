using System.Numerics;

namespace Hc2000.Machine.Tape;

public sealed class TapeSignalIndexFactory : ITapeSignalIndexFactory
{
    public TapeResult<ITapeSignalIndex> Build(TapeSideImage image, TapeLimits limits)
    {
        try
        {
            TapePolicy.Validate(limits);
            if (image.Duration > limits.MaxDuration) return Fail(TapeError.CapacityExceeded, "Side exceeds duration limit.");
            if (image.SourceDuration > image.Duration) return Fail(TapeError.InvalidFormat, "Source exceeds side.");
            BigInteger count = 0, bytes = 0;
            var total = default(TapeTime);
            foreach (var chunk in image.Chunks)
            {
                count += (BigInteger)chunk.Repeat * chunk.Runs.Length;
                bytes += 64 + (BigInteger)chunk.Runs.Length * 64;
                if (count > limits.VirtualTransitions || bytes > limits.DecodedBytes)
                    return Fail(TapeError.BoundsExceeded, "Signal expansion/storage limit.");
                var period = default(TapeTime);
                foreach (var run in chunk.Runs) period += run.Duration;
                total += period * chunk.Repeat;
            }
            if (total != image.Duration) return Fail(TapeError.InvalidFormat, "Chunks must cover side exactly.");
            if (image.SourceRoute.Length > limits.RouteSteps || image.ImportOptions.Selections.Length > limits.RouteSteps)
                return Fail(TapeError.BoundsExceeded, "Route limit.");
            ValidateOrder(image.Markers.Select(m => (m.Position, m.Ordinal, m.Label)), image.Duration);
            ValidateOrder(image.Annotations.Select(m => (m.Position, m.Ordinal, m.Label)), image.Duration);
            if (image.Annotations.Any(a => !Enum.IsDefined(a.Kind))
                || image.Markers.Any(m => m.SourceBlock >= limits.SourceBlocks)
                || image.Annotations.Any(m => m.SourceBlock >= limits.SourceBlocks)
                || image.SourceRoute.Any(r => r.BlockIndex >= limits.SourceBlocks || r.Visit > limits.RouteSteps))
                return Fail(TapeError.BoundsExceeded, "Provenance index limit.");
            var eof = image.Annotations.Where(a => a.Kind == TapeAnnotationKind.SourceEof).ToArray();
            if (eof.Length > 1 || eof.Length == 1 && eof[0].Position != image.SourceDuration)
                return Fail(TapeError.InvalidFormat, "Source extent annotation conflicts.");
            bytes += (BigInteger)(image.Markers.Length + image.Annotations.Length) * 128 + (BigInteger)image.SourceRoute.Length * 64;
            foreach (var label in image.Markers.Select(m => m.Label).Concat(image.Annotations.Select(m => m.Label))) bytes += label.Length * 2L;
            if (bytes > limits.DecodedBytes) return Fail(TapeError.BoundsExceeded, "Metadata storage limit.");
            if (!Enum.IsDefined(image.ImportOptions.Format) || image.ImportOptions.Channel is { } channel && !Enum.IsDefined(channel)
                || image.ImportOptions.Format != TapeFormat.Wav && (image.ImportOptions.Channel is not null || image.ImportOptions.ComparatorModel is not null)
                || image.ImportOptions.Format != TapeFormat.Tzx && !image.ImportOptions.Selections.IsEmpty)
                return Fail(TapeError.InvalidFormat, "Invalid import provenance.");
            return TapeResult<ITapeSignalIndex>.Ok(new TapeSignalIndex(image));
        }
        catch (OverflowException) { return Fail(TapeError.BoundsExceeded, "Rational range exceeded."); }
        catch (ArgumentException e) { return Fail(TapeError.InvalidFormat, e.Message); }
    }
    private static TapeResult<ITapeSignalIndex> Fail(TapeError error, string detail) => TapeResult<ITapeSignalIndex>.Fail(error, detail);
    private static void ValidateOrder(IEnumerable<(TapeTime Position, uint Ordinal, string Label)> items, TapeTime duration)
    {
        TapeTime previous = default; uint ordinal = 0; bool first = true;
        foreach (var item in items)
        {
            if (item.Position > duration || !first && (item.Position < previous || item.Position == previous && item.Ordinal <= ordinal)
                || new System.Text.UTF8Encoding(false, true).GetByteCount(item.Label) > 4096)
                throw new ArgumentException("Invalid metadata position/order/text.");
            previous = item.Position; ordinal = item.Ordinal; first = false;
        }
    }
}

internal static class TapePolicy
{
    internal static void Validate(TapeLimits limits)
    {
        if (limits.InputBytes is < 0 or > 64 * 1024 * 1024 || limits.DecodedBytes is < 0 or > 256 * 1024 * 1024
            || limits.SourceBlocks is < 0 or > 100_000 || limits.RouteSteps is < 0 or > 1_000_000
            || limits.VirtualTransitions is < 0 or > 16_000_000 || limits.MaxDuration > new TapeTime(2700, 1))
            throw new ArgumentException("Tape limits may only lower policy bounds.");
    }
}

/// <summary>Prefix indexes contain one entry per stored chunk/run, never one per repetition.
/// All coordinates remain rational seconds; projected device ticks do not enter this index.</summary>
internal sealed class TapeSignalIndex : ITapeSignalIndex
{
    private sealed class Span
    {
        internal readonly TapeSignalChunk Chunk;
        internal readonly TapeTime Start, End, Period;
        internal readonly TapeTime[] Prefix;
        internal Span(TapeSignalChunk chunk, TapeTime start)
        {
            Chunk = chunk; Start = start; Prefix = new TapeTime[chunk.Runs.Length + 1];
            for (int i = 0; i < chunk.Runs.Length; i++) Prefix[i + 1] = Prefix[i] + chunk.Runs[i].Duration;
            Period = Prefix[^1]; End = start + Period * chunk.Repeat;
        }
    }
    private readonly Span[] spans;
    public TapeSideImage Image { get; }
    internal TapeSignalIndex(TapeSideImage image)
    {
        Image = image; spans = new Span[image.Chunks.Length]; var start = default(TapeTime);
        for (int i = 0; i < spans.Length; i++) { spans[i] = new(image.Chunks[i], start); start = spans[i].End; }
    }
    private int FindSpan(TapeTime p)
    {
        if (p >= Image.Duration) throw new ArgumentOutOfRangeException(nameof(p));
        int lo = 0, hi = spans.Length - 1;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (p >= spans[mid].End) lo = mid + 1; else hi = mid; }
        return lo;
    }
    private static int FindRun(Span s, TapeTime local)
    {
        int lo = 0, hi = s.Chunk.Runs.Length - 1;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (local >= s.Prefix[mid + 1]) lo = mid + 1; else hi = mid; }
        return lo;
    }
    public bool Sample(TapeTime position)
    {
        var s = spans[FindSpan(position)]; var relative = position - s.Start;
        var local = relative - s.Period * relative.Quotient(s.Period);
        return s.Chunk.Runs[FindRun(s, local)].Level;
    }
    public TapeTime? NextEdge(TapeTime position)
    {
        if (position >= Image.Duration) return null;
        int si = FindSpan(position); bool level = Sample(position);
        for (; si < spans.Length; si++)
        {
            var s = spans[si]; var relative = position > s.Start ? position - s.Start : default;
            ulong repetition = relative.Quotient(s.Period);
            int ri = FindRun(s, relative - s.Period * repetition);
            if (s.Start > position && s.Chunk.Runs[0].Level != level) return s.Start;
            // At most two sequences: if none changes level, all later repetitions are constant.
            for (int cycle = 0; cycle < 2 && repetition < s.Chunk.Repeat; cycle++, repetition++, ri = -1)
                for (int next = ri + 1; next < s.Chunk.Runs.Length; next++)
                    if (s.Chunk.Runs[next].Level != level) return s.Start + s.Period * repetition + s.Prefix[next];
        }
        return null;
    }
    private IEnumerable<TapeSignalChunk> Cut(TapeTime start, TapeTime end)
    {
        if (start == end) yield break;
        for (int i = FindSpan(start); i < spans.Length && spans[i].Start < end; i++)
        {
            var s = spans[i]; var from = start > s.Start ? start - s.Start : default;
            var to = (end < s.End ? end : s.End) - s.Start;
            ulong first = from.Quotient(s.Period), last = to.Quotient(s.Period);
            var firstOffset = from - s.Period * first;
            var lastOffset = to - s.Period * last;
            if (first == last)
            {
                if (from < to) yield return Fragment(s, firstOffset, lastOffset);
                continue;
            }
            if (firstOffset.Numerator != 0) { yield return Fragment(s, firstOffset, s.Period); first++; }
            if (last > first) yield return new(last - first, s.Chunk.Runs);
            if (lastOffset.Numerator != 0) yield return Fragment(s, default, lastOffset);
        }
    }
    private static TapeSignalChunk Fragment(Span s, TapeTime from, TapeTime to)
    {
        var runs = new List<TapeRun>();
        for (int i = FindRun(s, from); i < s.Chunk.Runs.Length && s.Prefix[i] < to; i++)
        {
            var a = from > s.Prefix[i] ? from : s.Prefix[i]; var b = to < s.Prefix[i + 1] ? to : s.Prefix[i + 1];
            if (a < b) runs.Add(new(s.Chunk.Runs[i].Level, b - a));
        }
        return new(1, runs);
    }
    public TapeResult<TapeSideImage> Slice(TapeTime start, TapeTime end, TapeLimits limits)
    {
        if (end < start || end > Image.Duration) return TapeResult<TapeSideImage>.Fail(TapeError.InvalidFormat, "Invalid slice interval.");
        var extent = Image.SourceDuration <= start ? default : (Image.SourceDuration < end ? Image.SourceDuration : end) - start;
        return Validate(new(end - start, extent, Cut(start, end),
            Image.Markers.Where(m => m.Position >= start && m.Position < end).Select(m => m with { Position = m.Position - start }),
            Image.Annotations.Where(m => m.Kind != TapeAnnotationKind.SourceEof && m.Position >= start && m.Position < end)
                .Select(m => m with { Position = m.Position - start }),
            Image.SourceRoute, Image.ImportOptions), limits);
    }
    public TapeResult<TapeSideImage> Replace(TapeTime start, TapeTime end, TapeSideImage replacement, TapeLimits limits)
    {
        if (end < start || end > Image.Duration || replacement.Duration != end - start)
            return TapeResult<TapeSideImage>.Fail(TapeError.InvalidFormat, "Replacement must cover the exact interval.");
        var candidate = new TapeSignalIndexFactory().Build(replacement, limits);
        if (!candidate.Success) return TapeResult<TapeSideImage>.Fail(candidate.Failure!.Code, candidate.Failure.Detail);
        var markers = Image.Markers.Where(m => m.Position < start).Concat(replacement.Markers.Select(m => m with { Position = m.Position + start }))
            .Concat(Image.Markers.Where(m => m.Position >= end)).OrderBy(m => m.Position).ThenBy(m => m.Ordinal);
        var annotations = Image.Annotations.Where(m => m.Position < start || m.Position >= end || m.Kind == TapeAnnotationKind.SourceEof)
            .Concat(replacement.Annotations.Where(m => m.Kind != TapeAnnotationKind.SourceEof).Select(m => m with { Position = m.Position + start }))
            .OrderBy(m => m.Position).ThenBy(m => m.Ordinal);
        return Validate(new(Image.Duration, Image.SourceDuration, TapeSignalNormalization.Coalesce(Cut(default, start).Concat(replacement.Chunks).Concat(Cut(end, Image.Duration))),
            markers, annotations, Image.SourceRoute, Image.ImportOptions), limits);
    }
    private static TapeResult<TapeSideImage> Validate(TapeSideImage image, TapeLimits limits)
    {
        var result = new TapeSignalIndexFactory().Build(image, limits);
        return result.Success ? TapeResult<TapeSideImage>.Ok(image) : TapeResult<TapeSideImage>.Fail(result.Failure!.Code, result.Failure.Detail);
    }
}
