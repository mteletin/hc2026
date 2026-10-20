using System.Buffers.Binary;
using System.Text;

namespace Hc2000.Machine.Tape.Codecs;

/// <summary>Native PZX 1.0 selected signal, all six standard tags.</summary>
public sealed class PzxTapeCodec : ITapeCodec
{
    private const uint Rate = 3_500_000;
    public TapeFormat Format => TapeFormat.Pzx;
    public TapeCodecCapabilities Capabilities => new(true, true, null);
    public TapeResult<TapeReadResult> Read(ReadOnlySpan<byte> bytes, TapeImportOptions options)
    {
        try
        {
            CodecSignal.ValidateImport(bytes, options, Format);
            var signal = new CodecSignal(options, bytes);
            int offset = 0; uint block = 0;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 8) throw new CodecException(TapeError.InvalidFormat, "Truncated PZX block header.");
                if (block >= options.Limits.SourceBlocks || block >= options.Limits.RouteSteps)
                    throw new CodecException(TapeError.BoundsExceeded, "PZX block/route limit.");
                string tag = System.Text.Encoding.ASCII.GetString(bytes.Slice(offset, 4));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
                if (size > bytes.Length - offset - 8) throw new CodecException(TapeError.InvalidFormat, "Truncated PZX block payload.");
                var body = bytes.Slice(offset + 8, (int)size);
                if (block == 0 && tag != "PZXT") throw new CodecException(TapeError.InvalidFormat, "PZXT must be first.");
                signal.Route.Add(new(signal.Id, block, 0)); signal.Note(block, $"{tag} offset={offset} size={size}");
                switch (tag)
                {
                    case "PZXT":
                        if (body.Length < 2) throw new CodecException(TapeError.InvalidFormat, "Truncated PZXT version.");
                        if (body[0] != 1) throw new CodecException(TapeError.UnsupportedVersion, "Unsupported PZX major version.");
                        if (body[1] != 0) signal.Warnings.Add(new("NewerMinorVersion", $"PZX1.{body[1]}"));
                        if (body.Length > 2) signal.Note(block, Utf8(body[2..]), TapeAnnotationKind.Text);
                        break;
                    case "PULS": Puls(body, signal); break;
                    case "DATA": Data(body, signal, options.Limits); break;
                    case "PAUS":
                        if (body.Length != 4) throw new CodecException(TapeError.InvalidFormat, "PAUS requires four bytes.");
                        uint pause = BinaryPrimitives.ReadUInt32LittleEndian(body);
                        if ((pause & 0x7fffffff) != 0) signal.Add((pause & 0x80000000) != 0, new(pause & 0x7fffffff, Rate));
                        break;
                    case "BRWS": signal.Note(block, Utf8(body), TapeAnnotationKind.Text); break;
                    case "STOP":
                        if (body.Length != 2) throw new CodecException(TapeError.InvalidFormat, "STOP requires two bytes.");
                        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(body);
                        if (flags != 1 || options.Target48K) signal.Markers.Add(new(signal.Position, (uint)signal.Markers.Count, "PZX STOP", signal.Id, block));
                        break;
                    default:
                        signal.Note(block, "Unknown payload: " + Convert.ToHexString(body));
                        signal.Warnings.Add(new("UnsupportedBlockAction", $"Unknown bounded PZX tag {tag} retained as metadata.")); break;
                }
                offset += 8 + (int)size; block++;
            }
            if (block == 0) throw new CodecException(TapeError.InvalidFormat, "Empty PZX lacks header.");
            return TapeResult<TapeReadResult>.Ok(signal.Finish(bytes));
        }
        catch (CodecException e) { return TapeResult<TapeReadResult>.Fail(e.Code, e.Message); }
        catch (DecoderFallbackException) { return TapeResult<TapeReadResult>.Fail(TapeError.InvalidFormat, "Invalid PZX UTF-8."); }
        catch (OverflowException) { return TapeResult<TapeReadResult>.Fail(TapeError.BoundsExceeded, "PZX arithmetic overflow."); }
    }
    private static string Utf8(ReadOnlySpan<byte> bytes) => new UTF8Encoding(false, true).GetString(bytes);
    private static ushort Word(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (bytes.Length - offset < 2) throw new CodecException(TapeError.InvalidFormat, "Truncated PZX pulse word.");
        ushort word = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]); offset += 2; return word;
    }
    private static void Puls(ReadOnlySpan<byte> body, CodecSignal signal)
    {
        int offset = 0; bool level = false;
        while (offset < body.Length)
        {
            ushort word = Word(body, ref offset); uint repeat = 1;
            if (word > 0x8000) { repeat = (uint)(word & 0x7fff); word = Word(body, ref offset); }
            uint width = word >= 0x8000 ? ((uint)(word & 0x7fff) << 16) | Word(body, ref offset) : word;
            signal.Evaluate(repeat);
            if (width != 0)
            {
                var duration = new TapeTime(width, Rate);
                if (repeat / 2 != 0) signal.Add(repeat / 2, [new(level, duration), new(!level, duration)]);
                if ((repeat & 1) != 0) signal.Add(level, duration);
            }
            if ((repeat & 1) != 0) level = !level;
        }
    }
    private static void Data(ReadOnlySpan<byte> body, CodecSignal signal, TapeLimits limits)
    {
        if (body.Length < 8) throw new CodecException(TapeError.InvalidFormat, "Truncated DATA header.");
        uint bitsAndLevel = BinaryPrimitives.ReadUInt32LittleEndian(body); uint bits = bitsAndLevel & 0x7fffffff;
        ushort tail = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]); int p0 = body[6], p1 = body[7], offset = 8;
        ushort[] zero = new ushort[p0], one = new ushort[p1];
        for (int i = 0; i < p0; i++) zero[i] = Word(body, ref offset);
        for (int i = 0; i < p1; i++) one[i] = Word(body, ref offset);
        if ((long)offset + ((long)bits + 7) / 8 != body.Length) throw new CodecException(TapeError.InvalidFormat, "DATA bit payload length mismatch.");
        if (bits > limits.RouteSteps) throw new CodecException(TapeError.BoundsExceeded, "DATA bit work limit.");
        bool level = (bitsAndLevel & 0x80000000) != 0;
        for (uint bit = 0; bit < bits;)
        {
            bool value = (body[offset + (int)(bit / 8)] & (0x80 >> (int)(bit % 8))) != 0;
            uint end = bit + 1;
            while (end < bits && ((body[offset + (int)(end / 8)] & (0x80 >> (int)(end % 8))) != 0) == value) end++;
            var sequence = value ? one : zero;
            ulong repeat = end - bit; signal.Evaluate(repeat * (ulong)sequence.Length);
            var pattern = new List<TapeRun>(); bool patternLevel = level;
            foreach (ushort width in sequence)
            {
                if (width != 0) pattern.Add(new(patternLevel, new(width, Rate)));
                patternLevel = !patternLevel;
            }
            if (pattern.Count != 0)
            {
                if ((sequence.Length & 1) == 0) signal.Add(repeat, pattern.ToArray());
                else
                {
                    var alternating = pattern.Concat(pattern.Select(r => new TapeRun(!r.Level, r.Duration))).ToArray();
                    if (repeat / 2 != 0) signal.Add(repeat / 2, alternating);
                    if ((repeat & 1) != 0) signal.Add(1, pattern.ToArray());
                }
            }
            if ((sequence.Length & 1) != 0 && (repeat & 1) != 0) level = !level;
            bit = end;
        }
        if (tail != 0) signal.Add(level, new(tail, Rate));
    }
    public TapeResult<TapeWriteResult> Write(TapeSideImage image, TapeExportOptions options)
    {
        try
        {
            if (options.Target != Format || options.Rate is not null && options.Rate != Rate)
                throw new CodecException(TapeError.InvalidFormat, "PZX uses the fixed 3.5MHz grid.");
            var p = CodecSignal.Project(image, options, Rate);
            using var stream = new MemoryStream();
            int blocks = 0;
            void Block(string tag, ReadOnlySpan<byte> body)
            {
                if (++blocks > options.Limits.SourceBlocks || blocks > options.Limits.RouteSteps || stream.Length + body.Length + 8 > options.Limits.InputBytes || stream.Length + body.Length + 8 > options.Limits.DecodedBytes)
                    throw new CodecException(TapeError.BoundsExceeded, "PZX output block/byte limit.");
                stream.Write(System.Text.Encoding.ASCII.GetBytes(tag)); Span<byte> size = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length); stream.Write(size); stream.Write(body);
            }
            Block("PZXT", [1, 0]);
            foreach (var annotation in image.Annotations.Where(a => a.Kind != TapeAnnotationKind.SourceEof))
                Block("BRWS", System.Text.Encoding.UTF8.GetBytes(annotation.Label));
            var markers = image.Markers.OrderBy(m => m.Position, Comparer<TapeTime>.Create(CodecSignal.Compare)).ThenBy(m => m.Ordinal).ToArray();
            int markerIndex = 0; ulong position = 0;
            ulong MarkerSample(TapeStopMarker marker)
            {
                if (CodecSignal.Compare(marker.Position, image.Duration) > 0) throw new CodecException(TapeError.InvalidFormat, "Stop outside source extent.");
                var n = (System.Numerics.BigInteger)marker.Position.Numerator * Rate;
                var d = (System.Numerics.BigInteger)marker.Position.Denominator;
                if (n % d != 0)
                {
                    if (!options.AllowQuantize) throw new CodecException(TapeError.ExactEncodingUnavailable, "Stop marker is off-grid.");
                    var error = CodecSignal.Difference(CodecSignal.Rational((n + d - 1) / d, Rate), marker.Position);
                    p = p with { Exact = false, Error = CodecSignal.Compare(error, p.Error) > 0 ? error : p.Error };
                }
                return checked((ulong)((n + d - 1) / d));
            }
            void Stop() { Block("STOP", [0, 0]); markerIndex++; }
            foreach (var run in p.Runs)
            {
                ulong remaining = run.Count;
                while (remaining > 0)
                {
                    while (markerIndex < markers.Length && MarkerSample(markers[markerIndex]) == position) Stop();
                    ulong count = Math.Min(remaining, 0x7fffffff);
                    if (markerIndex < markers.Length)
                    {
                        ulong next = MarkerSample(markers[markerIndex]);
                        if (next < position) throw new CodecException(TapeError.InvalidFormat, "Stop marker ordering.");
                        count = Math.Min(count, next - position);
                    }
                    if (count == 0) continue;
                    byte[] pause = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(pause, (uint)count | (run.Level ? 0x80000000u : 0));
                    Block("PAUS", pause); position += count; remaining -= count;
                }
            }
            while (markerIndex < markers.Length && MarkerSample(markers[markerIndex]) == position) Stop();
            if (markerIndex != markers.Length) throw new CodecException(TapeError.InvalidFormat, "Uncovered stop marker.");
            return TapeResult<TapeWriteResult>.Ok(CodecSignal.Written(stream.ToArray(), image, options, Rate, p));
        }
        catch (CodecException e) { return TapeResult<TapeWriteResult>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<TapeWriteResult>.Fail(TapeError.BoundsExceeded, "PZX arithmetic overflow."); }
    }
}
