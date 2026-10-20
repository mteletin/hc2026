using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Encoding;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeRunEncodingTests
{
    private readonly ITapeRunEncoding codec = new TapeRunEncoding();

    [Fact]
    public void IndependentLittleEndianOracle()
    {
        uint[] runs = [1, 255, 256, uint.MaxValue];
        byte[] expected = [1, 255, 0, 0, 1, 0, 0, 0, 255, 255, 255, 255];
        var encoded = codec.EncodeRle(runs);
        Assert.True(encoded.Success, encoded.Failure?.Detail);
        Assert.Equal(expected, encoded.Value.ToArray());
        var decoded = codec.DecodeRle(expected, 4, new());
        Assert.True(decoded.Success, decoded.Failure?.Detail);
        Assert.Equal(runs, decoded.Value.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0, 1, 2, 3 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0 })]
    public void TruncatedOrZeroEscapeFails(byte[] bytes) =>
        Assert.Equal(TapeError.InvalidFormat, codec.DecodeRle(bytes, null, new()).Failure?.Code);

    [Fact]
    public void CountsAndBudgetsAreChecked()
    {
        Assert.Equal(TapeError.InvalidFormat, codec.DecodeRle([1, 2], 1, new()).Failure?.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.DecodeRle([1, 2], null, new() { VirtualTransitions = 1 }).Failure?.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.DecodeRle([1], null, new() { DecodedBytes = 3 }).Failure?.Code);
        Assert.Equal(TapeError.InvalidFormat, codec.EncodeRle([0]).Failure?.Code);
    }

    [Fact]
    public void PublishedZlibWrapperAndIndependentUncompressedDeflateOracle()
    {
        // RFC1950 78 01, RFC1951 final uncompressed block, bytes 01 FF, Adler32 01030101.
        byte[] zlib = [0x78, 0x01, 0x01, 0x02, 0x00, 0xFD, 0xFF, 1, 255, 1, 3, 1, 1];
        var result = codec.DecodeZrle(zlib, 2, new());
        Assert.True(result.Success, result.Failure?.Detail);
        Assert.Equal(new uint[] { 1, 255 }, result.Value.ToArray());
        var compressed = codec.EncodeZrle([1, 255]);
        Assert.True(compressed.Success);
        Assert.Equal((byte)0x78, compressed.Value[0]);
        Assert.Equal(new uint[] { 1, 255 }, codec.DecodeZrle(compressed.Value.AsSpan(), 2, new()).Value.ToArray());
    }

    [Fact]
    public void CompressionErrorsAndExpansionLimitsAreNamed()
    {
        Assert.Equal(TapeError.InvalidFormat, codec.DecodeZrle([0x1F, 0x8B], null, new()).Failure?.Code);
        var bytes = codec.EncodeZrle(Enumerable.Repeat(1U, 1000).ToArray());
        Assert.True(bytes.Success);
        Assert.Equal(TapeError.BoundsExceeded, codec.DecodeZrle(bytes.Value.AsSpan(), null, new() { DecodedBytes = 8 }).Failure?.Code);
        var corrupt = bytes.Value.ToArray();
        corrupt[^1] ^= 1;
        Assert.Equal(TapeError.InvalidFormat, codec.DecodeZrle(corrupt, null, new()).Failure?.Code);
        Assert.Equal(TapeError.InvalidFormat, codec.DecodeZrle(bytes.Value.AsSpan()[..^1], null, new()).Failure?.Code);
    }
}
