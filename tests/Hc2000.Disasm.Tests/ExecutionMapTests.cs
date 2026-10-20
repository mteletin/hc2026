using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Hc2000.Asm;
using Xunit;

namespace Hc2000.Disasm.Tests;

public sealed class ExecutionMapTests
{
    private const string Config = "7e-0f-cpm-1-shadow-0";
    private const string Other = "7e-03-cpm-0-shadow-1";
    private static string Header(byte[] image) => $"hcmap 1\nimage {image.Length:X8} {Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant()}\nprofile test {new string('0', 64)}\n";
    private static string Block(string id, string windows, string marks = "") => $"config {id}\n{windows}\n{marks}\nend\n";
    private static ExecutionMap Text(string text) => ExecutionMap.Parse(Encoding.UTF8.GetBytes(text));
    private static byte[] Bitmap(string manifest, params byte[][] flags)
    {
        var text = Encoding.UTF8.GetBytes(manifest); var output = new byte[12 + text.Length + flags.Length * 65536];
        "HCMAPB1\0"u8.CopyTo(output); BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8, 4), (uint)text.Length);
        text.CopyTo(output, 12);
        for (var i = 0; i < flags.Length; i++) flags[i].CopyTo(output, 12 + text.Length + i * 65536);
        return output;
    }
    private static void RoundTrip(byte[] bytes, DisassemblyResult result, int origin = 0xe000)
    {
        var asm = Assembler.AssembleText(result.Source); Assert.True(asm.Success, string.Join(';', asm.Diagnostics.Select(d => d.Message)));
        Assert.Equal(bytes, asm.ToBinary(origin, bytes.Length));
    }

    [Fact]
    public void TextAndBitmapMatchWithOrRangesAndInclusiveFfff()
    {
        byte[] image = [0x00, 0xc9];
        var windows = "window FFFE 00000000 00000002";
        var text = Text(Header(image) + Block(Config, windows, "mark FFFE-FFFF 01\nmark FFFE-FFFE 10\nmark FFFF-FFFF 08"));
        var flags = new byte[65536]; flags[0xfffe] = 0x11; flags[0xffff] = 9;
        var binary = ExecutionMap.Parse(Bitmap(Header(image) + Block(Config, windows), flags));
        Assert.True(text.Success); Assert.True(binary.Success);
        Assert.Equal(text.ImageHash, binary.ImageHash); Assert.Equal(text.Configurations[0].Windows, binary.Configurations[0].Windows);
        for (var a = 0; a < 65536; a++) Assert.Equal(text.Configurations[0].FlagsAt(a), binary.Configurations[0].FlagsAt(a));
        var result = new Disassembler().Disassemble(image, 0xfffe, map: binary);
        Assert.True(result.Success); Assert.Equal(2, result.Statistics.CodeBytes); RoundTrip(image, result, 0xfffe);
    }

    [Fact]
    public void PrefixContinuationM1DoesNotSeedAnotherInstructionAndReadonlyBecomesData()
    {
        byte[] bytes = [0xdd, 0x21, 0x34, 0x12, 0xc9, 0x55, 0x66];
        var map = Text(Header(bytes) + Block(Config, "window E000 00000000 00000007",
            "mark E000-E000 11\nmark E001-E001 01\nmark E005-E005 02\nmark E006-E006 06"));
        var result = new Disassembler().Disassemble(bytes, 0xe000, map: map);
        Assert.True(result.Success); Assert.Contains("LD IX,0x1234", result.Source);
        Assert.Equal(new DisasmStatistics(5, 1, 1), result.Statistics); RoundTrip(bytes, result);
    }

    [Fact]
    public void ConfigurationSelectionNeverUnionsBanksOrValidatesUnselectedLayout()
    {
        byte[] image = [0xc9, 0xaa];
        var map = Text(Header(image) + Block(Config, "window E000 00000000 00000002", "mark E000-E000 11")
            + Block(Other, "window 0000 00000000 00000002", "mark 0000-0001 02"));
        Assert.True(map.Success);
        var disasm = new Disassembler();
        Assert.False(disasm.Disassemble(image, 0xe000, map: map).Success);
        Assert.False(disasm.Disassemble(image, 0xe000, map: map, mapConfig: "unknown").Success);
        var selected = disasm.Disassemble(image, 0xe000, map: map, mapConfig: Config);
        Assert.True(selected.Success); Assert.Equal(new DisasmStatistics(1, 0, 1), selected.Statistics);
        var other = disasm.Disassemble(image, 0, map: map, mapConfig: Other);
        Assert.True(other.Success); Assert.Equal(new DisasmStatistics(0, 2, 0), other.Statistics);
    }

    [Fact]
    public void PhaseMustMatchWindowAndAliasWindowsCannotBeImplicitlyRebased()
    {
        byte[] image = [0xc9];
        var map = Text(Header(image) + Block(Config, "window DB00 00000000 00000001", "mark DB00-DB00 11"));
        Assert.False(new Disassembler().Disassemble(image, 0xe000, map: map).Success);
        var result = new Disassembler().Disassemble(image, 0xe000, ControlFile.Parse("phase E000-E000 at DB00"), map);
        Assert.True(result.Success); RoundTrip(image, result);
        var aliases = Text(Header(image) + Block(Config, "window DB00 00000000 00000001\nwindow E000 00000000 00000001"));
        Assert.True(aliases.Success); // Wire format permits aliases, current PHASE must resolve the selected view.
        Assert.False(new Disassembler().Disassemble(image, 0xe000, map: aliases).Success);
    }

    [Fact]
    public void ImageLengthAndHashAreBoundExactly()
    {
        byte[] image = [0xc9];
        var map = Text(Header(image) + Block(Config, "window E000 00000000 00000001"));
        Assert.False(new Disassembler().Disassemble([0], 0xe000, map: map).Success);
        Assert.False(new Disassembler().Disassemble([0xc9, 0], 0xe000, map: map).Success);
    }

    [Fact]
    public void MismatchStaysDbUnlessExplicitCodeResolvesIt()
    {
        byte[] bytes = [0x3e, 0x42, 0xc9];
        var map = Text(Header(bytes) + Block(Config, "window E000 00000000 00000003", "mark E000-E000 31\nmark E001-E001 22"));
        var result = new Disassembler().Disassemble(bytes, 0xe000, map: map);
        Assert.False(result.Success); Assert.Equal(0, result.Statistics.CodeBytes); Assert.DoesNotContain("LD A,", result.Source); RoundTrip(bytes, result);
        var ctl = new Disassembler().Disassemble(bytes, 0xe000, ControlFile.Parse("code E000-E002"), map);
        Assert.True(ctl.Success); Assert.Contains("LD A,0x42", ctl.Source); RoundTrip(bytes, ctl);
        var traced = new Disassembler().Disassemble(bytes, 0xe000, ControlFile.Parse("entry E000"), map);
        Assert.False(traced.Success); Assert.DoesNotContain("LD A,", traced.Source);
    }

    [Fact]
    public void ExplicitDataAndMappedCodeConflictAndReadonlyNeverOverridesTracedCode()
    {
        byte[] bytes = [0xc9];
        var map = Text(Header(bytes) + Block(Config, "window E000 00000000 00000001", "mark E000-E000 11"));
        Assert.False(new Disassembler().Disassemble(bytes, 0xe000, ControlFile.Parse("data E000-E000"), map).Success);
        var read = Text(Header(bytes) + Block(Config, "window E000 00000000 00000001", "mark E000-E000 02"));
        var result = new Disassembler().Disassemble(bytes, 0xe000, ControlFile.Parse("entry E000"), read);
        Assert.True(result.Success); Assert.Equal(new DisasmStatistics(1, 0, 0), result.Statistics);
    }

    [Theory]
    [InlineData("hcmap 2", "version")]
    [InlineData("config 7e-ff-cpm-1-shadow-0", "configuration")]
    [InlineData("window FFFF 00000000 00000002", "bounds")]
    [InlineData("window E000 00000002 00000001", "bounds")]
    [InlineData("window E000 00000000 00000000", "bounds")]
    [InlineData("mark E000-E000 40", "mask")]
    [InlineData("mark E000-E000 10", "Start")]
    [InlineData("mark E001-E000 01", "range")]
    [InlineData("mark DFFF-E000 01", "windows")]
    [InlineData("nonsense", "Expected")]
    public void RejectsInvalidText(string replace, string diagnostic)
    {
        byte[] bytes = [0xc9, 0];
        var text = Header(bytes) + Block(Config, "window E000 00000000 00000002", "mark E000-E000 11");
        var needle = replace.Split(' ')[0] switch { "hcmap" => "hcmap 1", "config" => "config " + Config,
            "window" => "window E000 00000000 00000002", _ => "mark E000-E000 11" };
        var map = Text(text.Replace(needle, replace)); Assert.False(map.Success);
        Assert.Contains(diagnostic, map.Diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(map.Configurations);
    }

    [Fact]
    public void RejectsTruncationTrailingBitmapReservedBitsAndMalformedManifest()
    {
        byte[] bytes = [0xc9]; var text = Header(bytes) + Block(Config, "window E000 00000000 00000001");
        var flags = new byte[65536]; var binary = Bitmap(text, flags);
        Assert.False(ExecutionMap.Parse(binary[..^1]).Success);
        Assert.False(ExecutionMap.Parse(binary.Concat(new byte[] { 0 }).ToArray()).Success);
        flags[0xe000] = 0x80; Assert.False(ExecutionMap.Parse(Bitmap(text, flags)).Success);
        flags[0xe000] = 0x10; Assert.False(ExecutionMap.Parse(Bitmap(text, flags)).Success);
        flags[0xe000] = 0; flags[0] = 1; Assert.False(ExecutionMap.Parse(Bitmap(text, flags)).Success);
        Assert.False(ExecutionMap.Parse(Bitmap(text.Replace("end", "mark E000-E000 11\nend"), new byte[65536])).Success);
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(8, 4), 1048577); Assert.False(ExecutionMap.Parse(binary).Success);
        Assert.False(Text("\ufeff" + text).Success); Assert.False(Text(text.Replace("00000001", "zzzzzzzz")).Success);
        Assert.False(Text(text.Replace(new string('0', 64), "not-a-hash")).Success);
        Assert.False(Text(text + Block(Config, "window E000 00000000 00000001")).Success);
        Assert.False(Text(text.Replace("window E000 00000000 00000001", "window E000 00000000 00000001\nwindow E000 00000000 00000001")).Success);
    }
}
