using System.Security.Cryptography;
using Hc2000.App.Library;
using static Hc2000.App.Tests.DiskLibraryCatalogLiteralFixtures;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class DiskLibraryCatalogContractTests
{
    private const string CopyJson = """
        {"id":"22222222-2222-4222-8222-222222222222","entryId":"11111111-1111-4111-8111-111111111111","relativePath":"copies/22222222-2222-4222-8222-222222222222.dsk","originalSha256":"ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008","initialSha256":"ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008","length":778496}
        """;

    public static IEnumerable<object[]> InvalidInputs() => InvalidCatalogs().Select(c => new object[] { c.Name, c.Bytes });

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void CopiesPerOriginalBudgetIsEnforcedWithoutCreatingAnyCopies(int count, bool accepted)
    {
        var copies = Enumerable.Range(1, count).Select(i => CopyJson.Replace("22222222-2222-4222-8222-222222222222", $"{i:x8}-2222-4222-8222-222222222222", StringComparison.Ordinal));
        var json = CatalogJson().Replace("\"copies\":[]", "\"copies\":[" + string.Join(',', copies) + "]", StringComparison.Ordinal);
        if (accepted) Assert.Equal(16, DiskLibrarySchema.Load(Utf8(json)).Copies.Length);
        else Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Load(Utf8(json)));
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void TotalCopyBudgetDoesNotConfuseItWithPerOriginalBudget(int count, bool accepted)
    {
        var entries = Enumerable.Range(1, 65).Select(i => EntryJson.Replace(EntryId, $"{i:x8}-1111-4111-8111-111111111111", StringComparison.Ordinal));
        var copies = Enumerable.Range(0, count).Select(i => CopyJson
            .Replace("22222222-2222-4222-8222-222222222222", $"{i + 1:x8}-2222-4222-8222-222222222222", StringComparison.Ordinal)
            .Replace(EntryId, $"{i / 16 + 1:x8}-1111-4111-8111-111111111111", StringComparison.Ordinal));
        var json = CatalogJson(string.Join(',', entries)).Replace("\"copies\":[]", "\"copies\":[" + string.Join(',', copies) + "]", StringComparison.Ordinal);
        if (accepted) Assert.Equal(1024, DiskLibrarySchema.Load(Utf8(json)).Copies.Length);
        else Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Load(Utf8(json)));
    }

    [Theory]
    [InlineData("dangling")]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("wrong-id-path")]
    [InlineData("different-initial-hash")]
    [InlineData("different-original-hash")]
    [InlineData("different-length")]
    [InlineData("duplicate-key")]
    public void CopyReferencesAreClosedMetadataNotNewAuthority(string kind)
    {
        var copy = kind switch
        {
            "dangling" => CopyJson.Replace(EntryId, "33333333-3333-4333-8333-333333333333", StringComparison.Ordinal),
            "traversal" => CopyJson.Replace("copies/", "../", StringComparison.Ordinal),
            "absolute" => CopyJson.Replace("copies/", "/copies/", StringComparison.Ordinal),
            "wrong-id-path" => CopyJson.Replace("copies/22222222", "copies/33333333", StringComparison.Ordinal),
            "different-length" => CopyJson.Replace("778496", "778495", StringComparison.Ordinal),
            "duplicate-key" => CopyJson.Replace("\"length\":", "\"length\":1,\"length\":", StringComparison.Ordinal),
            "different-initial-hash" => CopyJson.Replace("\"initialSha256\":\"ff", "\"initialSha256\":\"aa", StringComparison.Ordinal),
            _ => CopyJson.Replace("\"originalSha256\":\"ff", "\"originalSha256\":\"aa", StringComparison.Ordinal)
        };
        var json = CatalogJson().Replace("\"copies\":[]", "\"copies\":[" + copy + "]", StringComparison.Ordinal);
        Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Load(Utf8(json)));
    }

    [Theory]
    [InlineData("Original CP/M", 256, true)]
    [InlineData("Original CP/M", 257, false)]
    [InlineData("/catalog-fixture/CPM22-HC.DSK", 4096, true)]
    [InlineData("/catalog-fixture/CPM22-HC.DSK", 4097, false)]
    [InlineData("repository disks/cpm/CPM22-HC.DSK", 2048, true)]
    [InlineData("repository disks/cpm/CPM22-HC.DSK", 2049, false)]
    [InlineData("independent attribution; not ownership permission", 2048, true)]
    [InlineData("independent attribution; not ownership permission", 2049, false)]
    public void LiteralStringBoundaryPairs(string original, int length, bool accepted)
    {
        var value = original.StartsWith('/') ? "/" + new string('x', length - 1) : new string('x', length);
        var json = Utf8(CatalogJson(EntryJson.Replace(original, value, StringComparison.Ordinal)));
        if (accepted) Assert.Single(DiskLibrarySchema.Load(json).Entries);
        else Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Load(json));
    }


    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void StrictLoaderRejectsWholeLiteralInput(string name, byte[] input)
    {
        Assert.False(string.IsNullOrEmpty(name));
        var before = input.ToArray();
        Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Load(input));
        Assert.Equal(before, input);
    }

    [Fact]
    public void MetadataLoadNeedsNoSourceAndRetainsLiteralObservations()
    {
        Assert.Empty(DiskLibrarySchema.Load(Utf8(EmptyJson)).Entries);
        var entry = Assert.Single(DiskLibrarySchema.Load(Utf8(CatalogJson())).Entries);
        Assert.Equal(EntryId, entry.Id);
        Assert.Equal("Original CP/M", entry.Name);
        Assert.Equal(778496, entry.Length);
        Assert.Equal(DiskLibraryContentState.Unknown, entry.Contents.State);
        Assert.Equal("not-scanned", entry.Contents.Reason);
        Assert.Empty(entry.Contents.Files);
        Assert.Null(entry.FileId);
        Assert.Equal("independent attribution; not ownership permission", entry.Provenance.Note);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4194304)]
    public void MetadataLengthBoundariesDoNotReadImagePayload(long length)
    {
        var document = DiskLibrarySchema.Load(Utf8(CatalogJson(EntryJson.Replace("778496", length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))));
        Assert.Equal(length, Assert.Single(document.Entries).Length);
    }

    [Fact]
    public void ExactEncodedByteBudgetAcceptedAndOneExtraByteRefused()
    {
        var bytes = Enumerable.Repeat((byte)' ', 8 * 1024 * 1024).ToArray();
        Utf8(EmptyJson).CopyTo(bytes, 0);
        Assert.Empty(DiskLibrarySchema.Load(bytes).Entries);
        Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Load([.. bytes, (byte)' ']));
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void EntryCountBudgetIsLiteralInputNotEncoderOracle(int count, bool accepted)
    {
        var json = CatalogJson(string.Join(',', Enumerable.Range(1, count).Select(i => EntryJson.Replace(EntryId, $"{i:x8}-1111-4111-8111-111111111111", StringComparison.Ordinal))));
        if (accepted) Assert.Equal(count, DiskLibrarySchema.Load(Utf8(json)).Entries.Length);
        else Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Load(Utf8(json)));
    }

    [Fact]
    public void SearchIsOrdinalCaseInsensitiveBoundedAndOrderedWithoutPayloadReads()
    {
        var entries = Enumerable.Range(1, 101).Reverse().Select(i => EntryJson.Replace(EntryId, $"{i:x8}-1111-4111-8111-111111111111", StringComparison.Ordinal));
        var document = DiskLibrarySchema.Load(Utf8(CatalogJson(string.Join(',', entries))));
        var result = DiskLibrarySchema.Search(document, "oRiGiNaL");
        Assert.Equal(100, result.Matches.Length);
        Assert.Equal(1, result.Remaining);
        Assert.Equal("00000001-1111-4111-8111-111111111111", result.Matches[0].Id);
        Assert.Equal("00000064-1111-4111-8111-111111111111", result.Matches[^1].Id);
        Assert.Equal(100, DiskLibrarySchema.Search(document, "ATTRIBUTION").Matches.Length);
        Assert.Equal(100, DiskLibrarySchema.Search(document, "ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008").Matches.Length);
        Assert.Empty(DiskLibrarySchema.Search(document, new string('x', 256)).Matches);
        Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Search(document, new string('x', 257)));
    }

    [Theory]
    [InlineData("records", 2147483647)]
    [InlineData("allocatedBytes", 2147483647)]
    public void DirectoryRowsCannotClaimMoreThanSupportedContainerCapacity(string field, int value)
    {
        var row = "{\"user\":0,\"name\":\"FILE.COM\",\"readOnly\":false,\"system\":false,\"records\":1,\"allocatedBytes\":1024,\"directoryIndex\":0}";
        row = field == "records" ? row.Replace("\"records\":1", "\"records\":" + value, StringComparison.Ordinal)
            : row.Replace("\"allocatedBytes\":1024", "\"allocatedBytes\":" + value, StringComparison.Ordinal);
        var entry = EntryJson.Replace("\"state\":\"unknown\"", "\"state\":\"directory-listed\"", StringComparison.Ordinal)
            .Replace("\"profile\":null", "\"profile\":\"cpm-hc40\"", StringComparison.Ordinal)
            .Replace("not-scanned", "listing-only", StringComparison.Ordinal)
            .Replace("\"files\":[]", "\"files\":[" + row + "]", StringComparison.Ordinal);
        Assert.Throws<DiskLibrarySchemaException>(() => DiskLibrarySchema.Load(Utf8(CatalogJson(entry))));
    }

    [Theory]
    [InlineData(false, DiskLibraryCatalogLiteralFixtures.StandardHash, DiskFormat.Standard)]
    [InlineData(true, DiskLibraryCatalogLiteralFixtures.ExtendedHash, DiskFormat.Extended)]
    public void LiteralContainersHavePinnedWholeFileDigestsAndNativeSectorObservations(
        bool extended, string digest, DiskFormat format)
    {
        var bytes = extended ? DiskLibraryCatalogLiteralFixtures.Extended() : DiskLibraryCatalogLiteralFixtures.Standard();
        var before = bytes.ToArray();
        Assert.Equal(768, bytes.Length);
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var disk = Dsk.Load(bytes);
        Assert.Equal(format, disk.Format);
        Assert.Equal(1, disk.Cylinders);
        Assert.Equal(1, disk.Sides);
        var sector = Assert.Single(Assert.Single(disk.Tracks).Sectors);
        Assert.Equal(1, sector.R);
        Assert.Equal(128, sector.Data.Length);
        Assert.All(sector.Data, b => Assert.Equal((byte)0xe5, b));
        Assert.Equal(before, bytes);
    }

    [Fact]
    public void LiteralUnformattedContainerIsNotAnEmptyCpmFilesystem()
    {
        var bytes = DiskLibraryCatalogLiteralFixtures.Unformatted();
        Assert.Equal(DiskLibraryCatalogLiteralFixtures.UnformattedHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var disk = Dsk.Load(bytes);
        Assert.Equal(DiskFormat.Extended, disk.Format);
        Assert.Empty(Assert.Single(disk.Tracks).Sectors);
        Assert.Throws<InvalidDataException>(() => disk.UniformGeometry());
    }
}
