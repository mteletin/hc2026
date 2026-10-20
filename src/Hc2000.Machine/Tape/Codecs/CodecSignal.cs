using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;

namespace Hc2000.Machine.Tape.Codecs;

// Container materialization only: no index, playback clock, deck or scheduler.
internal sealed class CodecSignal(TapeImportOptions options, ReadOnlySpan<byte> source)
{
    internal string Id { get; } = Convert.ToHexString(SHA256.HashData(source));
    internal readonly List<TapeSignalChunk> Chunks = [];
    internal readonly List<TapeAnnotation> Annotations = [];
    internal readonly List<TapeStopMarker> Markers = [];
    internal readonly List<TapeRouteVisit> Route = [];
    internal readonly List<TapeWarning> Warnings = [];
    internal TapeTime Position;
    private ulong runs;
    private long storedRuns;
    private ulong evaluated;
    private long metadataBytes;
    internal void Evaluate(ulong count)
    {
        evaluated = checked(evaluated + count);
        if (evaluated > (ulong)options.Limits.VirtualTransitions) throw new CodecException(TapeError.BoundsExceeded, "Pulse work exceeds limits (including zero widths).");
    }
    internal void Add(bool level, TapeTime time) => Add(1, [new TapeRun(level, time)]);
    internal void Add(ulong repeat, TapeRun[] sequence)
    {
        runs = checked(runs + checked(repeat * (ulong)sequence.Length));
        storedRuns = checked(storedRuns + sequence.Length);
        if (runs > (ulong)options.Limits.VirtualTransitions || storedRuns * 48 + metadataBytes > options.Limits.DecodedBytes)
            throw new CodecException(TapeError.BoundsExceeded, "Signal expansion exceeds limits.");
        var span = sequence.Aggregate(default(TapeTime), (a, r) => Sum(a, r.Duration));
        Position = Sum(Position, Scale(span, repeat));
        if (Compare(Position, new(options.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1)) > 0)
            throw new CodecException(TapeError.CapacityExceeded, "Source exceeds selected cassette capacity.");
        if (Compare(Position, options.Limits.MaxDuration) > 0)
            throw new CodecException(TapeError.BoundsExceeded, "Source exceeds duration limit.");
        Chunks.Add(new(repeat, sequence));
    }
    internal void Note(uint block, string text, TapeAnnotationKind kind = TapeAnnotationKind.Metadata)
    {
        metadataBytes = checked(metadataBytes + (long)text.Length * 2 + 128);
        if (metadataBytes + storedRuns * 48 > options.Limits.DecodedBytes) throw new CodecException(TapeError.BoundsExceeded, "Metadata exceeds decoded budget.");
        Annotations.Add(new(Position, (uint)Annotations.Count, kind, text, Id, block));
    }
    internal TapeReadResult Finish(ReadOnlySpan<byte> bytes)
    {
        var duration = new TapeTime(options.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1);
        var eof = Position;
        Annotations.Add(new(eof, (uint)Annotations.Count, TapeAnnotationKind.SourceEof, "source-eof", Id, null));
        if (Compare(eof, duration) < 0) Chunks.Add(new(1, [new(false, Difference(duration, eof))]));
        return new(new(duration, eof, Chunks, Markers, Annotations, Route,
            new(options.Format, null, options.Target48K, options.Selections, null)),
            [new(Id, options.Format, bytes)], Warnings.ToImmutableArray());
    }
    internal static int Compare(TapeTime a, TapeTime b) => ((BigInteger)a.Numerator * (BigInteger)b.Denominator).CompareTo((BigInteger)b.Numerator * (BigInteger)a.Denominator);
    internal static TapeTime Sum(TapeTime a, TapeTime b) => Rational((BigInteger)a.Numerator * (BigInteger)b.Denominator + (BigInteger)b.Numerator * (BigInteger)a.Denominator, (BigInteger)a.Denominator * (BigInteger)b.Denominator);
    internal static TapeTime Difference(TapeTime a, TapeTime b) => Rational((BigInteger)a.Numerator * (BigInteger)b.Denominator - (BigInteger)b.Numerator * (BigInteger)a.Denominator, (BigInteger)a.Denominator * (BigInteger)b.Denominator);
    private static TapeTime Scale(TapeTime a, ulong n) => Rational((BigInteger)a.Numerator * n, (BigInteger)a.Denominator);
    internal static TapeTime Rational(BigInteger n, BigInteger d)
    {
        var gcd = BigInteger.GreatestCommonDivisor(n, d); n /= gcd; d /= gcd;
        if (n < 0 || n > (BigInteger)Int128.MaxValue || d <= 0 || d > (BigInteger)Int128.MaxValue)
            throw new CodecException(TapeError.BoundsExceeded, "Rational field overflow.");
        return new((Int128)n, (Int128)d);
    }
    internal static void Validate(TapeLimits limits)
    {
        if (limits.InputBytes < 0 || limits.DecodedBytes < 0 || limits.SourceBlocks < 1 || limits.RouteSteps < 1 || limits.VirtualTransitions < 0)
            throw new CodecException(TapeError.BoundsExceeded, "Invalid codec limits.");
    }
    internal static void ValidateImport(ReadOnlySpan<byte> bytes, TapeImportOptions options, TapeFormat format)
    {
        Validate(options.Limits);
        if (bytes.Length > options.Limits.InputBytes || bytes.Length > options.Limits.DecodedBytes)
            throw new CodecException(TapeError.BoundsExceeded, "Input/provenance exceeds byte budget.");
        if (options.Format != format || !Enum.IsDefined(options.Capacity) || !Enum.IsDefined(options.Side))
            throw new CodecException(TapeError.InvalidFormat, "Invalid import format/side/capacity.");
        if (!options.Selections.IsEmpty) throw new CodecException(TapeError.InvalidRoute, "This format has no route selections.");
    }
    // Export the entire selected side. SourceDuration is provenance, never a crop point.
    // Cumulative projection folds zero-sample collisions in source order and merges holds.
    internal static Projection Project(TapeSideImage image, TapeExportOptions options, uint rate)
    {
        Validate(options.Limits);
        if (rate == 0) throw new CodecException(TapeError.InvalidFormat, "Rate must be positive.");
        if (Compare(image.SourceDuration, image.Duration) > 0) throw new CodecException(TapeError.InvalidFormat, "Invalid source extent.");
        if (Compare(image.Duration, options.Limits.MaxDuration) > 0) throw new CodecException(TapeError.BoundsExceeded, "Export duration limit.");
        var output = new List<(bool Level, ulong Count)>();
        var holds = new List<(bool Level, TapeTime End)>();
        TapeTime position = default, maxError = default;
        ulong lastSample = 0, work = 0;
        bool exact = true;
        foreach (var chunk in image.Chunks)
        {
            for (ulong repeat = 0; repeat < chunk.Repeat; repeat++)
            {
                foreach (var run in chunk.Runs)
                {
                    if (++work > (ulong)options.Limits.VirtualTransitions || work * 64 > (ulong)options.Limits.DecodedBytes)
                        throw new CodecException(TapeError.BoundsExceeded, "Export expansion limit.");
                    position = Sum(position, run.Duration);
                    if (Compare(position, image.Duration) > 0) throw new CodecException(TapeError.InvalidFormat, "Signal exceeds side duration.");
                    if (holds.Count > 0 && holds[^1].Level == run.Level) holds[^1] = (run.Level, position);
                    else holds.Add((run.Level, position));
                }
            }
        }
        if (Compare(position, image.Duration) != 0) throw new CodecException(TapeError.InvalidFormat, "Incomplete signal coverage.");
        foreach (var hold in holds)
        {
            var n = (BigInteger)hold.End.Numerator * rate; var d = (BigInteger)hold.End.Denominator;
            if (n % d != 0) exact = false;
            var sample = (n + d - 1) / d;
            if (sample > ulong.MaxValue) throw new CodecException(TapeError.BoundsExceeded, "Sample count overflow.");
            var error = Difference(Rational(sample, rate), hold.End);
            if (Compare(error, maxError) > 0) maxError = error;
            ulong count = (ulong)sample - lastSample;
            if (count == 0) { /* No positive output interval: the edge does not survive. */ }
            else if (output.Count > 0 && output[^1].Level == hold.Level)
                output[^1] = (hold.Level, checked(output[^1].Count + count));
            else output.Add((hold.Level, count));
            lastSample = (ulong)sample;
        }
        if (!exact && !options.AllowQuantize) throw new CodecException(TapeError.ExactEncodingUnavailable, "Signal is off the target sample grid; explicit quantize required.");
        // Equal-level joins were normalized above. Only interior physical edges count;
        // output-end changes without a following interval are absent from output.Runs.
        ulong eliminated = checked((ulong)Math.Max(0, holds.Count - 1) - (ulong)Math.Max(0, output.Count - 1));
        return new(output, exact, maxError, eliminated);
    }
    internal sealed record Projection(List<(bool Level, ulong Count)> Runs, bool Exact, TapeTime Error, ulong Collisions);
    internal static TapeWriteResult Written(byte[] bytes, TapeSideImage image, TapeExportOptions options, uint rate, Projection p)
    {
        if (bytes.Length > options.Limits.InputBytes || bytes.Length > options.Limits.DecodedBytes)
            throw new CodecException(TapeError.BoundsExceeded, "Export bytes exceed budget.");
        return new(bytes, new(options.Target, p.Exact ? TapeSignalFidelity.ExactSignal : TapeSignalFidelity.Quantized,
            TapeByteFidelity.Transformed, TapeAmplitudeFidelity.NotApplicable, rate, p.Error, p.Collisions,
            image.Annotations.Where(a => a.Kind != TapeAnnotationKind.SourceEof).ToImmutableArray()));
    }
}
internal sealed class CodecException(TapeError code, string detail) : Exception(detail)
{
    internal TapeError Code { get; } = code;
}
