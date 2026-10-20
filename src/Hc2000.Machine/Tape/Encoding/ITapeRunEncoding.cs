using System.Collections.Immutable;

namespace Hc2000.Machine.Tape.Encoding;

/// <summary>Shared container-independent u32 nonzero sample runs. Z-RLE means RFC1950 zlib,
/// never gzip/raw DEFLATE. Implemented once by hc2000-tape-tzx, reused by TZX18 and CSW.
/// Decoders must bound bytes/expansion and verify exact declared counts when supplied.</summary>
public interface ITapeRunEncoding
{
    TapeResult<ImmutableArray<uint>> DecodeRle(ReadOnlySpan<byte> encodedBytes, uint? expectedRunCount, TapeLimits limits);
    TapeResult<ImmutableArray<uint>> DecodeZrle(ReadOnlySpan<byte> encodedBytes, uint? expectedRunCount, TapeLimits limits);
    TapeResult<ImmutableArray<byte>> EncodeRle(ReadOnlySpan<uint> sampleCounts);
    TapeResult<ImmutableArray<byte>> EncodeZrle(ReadOnlySpan<uint> sampleCounts);
}

/// <summary>Explicit foundation placeholder; none of the encoding operations can succeed.</summary>
public sealed class UnavailableTapeRunEncoding : ITapeRunEncoding
{
    public TapeResult<ImmutableArray<uint>> DecodeRle(ReadOnlySpan<byte> encodedBytes, uint? expectedRunCount, TapeLimits limits) =>
        TapeResult<ImmutableArray<uint>>.Fail(TapeError.RuntimeUnavailable, "TapeRunEncoding is not installed.");
    public TapeResult<ImmutableArray<uint>> DecodeZrle(ReadOnlySpan<byte> encodedBytes, uint? expectedRunCount, TapeLimits limits) =>
        TapeResult<ImmutableArray<uint>>.Fail(TapeError.RuntimeUnavailable, "TapeRunEncoding is not installed.");
    public TapeResult<ImmutableArray<byte>> EncodeRle(ReadOnlySpan<uint> sampleCounts) =>
        TapeResult<ImmutableArray<byte>>.Fail(TapeError.RuntimeUnavailable, "TapeRunEncoding is not installed.");
    public TapeResult<ImmutableArray<byte>> EncodeZrle(ReadOnlySpan<uint> sampleCounts) =>
        TapeResult<ImmutableArray<byte>>.Fail(TapeError.RuntimeUnavailable, "TapeRunEncoding is not installed.");
}
