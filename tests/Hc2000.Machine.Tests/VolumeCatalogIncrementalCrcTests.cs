using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Actual shared CRC register API; fixed ISO-HDLC/zlib literals, no second CRC implementation.
/// Wire prefixes are the existing HCVD/HCVM1END corpus, not encoder-generated expectations.</summary>
public sealed class VolumeCatalogIncrementalCrcTests
{
    public static TheoryData<byte[], uint> Vectors => new()
    {
        { [], 0x00000000u },
        { "123456789"u8.ToArray(), 0xCBF43926u },
        { [0x00], 0xD202EF8Du },
        { [0xff], 0xFF000000u },
        { Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 0x29058C73u },
        { Convert.FromHexString("4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25"), 0xCE4582DAu },
        { Convert.FromHexString("4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000"), 0xB73ACDB7u }
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void OneShotAndSingleUpdateMatchIndependentLiterals(byte[] input, uint expected)
    {
        var original = (byte[])input.Clone();
        Assert.Equal(0xffffffffu, VolumeCatalogMetadata.Crc32InitialState);
        Assert.Equal(expected, VolumeCatalogMetadata.Crc32(input));
        var initial = VolumeCatalogMetadata.Crc32InitialState;
        var state = VolumeCatalogMetadata.Crc32Update(initial, input);
        Assert.Equal(0xffffffffu, initial);
        Assert.Equal(~expected, state);
        Assert.Equal(expected, VolumeCatalogMetadata.Crc32Finish(state));
        Assert.Equal(~expected, state);
        Assert.Equal(expected, VolumeCatalogMetadata.Crc32Finish(state));
        Assert.Equal(original, input);
    }

    [Theory]
    [InlineData(0x00000000u, 0xffffffffu)]
    [InlineData(0xffffffffu, 0x00000000u)]
    [InlineData(0x12345678u, 0xedcba987u)]
    [InlineData(0x80000001u, 0x7ffffffeu)]
    public void EmptyUpdateIsIdentityAndFinishOnlyComplements(uint state, uint expected)
    {
        var original = state;
        Assert.Equal(original, VolumeCatalogMetadata.Crc32Update(state, ReadOnlySpan<byte>.Empty));
        Assert.Equal(expected, VolumeCatalogMetadata.Crc32Finish(state));
        Assert.Equal(original, state);
        Assert.Equal(expected, VolumeCatalogMetadata.Crc32Finish(state));
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void EverySplitWithEmptyChunksPreservesLiteralAndCanResumeAfterFinish(byte[] input, uint expected)
    {
        var original = (byte[])input.Clone();
        for (var split = 0; split <= input.Length; split++)
        {
            var state = VolumeCatalogMetadata.Crc32Update(VolumeCatalogMetadata.Crc32InitialState, ReadOnlySpan<byte>.Empty);
            state = VolumeCatalogMetadata.Crc32Update(state, input.AsSpan(0, split));
            var prefixState = state;
            _ = VolumeCatalogMetadata.Crc32Finish(state);
            Assert.Equal(prefixState, state);
            state = VolumeCatalogMetadata.Crc32Update(state, ReadOnlySpan<byte>.Empty);
            state = VolumeCatalogMetadata.Crc32Update(state, input.AsSpan(split));
            state = VolumeCatalogMetadata.Crc32Update(state, ReadOnlySpan<byte>.Empty);
            Assert.Equal(~expected, state);
            Assert.Equal(expected, VolumeCatalogMetadata.Crc32Finish(state));
        }
        Assert.Equal(original, input);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void OneByteAndUnevenChunksPreserveLiteral(byte[] input, uint expected)
    {
        var original = (byte[])input.Clone();
        foreach (var widths in new[] { new[] { 1 }, new[] { 2, 5, 1, 13, 3, 29 } })
        {
            var state = VolumeCatalogMetadata.Crc32InitialState;
            var offset = 0;
            var chunk = 0;
            while (offset < input.Length)
            {
                var length = Math.Min(widths[chunk++ % widths.Length], input.Length - offset);
                state = VolumeCatalogMetadata.Crc32Update(state, input.AsSpan(offset, length));
                state = VolumeCatalogMetadata.Crc32Update(state, ReadOnlySpan<byte>.Empty);
                offset += length;
            }
            Assert.Equal(~expected, state);
            Assert.Equal(expected, VolumeCatalogMetadata.Crc32Finish(state));
        }
        Assert.Equal(original, input);
    }
}
