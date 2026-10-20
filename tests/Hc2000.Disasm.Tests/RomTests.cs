using System.Security.Cryptography;
using Hc2000.Asm;
using Xunit;

namespace Hc2000.Disasm.Tests;

public sealed class RomTests
{
    [Theory]
    [InlineData("v1", "basic")]
    [InlineData("v1", "cpm")]
    [InlineData("v1", "if1")]
    [InlineData("v1", "if1_old-10k")]
    [InlineData("v2", "basic")]
    [InlineData("v2", "cpm")]
    [InlineData("v2", "if1")]
    public void ControlGeneratedSourceIsCurrentAndMatchesManifest(string version, string name)
    {
        var root = Root(); var stem = Path.Combine(root, "bios", "src", "hc2000", version, name);
        var rom = Path.Combine(root, "roms", "hc2000", version, name + ".rom");
        var bytes = File.ReadAllBytes(rom); var origin = version == "v2" && name == "cpm" ? 0xe000 : 0;
        var result = new Disassembler().Disassemble(bytes, origin, ControlFile.Parse(File.ReadAllText(stem + ".ctl")));
        Assert.True(result.Success, string.Join(';', result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(File.ReadAllText(stem + ".asm").Replace("\r", ""), result.Source.Replace("\r", ""));
        var asm = Assembler.AssembleText(result.Source);
        Assert.True(asm.Success, string.Join(';', asm.Diagnostics.Select(d => d.Message)));
        Assert.Equal(bytes, asm.ToBinary());
        var relative = Path.GetRelativePath(root, rom).Replace('\\', '/');
        var manifest = File.ReadLines(Path.Combine(root, "MANIFEST.sha256")).Single(l => l.EndsWith("  " + relative, StringComparison.Ordinal));
        Assert.Equal(manifest[..64], Convert.ToHexString(SHA256.HashData(asm.ToBinary())).ToLowerInvariant());
        Assert.Equal(bytes.Length, result.Statistics.CodeBytes + result.Statistics.DataBytes + result.Statistics.UnknownBytes);
    }

    [Theory]
    [InlineData("v1", "basic")]
    [InlineData("v1", "cpm")]
    [InlineData("v1", "if1")]
    [InlineData("v1", "if1_old-10k")]
    [InlineData("v2", "basic")]
    [InlineData("v2", "cpm")]
    [InlineData("v2", "if1")]
    public void EmptyControlAlsoRoundTripsButClaimsNoCode(string version, string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "roms", "hc2000", version, name + ".rom"));
        var origin = version == "v2" && name == "cpm" ? 0xe000 : 0;
        var result = new Disassembler().Disassemble(bytes, origin);
        Assert.True(result.Success); Assert.Equal(new DisasmStatistics(0, 0, bytes.Length), result.Statistics);
        var asm = Assembler.AssembleText(result.Source); Assert.True(asm.Success);
        Assert.Equal(bytes, asm.ToBinary());
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hc2000.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
