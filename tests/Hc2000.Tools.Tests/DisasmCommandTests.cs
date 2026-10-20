using System.Security.Cryptography;
using Hc2000.Tools;
using Xunit;

namespace Hc2000.Tools.Tests;

public sealed class DisasmCommandTests
{
    [Fact]
    public void ExecutionMapCliSelectsConfigWritesXrefAndDoesNotOverwriteOnDiagnostics()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hcdisasm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var binary = Path.Combine(dir, "test.bin"); var map = Path.Combine(dir, "map.hcm.txt");
            var source = Path.Combine(dir, "test.asm"); var xref = Path.Combine(dir, "test.xref");
            byte[] bytes = [0xc9]; File.WriteAllBytes(binary, bytes);
            File.WriteAllText(map, $"hcmap 1\nimage 00000001 {Convert.ToHexString(SHA256.HashData(bytes))}\nprofile test {new string('0', 64)}\nconfig 7e-0f-cpm-1-shadow-0\nwindow E000 00000000 00000001\nmark E000-E000 11\nend\n");
            var error = new StringWriter();
            Assert.Equal(0, DisasmCommand.Run([binary, "--org", "E000", "--map", map, "--map-config", "7e-0f-cpm-1-shadow-0", "-o", source, "--xref", xref], error));
            Assert.Contains("RET", File.ReadAllText(source)); Assert.True(File.Exists(xref));
            Assert.Contains("code 1, data 0, unknown 0", error.ToString());
            File.WriteAllText(source, "sentinel"); File.WriteAllText(xref, "sentinel");
            Assert.Equal(1, DisasmCommand.Run([binary, "--org", "E000", "--map", map, "--map-config", "unknown", "-o", source, "--xref", xref], new StringWriter()));
            Assert.Equal("sentinel", File.ReadAllText(source)); Assert.Equal("sentinel", File.ReadAllText(xref));
            Assert.Equal(1, DisasmCommand.Run([binary, "--map-config", "unknown", "-o", source], new StringWriter()));
            Assert.Equal("sentinel", File.ReadAllText(source));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("--org")]
    [InlineData("--org", "10000")]
    [InlineData("--map")]
    [InlineData("--unknown")]
    [InlineData("--xref")]
    [InlineData("-o")]
    public void MalformedOptionsReturnErrorsWithoutThrowing(params string[] options)
        => Assert.Equal(1, DisasmCommand.Run(options, new StringWriter()));

    [Fact]
    public void DispatchGeneratesSourceThatExistingAsmCliReassembles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hcdisasm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var binary = Path.Combine(dir, "test.bin");
            var ctl = Path.Combine(dir, "test.ctl");
            var source = Path.Combine(dir, "test.asm");
            var output = Path.Combine(dir, "out.bin");
            File.WriteAllBytes(binary, [0x3e, 0x42, 0xc9, 0x55]);
            File.WriteAllText(ctl, "entry E000\nlabel E000 Start");
            Assert.Equal(0, Program.Dispatch("disasm", [binary, "--org", "E000", "--ctl", ctl, "-o", source]));
            Assert.Contains("Start:", File.ReadAllText(source));
            Assert.Contains("LD A,0x42", File.ReadAllText(source));
            Assert.Equal(0, AsmCommand.Run([source, "-o", output], new StringWriter()));
            Assert.Equal(File.ReadAllBytes(binary), File.ReadAllBytes(output));
        }
        finally { Directory.Delete(dir, true); }
    }
}
