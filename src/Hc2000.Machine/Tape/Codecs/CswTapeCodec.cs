using System.Buffers.Binary;
using System.Collections.Immutable;
using Hc2000.Machine.Tape.Encoding;

namespace Hc2000.Machine.Tape.Codecs;

/// <summary>CSW 1.00/1.01 and 2.00 container. RLE/Z-RLE is the shared TZX18 service.</summary>
public sealed class CswTapeCodec(ITapeRunEncoding encoding) : ITapeCodec
{
    public TapeFormat Format => TapeFormat.Csw;
    public TapeCodecCapabilities Capabilities => new(true, true, null);
    public TapeResult<TapeReadResult> Read(ReadOnlySpan<byte> bytes, TapeImportOptions options)
    {
        try
        {
            CodecSignal.ValidateImport(bytes, options, Format);
            if (bytes.Length < 25 || !bytes[..22].SequenceEqual("Compressed Square Wave"u8) || bytes[22] != 26)
                throw new CodecException(TapeError.InvalidFormat, "Invalid CSW header.");
            byte major = bytes[23], minor = bytes[24];
            uint rate, count = 0; byte compression, flags; int offset;
            if (major == 1 && minor <= 1)
            {
                if (bytes.Length < 32) throw new CodecException(TapeError.InvalidFormat, "Truncated CSW1 header.");
                rate = BinaryPrimitives.ReadUInt16LittleEndian(bytes[25..]); compression = bytes[27]; flags = bytes[28]; offset = 32;
                if (compression != 1) throw new CodecException(TapeError.UnsupportedCompression, "CSW1 requires RLE.");
            }
            else if (major == 2 && minor == 0)
            {
                if (bytes.Length < 52) throw new CodecException(TapeError.InvalidFormat, "Truncated CSW2 header.");
                rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[25..]); count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]);
                compression = bytes[33]; flags = bytes[34]; offset = 52 + bytes[35];
                if (offset > bytes.Length) throw new CodecException(TapeError.InvalidFormat, "Truncated CSW extension.");
            }
            else throw new CodecException(TapeError.UnsupportedVersion, $"Unsupported CSW {major}.{minor}.");
            if (rate == 0) throw new CodecException(TapeError.InvalidFormat, "Zero CSW sample rate.");
            uint? expected = major == 2 ? count : null;
            var decoded = compression switch
            {
                1 => encoding.DecodeRle(bytes[offset..], expected, options.Limits),
                2 => encoding.DecodeZrle(bytes[offset..], expected, options.Limits),
                _ => TapeResult<ImmutableArray<uint>>.Fail(TapeError.UnsupportedCompression, "Unknown CSW compression.")
            };
            if (!decoded.Success) return TapeResult<TapeReadResult>.Fail(decoded.Failure!.Code, decoded.Failure.Detail);
            var signal = new CodecSignal(options, bytes);
            signal.Route.Add(new(signal.Id, 0, 0));
            signal.Note(0, $"CSW {major}.{minor:D2}; rate={rate}; compression={compression}; flags={flags}");
            if (major == 2)
            {
                signal.Note(0, "Application: " + System.Text.Encoding.Latin1.GetString(bytes.Slice(36, 16)).TrimEnd('\0'));
                if (bytes[35] > 0) signal.Note(0, "Extension: " + Convert.ToHexString(bytes.Slice(52, bytes[35])));
            }
            if (major == 1 && minor == 0) signal.Warnings.Add(new("UnknownInitialPolarity", "CSW1.00 initial polarity defined as low by import policy."));
            bool level = !(major == 1 && minor == 0) && (flags & 1) != 0;
            for (int i = 0; i < decoded.Value.Length;)
            {
                uint samples = decoded.Value[i]; int end = i + 1;
                while (end < decoded.Value.Length && decoded.Value[end] == samples) end++;
                ulong repeat = (ulong)(end - i); var time = new TapeTime(samples, rate);
                if (repeat / 2 != 0) signal.Add(repeat / 2, [new(level, time), new(!level, time)]);
                if ((repeat & 1) != 0) { signal.Add(level, time); level = !level; }
                i = end;
            }
            return TapeResult<TapeReadResult>.Ok(signal.Finish(bytes));
        }
        catch (CodecException e) { return TapeResult<TapeReadResult>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<TapeReadResult>.Fail(TapeError.BoundsExceeded, "CSW arithmetic overflow."); }
    }
    public TapeResult<TapeWriteResult> Write(TapeSideImage image, TapeExportOptions options)
    {
        try
        {
            if (options.Target != Format) throw new CodecException(TapeError.InvalidFormat, "Wrong export target.");
            uint rate = options.Rate ?? 44_100;
            var projected = CodecSignal.Project(image, options, rate);
            if (!image.Markers.IsEmpty) throw new CodecException(TapeError.ExactEncodingUnavailable, "CSW cannot encode stop markers; select PZX or TZX.");
            var counts = new uint[projected.Runs.Count];
            for (int i = 0; i < counts.Length; i++)
            {
                if (projected.Runs[i].Count > uint.MaxValue)
                    throw new CodecException(TapeError.ExactEncodingUnavailable, "CSW hold exceeds u32; cannot split without adding edges.");
                counts[i] = (uint)projected.Runs[i].Count;
            }
            var encoded = options.UseZrle ? encoding.EncodeZrle(counts) : encoding.EncodeRle(counts);
            if (!encoded.Success) return TapeResult<TapeWriteResult>.Fail(encoded.Failure!.Code, encoded.Failure.Detail);
            if ((long)encoded.Value.Length + 52 > options.Limits.InputBytes || (long)encoded.Value.Length + 52 > options.Limits.DecodedBytes)
                throw new CodecException(TapeError.BoundsExceeded, "CSW output exceeds byte limits.");
            byte[] bytes = new byte[52 + encoded.Value.Length];
            "Compressed Square Wave"u8.CopyTo(bytes); bytes[22] = 26; bytes[23] = 2;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(25), rate);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(29), (uint)counts.Length);
            bytes[33] = options.UseZrle ? (byte)2 : (byte)1; bytes[34] = projected.Runs.Count > 0 && projected.Runs[0].Level ? (byte)1 : (byte)0;
            "HC2000"u8.CopyTo(bytes.AsSpan(36)); encoded.Value.AsSpan().CopyTo(bytes.AsSpan(52));
            return TapeResult<TapeWriteResult>.Ok(CodecSignal.Written(bytes, image, options, rate, projected));
        }
        catch (CodecException e) { return TapeResult<TapeWriteResult>.Fail(e.Code, e.Message); }
        catch (OverflowException) { return TapeResult<TapeWriteResult>.Fail(TapeError.BoundsExceeded, "CSW arithmetic overflow."); }
    }
}
