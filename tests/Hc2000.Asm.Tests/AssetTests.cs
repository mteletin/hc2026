using System.Security.Cryptography;
using Xunit;

namespace Hc2000.Asm.Tests;

public class AssetTests
{
    [Theory]
    [InlineData("instructions")]
    [InlineData("directives")]
    public void PasmoCompatibilityCorpus(string name)
    {
        var root = RepositoryRoot();
        var source = Path.Combine(root, "tests", "assets", "asm", "pasmo", name + ".asm");
        var result = Assembler.Assemble(source);
        Assert.True(result.Success, string.Join(';', result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(source, ".bin")), result.ToBinary());
    }

    // wi-113's control-file-generated sources are now committed acceptance fixtures.
    // Missing fixtures must fail, not silently turn the final ROM gate into a skip.
    [Theory]
    [InlineData("v1", "basic")]
    [InlineData("v1", "cpm")]
    [InlineData("v1", "if1")]
    [InlineData("v1", "if1_old-10k")]
    [InlineData("v2", "basic")]
    [InlineData("v2", "cpm")]
    [InlineData("v2", "if1")]
    public void GeneratedRomSourceMatchesManifest(string version, string name)
    {
        var root = RepositoryRoot();
        var rom = Path.Combine(root, "roms", "hc2000", version, name + ".rom");
        var source = Path.Combine(root, "bios", "src", "hc2000", version, name + ".asm");
        var result = Assembler.Assemble(source);
        Assert.True(result.Success, string.Join(';', result.Diagnostics.Select(d => d.Message)));
        var actual = result.ToBinary();
        Assert.Equal(File.ReadAllBytes(rom), actual);
        // Baseline assets use MANIFEST.sha256, rather than per-ROM sidecars.
        var relative = Path.GetRelativePath(root, rom).Replace('\\', '/');
        var manifest = File.ReadLines(Path.Combine(root, "MANIFEST.sha256"))
            .Single(line => line.Length > 66 && line[66..] == relative);
        Assert.Equal(manifest[..64], Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant());
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Hc2000.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
