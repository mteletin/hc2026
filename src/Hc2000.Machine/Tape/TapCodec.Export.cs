using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Hc2000.Machine.Tape;

public sealed partial class TapCodec
{
    /// <summary>Decode the recorded digital signal, never RAM or original source bytes.
    /// These explicit bounds are the spec-10 ROM-family recognizer, not a universal loader.</summary>
    public TapeResult<TapeWriteResult> Write(TapeSideImage image, TapeExportOptions options)
    {
        try
        {
            TapePolicy.Validate(options.Limits);
            if (options.Target != TapeFormat.Tap || !Enum.IsDefined(options.Side)
                || options.Rate is not null || options.Channel is not null)
                return WriteFail(TapeError.InvalidFormat, "Invalid TAP export options.");
            var indexed = new TapeSignalIndexFactory().Build(image, options.Limits);
            if (!indexed.Success) return WriteFail(indexed.Failure!.Code, indexed.Failure.Detail);
            var cursor = new PulseCursor(indexed.Value);
            var output = new List<byte>(); int blocks = 0;
            Span<byte> length = stackalloc byte[2];
            while (!cursor.End)
            {
                SkipLowBlank(cursor);
                if (cursor.End) break;
                var decoded = DecodeBlock(cursor);
                if (!decoded.Success) return WriteFail(decoded.Failure!.Code, decoded.Failure.Detail);
                var frame = decoded.Value.Frame;
                if (++blocks > options.Limits.SourceBlocks || output.Count + (long)frame.Count + 2 > options.Limits.InputBytes)
                    return WriteFail(TapeError.BoundsExceeded, "TAP output block/byte limit.");
                BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)frame.Count);
                output.Add(length[0]); output.Add(length[1]); output.AddRange(frame);
                var gapFailure = AdvanceGap(cursor);
                if (gapFailure is not null) return WriteFail(gapFailure.Code, gapFailure.Detail);
            }
            var changes = image.Annotations.Concat(image.Markers.Select(m => new TapeAnnotation(m.Position, m.Ordinal,
                TapeAnnotationKind.UnsupportedAction, "TAP omits stop marker: " + m.Label, m.SourceId, m.SourceBlock)))
                .Append(new TapeAnnotation(default, 0, TapeAnnotationKind.Metadata,
                    "TAP preserves decoded bytes only; pulse timing, polarity and gaps are not stored.", null, null)).ToImmutableArray();
            return TapeResult<TapeWriteResult>.Ok(new(output.ToArray(), new(TapeFormat.Tap, TapeSignalFidelity.DataOnly,
                TapeByteFidelity.Transformed, TapeAmplitudeFidelity.NotApplicable, null, default, 0, changes)));
        }
        catch (OverflowException e) { return WriteFail(TapeError.BoundsExceeded, e.Message); }
        catch (ArgumentException e) { return WriteFail(TapeError.InvalidFormat, e.Message); }
    }
    private sealed record DecodedBlock(TapeTime Start, TapeTime End, List<byte> Frame);

    // One reviewed recognizer for export and the port's tolerant search. Export's caller
    // still applies its original cumulative output bounds before advancing the high gap.
    private static TapeResult<DecodedBlock> DecodeBlock(PulseCursor cursor)
    {
        var start = cursor.Position; int pilot = 0;
        while (!cursor.End && Within(cursor.Width, 2168, 10)) { pilot++; cursor.Move(); }
        if (pilot < 3000 || cursor.End || !Within(cursor.Width, 667, 20))
            return TapeResult<DecodedBlock>.Fail(TapeError.ExactEncodingUnavailable, "Not a complete standard pilot/sync sequence.");
        cursor.Move();
        if (cursor.End || !Within(cursor.Width, 735, 20))
            return TapeResult<DecodedBlock>.Fail(TapeError.ExactEncodingUnavailable, "Incomplete or nonstandard second sync pulse.");
        cursor.Move();
        var frame = new List<byte>(); int bitCount = 0, accumulator = 0;
        while (!cursor.End)
        {
            int bit = Bit(cursor.Width);
            if (bit < 0) break;
            cursor.Move();
            // The accepted ROM SAVE exit is a lone zero-window edge, not data.
            // It requires a following steady D6-high gap >= 2800 reference ticks;
            // end of capture and low tape cannot substitute. XOR is still checked below.
            if ((bitCount & 7) == 0 && frame.Count >= 2 && (cursor.End || Bit(cursor.Width) < 0))
            {
                if (bit != 0 || cursor.End || !cursor.Level
                    || cursor.Width < new TapeTime(2800, TapeTransport.ReferenceHz))
                    return TapeResult<DecodedBlock>.Fail(TapeError.ExactEncodingUnavailable, "Invalid SAVE terminator or missing high gap.");
                break;
            }
            if (cursor.End || Bit(cursor.Width) != bit)
                return TapeResult<DecodedBlock>.Fail(TapeError.ExactEncodingUnavailable, "Mismatched/incomplete half-bit pair.");
            cursor.Move(); accumulator = (accumulator << 1) | bit; bitCount++;
            if ((bitCount & 7) == 0)
            {
                if (frame.Count == ushort.MaxValue) return TapeResult<DecodedBlock>.Fail(TapeError.BoundsExceeded, "TAP block length exceeds u16.");
                frame.Add((byte)accumulator); accumulator = 0;
            }
        }
        if ((bitCount & 7) != 0 || frame.Count < 2 || frame.Aggregate(0, (xor, b) => xor ^ b) != 0)
            return TapeResult<DecodedBlock>.Fail(TapeError.ExactEncodingUnavailable, "Incomplete bytes, missing flag/checksum or bad XOR.");
        return TapeResult<DecodedBlock>.Ok(new(start, cursor.Position, frame));
    }

    private static TapeFailure? AdvanceGap(PulseCursor cursor)
    {
        // Preserve synthesized TAP's terminal high hold and low blank beyond it.
        // Recorded ROM SAVE idles MIC low, held as D6 high: the steady high
        // gap has a 2800-reference-tick minimum and no recognizer upper bound.
        if (!cursor.End && cursor.Level)
        {
            if (cursor.Width < new TapeTime(3500, TapeTransport.ReferenceHz) * 80 / 100)
                return new(TapeError.ExactEncodingUnavailable, "Unexplained nonblank material after the complete block.");
            cursor.Move();
        }
        // Do not publish a gap before a refused low exit edge followed by nonblank
        // high material. This is export's existing outer-loop admission, shared
        // with Blocks so a valid byte prefix cannot invent an earlier block end.
        SkipLowBlank(cursor);
        if (!cursor.End && !Within(cursor.Width, 2168, 10))
            return new(TapeError.ExactEncodingUnavailable, "Not a complete standard pilot/sync sequence.");
        return null;
    }

    private static void SkipLowBlank(PulseCursor cursor)
    {
        while (!cursor.End && !cursor.Level && !Within(cursor.Width, 2168, 10)) cursor.Move();
    }

    private static TapeResult<TapeWriteResult> WriteFail(TapeError error, string message) => TapeResult<TapeWriteResult>.Fail(error, message);
    private static bool Within(TapeTime actual, int referenceTicks, uint percent)
    {
        var nominal = new TapeTime(referenceTicks, TapeTransport.ReferenceHz);
        return actual >= nominal * (100 - percent) / 100 && actual <= nominal * (100 + percent) / 100;
    }
    private static int Bit(TapeTime width) => Within(width, 855, 10) ? 0 : Within(width, 1710, 10) ? 1 : -1;
    private sealed class PulseCursor
    {
        private readonly ITapeSignalIndex signal;
        private TapeTime position, next;
        public bool End => position == signal.Image.Duration;
        public bool Level => signal.Sample(position);
        public TapeTime Width => next - position;
        public TapeTime Position => position;
        internal PulseCursor(ITapeSignalIndex signal) { this.signal = signal; SetNext(); }
        public void Move() { position = next; SetNext(); }
        private void SetNext() => next = End ? position : signal.NextEdge(position) ?? signal.Image.Duration;
    }
}
