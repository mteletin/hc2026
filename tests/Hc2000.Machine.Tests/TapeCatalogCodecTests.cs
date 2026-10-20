using System.Buffers.Binary;
using System.Collections.Immutable;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Source-first independent spec13 fixtures. No guest, assets, database or directory binding.</summary>
public sealed class TapeCatalogCodecTests
{
    private const string AHex = "4843415401000100564f4c312020202044534b00004000000000000086d254abb0d45c2b";
    private const string BHex = "4843415401000100564f4c322020202044534b01004000000100000086d254ab488f7e41";
    private static readonly TapeLimits Limits = new();
    private readonly TapeCatalogCodec codec = new();
    private static ImmutableArray<byte> Bytes(byte[] bytes) => ImmutableArray.CreateRange(bytes);
    // Independent TEST-only IEEE reference. Production encoding never supplies the fixture oracle.
    private static uint ReferenceCrc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (var b in bytes)
        {
            value ^= b;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~value;
    }
    private static byte[] Raw(byte[] content, byte flag = 0xff)
    {
        var raw = new byte[content.Length + 2]; raw[0] = flag; content.CopyTo(raw, 1);
        foreach (var b in raw.AsSpan(0, raw.Length - 1)) raw[^1] ^= b;
        return raw;
    }
    private static TapeCatalogSide Side(params byte[][] raw) => new(TapeSide.A,
        raw.Select(Bytes).ToImmutableArray());
    private static byte[] A => Convert.FromHexString(AHex);
    private static byte[] B => Convert.FromHexString(BHex);
    private static TapeCatalogSide FixtureA() => Side(Raw(new byte[16384]), Raw(A[..20]), Raw(A[20..]));
    private static TapeCatalogSide FixtureBoth() => Side(Raw(new byte[16384]), Raw(new byte[16384]), Raw(A[..20]), Raw(A[20..]), Raw(B));
    private static T Value<T>(TapeResult<T> result) where T : notnull { Assert.True(result.Success, result.Failure?.Detail); return result.Value; }
    private static void Refused<T>(TapeResult<T> result, TapeError? code = null) where T : notnull
    {
        Assert.False(result.Success); Assert.NotNull(result.Failure);
        if (code is not null) Assert.Equal(code, result.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => { _ = result.Value; });
    }
    private static byte[] Recrc(byte[] record)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(record.Length - 4), ReferenceCrc(record.AsSpan(0, record.Length - 4)));
        return record;
    }
    private static byte[] ChangeField(int offset, byte value)
    {
        var record = A; record[offset] = value; return Recrc(record);
    }
    private static string Fingerprint(TapeCatalogSide side) => string.Join("/", side.RawBlocks.Select(b => Convert.ToHexString(b.AsSpan())));

    [Fact]
    public void PinnedAAndBDecodeAndEncodeExactly()
    {
        Assert.Equal(0xAB54D286u, ReferenceCrc(new byte[16384]));
        Assert.Equal(0x2B5CD4B0u, ReferenceCrc(A.AsSpan(0, 32)));
        Assert.Equal(0x417E8F48u, ReferenceCrc(B.AsSpan(0, 32)));
        var a = Value(codec.ParseRecord(FixtureA(), 1, Limits));
        Assert.Equal(0x2B5CD4B0u, a.Crc); Assert.Equal(1, a.StartBlock); Assert.Equal(2, a.EndBlock);
        Assert.Equal(TapeSide.A, a.Side);
        Assert.Equal(new TapeCatalogEntry("VOL1.DSK", false, 16384, 0, 0xAB54D286), Assert.Single(a.Entries));
        Assert.Equal(A, Value(codec.EncodeRecord(a.Side, a.Entries, Limits)).ToArray());
        var b = Value(codec.ParseRecord(FixtureBoth(), 4, Limits));
        Assert.Equal(0x417E8F48u, b.Crc);
        Assert.Equal(new TapeCatalogEntry("VOL2.DSK", true, 16384, 1, 0xAB54D286), Assert.Single(b.Entries));
        Assert.Equal(B, Value(codec.EncodeRecord(b.Side, b.Entries, Limits)).ToArray());
        Assert.Equal(16384, Value(codec.ReadImage(FixtureA(), a.Entries[0], Limits)).Length);
        Assert.Equal(3u, a.Entries[0].ImageBytes / 4096 - 1); // Not a directory EXM assertion.
    }

    [Fact]
    public void NewestValidIgnoresTornTruncatedAndTrailingCandidates()
    {
        var torn = A; torn[^1] = 0x2a;
        foreach (var invalid in new[] { new[] { Raw(torn[..20]), Raw(torn[20..]) },
            new[] { Raw(A[..20]) }, new[] { Raw(A[..32]) }, new[] { Raw([.. A, 0]) } })
        {
            var original = FixtureA(); var side = original with { RawBlocks = original.RawBlocks.AddRange(invalid.Select(Bytes)) };
            var before = Fingerprint(side);
            var newest = Value(codec.FindNewest(side, Limits));
            Assert.Equal(0x2B5CD4B0u, newest.Newest!.Crc); Assert.Equal(before, Fingerprint(side));
            Assert.Null(Value(codec.FindNewest(Side(invalid), Limits)).Newest);
        }
        var both = Value(codec.FindNewest(FixtureBoth(), Limits));
        Assert.Equal(0x417E8F48u, both.Newest!.Crc); Assert.Equal(0x2B5CD4B0u, Assert.Single(both.Older).Crc);
        var later = FixtureA() with { RawBlocks = FixtureA().RawBlocks.Add(Bytes(Raw(A[..20]))).Add(Bytes(Raw(B))) };
        Assert.Equal(0x417E8F48u, Value(codec.FindNewest(later, Limits)).Newest!.Crc);
    }

    [Fact]
    public void HeadersInterruptRecordsWithoutShiftingDataIndex()
    {
        var side = Side(Raw(new byte[17], 0), Raw(new byte[16384]), Raw(A[..20]), Raw(new byte[17], 0), Raw(A[20..]), Raw(A));
        Refused(codec.ParseRecord(side, 2, Limits));
        var record = Value(codec.FindNewest(side, Limits)).Newest!;
        Assert.Equal(5, record.StartBlock);
        Assert.Equal(16384, Value(codec.ReadImage(side, record.Entries[0], Limits)).Length);
        Assert.Equal(new TapeCatalogPosition(1, 0, 128), Value(codec.MapImageOffset(side, record.Entries[0], 128, Limits)));
        Refused(codec.ParseRecord(side, 0, Limits));
        Refused(codec.ParseRecord(side, -1, Limits));
    }

    [Fact]
    public void ImageBoundaryAndCrcFailuresReturnNoBytes()
    {
        foreach (var length in new[] { 16383, 16385 })
        {
            var side = Side(Raw(new byte[length]), Raw(A));
            var entry = Assert.Single(Value(codec.FindNewest(side, Limits)).Newest!.Entries);
            Refused(codec.ReadImage(side, entry, Limits));
        }
        var corrupt = new byte[16384]; corrupt[0] = 1;
        var bad = Side(Raw(corrupt), Raw(A)); var before = Fingerprint(bad);
        Refused(codec.ReadImage(bad, Value(codec.FindNewest(bad, Limits)).Newest!.Entries[0], Limits));
        Assert.Equal(before, Fingerprint(bad));
        var split = Side(Raw(new byte[8192]), Raw(new byte[8192]), Raw(A));
        var entry2 = Value(codec.ParseRecord(split, 2, Limits)).Entries[0];
        Assert.Equal(new TapeCatalogPosition(1, 1, 0), Value(codec.MapImageOffset(split, entry2, 8192, Limits)));
        Refused(codec.MapImageOffset(split, entry2, 16384, Limits));
        Refused(codec.ReadImage(Side(Raw(new byte[8192]), Raw(new byte[17], 0), Raw(new byte[8192])), entry2, Limits));
    }

    [Theory]
    [InlineData(4, 2)] [InlineData(5, 1)] [InlineData(19, 2)] [InlineData(8, 0)] [InlineData(20, 1)]
    public void IndependentlyCrcCorrectInvalidFieldsRefuse(int offset, byte value)
    {
        Refused(codec.ParseRecord(Side(Raw(new byte[16384]), Raw(ChangeField(offset, value))), 1, Limits));
    }

    [Fact]
    public void DuplicateNamesAndOverlappingImagesRefuse()
    {
        var first = new TapeCatalogEntry("ONE.DSK", false, 16384, 0, 0xAB54D286);
        Refused(codec.EncodeRecord(TapeSide.A, [first, first with { Name = "one.dsk", FirstBlock = 1 }], Limits));
        var bytes = new byte[60]; A.AsSpan(0, 8).CopyTo(bytes); bytes[6] = 2;
        A.AsSpan(8, 24).CopyTo(bytes.AsSpan(8)); A.AsSpan(8, 24).CopyTo(bytes.AsSpan(32));
        bytes[32 + 3] = (byte)'9'; Recrc(bytes);
        Refused(codec.ParseRecord(Side(Raw(new byte[16384]), Raw(bytes)), 1, Limits));
        bytes[32 + 3] = (byte)'1'; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 1); Recrc(bytes);
        Refused(codec.ParseRecord(Side(Raw(new byte[16384]), Raw(new byte[16384]), Raw(bytes)), 2, Limits));
    }

    [Fact]
    public void ImageSizesAndBudgetsAreBounded()
    {
        var entry = new TapeCatalogEntry("ONE.DSK", false, 16384, 0, 0xAB54D286);
        foreach (var size in new uint[] { 4096, 16383, 16385, 8388609 }) Refused(codec.EncodeRecord(TapeSide.A, [entry with { ImageBytes = size }], Limits));
        Assert.Equal(36, Value(codec.EncodeRecord(TapeSide.A, [entry with { ImageBytes = 8388608 }], Limits)).Length);
        foreach (var limits in new[] { Limits with { InputBytes = 1 }, Limits with { SourceBlocks = 0 }, Limits with { DecodedBytes = 7 }, Limits with { InputBytes = -1 } })
            Refused(codec.FindNewest(FixtureA(), limits), TapeError.BoundsExceeded);
        Refused(codec.EncodeRecord((TapeSide)2, [entry], Limits));
        Refused(codec.ParseRecord(Side(Raw([])), 0, Limits));
        var raw = Raw(A); raw[^1] ^= 1;
        Refused(codec.FindNewest(Side(raw), Limits));
    }

    [Fact]
    public void AppendRefusesStaleIdentityWriteProtectionAndCapacityWithoutMutation()
    {
        var side = FixtureA(); var before = Fingerprint(side);
        var identity = Value(codec.FindNewest(side, Limits)).Newest!.Identity;
        var mutation = new TapeCatalogMutation("VOL1.DSK", Bytes(new byte[16384]));
        var budget = new TapeCatalogAppendBudget(100000, 10, true);
        Refused(codec.PlanAppend(side, identity with { Crc = 1 }, mutation, budget, Limits), TapeError.Interlock);
        Refused(codec.PlanAppend(side with { WriteProtected = true }, identity, mutation, budget, Limits), TapeError.WriteProtected);
        foreach (var b in new[] { budget with { FramedBytes = 1 }, budget with { Blocks = 0 }, budget with { PhysicalAdmission = false } })
            Refused(codec.PlanAppend(side, identity, mutation, b, Limits), TapeError.CapacityExceeded);
        var readOnly = FixtureBoth(); var id2 = Value(codec.FindNewest(readOnly, Limits)).Newest!.Identity;
        Refused(codec.PlanAppend(readOnly, id2, mutation with { Name = "VOL2.DSK" }, budget, Limits), TapeError.WriteProtected);
        Assert.Equal(before, Fingerprint(side));
        var plan = Value(codec.PlanAppend(side, identity, mutation, budget, Limits));
        Assert.Equal(2, plan.AppendedBlocks.Length); Assert.Equal(identity, plan.Expected);
        Assert.Equal(3u, plan.Published.Entries[0].FirstBlock);
        Assert.Equal(before, Fingerprint(side));
        // Exact framing accounting: one image + one 36-byte record, each raw flag/XOR plus TAP word.
        Value(codec.PlanAppend(side, identity, mutation, budget with { FramedBytes = 16428 }, Limits));
        Refused(codec.PlanAppend(side, identity, mutation, budget with { FramedBytes = 16427 }, Limits));
    }

    [Fact]
    public void OmissionErasesOnlyInNewCatalog()
    {
        var side = FixtureA(); var current = Value(codec.FindNewest(side, Limits)).Newest!;
        var plan = Value(codec.PlanAppend(side, current.Identity, new("VOL1.DSK", default), new(100, 1, true), Limits));
        Assert.Empty(plan.Published.Entries);
        var next = side with { RawBlocks = side.RawBlocks.AddRange(plan.AppendedBlocks) };
        Assert.Empty(Value(codec.FindNewest(next, Limits)).Newest!.Entries);
        Assert.Single(Value(codec.FindNewest(next, Limits)).Older[0].Entries);
    }

    [Fact]
    public void PublicationRequiresNewestRecordAndMatchingImage()
    {
        var side = FixtureA(); var current = Value(codec.FindNewest(side, Limits)).Newest!;
        var plan = Value(codec.PlanAppend(side, current.Identity, new("VOL1.DSK", Bytes(new byte[16384])), new(100000, 10, true), Limits));
        Refused(codec.CheckPublication(side, plan, Limits));
        var interrupted = side with { RawBlocks = side.RawBlocks.Add(plan.AppendedBlocks[0]) };
        Refused(codec.CheckPublication(interrupted, plan, Limits));
        var published = side with { RawBlocks = side.RawBlocks.AddRange(plan.AppendedBlocks) };
        Assert.True(Value(codec.CheckPublication(published, plan, Limits)));
        var corrupt = published.RawBlocks[3].ToArray(); corrupt[1] ^= 1; corrupt[^1] ^= 1;
        Refused(codec.CheckPublication(published with { RawBlocks = published.RawBlocks.SetItem(3, Bytes(corrupt)) }, plan, Limits));
        var later = published with { RawBlocks = published.RawBlocks.Add(Bytes(Raw(B))) };
        Refused(codec.CheckPublication(later, plan, Limits));
    }

    [Fact]
    public void ExplicitRecoveryDoesNotReplaceNewestSelection()
    {
        var corrupt = new byte[16384]; corrupt[0] = 1;
        var newer = B; newer[11] = (byte)'1'; Recrc(newer);
        var side = Side(Raw(new byte[16384]), Raw(corrupt), Raw(A[..20]), Raw(A[20..]), Raw(newer));
        var selected = Value(codec.FindNewest(side, Limits));
        Refused(codec.ReadImage(side, selected.Newest!.Entries[0], Limits));
        Assert.Equal(0x2B5CD4B0u, Value(codec.FindRecovery(side, "VOL1.DSK", selected.Older, Limits)).Crc);
        Assert.Equal(selected.Newest.Identity, Value(codec.FindNewest(side, Limits)).Newest!.Identity);
        Refused(codec.FindRecovery(side, "MISSING.DSK", selected.Older, Limits));
    }

    [Fact]
    public void CandidateScanningAndNestedPlanningShareTheirBudget()
    {
        var blocks = Enumerable.Range(0, 100).Select(_ => Raw(new byte[24])).ToArray();
        Assert.Null(Value(codec.FindNewest(Side(blocks), Limits)).Newest);
        Refused(codec.FindNewest(Side(blocks), Limits with { DecodedBytes = 6500 }), TapeError.BoundsExceeded);
        var side = FixtureA(); var current = Value(codec.FindNewest(side, Limits)).Newest!;
        Refused(codec.PlanAppend(side, current.Identity, new("VOL1.DSK", Bytes(new byte[16384])),
            new(100000, 10, true), Limits with { DecodedBytes = 100000 }), TapeError.BoundsExceeded);
    }

    [Fact]
    public void CanonicalCatalogFieldsAndSharedImageStartsAreNotDistinctIdentities()
    {
        var entry = new TapeCatalogEntry("ONE", false, 16384, 0, 0xAB54D286);
        Refused(codec.EncodeRecord(TapeSide.A, [entry, entry with { Name = "ONE.", FirstBlock = 1 }], Limits));
        Refused(codec.EncodeRecord(TapeSide.A, [entry, entry with { Name = "TWO" }], Limits));
    }

    [Fact]
    public void TapAndStandardTzxPayloadViewsRoundtrip()
    {
        var fixture = FixtureA(); using var tapBytes = new MemoryStream(); using var tzxBytes = new MemoryStream();
        tzxBytes.Write("ZXTape!\x1a"u8); tzxBytes.WriteByte(1); tzxBytes.WriteByte(20);
        foreach (var raw in fixture.RawBlocks)
        {
            var length = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)raw.Length);
            tapBytes.Write(length); tapBytes.Write(raw.AsSpan());
            tzxBytes.WriteByte(0x10); tzxBytes.Write([0, 0]); tzxBytes.Write(length); tzxBytes.Write(raw.AsSpan());
        }
        var tap = Tap.Load(tapBytes.ToArray()); Assert.Equal(tapBytes.ToArray(), tap.Save());
        var tapView = Value(codec.FromTap(tapBytes.ToArray(), TapeSide.A, Limits));
        var document = Value(TzxDocument.Parse(tzxBytes.ToArray(), Limits));
        var tzxView = Value(codec.FromTzx(document, TapeSide.A, Limits));
        Assert.Equal(Fingerprint(fixture), Fingerprint(tapView)); Assert.Equal(Fingerprint(tapView), Fingerprint(tzxView));
        Assert.Equal(0x2B5CD4B0u, Value(codec.FindNewest(tzxView, Limits)).Newest!.Crc);
        Refused(codec.FromTzx(document with { Blocks = document.Blocks.SetItem(0, document.Blocks[0] with { Id = 0x11 }) }, TapeSide.A, Limits), TapeError.UnsupportedFormat);
        Refused(codec.FromTap(tapBytes.ToArray(), TapeSide.A, Limits with { InputBytes = 1 }), TapeError.BoundsExceeded);
    }

    [Fact]
    public void TapWireAdmissionReservesCopiesAndMetadataBeforeLoading()
    {
        byte[] wire = [2, 0, 0xff, 0xff]; // Independent raw-data TAP frame, no named file/header.
        var exact = Limits with { InputBytes = 4, DecodedBytes = 144, SourceBlocks = 1 };
        var view = Value(codec.FromTap(wire, TapeSide.A, exact));
        Assert.Equal("FFFF", Fingerprint(view));
        Assert.Equal(wire, Tap.Load(wire).Save());
        Refused(codec.FromTap(wire, TapeSide.A, exact with { InputBytes = 3 }), TapeError.BoundsExceeded);
        Refused(codec.FromTap(wire, TapeSide.A, exact with { DecodedBytes = 143 }), TapeError.BoundsExceeded);
        Refused(codec.FromTap(wire, TapeSide.A, exact with { SourceBlocks = 0 }), TapeError.BoundsExceeded);
        // Byte reservation beats parsing a corrupt wire; admitted native count beats malformed suffix.
        Refused(codec.FromTap([1], TapeSide.A, exact with { DecodedBytes = 3 }), TapeError.BoundsExceeded);
        Refused(codec.FromTap([1], TapeSide.A, exact with { DecodedBytes = 131 }), TapeError.BoundsExceeded);
        Assert.Equal(new byte[] { 2, 0, 0xff, 0xff }, wire);
    }

    [Theory]
    [InlineData("01")]
    [InlineData("0200FF")]
    [InlineData("010000")]
    [InlineData("0200FF00")]
    [InlineData("0200FFFF01")]
    public void TapWireImportForwardsNativeFailureWithoutPartialValue(string hex)
    {
        var wire = Convert.FromHexString(hex); var before = wire.ToArray();
        var limits = Limits with { InputBytes = wire.Length, DecodedBytes = 4 * wire.Length + 256, SourceBlocks = 2 };
        var native = Tap.LoadBounded(wire, limits.InputBytes, 2);
        Refused(native, TapeError.InvalidFormat);
        var result = codec.FromTap(wire, TapeSide.A, limits);
        Refused(result, TapeError.InvalidFormat);
        Assert.Equal(native.Failure, result.Failure);
        Assert.Equal(before, wire);
    }

    [Fact]
    public void TapWireImportHandlesZeroNegativeAndLargeFiniteBudgets()
    {
        var zero = Limits with { InputBytes = 0, DecodedBytes = 0, SourceBlocks = 0 };
        Assert.Empty(Value(codec.FromTap([], TapeSide.B, zero)).RawBlocks);
        Refused(codec.FromTap([], (TapeSide)2, zero), TapeError.InvalidFormat);
        foreach (var limits in new[] { zero with { InputBytes = -1 }, zero with { DecodedBytes = -1 }, zero with { SourceBlocks = -1 } })
            Refused(codec.FromTap([], TapeSide.A, limits), TapeError.BoundsExceeded);
        var large = Limits with { InputBytes = int.MaxValue, DecodedBytes = int.MaxValue, SourceBlocks = int.MaxValue };
        Assert.Equal("FFFF", Fingerprint(Value(codec.FromTap([2, 0, 0xff, 0xff], TapeSide.A, large))));
        Refused(codec.FromTap([1], TapeSide.A, large with { SourceBlocks = 0 }), TapeError.BoundsExceeded);
        // Native framing accepts other flags; the catalog payload view still refuses them.
        Refused(codec.FromTap([2, 0, 2, 2], TapeSide.A, large), TapeError.InvalidFormat);
    }
}
