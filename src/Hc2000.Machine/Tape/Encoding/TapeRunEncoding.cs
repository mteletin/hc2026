using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;

namespace Hc2000.Machine.Tape.Encoding;

/// <summary>Native CSW sample-run coding shared by TZX18 and standalone CSW. RFC1950 only.</summary>
public sealed class TapeRunEncoding : ITapeRunEncoding
{
    public TapeResult<ImmutableArray<uint>> DecodeRle(ReadOnlySpan<byte> bytes, uint? expectedRunCount, TapeLimits limits)
    {
        if (limits.InputBytes < 0 || limits.DecodedBytes < 0 || limits.VirtualTransitions < 0 ||
            bytes.Length > limits.InputBytes || expectedRunCount > (uint)limits.VirtualTransitions ||
            expectedRunCount > (uint)(limits.DecodedBytes / sizeof(uint)))
            return FailRuns(TapeError.BoundsExceeded, "CSW run budget exceeded.");
        var runs = ImmutableArray.CreateBuilder<uint>();
        for (int offset = 0; offset < bytes.Length;)
        {
            uint value = bytes[offset++];
            if (value == 0)
            {
                if (bytes.Length - offset < 4) return FailRuns(TapeError.InvalidFormat, "Truncated CSW wide run.");
                value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                offset += 4;
                if (value == 0) return FailRuns(TapeError.InvalidFormat, "CSW run duration is zero.");
            }
            if (runs.Count >= limits.VirtualTransitions || runs.Count >= limits.DecodedBytes / sizeof(uint))
                return FailRuns(TapeError.BoundsExceeded, "CSW decoded runs exceed budget.");
            if (expectedRunCount.HasValue && (uint)runs.Count >= expectedRunCount.Value)
                return FailRuns(TapeError.InvalidFormat, "CSW decoded run count exceeds declaration.");
            runs.Add(value);
        }
        if (expectedRunCount.HasValue && (uint)runs.Count != expectedRunCount.Value)
            return FailRuns(TapeError.InvalidFormat, "CSW decoded run count differs from declaration.");
        return TapeResult<ImmutableArray<uint>>.Ok(runs.ToImmutable());
    }

    public TapeResult<ImmutableArray<uint>> DecodeZrle(ReadOnlySpan<byte> bytes, uint? expectedRunCount, TapeLimits limits)
    {
        if (limits.InputBytes < 0 || limits.DecodedBytes < 0 || limits.VirtualTransitions < 0 || bytes.Length > limits.InputBytes ||
            expectedRunCount > (uint)limits.VirtualTransitions || expectedRunCount > (uint)(limits.DecodedBytes / sizeof(uint)))
            return FailRuns(TapeError.BoundsExceeded, "CSW compressed input/count exceeds budget.");
        if (bytes.Length < 6 || (bytes[0] & 15) != 8 || (bytes[0] >> 4) > 7 ||
            ((bytes[0] << 8) + bytes[1]) % 31 != 0 || (bytes[1] & 32) != 0)
            return FailRuns(TapeError.InvalidFormat, "Expected RFC1950 zlib without a preset dictionary.");
        try
        {
            // Single-byte reads prevent inflater read-ahead hiding an extra stream/trailing payload.
            using var input = new ExactInput(bytes.ToArray());
            using var inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var output = new MemoryStream();
            byte[] buffer = new byte[4096];
            uint a = 1, b = 0;
            int count;
            while ((count = inflater.Read(buffer)) != 0)
            {
                if (output.Length + count > limits.DecodedBytes)
                    return FailRuns(TapeError.BoundsExceeded, "CSW zlib expansion exceeds budget.");
                for (int i = 0; i < count; i++) { a = (a + buffer[i]) % 65521; b = (b + a) % 65521; }
                output.Write(buffer, 0, count);
            }
            if (input.Position != input.Length || BinaryPrimitives.ReadUInt32BigEndian(bytes[^4..]) != (b << 16 | a))
                return FailRuns(TapeError.InvalidFormat, "Truncated zlib checksum or trailing compressed bytes.");
            // Inflated RLE has its own bound, not the compressed-input bound.
            return DecodeRle(output.GetBuffer().AsSpan(0, checked((int)output.Length)), expectedRunCount,
                limits with { InputBytes = limits.DecodedBytes });
        }
        catch (InvalidDataException) { return FailRuns(TapeError.InvalidFormat, "Invalid zlib data/checksum."); }
        catch (IOException) { return FailRuns(TapeError.InvalidFormat, "Invalid zlib stream."); }
    }

    public TapeResult<ImmutableArray<byte>> EncodeRle(ReadOnlySpan<uint> counts)
    {
        long size = 0;
        foreach (uint count in counts)
        {
            if (count == 0) return TapeResult<ImmutableArray<byte>>.Fail(TapeError.InvalidFormat, "CSW run duration is zero.");
            size += count <= 255 ? 1 : 5;
        }
        if (size > int.MaxValue) return TapeResult<ImmutableArray<byte>>.Fail(TapeError.BoundsExceeded, "CSW encoded size overflow.");
        byte[] result = new byte[(int)size];
        int offset = 0;
        foreach (uint count in counts)
        {
            if (count <= 255) result[offset++] = (byte)count;
            else
            {
                result[offset++] = 0;
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), count);
                offset += 4;
            }
        }
        return TapeResult<ImmutableArray<byte>>.Ok(ImmutableArray.Create(result));
    }

    public TapeResult<ImmutableArray<byte>> EncodeZrle(ReadOnlySpan<uint> counts)
    {
        var encoded = EncodeRle(counts);
        if (!encoded.Success) return TapeResult<ImmutableArray<byte>>.Fail(encoded.Failure!.Code, encoded.Failure.Detail);
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(encoded.Value.AsSpan());
        return TapeResult<ImmutableArray<byte>>.Ok(ImmutableArray.Create(output.ToArray()));
    }

    private static TapeResult<ImmutableArray<uint>> FailRuns(TapeError error, string detail) =>
        TapeResult<ImmutableArray<uint>>.Fail(error, detail);

    private sealed class ExactInput(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }
}
