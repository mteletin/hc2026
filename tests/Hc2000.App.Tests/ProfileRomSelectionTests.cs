using System.Security.Cryptography;
using Hc2000.App.Media;
using Hc2000.Machine;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class ProfileRomSelectionTests
{
    [Theory]
    [InlineData("HC2000-V1")]
    [InlineData("hc2000-v3")]
    [InlineData("../hc2000-v1")]
    [InlineData("")]
    public async Task UnknownOrMismatchedProfileIdRejectsBeforeFileIo(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ProfileRomSelection.SelectAsync(id, ""));
    }

    [Fact]
    public async Task V2MissingLowerHalfRejectsWithoutPaddingOrShiftEvenWhenFilesExist()
    {
        var profile = ProfileRomSelection.Describe("hc2000-v2");
        var upper = Assert.Single(profile.RomRegions, r => r.Role == ProfileRomRole.CpmUpper);
        Assert.Equal("roms/hc2000/v2/cpm-hc2kzip.bin", upper.SourcePath);
        Assert.Equal(0x6000, upper.StoreOffset);
        Assert.Equal(0x2000, upper.Length);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProfileRomSelection.SelectAsync("hc2000-v2", RomSelectionFixture.RepositoryRoot()));
        Assert.Contains("0x4000..0x5fff", error.Message);
        Assert.Contains("no padding/substitution", error.Message);
    }

    [Fact]
    public async Task EmptyAssetRootRejectsRatherThanImplicitlySearchingWorkingDirectory()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ProfileRomSelection.SelectAsync("hc2000-v1", ""));
    }

    [Fact]
    public async Task AbsentV1FilesRejectWithNamedCanonicalRegion()
    {
        using var fixture = new RomSelectionFixture();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProfileRomSelection.SelectAsync("hc2000-v1", fixture.Root));
        Assert.Contains("ROM unavailable: roms/hc2000/v1/basic.rom", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)] // pointer-sized file, never interpreted as ROM bytes
    [InlineData(16383)]
    [InlineData(16385)]
    public async Task IncorrectSizeRejectsWithoutReadingAnUnboundedAsset(int length)
    {
        using var fixture = new RomSelectionFixture();
        await File.WriteAllBytesAsync(fixture.V1Path("basic.rom"), new byte[length]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProfileRomSelection.SelectAsync("hc2000-v1", fixture.Root));
        Assert.Contains("size mismatch: roms/hc2000/v1/basic.rom", error.Message);
    }

    [Fact]
    public async Task SameSizeWrongBytesAndUserSuppliedManifestCannotRedefineIdentity()
    {
        using var fixture = new RomSelectionFixture();
        var fake = new byte[0x4000];
        await File.WriteAllBytesAsync(fixture.V1Path("basic.rom"), fake);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "MANIFEST.sha256"),
            $"{Convert.ToHexString(SHA256.HashData(fake)).ToLowerInvariant()}  roms/hc2000/v1/basic.rom\n");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProfileRomSelection.SelectAsync("hc2000-v1", fixture.Root));
        Assert.Contains("ROM identity mismatch: roms/hc2000/v1/basic.rom", error.Message);
    }

    [V1RomFact]
    public async Task CanonicalV1SelectsAllNamedRegionsWithManifestHashesAndOwnsDetachedBytes()
    {
        using var fixture = new RomSelectionFixture(copyV1: true);
        var set = await ProfileRomSelection.SelectAsync("hc2000-v1", fixture.Root);
        Assert.Equal("hc2000-v1", set.ProfileId);
        Assert.Equal(fixture.Root, set.AssetRoot);
        Assert.Equal(new[] { ProfileRomRole.Basic, ProfileRomRole.Cpm, ProfileRomRole.If1 }, set.Regions.Select(r => r.Region.Role));
        Assert.Equal(new[] { 0, 0x4000, 0 }, set.Regions.Select(r => r.Region.StoreOffset));
        Assert.Equal(new[] { ProfileRomStore.MainRom, ProfileRomStore.MainRom, ProfileRomStore.ShadowRom }, set.Regions.Select(r => r.Region.Store));
        var manifest = File.ReadAllLines(Path.Combine(RomSelectionFixture.RepositoryRoot(), "MANIFEST.sha256"));
        foreach (var rom in set.Regions)
        {
            Assert.Equal(0x4000, rom.Bytes.Length);
            Assert.Contains($"{rom.Sha256}  {rom.Region.SourcePath}", manifest);
            Assert.Equal(rom.Sha256, Convert.ToHexString(SHA256.HashData(rom.Bytes.AsSpan())).ToLowerInvariant());
        }
        await File.WriteAllBytesAsync(fixture.V1Path("basic.rom"), new byte[0x4000]);
        Assert.Equal(set.Regions[0].Sha256, Convert.ToHexString(SHA256.HashData(set.Regions[0].Bytes.AsSpan())).ToLowerInvariant());
    }

    [V1RomFact]
    public async Task V1RejectsV2BasicSameSizeSubstitution()
    {
        using var fixture = new RomSelectionFixture(copyV1: true);
        File.Copy(Path.Combine(RomSelectionFixture.RepositoryRoot(), "roms/hc2000/v2/basic.rom"), fixture.V1Path("basic.rom"), overwrite: true);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProfileRomSelection.SelectAsync("hc2000-v1", fixture.Root));
        Assert.Contains("identity mismatch", error.Message);
    }
}

internal sealed class RomSelectionFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc2000-rom-ui-" + Guid.NewGuid().ToString("N"));
    public RomSelectionFixture(bool copyV1 = false)
    {
        Directory.CreateDirectory(Path.Combine(Root, "roms/hc2000/v1"));
        if (copyV1)
            foreach (var file in new[] { "basic.rom", "cpm.rom", "if1.rom" })
                File.Copy(Path.Combine(RepositoryRoot(), "roms/hc2000/v1", file), V1Path(file));
    }
    public string V1Path(string file) => Path.Combine(Root, "roms/hc2000/v1", file);
    public void Dispose() => Directory.Delete(Root, recursive: true);
    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Hc2000.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository fixture root not found.");
    }
    public static string? MissingAssets()
    {
        foreach (var path in new[] { "v1/basic.rom", "v1/cpm.rom", "v1/if1.rom", "v2/basic.rom" })
        {
            var full = Path.Combine(RepositoryRoot(), "roms/hc2000", path);
            if (!File.Exists(full) || new FileInfo(full).Length != 0x4000)
                return "Canonical ROM absent or not LFS-materialized: " + path;
        }
        return null;
    }
}

public sealed class V1RomFactAttribute : FactAttribute
{
    public V1RomFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = RomSelectionFixture.MissingAssets();
}
