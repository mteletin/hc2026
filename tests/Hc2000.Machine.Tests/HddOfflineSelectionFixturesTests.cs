using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

// Independent data/wire literals from the accepted hdd-fixture-data/wire contract.
// No selector replacement: these helpers only manufacture caller inputs.
public sealed class HddOfflineSelectionFixturesTests
{
    private const int SlotBytes = 8_388_608;
    private const ulong FileBytes = 16_777_340;
    private const string P0Sha = "9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F";
    private const string P1Sha = "F484C7D32E06CBA31D0DD508A1EBC7C26CC4ECDC18A738E040CC3D6BEF7F1EC3";
    private const string StoreSha = "AF3406F99F322A33D6AE6A52A5ECB66BA90342A271582CDCD5020273BDB0B5E2";
    private static byte[] Index() => Convert.FromHexString(
        "4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce");
    private static byte[] Footer() => Convert.FromHexString(
        "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7");
    private static byte[] Payload(byte content)
    {
        var bytes = new byte[SlotBytes];
        bytes.AsSpan(0, 32768).Fill(0xe5);
        Convert.FromHexString("0053454544202020205458540000000108000000000000000000000000000000").CopyTo(bytes, 0);
        bytes.AsSpan(32768, 4096).Fill(content);
        return bytes;
    }

    private static HddContainerDescriptor Descriptor()
    {
        var result = HddCatalogCodec.DecodeHdd(Footer(), Index(), FileBytes, new HddLimits(16_777_340, 2, 60, 624));
        Assert.True(result.Success, result.Failure?.Detail);
        return result.Value!;
    }

    // Decode and token hash share these exact owned wire arrays, kept stable during Prepare.
    private static (HddContainerDescriptor Descriptor, HddStoreToken Store) PairedStore()
    {
        var index = Index();
        var footer = Footer();
        var decoded = HddCatalogCodec.DecodeHdd(footer, index, FileBytes, new HddLimits(16_777_340, 2, 60, 624));
        Assert.True(decoded.Success, decoded.Failure?.Detail);
        var hash = SHA256.HashData([.. index, .. footer]);
        Assert.Equal(StoreSha, Convert.ToHexString(hash));
        var descriptor = decoded.Value;
        return (descriptor, new HddStoreToken([.. descriptor.Footer.ContainerId], descriptor.Footer.Revision, [.. hash]));
    }

    private static HddSelectionLimits Limits() => new(FileBytes, 2, 60, 624,
        SlotBytes, 4UL * SlotBytes, 0, 3UL * SlotBytes + 65536, 58_786_416, 1, 1);
    private static HddSelectionUsage Usage() => new(1, 4UL * SlotBytes, 0, 65536);
    private static HddVolumeIdentity Expected(HddStoreToken store, string name = "VOL2.DSK", string sha = P1Sha)
        => new(store, name, [.. Convert.FromHexString(sha)]);

    private static HddSelectionResult Prepare(HddContainerDescriptor descriptor, HddStoreToken store,
        HddVolumeIdentity expected, byte[] image, bool hostReadOnly = false,
        HddSelectionLimits? limits = null, HddSelectionUsage? usage = null,
        ImmutableArray<HddMountedVolumeIdentity> mounted = default)
    {
        var imageSha = SHA256.HashData(image);
        var idBefore = (byte[])descriptor.Footer.ContainerId.Clone();
        var entriesBefore = descriptor.Entries;
        var result = HddVolumeSelection.Prepare(descriptor, store, expected, image, hostReadOnly,
            limits ?? Limits(), usage ?? Usage(), mounted.IsDefault ? [] : mounted);
        Assert.Equal(imageSha, SHA256.HashData(image));
        Assert.Equal(idBefore, descriptor.Footer.ContainerId);
        Assert.Equal(entriesBefore, descriptor.Entries);
        return result;
    }

