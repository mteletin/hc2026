using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Independent KB hdd-fixture-wire/data literals, not candidate-derived expectations.
/// Pure encoder only: no DB, assets, file publication, payload authentication or guest claim.</summary>
public sealed class HddCatalogEncodingTests
{
    private const string IndexHex = "4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce";
    private const string FooterHex = "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7";
    private static byte[] Id() => Convert.FromHexString("0102030405060708090a0b0c0d0e0f10");
    private static HddLimits Exact => new(16_777_340, 2, 60, 624);
    private static ImmutableArray<HddCatalogEntry> Rows() =>
    [new("VOL1.DSK", false, 8_388_608, new(0), 0x0362A35B),
     new("VOL2.DSK", true, 8_388_608, new(1), 0x25CFECB9)];

    private static T Value<T>(TapeResult<T> result) where T : notnull
    { Assert.True(result.Success, result.Failure?.Detail); return result.Value; }
    private static void Refused<T>(TapeResult<T> result, TapeError code) where T : notnull
    {
        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Equal(code, result.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => { _ = result.Value; });
    }
    // Independent fixture arithmetic for additional revisions/counts, never product CRC as oracle.
    private static uint ReferenceCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }
    private static void Recrc(byte[] bytes) => BinaryPrimitives.WriteUInt32LittleEndian(
        bytes.AsSpan(bytes.Length - 4), ReferenceCrc(bytes.AsSpan(0, bytes.Length - 4)));
    private static TapeResult<HddEncodedMetadata> Encode(ImmutableArray<HddCatalogEntry> rows, HddLimits? limits = null)
        => HddCatalogCodec.EncodeHdd(Id(), 1, rows, limits ?? Exact);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormativeWireBytesIdentityAndRowOrderAreExact(bool reverse)
    {
        var rows = Rows();
        if (reverse) rows = rows.Reverse().ToImmutableArray();
        var before = rows.ToArray();
        var output = Value(Encode(rows));
        Assert.Equal(Convert.FromHexString(IndexHex), output.Index.ToArray());
        Assert.Equal(Convert.FromHexString(FooterHex), output.Footer.ToArray());
        Assert.Equal(16_777_340UL, output.FileLength);
        Assert.Equal("AF3406F99F322A33D6AE6A52A5ECB66BA90342A271582CDCD5020273BDB0B5E2",
            Convert.ToHexString(SHA256.HashData(output.Index.Concat(output.Footer).ToArray())));
        Assert.Equal(before, rows.ToArray());
        var decoded = Value(HddCatalogCodec.DecodeHdd(output.Footer.AsSpan(), output.Index.AsSpan(), output.FileLength, Exact));
        Assert.Equal(Id(), decoded.Footer.ContainerId);
        Assert.Equal(1UL, decoded.Footer.Revision);
        Assert.Equal(0xCE4582DAu, decoded.Footer.IndexCrc);
        Assert.Equal(Rows().ToArray(), decoded.Entries.ToArray());
    }

    [Fact]
    public void OutputOwnsItsBytesAfterCallerIdentityChanges()
    {
        var id = Id();
        var rows = Rows();
        var output = Value(HddCatalogCodec.EncodeHdd(id, 1, rows, Exact));
        Array.Fill(id, (byte)0xff);
        rows = rows.SetItem(0, rows[0] with { Name = "OTHER.DSK", ImageCrc = 0 });
        Assert.Equal(Convert.FromHexString(IndexHex), output.Index.ToArray());
        Assert.Equal(Convert.FromHexString(FooterHex), output.Footer.ToArray());
        var detached = output.Footer.ToArray();
        detached[16] = 0;
        Assert.Equal(1, output.Footer[16]);
        Assert.Equal(Convert.FromHexString(IndexHex), Value(Encode(Rows())).Index.ToArray());
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public void RevisionIsEncodedVerbatimNotIncremented(ulong revision)
    {
        var expected = Convert.FromHexString(FooterHex);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(32), revision);
        Recrc(expected);
        var result = Value(HddCatalogCodec.EncodeHdd(Id(), revision, Rows(), Exact));
        Assert.Equal(expected, result.Footer.ToArray());
        Assert.Equal(Convert.FromHexString(IndexHex), result.Index.ToArray());
        Assert.Equal(revision, Value(HddCatalogCodec.DecodeHdd(result.Footer.AsSpan(), result.Index.AsSpan(), result.FileLength, Exact)).Footer.Revision);
    }

    [Fact]
    public void EmptyContainerHasExactFiniteShapeAndZeroArena()
    {
        var index = Convert.FromHexString("484356440100000000000000");
        Recrc(index);
        var footer = Convert.FromHexString(FooterHex);
        BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(40), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(48), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(52), ReferenceCrc(index.AsSpan(0, 8)));
        Recrc(footer);
        var limits = new HddLimits(76, 0, 12, 176);
        var result = Value(Encode([], limits));
        Assert.Equal(index, result.Index.ToArray());
        Assert.Equal(footer, result.Footer.ToArray());
        Assert.Equal(76UL, result.FileLength);
        Assert.Empty(Value(HddCatalogCodec.DecodeHdd(result.Footer.AsSpan(), result.Index.AsSpan(), 76, limits)).Entries);
        Refused(Encode([], limits with { MaxFileBytes = 75 }), TapeError.BoundsExceeded);
        Refused(Encode([], limits with { MaxIndexBytes = 11 }), TapeError.BoundsExceeded);
        Refused(Encode([], limits with { MaxMetadataBytes = 175 }), TapeError.BoundsExceeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryExactAdmissionLimitRejectsOneLessWithoutChangingInputs(int field)
    {
        var limits = field switch
        {
            0 => Exact with { MaxFileBytes = 16_777_339 },
            1 => Exact with { MaxEntries = 1 },
            2 => Exact with { MaxIndexBytes = 59 },
            _ => Exact with { MaxMetadataBytes = 623 }
        };
        var id = Id();
        var rows = Rows();
        Refused(HddCatalogCodec.EncodeHdd(id, 1, rows, limits), TapeError.BoundsExceeded);
        Assert.Equal(Id(), id);
        Assert.Equal(Rows().ToArray(), rows.ToArray());
        Assert.Equal(Convert.FromHexString(FooterHex), Value(Encode(rows)).Footer.ToArray());
    }

    [Theory]
    [InlineData(512)]
    [InlineData(65535)]
    [InlineData(65536)]
    public void CountAndFileCeilingsRefuseBeforeInvalidRowsAreProcessed(int count)
    {
        // ImmutableArray allocation is test input only; repeated malformed row avoids arena allocation.
        var rows = Enumerable.Repeat(new HddCatalogEntry(null!, false, 0, new(0), 0), count).ToImmutableArray();
        Refused(Encode(rows, new(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue)), TapeError.BoundsExceeded);
    }

    [Fact]
    public void LastCountWithinUintFileCapIsAdmittedWithoutAllocatingArena()
    {
        const int count = 511;
        var rows = Enumerable.Range(0, count).Select(i => new HddCatalogEntry(
            $"V{i:D7}.DSK", false, 8_388_608, new((uint)i), 0)).ToImmutableArray();
        var result = Value(Encode(rows, new(uint.MaxValue, 511, 12_276, 114_640)));
        Assert.Equal(4_286_591_028UL, result.FileLength);
        Assert.Equal(12_276, result.Index.Length);
        Assert.Equal(64, result.Footer.Length);
        Assert.Equal(511, BinaryPrimitives.ReadUInt16LittleEndian(result.Index.AsSpan()[6..8]));
        Assert.Equal(ReferenceCrc(result.Index.AsSpan()[..^4]),
            BinaryPrimitives.ReadUInt32LittleEndian(result.Index.AsSpan()[^4..]));
    }

    [Fact]
    public void IdentityIsOpaqueNonzeroBytesNotGuidOrAllNonzeroRequirement()
    {
        var id = new byte[16];
        id[15] = 0x80;
        var result = Value(HddCatalogCodec.EncodeHdd(id, 1, Rows(), Exact));
        Assert.Equal(id, result.Footer.AsSpan().Slice(16, 16).ToArray());
        Assert.Equal(id, Value(HddCatalogCodec.DecodeHdd(result.Footer.AsSpan(), result.Index.AsSpan(), result.FileLength, Exact)).Footer.ContainerId);
    }

    [Fact]
    public void InvalidIdentityRevisionDefaultRowsAndLimitsReturnTypedFailure()
    {
        foreach (var id in new[] { Array.Empty<byte>(), new byte[15], new byte[16], new byte[17] })
            Refused(HddCatalogCodec.EncodeHdd(id, 1, Rows(), Exact), TapeError.InvalidFormat);
        Refused(HddCatalogCodec.EncodeHdd(Id(), 0, Rows(), Exact), TapeError.InvalidFormat);
        Refused(Encode(default), TapeError.InvalidFormat);
        Refused(Encode(Rows().SetItem(0, null!)), TapeError.InvalidFormat);
        Refused(HddCatalogCodec.EncodeHdd(Id(), 1, Rows(), null!), TapeError.InvalidFormat);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("         ")]
    [InlineData("ABCDEFGHI.DSK")]
    [InlineData("A.LONG")]
    [InlineData("A.B.C")]
    [InlineData("A/B.DSK")]
    [InlineData("Aé.DSK")]
    [InlineData("A\u0001.DSK")]
    public void InvalidNamesRefuseWithoutPartialOutput(string? name)
        => Refused(Encode(Rows().SetItem(0, Rows()[0] with { Name = name! })), TapeError.InvalidFormat);

    [Theory]
    [InlineData("VOL1.DSK")]
    [InlineData("vol1.dsk")]
    [InlineData("VOL1 .DSK")]
    public void CanonicalOrCaseNameAliasesAreNotSeparateVolumes(string name)
        => Refused(Encode(Rows().SetItem(1, Rows()[1] with { Name = name })), TapeError.InvalidFormat);

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void DuplicateGappedOrOutOfRangeOrdinalsRefuse(uint ordinal)
        => Refused(Encode(Rows().SetItem(1, Rows()[1] with { SlotOrdinal = new(ordinal) })), TapeError.InvalidFormat);

    [Theory]
    [InlineData(0u)]
    [InlineData(4096u)]
    [InlineData(8_388_607u)]
    [InlineData(8_388_609u)]
    [InlineData(uint.MaxValue)]
    public void SlotWidthIsNotNegotiable(uint width)
        => Refused(Encode(Rows().SetItem(0, Rows()[0] with { ImageBytes = width })), TapeError.InvalidFormat);

    [Fact]
    public void TapeRecordBytesRemainTheIndependentBaseline()
    {
        var codec = new TapeCatalogCodec();
        var a = ImmutableArray.Create(new TapeCatalogEntry("VOL1.DSK", false, 16384, 0, 0xAB54D286));
        var b = ImmutableArray.Create(new TapeCatalogEntry("VOL2.DSK", true, 16384, 1, 0xAB54D286));
        Assert.Equal(Convert.FromHexString("4843415401000100564f4c312020202044534b00004000000000000086d254abb0d45c2b"),
            Value(codec.EncodeRecord(TapeSide.A, a, new TapeLimits())).ToArray());
        Assert.Equal(Convert.FromHexString("4843415401000100564f4c322020202044534b01004000000100000086d254ab488f7e41"),
            Value(codec.EncodeRecord(TapeSide.A, b, new TapeLimits())).ToArray());
    }
}
