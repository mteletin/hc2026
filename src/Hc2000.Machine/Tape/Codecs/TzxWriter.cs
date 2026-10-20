using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;

namespace Hc2000.Machine.Tape.Codecs;

public sealed partial class TzxTapeCodec
{
    public TapeResult<TapeWriteResult> Write(TapeSideImage image, TapeExportOptions options)
    {
        try
        {
            if (options.Target != TapeFormat.Tzx) throw new TzxException(TapeError.UnsupportedFormat, "TZX writer requires TZX target.");
            if (options.Limits.InputBytes < 10 || options.Limits.VirtualTransitions < 0 || options.Limits.DecodedBytes < 0)
                throw new TzxException(TapeError.BoundsExceeded, "Invalid TZX output budgets.");
            if (TzxTime.Compare(image.Duration, options.Limits.MaxDuration) > 0)
                throw new TzxException(TapeError.BoundsExceeded, "TZX export duration exceeds policy.");
            if (TzxTime.Compare(image.SourceDuration, image.Duration) > 0)
                throw new TzxException(TapeError.InvalidFormat, "TZX source extent exceeds side duration.");
            if (image.Annotations.Any(a => TzxTime.Compare(a.Position, image.Duration) > 0))
                throw new TzxException(TapeError.InvalidFormat, "TZX annotation outside side duration.");
            var eofs = image.Annotations.Where(a => a.Kind == TapeAnnotationKind.SourceEof).ToArray();
            if (eofs.Length > 1 || (eofs.Length == 1 && eofs[0].Position != image.SourceDuration))
                throw new TzxException(TapeError.InvalidFormat, "Inconsistent TZX source-eof provenance.");
            if (options.Rate is 0 or > 0xFFFFFF) throw new TzxException(TapeError.ExactEncodingUnavailable, "TZX CSW rate must fit positive u24.");
            using var output = new MemoryStream(); output.Write("ZXTape!\x1a"u8); output.WriteByte(1); output.WriteByte(20);
            void CheckSize() { if (output.Length > options.Limits.InputBytes) throw new TzxException(TapeError.BoundsExceeded, "TZX output exceeds byte budget."); }
            void Word(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); output.Write(b); }
            void Dword(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); output.Write(b); }
            void Text(string text)
            {
                var bytes = System.Text.Encoding.Latin1.GetBytes(text);
                for (int i = 0; i < bytes.Length; i += 255)
                { int count = Math.Min(255, bytes.Length - i); output.WriteByte(0x30); output.WriteByte((byte)count); output.Write(bytes, i, count); CheckSize(); }
            }
            void Runs(bool level, uint rate, BigInteger samples)
            {
                // Split long constant holds with an absolute level reset, not false audible toggles.
                while (samples > 0)
                {
                    uint count = (uint)BigInteger.Min(samples, uint.MaxValue);
                    output.WriteByte(0x2B); Dword(1); output.WriteByte(level ? (byte)1 : (byte)0);
                    var payload = options.UseZrle ? encoding.EncodeZrle([count]) : encoding.EncodeRle([count]);
                    if (!payload.Success) throw new TzxException(payload.Failure!.Code, payload.Failure.Detail);
                    output.WriteByte(0x18); Dword(checked((uint)payload.Value.Length + 10)); Word(0);
                    output.WriteByte((byte)rate); output.WriteByte((byte)(rate >> 8)); output.WriteByte((byte)(rate >> 16));
                    output.WriteByte(options.UseZrle ? (byte)2 : (byte)1); Dword(1); output.Write(payload.Value.AsSpan());
                    CheckSize(); samples -= count;
                }
            }
            var markers = image.Markers;
            for (int i = 0; i < markers.Length; i++)
                if (TzxTime.Compare(markers[i].Position, image.Duration) > 0 ||
                    (i > 0 && TzxTime.Compare(markers[i - 1].Position, markers[i].Position) > 0))
                    throw new TzxException(TapeError.InvalidFormat, "TZX export stop markers are unordered/out of bounds.");
            TapeTime position = default, displacement = default;
            int marker = 0;
            long visited = 0;
            bool quantized = false;
            ulong originalEdges = 0, emittedEdges = 0;
            bool? originalLevel = null, emittedLevel = null;
            BigInteger samplePosition = 0;
            void Stops()
            {
                while (marker < markers.Length && markers[marker].Position == position)
                {
                    Text(markers[marker++].Label); output.WriteByte(0x20); Word(0); CheckSize();
                }
            }
            void Interval(bool level, TapeTime end)
            {
                var duration = TzxTime.Subtract(end, position);
                if (duration.Numerator == 0) return;
                uint rate;
                BigInteger samples;
                if (options.AllowQuantize)
                {
                    rate = options.Rate ?? 3_500_000;
                    var ideal = (BigInteger)end.Numerator * rate;
                    var denominator = (BigInteger)end.Denominator;
                    var target = (ideal + denominator - 1) / denominator;
                    samples = target - samplePosition;
                    var error = TzxTime.Subtract(new((Int128)target, rate), end);
                    if (error.Numerator != 0) quantized = true;
                    if (TzxTime.Compare(error, displacement) > 0) displacement = error;
                    samplePosition = target;
                }
                else
                {
                    if (options.Rate is uint explicitRate)
                    {
                        rate = explicitRate;
                        var numerator = (BigInteger)duration.Numerator * rate;
                        if (numerator % (BigInteger)duration.Denominator != 0)
                            throw new TzxException(TapeError.ExactEncodingUnavailable, $"Interval at {position} does not fit rate {rate}.");
                        samples = numerator / (BigInteger)duration.Denominator;
                    }
                    else
                    {
                        if (duration.Denominator > 0xFFFFFF)
                            throw new TzxException(TapeError.ExactEncodingUnavailable, $"Interval at {position} requires rate {duration.Denominator}, beyond u24.");
                        rate = (uint)duration.Denominator;
                        samples = (BigInteger)duration.Numerator;
                    }
                }
                if (samples > 0)
                {
                    if (emittedLevel.HasValue && emittedLevel != level) emittedEdges++;
                    emittedLevel = level;
                }
                Runs(level, rate, samples); position = end;
            }
            IEnumerable<TapeRun> CanonicalRuns()
            {
                TapeRun? pending = null;
                foreach (var chunk in image.Chunks)
                {
                    if (chunk.Repeat > (ulong)options.Limits.VirtualTransitions ||
                        (BigInteger)chunk.Repeat * chunk.Runs.Length > options.Limits.VirtualTransitions - visited)
                        throw new TzxException(TapeError.BoundsExceeded, "TZX export virtual-run budget exceeded.");
                    for (ulong repeat = 0; repeat < chunk.Repeat; repeat++)
                        foreach (var run in chunk.Runs)
                        {
                            visited++;
                            if (pending is not null && pending.Level == run.Level)
                                pending = new(run.Level, TzxTime.Add(pending.Duration, run.Duration));
                            else
                            {
                                if (pending is not null) yield return pending;
                                pending = run;
                            }
                        }
                }
                if (pending is not null) yield return pending;
            }
            Stops();
            foreach (var run in CanonicalRuns())
            {
                if (originalLevel.HasValue && originalLevel != run.Level) originalEdges++;
                originalLevel = run.Level;
                var end = TzxTime.Add(position, run.Duration);
                if (TzxTime.Compare(end, image.Duration) > 0) throw new TzxException(TapeError.InvalidFormat, "TZX signal exceeds side duration.");
                while (marker < markers.Length && TzxTime.Compare(markers[marker].Position, end) < 0)
                {
                    Interval(run.Level, markers[marker].Position); Stops();
                }
                Interval(run.Level, end); Stops();
            }
            if (position != image.Duration || marker != markers.Length)
                throw new TzxException(TapeError.InvalidFormat, "TZX signal does not cover side/markers.");
            // Annotations remain human-readable route metadata, not executable CPU actions.
            foreach (var annotation in image.Annotations.Where(a => a.Kind != TapeAnnotationKind.SourceEof)) Text(annotation.Label);
            var changed = image.Annotations; // Includes the source-eof provenance flattened into this file's new extent.
            var report = new TapeWriteReport(TapeFormat.Tzx, quantized ? TapeSignalFidelity.Quantized : TapeSignalFidelity.ExactSignal,
                TapeByteFidelity.Transformed, image.ImportOptions.Format == TapeFormat.Wav
                    ? TapeAmplitudeFidelity.NotPreserved : TapeAmplitudeFidelity.NotApplicable, options.Rate,
                displacement, originalEdges - emittedEdges, changed);
            return TapeResult<TapeWriteResult>.Ok(new(output.ToArray(), report));
        }
        catch (TzxException e) { return TapeResult<TapeWriteResult>.Fail(e.Error, e.Message); }
        catch (OverflowException) { return TapeResult<TapeWriteResult>.Fail(TapeError.BoundsExceeded, "TZX writer overflow."); }
    }
}
