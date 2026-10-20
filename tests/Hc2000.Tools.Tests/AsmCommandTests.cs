using System.Security.Cryptography;
using Hc2000.Tools;
using Xunit;

namespace Hc2000.Tools.Tests;

public sealed class AsmCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hccli-" + Guid.NewGuid().ToString("N"));
    public AsmCommandTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string Source(string text)
    {
        var path = Path.Combine(directory, "test.asm"); File.WriteAllText(path, text); return path;
    }

    [Theory]
    [InlineData("v1", "basic")]
    [InlineData("v1", "cpm")]
    [InlineData("v1", "if1")]
    [InlineData("v1", "if1_old-10k")]
    [InlineData("v2", "basic")]
    [InlineData("v2", "cpm")]
    [InlineData("v2", "if1")]
    public void GeneratedRomSourceThroughCliMatchesManifest(string version, string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Hc2000.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var source = Path.Combine(root.FullName, "bios", "src", "hc2000", version, name + ".asm");
        var relative = $"roms/hc2000/{version}/{name}.rom";
        var output = Path.Combine(directory, "out.bin");
        var error = new StringWriter();
        Assert.Equal(0, AsmCommand.Run([source, "-o", output], error));
        Assert.Equal("", error.ToString());
        var bytes = File.ReadAllBytes(output);
        Assert.Equal(File.ReadAllBytes(Path.Combine(root.FullName, relative)), bytes);
        var manifest = File.ReadLines(Path.Combine(root.FullName, "MANIFEST.sha256"))
            .Single(line => line.EndsWith("  " + relative, StringComparison.Ordinal));
        Assert.Equal(manifest[..64], Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void WritesBinaryListingSymbolsWithDefinesAndFill()
    {
        var source = Source("ORG 16\nlabel: DB mode\nORG 18\n DB 2");
        var output = Path.Combine(directory, "out.bin");
        var error = new StringWriter();
        Assert.Equal(0, AsmCommand.Run([source, "-o", output, "-D", "mode=3", "--fill", "0xAA", "--lst", "--sym"], error));
        Assert.Equal("", error.ToString());
        Assert.Equal(new byte[] { 3, 170, 2 }, File.ReadAllBytes(output));
        Assert.Contains("label = 0x0010", File.ReadAllText(Path.ChangeExtension(output, ".sym")));
        Assert.Contains("0010 03", File.ReadAllText(Path.ChangeExtension(output, ".lst")));
    }

    [Fact]
    public void WritesIntelHexAndCompactDefine()
    {
        var source = Source(" DB N"); var output = Path.Combine(directory, "out.hex");
        Assert.Equal(0, AsmCommand.Run([source, "-o", output, "--hex", "-DN=5"], new StringWriter()));
        Assert.Equal(":0100000005FA\n:00000001FF\n", File.ReadAllText(output).Replace("\r", ""));
    }

    [Fact]
    public void SourceErrorsDoNotOverwriteAnyOutput()
    {
        var source = Source("OUTPUT \"directive.bin\"\n DB missing");
        var output = Path.Combine(directory, "out.bin"); File.WriteAllBytes(output, [9]);
        var error = new StringWriter();
        Assert.Equal(1, AsmCommand.Run([source, "-o", output], error));
        Assert.Contains("(2,2): error: Undefined label", error.ToString());
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(output));
        Assert.False(File.Exists(Path.Combine(directory, "directive.bin")));
    }

    [Fact]
    public void HonorsOutputAndSavebinWithoutExplicitOutput()
    {
        var source = Source("OUTPUT \"directive.bin\"\nORG 16\n DB 1,2,3\nSAVEBIN \"piece.bin\",17,1");
        Assert.Equal(0, AsmCommand.Run([source, "--lst"], new StringWriter()));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "directive.bin")));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(directory, "piece.bin")));
        Assert.True(File.Exists(Path.Combine(directory, "directive.lst")));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("-o")]
    [InlineData("--fill", "256")]
    [InlineData("--fill", "unknown")]
    [InlineData("-D", "bad")]
    [InlineData("-D", "2bad=1")]
    public void BadOptionsReturnError(params string[] options)
    {
        var source = Source(" DB 1"); var error = new StringWriter();
        Assert.Equal(1, AsmCommand.Run(new[] { source }.Concat(options).ToArray(), error));
        Assert.Contains("hc asm:", error.ToString());
    }

    [Fact]
    public void MissingSourceOrOutputReturnsError()
    {
        Assert.Equal(1, AsmCommand.Run([], new StringWriter()));
        Assert.Equal(1, AsmCommand.Run([Source(" DB 1")], new StringWriter()));
    }
}
