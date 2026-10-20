using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Shared metadata/descriptor seam against independent spec-pinned literals
/// (KB hc2000-tape-catalog-format-v1: hdd-fixture-wire / hdd-fixture-data); no asset, DB or guest binding.
/// The tape cross-check uses the corpus's own HCAT fixtures byte-identically.</summary>
public sealed class VolumeCatalogMetadataTests
{
    private const string AHex = "4843415401000100564f4c312020202044534b00004000000000000086d254abb0d45c2b";
    private const string BHex = "4843415401000100564f4c322020202044534b01004000000100000086d254ab488f7e41";
    private const string IndexHex =
        "4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce";
    private const string FooterHex =
        "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7";
    private static readonly byte[] A = Convert.FromHexString(AHex);
    private static readonly byte[] B = Convert.FromHexString(BHex);
    private static readonly byte[] Index = Convert.FromHexString(IndexHex);
    private static readonly byte[] Footer = Convert.FromHexString(FooterHex);
    private static readonly byte[] Seed =
        Convert.FromHexString("0053454544202020205458540000000108000000000000000000000000000000");
    private static readonly HddLimits ExactLimits = new(16_777_340, 2, 60, 624);
    private readonly TapeCatalogCodec codec = new();

    // TEST-only independent reflected CRC-32 reimplementation (poly 0xEDB88320), cross-pinned against python zlib.
    private static uint ReferenceCrc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (var b in bytes)
        {
            value ^= b;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1u) == 0 ? value >> 1 : (value >> 1) ^ 0xEDB88320u;
        }
        return ~value;
    }

    private static void StoreCrc(byte[] record, int prefix)
        => BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(prefix), ReferenceCrc(record.AsSpan(0, prefix)));

    private static uint StoredCrc(byte[] record) => BinaryPrimitives.ReadUInt32LittleEndian(record[^4..]);

    /// <summary>Container bytes with optional mutations; recompute/sync flags decide which CRC links stay stale.</summary>
    private static (byte[] Index, byte[] Footer) Container(
        Action<byte[]>? indexMutate = null,
        Action<byte[]>? footerMutate = null,
        bool recomputeIndexCrc = true,
        bool syncLink = true)
    {
        var index = (byte[])Index.Clone();
        if (indexMutate is not null) indexMutate(index);
        if (recomputeIndexCrc) StoreCrc(index, index.Length - 4);
        var footer = (byte[])Footer.Clone();
        if (footerMutate is not null) footerMutate(footer);
        if (syncLink)
            BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(52), StoredCrc(index));
        StoreCrc(footer, 60);
        return (index, footer);
    }

    private static byte[] SlotImage(byte marker)
    {
        var image = new byte[(int)VolumeCatalogMetadata.SlotBytes];
        for (var i = 0; i < 32768; i++) image[i] = 0xE5;
        Seed.CopyTo(image, 0);
        for (var i = 32768; i < 36864; i++) image[i] = marker;
        return image;
    }

    // Corpus-mirror helpers (byte-identical to TapeCatalogCodecTests) for the cross-check.
    private static ImmutableArray<byte> Bytes(byte[] bytes) => ImmutableArray.CreateRange(bytes);
    private static byte[] Raw(byte[] content, byte flag = 0xff)
    {
        var raw = new byte[content.Length + 2]; raw[0] = flag; content.CopyTo(raw, 1);
        foreach (var b in raw.AsSpan(0, raw.Length - 1)) raw[^1] ^= b;
        return raw;
    }
    private static TapeCatalogSide Side(params byte[][] raw) => new(TapeSide.A, raw.Select(Bytes).ToImmutableArray());
    private static TapeCatalogSide FixtureA() => Side(Raw(new byte[16384]), Raw(A[..20]), Raw(A[20..]));
    private static TapeCatalogSide FixtureBoth() => Side(Raw(new byte[16384]), Raw(new byte[16384]), Raw(A[..20]), Raw(A[20..]), Raw(B));
    private static byte[] Recrc(byte[] record)
    {
        StoreCrc(record, record.Length - 4);
        return record;
    }

    private static T Value<T>(TapeResult<T> result) where T : notnull
    { Assert.True(result.Success, result.Failure?.Detail); return result.Value; }

    private static void Refused<T>(TapeResult<T> result, string message, TapeError? code = null) where T : notnull
    {
        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        if (code is not null) Assert.Equal(code, result.Failure!.Code);
        Assert.Equal(message, result.Failure.Detail);
        Assert.Throws<InvalidOperationException>(() => { _ = result.Value; });
    }

    [Fact]
    public void CrcPinsMatchIndependentOracle()
    {
        var check = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, ReferenceCrc(check));
        Assert.Equal(VolumeCatalogMetadata.Crc32(check), ReferenceCrc(check));
        Assert.Equal(0xAB54D286u, ReferenceCrc(new byte[16384]));
        Assert.Equal(0xCE4582DAu, ReferenceCrc(Index.AsSpan(0, 56)));
        Assert.Equal(0xB73ACDB7u, ReferenceCrc(Footer.AsSpan(0, 60)));
        Assert.Equal(0xCE4582DAu, VolumeCatalogMetadata.Crc32(Index.AsSpan(0, 56)));
        Assert.Equal(0xB73ACDB7u, VolumeCatalogMetadata.Crc32(Footer.AsSpan(0, 60)));
    }

    [Fact]
    public void CanonicalName83AcceptsOnlyPrintableCanonicalForms()
    {
        var accepted = new (string Name, string Hex)[]
        {
            ("A", "4120202020202020202020"),
            ("ABCDEFGH.TXT", "4142434445464748545854"),
            ("MY FILE.TXT", "4D592046494C4520545854"),
            ("ABC ", "4142432020202020202020"),
            ("12345678.SYS", "3132333435363738535953"),
            ("A~", "417E202020202020202020"),
        };
        foreach (var (name, hex) in accepted)
            Assert.Equal(Convert.FromHexString(hex), VolumeCatalogMetadata.CanonicalName83(name));

        foreach (var name in new[] { null, "", "      ", "ABCDEFGHI", "A.DSK2000", "A.B.C", "A.B.C.DSK",
            "A<B", "A>B", "A/B", "A\\B", "A:B", "A*B", "A?B", "A|B", "A\"B", "Aé", "A\x01B" })
            Assert.True(VolumeCatalogMetadata.CanonicalName83(name!) is null, $"accepted {name ?? "<null>"}");
    }

    [Fact]
    public void ParseName83RequiresCanonicalFieldAndOnlyBit0Flag()
    {
        var vol1 = VolumeCatalogMetadata.CanonicalName83("VOL1.DSK")!;
        Assert.Equal("VOL1.DSK", VolumeCatalogMetadata.ParseName83(vol1, 0));
        Assert.Equal("VOL1.DSK", VolumeCatalogMetadata.ParseName83(vol1, 1));
        Assert.Equal("SEED.TXT", VolumeCatalogMetadata.ParseName83(VolumeCatalogMetadata.CanonicalName83("SEED.TXT")!, 1));
        Assert.Null(VolumeCatalogMetadata.ParseName83(vol1, 2));
        Assert.Null(VolumeCatalogMetadata.ParseName83(vol1, 0xfe));
        Assert.Null(VolumeCatalogMetadata.ParseName83(new byte[11], 0));
        Assert.Null(VolumeCatalogMetadata.ParseName83(vol1[..10], 0));
    }

    [Fact]
    public void TapeAndHddCoordinatesAreDistinctTypes()
    {
        Assert.False(new TapeBlockIndex(0u).Equals(new HddSlotOrdinal(0u)));
        Assert.False(new HddSlotOrdinal(0u).Equals(new TapeBlockIndex(0u)));
        Assert.Equal(new HddSlotOrdinal(3u), new HddSlotOrdinal(3u));
        Assert.Equal(new TapeBlockIndex(3u), new TapeBlockIndex(3u));
    }

    [Fact]
    public void InspectHddFooterAcceptsThePinnedContainer()
    {
        var view = Value(HddCatalogCodec.InspectHddFooter(Footer, 16_777_340, ExactLimits));
        Assert.Equal(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"), view.ContainerId);
        Assert.Equal(1UL, view.Revision);
        Assert.Equal(2u, view.Entries);
        Assert.Equal(16_777_216UL, view.ArenaBytes);
        Assert.Equal(60u, view.IndexBytes);
        Assert.Equal(0xCE4582DAu, view.IndexCrc);
        Assert.Equal(16_777_216UL, view.IndexStart);
        Assert.Equal(16_777_276UL, view.IndexEnd);
        Assert.Equal(16_777_340UL, view.FileLength);
    }

    [Fact]
    public void DecodeHddAcceptsThePinnedContainer()
    {
        var descriptor = Value(HddCatalogCodec.DecodeHdd(Footer, Index, 16_777_340, ExactLimits));
        Assert.Equal(new HddCatalogEntry("VOL1.DSK", false, 8_388_608, new HddSlotOrdinal(0), 0x0362A35B),
            descriptor.Entries[0]);
        Assert.Equal(new HddCatalogEntry("VOL2.DSK", true, 8_388_608, new HddSlotOrdinal(1), 0x25CFECB9),
            descriptor.Entries[1]);
        Assert.Equal(2, descriptor.Entries.Length);
        Assert.Equal(16_777_340UL, descriptor.Footer.FileLength);
    }

    [Fact]
    public void PinnedImageCrcsAndSha256MatchIndependentFixtures()
    {
        var p0 = SlotImage(0x41);
        var p1 = SlotImage(0x42);
        Assert.Equal(8_388_608, p0.Length);
        Assert.Equal(0x0362A35Bu, VolumeCatalogMetadata.Crc32(p0));
        Assert.Equal(0x25CFECB9u, VolumeCatalogMetadata.Crc32(p1));
        Assert.Equal(ReferenceCrc(p0), VolumeCatalogMetadata.Crc32(p0));
        Assert.Equal("9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F",
            Convert.ToHexString(SHA256.HashData(p0)));
        Assert.Equal("F484C7D32E06CBA31D0DD508A1EBC7C26CC4ECDC18A738E040CC3D6BEF7F1EC3",
            Convert.ToHexString(SHA256.HashData(p1)));
    }

    [Fact]
    public void BudgetLimitsAdmitExactFitAndRefuseOnePastEachBound()
    {
        Value(HddCatalogCodec.InspectHddFooter(Footer, 16_777_340, ExactLimits));
        Value(HddCatalogCodec.DecodeHdd(Footer, Index, 16_777_340, ExactLimits));

        Refused(HddCatalogCodec.InspectHddFooter(Footer, 16_777_340, new HddLimits(16_777_340, 1, 60, 624)),
            "Entry count exceeds caller limit.", TapeError.BoundsExceeded);
        Refused(HddCatalogCodec.InspectHddFooter(Footer, 16_777_340, new HddLimits(16_777_340, 2, 59, 624)),
            "Index bytes exceed caller limit.", TapeError.BoundsExceeded);
        Refused(HddCatalogCodec.InspectHddFooter(Footer, 16_777_340, new HddLimits(16_777_339, 2, 60, 624)),
            "File length exceeds caller limit.", TapeError.BoundsExceeded);
        Refused(HddCatalogCodec.InspectHddFooter(Footer, 16_777_340, new HddLimits(16_777_340, 2, 60, 623)),
            "Metadata reserve exceeds caller limit.", TapeError.BoundsExceeded);

        var (_, wireMax) = Container(footerMutate: c =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(c.AsSpan(40), 549_755_347_456UL);
            BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(48), 1_572_876u);
        });
        Refused(HddCatalogCodec.InspectHddFooter(wireMax, 549_756_920_396UL,
            new HddLimits(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue)),
            "Entry count exceeds wire maximum.", TapeError.BoundsExceeded);
    }

    [Fact]
    public void SemanticMutantsRefuseWithPinnedCrcs()
    {
        var name0 = Convert.FromHexString("564f4c312020202044534b");

        var (dupOrdinal, footer) = Container(indexMutate: c => c[48] = 0);
        Assert.Equal(0x02EF8244u, StoredCrc(dupOrdinal));
        Refused(HddCatalogCodec.DecodeHdd(footer, dupOrdinal, 16_777_340, ExactLimits), "Invalid slot ordinal.");

        var (outOfRange, footer2) = Container(indexMutate: c => c[48] = 2);
        Assert.Equal(0x40CA8539u, StoredCrc(outOfRange));
        Refused(HddCatalogCodec.DecodeHdd(footer2, outOfRange, 16_777_340, ExactLimits), "Invalid slot ordinal.");

        var (badImage, footer3) = Container(indexMutate: c => BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(44), 4096u));
        Assert.Equal(0x86B64F10u, StoredCrc(badImage));
        Refused(HddCatalogCodec.DecodeHdd(footer3, badImage, 16_777_340, ExactLimits), "Invalid slot image length.");

        var (dupName, footer4) = Container(indexMutate: c => name0.CopyTo(c, 32));
        Assert.Equal(0xB55B0039u, StoredCrc(dupName));
        Refused(HddCatalogCodec.DecodeHdd(footer4, dupName, 16_777_340, ExactLimits), "Duplicate volume name.");
    }

    [Fact]
    public void StructuralMutantsRefuseAtTheirOwnChecks()
    {
        // Footer side (CRC kept valid so the failure is the semantic check, not the CRC).
        Refused(HddCatalogCodec.InspectHddFooter(Footer[..63], 16_777_340, ExactLimits), "Invalid footer length.");
        var (_, badMagic) = Container(footerMutate: c => c[0] = (byte)'X');
        Refused(HddCatalogCodec.InspectHddFooter(badMagic, 16_777_340, ExactLimits), "Invalid footer magic.");
        var (_, badVersion) = Container(footerMutate: c => c[8] = 2);
        Refused(HddCatalogCodec.InspectHddFooter(badVersion, 16_777_340, ExactLimits),
            "Unsupported container version.", TapeError.UnsupportedVersion);
        var (_, badFlags) = Container(footerMutate: c => c[12] = 1);
        Refused(HddCatalogCodec.InspectHddFooter(badFlags, 16_777_340, ExactLimits),
            "Unsupported container flags.", TapeError.UnsupportedVersion);
        var (_, noIdentity) = Container(footerMutate: c => { for (var i = 16; i < 32; i++) c[i] = 0; });
        Refused(HddCatalogCodec.InspectHddFooter(noIdentity, 16_777_340, ExactLimits), "Empty container identity.");
        var (_, noRevision) = Container(footerMutate: c => c[32] = 0);
        Refused(HddCatalogCodec.InspectHddFooter(noRevision, 16_777_340, ExactLimits), "Invalid container revision.");
        var (_, badReserved) = Container(footerMutate: c => c[56] = 1);
        Refused(HddCatalogCodec.InspectHddFooter(badReserved, 16_777_340, ExactLimits), "Invalid footer reserved field.");
        var (_, badIndexBytes) = Container(footerMutate: c => BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(48), 58u));
        Refused(HddCatalogCodec.InspectHddFooter(badIndexBytes, 16_777_340, ExactLimits), "Invalid index length.");
        var (_, badArena) = Container(footerMutate: c => BinaryPrimitives.WriteUInt64LittleEndian(c.AsSpan(40), 16_777_217UL));
        Refused(HddCatalogCodec.InspectHddFooter(badArena, 16_777_340, ExactLimits), "Footer arena length mismatch.");
        Refused(HddCatalogCodec.InspectHddFooter(Footer, 16_777_341, ExactLimits),
            "Enclosing length mismatch.", TapeError.BoundsExceeded);
        var tornFooter = (byte[])Footer.Clone();
        tornFooter[33] ^= 1;
        Refused(HddCatalogCodec.InspectHddFooter(tornFooter, 16_777_340, ExactLimits), "Torn footer CRC.");

        // Index side.
        var (badMagicIndex, f1) = Container(indexMutate: c => c[0] = (byte)'X');
        Refused(HddCatalogCodec.DecodeHdd(f1, badMagicIndex, 16_777_340, ExactLimits), "Invalid index magic.");
        var (badIndexVersion, f2) = Container(indexMutate: c => c[4] = 2);
        Refused(HddCatalogCodec.DecodeHdd(f2, badIndexVersion, 16_777_340, ExactLimits),
            "Unsupported index version.", TapeError.UnsupportedVersion);
        var (badReservedIndex, f3) = Container(indexMutate: c => c[5] = 1);
        Refused(HddCatalogCodec.DecodeHdd(f3, badReservedIndex, 16_777_340, ExactLimits), "Invalid index reserved byte.");
        var (badCount, f4) = Container(indexMutate: c => BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(6), 1));
        Refused(HddCatalogCodec.DecodeHdd(f4, badCount, 16_777_340, ExactLimits), "Footer/index count mismatch.");
        Refused(HddCatalogCodec.DecodeHdd(Footer, Index[..58], 16_777_340, ExactLimits), "Invalid index length.");
        var (tornIndex, f5) = Container(indexMutate: c => c[30] ^= 1, recomputeIndexCrc: false);
        Refused(HddCatalogCodec.DecodeHdd(f5, tornIndex, 16_777_340, ExactLimits), "Torn index CRC.");
        var (linkedIndex, f6) = Container(
            footerMutate: c => BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(52), 0xDEADBEEFu), syncLink: false);
        Refused(HddCatalogCodec.DecodeHdd(f6, linkedIndex, 16_777_340, ExactLimits), "Footer/index CRC link mismatch.");
    }

    [Fact]
    public void EmptyContainerAndReorderedRowsAreAccepted()
    {
        // N=0: minimal 12-byte index, handcrafted, pinned CRC.
        var idx12 = new byte[12];
        "HCVD"u8.CopyTo(idx12);
        idx12[4] = 1;
        idx12[5] = 0;
        StoreCrc(idx12, 8);
        Assert.Equal(0x25A11BD3u, StoredCrc(idx12));
        var emptyFooter = (byte[])Footer.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(emptyFooter.AsSpan(40), 0UL);
        BinaryPrimitives.WriteUInt32LittleEndian(emptyFooter.AsSpan(48), 12u);
        BinaryPrimitives.WriteUInt32LittleEndian(emptyFooter.AsSpan(52), 0x25A11BD3u);
        StoreCrc(emptyFooter, 60);
        var emptyLimits = new HddLimits(76, 0, 12, 176);
        var view = Value(HddCatalogCodec.InspectHddFooter(emptyFooter, 76, emptyLimits));
        Assert.Equal(0u, view.Entries);
        Assert.Equal(0UL, view.ArenaBytes);
        Assert.Equal(12u, view.IndexBytes);
        Assert.Equal(12UL, view.IndexEnd);
        var empty = Value(HddCatalogCodec.DecodeHdd(emptyFooter, idx12, 76, emptyLimits));
        Assert.Empty(empty.Entries);

        // Reordered writer rows: ordinals permute, entries keep row order.
        var reordered = (byte[])Index.Clone();
        var rowA = new byte[24];
        reordered.AsSpan(8, 24).CopyTo(rowA.AsSpan());
        reordered.AsSpan(32, 24).CopyTo(reordered.AsSpan(8, 24));
        rowA.CopyTo(reordered.AsSpan(32, 24));
        StoreCrc(reordered, 56);
        Assert.Equal(0x8A765D6Fu, StoredCrc(reordered));
        var reorderedFooter = (byte[])Footer.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(reorderedFooter.AsSpan(52), 0x8A765D6Fu);
        StoreCrc(reorderedFooter, 60);
        var descriptor = Value(HddCatalogCodec.DecodeHdd(reorderedFooter, reordered, 16_777_340, ExactLimits));
        Assert.Equal("VOL2.DSK", descriptor.Entries[0].Name);
        Assert.Equal("VOL1.DSK", descriptor.Entries[1].Name);
        Assert.Equal(new uint[] { 1u, 0u },
            new[] { descriptor.Entries[0].SlotOrdinal.Value, descriptor.Entries[1].SlotOrdinal.Value });
    }

    [Fact]
    public void TapeCrossCheckMatchesCorpusParseRecord()
    {
        var limits = new TapeLimits();
        var a = Value(codec.ParseRecord(FixtureA(), 1, limits));
        var aShared = Value(HddCatalogCodec.DecodeTapeIndex(A, 0, limits));
        Assert.Equal(new TapeCatalogEntry("VOL1.DSK", false, 16_384, 0, 0xAB54D286), Assert.Single(a.Entries));
        // xunit 2.9.3 Assert.Equal mis-compares equal non-empty ImmutableArray<T> values as "Collections differ"; arrays force element-wise compare.
        Assert.Equal(a.Entries.ToArray(), aShared.ToArray());
        var b = Value(codec.ParseRecord(FixtureBoth(), 4, limits));
        var bShared = Value(HddCatalogCodec.DecodeTapeIndex(B, 0, limits));
        Assert.Equal(new TapeCatalogEntry("VOL2.DSK", true, 16_384, 1, 0xAB54D286), Assert.Single(b.Entries));
        Assert.Equal(b.Entries.ToArray(), bShared.ToArray());

        Refused(HddCatalogCodec.DecodeTapeIndex(A, 1, limits), "Catalog side mismatch.");
        Refused(HddCatalogCodec.DecodeTapeIndex(A, 0, new TapeLimits { InputBytes = 35 }),
            "Record decoding budget exceeded.", TapeError.BoundsExceeded);
        Refused(HddCatalogCodec.DecodeTapeIndex(A, 0, new TapeLimits { DecodedBytes = 235 }),
            "Record decoding budget exceeded.", TapeError.BoundsExceeded);
        Refused(HddCatalogCodec.DecodeTapeIndex(A[..7], 0, limits), "Truncated catalog record.");
        Refused(HddCatalogCodec.DecodeTapeIndex(A[..20], 0, limits), "Invalid catalog record length.");
        var badMagic = (byte[])A.Clone(); badMagic[0] = (byte)'X';
        Refused(HddCatalogCodec.DecodeTapeIndex(badMagic, 0, limits), "Not a catalog.");
        var badVersion = (byte[])A.Clone(); badVersion[4] = 2;
        Refused(HddCatalogCodec.DecodeTapeIndex(badVersion, 0, limits),
            "Unsupported catalog version.", TapeError.UnsupportedVersion);
        var torn = (byte[])A.Clone(); torn[12] ^= 1;
        Refused(HddCatalogCodec.DecodeTapeIndex(torn, 0, limits), "Torn catalog CRC.");
        var spaces = (byte[])A.Clone();
        for (var i = 8; i < 19; i++) spaces[i] = 0x20;
        Refused(HddCatalogCodec.DecodeTapeIndex(Recrc(spaces), 0, limits), "Invalid catalog 8.3 name.");
        var badFlags = (byte[])A.Clone(); badFlags[19] = 0x02;
        Refused(HddCatalogCodec.DecodeTapeIndex(Recrc(badFlags), 0, limits), "Invalid catalog name/flags.");
    }
}