    private static void Refused(HddSelectionResult result, HddSelectionError error)
    {
        Assert.False(result.Success);
        Assert.Equal(error, result.Error);
        Assert.Null(result.Selection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedVol2SelectsP1EvenWhenDescriptorRowsAreReversed(bool reverseRows)
    {
        var (descriptor, store) = PairedStore();
        if (reverseRows)
            descriptor = descriptor with { Entries = [descriptor.Entries[1], descriptor.Entries[0]] };
        var image = Payload(0x42);
        var result = Prepare(descriptor, store, Expected(store, "vol2.dsk"), image);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(HddSelectionError.None, result.Error);
        var selected = Assert.IsType<HddPreparedSelection>(result.Selection);
        Assert.Equal(new HddSlotOrdinal(1), selected.SlotOrdinal);
        Assert.Equal("VOL2.DSK", selected.Identity.Name);
        Assert.True(selected.WriteProtected);
        Assert.Equal(P1Sha, Convert.ToHexString(selected.BaselineSha256.AsSpan()));
        Assert.Equal(P1Sha, Convert.ToHexString(SHA256.HashData(selected.Image.AsSpan())));
        Assert.Equal(StoreSha, Convert.ToHexString(selected.Identity.Store.MetadataSha256.AsSpan()));
        Assert.Equal((byte)0x42, selected.Image[32768]);
        Assert.NotEqual(P0Sha, Convert.ToHexString(SHA256.HashData(selected.Image.AsSpan())));
        // Candidate owns bytes/ID after the call; input stability is not required forever.
        image[32768] = 0;
        descriptor.Footer.ContainerId[0] = 0;
        Assert.Equal((byte)0x42, selected.Image[32768]);
        Assert.Equal((byte)1, selected.Identity.Store.ContainerId[0]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void UnprotectedVol1ProtectionIsHostOrCatalog(bool hostReadOnly, bool protection)
    {
        var (descriptor, store) = PairedStore();
        var result = Prepare(descriptor, store, Expected(store, "VOL1.DSK", P0Sha), Payload(0x41), hostReadOnly);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(protection, result.Selection!.WriteProtected);
        Assert.Equal(new HddSlotOrdinal(0), result.Selection.SlotOrdinal);
        Assert.Equal(P0Sha, Convert.ToHexString(result.Selection.BaselineSha256.AsSpan()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ExactSizeRefusalsExposeNoCandidate(int delta)
    {
        var (descriptor, store) = PairedStore();
        Refused(Prepare(descriptor, store, Expected(store), new byte[SlotBytes + delta]), HddSelectionError.InvalidImageSize);
    }

    [Fact]
    public void CorruptPayloadAndWrongSlotAreCrcRefusals()
    {
        var (descriptor, store) = PairedStore();
        var corrupt = Payload(0x42);
        corrupt[32768] ^= 1;
        Refused(Prepare(descriptor, store, Expected(store), corrupt), HddSelectionError.PayloadCrcMismatch);
        Refused(Prepare(descriptor, store, Expected(store), Payload(0x41)), HddSelectionError.PayloadCrcMismatch);
    }

    [Fact]
    public void ValidCrcDoesNotReplaceExpectedPayloadSha()
    {
        var (descriptor, store) = PairedStore();
        Refused(Prepare(descriptor, store, Expected(store, sha: P0Sha), Payload(0x42)), HddSelectionError.PayloadHashMismatch);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("metadata")]
    public void EntireExpectedStoreTokenIsCompared(string field)
    {
        var (descriptor, store) = PairedStore();
        var wrong = field switch
        {
            "id" => store with { ContainerId = [.. Enumerable.Repeat((byte)0x22, 16)] },
            "revision" => store with { Revision = 2 },
            _ => store with { MetadataSha256 = [.. new byte[32]] }
        };
        Refused(Prepare(descriptor, store, Expected(wrong), Payload(0x42)), HddSelectionError.StaleIdentity);
    }

    [Theory]
    [InlineData("id-length")]
    [InlineData("zero-id")]
    [InlineData("zero-revision")]
    [InlineData("hash-length")]
    public void MalformedStoreIdentityRefusesWithoutCandidate(string field)
    {
        var (descriptor, store) = PairedStore();
        var wrong = field switch
        {
            "id-length" => store with { ContainerId = [1] },
            "zero-id" => store with { ContainerId = [.. new byte[16]] },
            "zero-revision" => store with { Revision = 0 },
            _ => store with { MetadataSha256 = [1] }
        };
        Refused(Prepare(descriptor, wrong, Expected(wrong), Payload(0x42)), HddSelectionError.InvalidIdentity);
    }

    [Fact]
    public void MissingVolumeAndMalformedExpectedHashRefuse()
    {
        var (descriptor, store) = PairedStore();
        Refused(Prepare(descriptor, store, Expected(store, "OTHER.DSK"), Payload(0x42)), HddSelectionError.VolumeNotFound);
        Refused(Prepare(descriptor, store, Expected(store) with { PayloadSha256 = [1] }, Payload(0x42)), HddSelectionError.InvalidIdentity);
        Refused(Prepare(descriptor, store, Expected(store, "BAD/NAME"), Payload(0x42)), HddSelectionError.InvalidIdentity);
    }

    [Fact]
    public void DuplicateStoreAndNameAliasRefusesEvenForProtectedVolume()
    {
        var (descriptor, store) = PairedStore();
        ImmutableArray<HddMountedVolumeIdentity> aliases = [new(store.ContainerId, "vol2.dsk")];
        Refused(Prepare(descriptor, store, Expected(store), Payload(0x42), mounted: aliases), HddSelectionError.AlreadyMounted);
        ImmutableArray<HddMountedVolumeIdentity> different = [new(store.ContainerId, "VOL1.DSK")];
        Assert.True(Prepare(descriptor, store, Expected(store), Payload(0x42), mounted: different).Success);
        different = [new([.. Enumerable.Repeat((byte)0x22, 16)], "VOL2.DSK")];
        Assert.True(Prepare(descriptor, store, Expected(store), Payload(0x42), mounted: different).Success);
        Assert.Equal("vol2.dsk", aliases[0].Name);
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("name")]
    [InlineData("size")]
    [InlineData("count")]
    [InlineData("id")]
    public void PublicDescriptorShapeMutantsRefuse(string field)
    {
        var (descriptor, store) = PairedStore();
        descriptor = field switch
        {
            "ordinal" => descriptor with { Entries = [descriptor.Entries[0], descriptor.Entries[1] with { SlotOrdinal = new(0) }] },
            "name" => descriptor with { Entries = [descriptor.Entries[0], descriptor.Entries[1] with { Name = "VOL1.DSK" }] },
            "size" => descriptor with { Entries = [descriptor.Entries[0], descriptor.Entries[1] with { ImageBytes = 4096 }] },
            "count" => descriptor with { Footer = descriptor.Footer with { Entries = 1 } },
            _ => descriptor with { Footer = descriptor.Footer with { ContainerId = [1] } }
        };
        Refused(Prepare(descriptor, store, Expected(store), Payload(0x42)), HddSelectionError.InvalidDescriptor);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("entries")]
    [InlineData("index")]
    [InlineData("metadata")]
    [InlineData("selected")]
    [InlineData("resident")]
    [InlineData("snapshot")]
    [InlineData("copy")]
    [InlineData("total")]
    public void FiniteBudgetsAdmitExactAndRefuseOneByteOrEntryLess(string field)
    {
        var (descriptor, store) = PairedStore();
        var limits = Limits();
        var usage = Usage();
        if (field == "snapshot")
        {
            usage = usage with { SnapshotBytes = 1 };
            limits = limits with { MaxSnapshotBytes = 1, MaxTotalLiveBytes = limits.MaxTotalLiveBytes + 1 };
        }
        Assert.True(Prepare(descriptor, store, Expected(store), Payload(0x42), limits: limits, usage: usage).Success);
        limits = field switch
        {
            "file" => limits with { MaxFileBytes = FileBytes - 1 },
            "entries" => limits with { MaxEntries = 1 },
            "index" => limits with { MaxIndexBytes = 59 },
            "metadata" => limits with { MaxMetadataBytes = 623 },
            "selected" => limits with { MaxSelectedBytes = SlotBytes - 1 },
            "resident" => limits with { MaxResidentBytes = 4UL * SlotBytes - 1 },
            "snapshot" => limits with { MaxSnapshotBytes = 0 },
            "copy" => limits with { MaxCopyWorkBytes = 3UL * SlotBytes + 65535 },
            _ => limits with { MaxTotalLiveBytes = 58_786_415 }
        };
        Refused(Prepare(descriptor, store, Expected(store), Payload(0x42), limits: limits, usage: usage), HddSelectionError.ResourceLimit);
    }

    [Fact]
    public void FourConcurrentCandidatesRequirePinned134283888Total()
    {
        var (descriptor, store) = PairedStore();
        var usage = Usage() with { ConcurrentSelectedImages = 4 };
        var limits = Limits() with
        {
            MaxSelectedBytes = 4UL * SlotBytes,
            MaxCopyWorkBytes = 12UL * SlotBytes + 65536,
            MaxTotalLiveBytes = 134_283_888
        };
        Assert.True(Prepare(descriptor, store, Expected(store), Payload(0x42), limits: limits, usage: usage).Success);
        Refused(Prepare(descriptor, store, Expected(store), Payload(0x42),
            limits: limits with { MaxTotalLiveBytes = 134_283_887 }, usage: usage), HddSelectionError.ResourceLimit);
    }

    [Theory]
    [InlineData("zero-k")]
    [InlineData("five-k")]
    [InlineData("q")]
    [InlineData("resident")]
    [InlineData("overflow")]
    public void InvalidAccountingUsageRefuses(string field)
    {
        var (descriptor, store) = PairedStore();
        var usage = field switch
        {
            "zero-k" => Usage() with { ConcurrentSelectedImages = 0 },
            "five-k" => Usage() with { ConcurrentSelectedImages = 5 },
            "q" => Usage() with { TransferBufferBytes = 65537 },
            "resident" => Usage() with { ResidentBytes = 4UL * SlotBytes + 1 },
            _ => Usage() with { SnapshotBytes = ulong.MaxValue }
        };
        Refused(Prepare(descriptor, store, Expected(store), Payload(0x42), usage: usage), HddSelectionError.ResourceLimit);
    }

    [Theory]
    [InlineData((byte)0x41, 0x0362A35Bu, P0Sha)]
    [InlineData((byte)0x42, 0x25CFECB9u, P1Sha)]
    public void LiteralPayloadsHavePinnedBytesCrcAndSha(byte content, uint crc, string sha)
    {
        var bytes = Payload(content);
        Assert.Equal(SlotBytes, bytes.Length);
        Assert.Equal(crc, VolumeCatalogMetadata.Crc32(bytes));
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.All(bytes[32..32768], b => Assert.Equal((byte)0xe5, b));
        Assert.All(bytes[32768..36864], b => Assert.Equal(content, b));
        Assert.Equal((byte)0, bytes[36864]);
        Assert.Equal((byte)0, bytes[^1]);
    }

    [Fact]
    public void LiteralStoreNamesP1AsProtectedVol2WithoutSelectingIt()
    {
        var descriptor = Descriptor();
        Assert.Equal(new HddCatalogEntry("VOL1.DSK", false, SlotBytes, new HddSlotOrdinal(0), 0x0362A35B), descriptor.Entries[0]);
        Assert.Equal(new HddCatalogEntry("VOL2.DSK", true, SlotBytes, new HddSlotOrdinal(1), 0x25CFECB9), descriptor.Entries[1]);
        Assert.Equal(Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"), descriptor.Footer.ContainerId);
        Assert.Equal(1UL, descriptor.Footer.Revision);
        Assert.Equal(StoreSha, Convert.ToHexString(SHA256.HashData([.. Index(), .. Footer()])));
    }

    [Fact]
    public void EachFixtureConstructionOwnsFreshCallerBytes()
    {
        var first = Payload(0x42);
        var second = Payload(0x42);
        first[32768] ^= 1;
        Assert.Equal(P1Sha, Convert.ToHexString(SHA256.HashData(second)));
        Assert.NotEqual(P1Sha, Convert.ToHexString(SHA256.HashData(first)));
        var index = Index();
        var footer = Footer();
        index[0] = 0;
        footer[0] = 0;
        Assert.Equal((byte)0x48, Index()[0]);
        Assert.Equal((byte)0x48, Footer()[0]);
    }
}
