using Hc2000.Asm;
using Hc2000.Z80;
using Xunit;

namespace Hc2000.Disasm.Tests;

public sealed class TraceTests
{
    private static DisassemblyResult Decode(byte[] bytes, string ctl = "entry E000", int origin = 0xe000)
        => new Disassembler().Disassemble(bytes, origin, ControlFile.Parse(ctl));
    private static void RoundTrip(byte[] bytes, DisassemblyResult disasm, int origin = 0xe000)
    {
        var asm = Assembler.AssembleText(disasm.Source);
        Assert.True(asm.Success, string.Join(';', asm.Diagnostics.Select(d => d.Message)) + "\n" + disasm.Source);
        Assert.Equal(bytes, asm.ToBinary(origin, bytes.Length));
    }

    [Fact]
    public void ParsesAllDirectivesAndQuotedSemicolons()
    {
        var ctl = ControlFile.Parse("""
            ; header
            entry E000 E003 E006
            label E021 Boot
            code E021-E088
            data E8A0-E8FF bytes 8
            words E000-E017
            text EF00-EF1F
            fill F800-FFFF FF
            font EA00-EBFF 4x8
            jumptable E100-E117
            phase E100-E3FF at DB00
            comment E402 "glyph; writer"
            port 85 FDC_MSR
            equ DB0F colpar
            rst 28 args calc
            rst 08 args 2
            """);
        Assert.True(ctl.Success, string.Join(';', ctl.Diagnostics.Select(d => d.Message)));
        Assert.Equal(17, ctl.Directives.Count);
        Assert.Equal("glyph; writer", ctl.Directives.Single(d => d.Kind == "comment").Argument);
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("entry G000")]
    [InlineData("foo 1234")]
    [InlineData("code 1000-0000")]
    [InlineData("code 0-10\ndata 8-20")]
    [InlineData("phase 0-10 at 100\nphase 8-20 at 200")]
    [InlineData("phase 0-100 at FF80")]
    [InlineData("label 10 1bad")]
    [InlineData("label 10 Name\nequ 20 name")]
    [InlineData("port 100 TooLarge")]
    [InlineData("data 0-10 bytes 0")]
    [InlineData("fill 0-10 100")]
    [InlineData("comment 0 \"unterminated")]
    [InlineData("rst 09 args 2")]
    [InlineData("rst 28 args nonsense")]
    public void RejectsMalformedControls(string ctl) => Assert.False(ControlFile.Parse(ctl).Success);

    [Fact]
    public void TracesBothConditionalPathsAndCallFallthroughNotUnreachedData()
    {
        byte[] bytes = [0x20, 0x05, 0xcd, 0x08, 0xe0, 0xc9, 0x55, 0x76, 0xc9, 0xaa];
        var result = Decode(bytes);
        Assert.True(result.Success);
        Assert.Equal(new DisasmStatistics(8, 0, 2), result.Statistics);
        Assert.Contains("JR NZ,L_E007", result.Source);
        Assert.Contains("CALL L_E008", result.Source);
        Assert.Contains("unreached", result.Source);
        Assert.Contains("L_E008: E002", result.CrossReferences);
        RoundTrip(bytes, result);
    }

    [Theory]
    [InlineData(0xc9)] // RET
    [InlineData(0x76)] // HALT
    [InlineData(0xe9)] // JP HL
    public void StopsAtTerminalInstructions(byte terminal)
    {
        byte[] bytes = [terminal, 0x00]; var result = Decode(bytes);
        Assert.Equal(1, result.Statistics.CodeBytes); Assert.Equal(1, result.Statistics.UnknownBytes);
        RoundTrip(bytes, result);
    }

    [Fact]
    public void ConditionalReturnContinuesAndUnconditionalJumpDoesNot()
    {
        byte[] bytes = [0xc0, 0xc3, 0x05, 0xe0, 0x55, 0xc9];
        var result = Decode(bytes); Assert.Equal(5, result.Statistics.CodeBytes); RoundTrip(bytes, result);
    }

    [Fact]
    public void DataKindsJumpTableRstFixedAndCalculatorInlineArguments()
    {
        byte[] bytes = [0xc3, 0x06, 0xe0, 0xc3, 0x0d, 0xe0, 0xef, 0x01, 0x02, 0x38, 0xcf, 0xaa, 0xbb, 0xc9, 0x34, 0x12, 0x41, 0x42, 0xff, 0xff, 0x0f];
        var result = Decode(bytes, """
            jumptable E000-E005
            rst 28 args calc
            rst 08 args 2
            words E00E-E00F
            text E010-E011
            fill E012-E013 FF
            font E014-E014 4x8
            """);
        Assert.True(result.Success, string.Join(';', result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(new DisasmStatistics(9, 12, 0), result.Statistics);
        Assert.Contains("DW 0x1234", result.Source); Assert.Contains("DM \"AB\"", result.Source);
        Assert.Contains("DS 2,0xFF", result.Source); Assert.Contains("00001111", result.Source);
        RoundTrip(bytes, result);
    }

    [Fact]
    public void PhaseUsesLogicalBranchAddressesWhileOutputStaysPhysical()
    {
        byte[] bytes = [0xc3, 0x04, 0xdb, 0x55, 0x18, 0x00, 0xc9];
        var result = Decode(bytes, "phase E000-E006 at DB00\nentry DB00\nlabel DB04 InRam");
        Assert.True(result.Success); Assert.Contains("PHASE 0xDB00", result.Source);
        Assert.Contains("JP InRam", result.Source); Assert.Contains("JR L_DB06", result.Source);
        RoundTrip(bytes, result);
    }

    [Fact]
    public void RelativeTargetWrapAndExternalLabelEquate()
    {
        byte[] bytes = [0x18, 0x00]; var result = Decode(bytes, "entry FFFE", 0xfffe);
        Assert.True(result.Success); Assert.Contains("L_0000 EQU 0x0000", result.Source); RoundTrip(bytes, result, 0xfffe);
    }

    [Theory]
    [InlineData("entry E000 E001", "Overlapping instruction")]
    [InlineData("entry E000\ndata E001-E001", "Overlapping instruction/data")]
    [InlineData("entry E000\nfill E004-E004 FF", "Fill range")]
    [InlineData("words E000-E000", "odd length")]
    [InlineData("jumptable E000-E003", "not divisible")]
    [InlineData("data DFFF-E000", "outside binary")]
    [InlineData("phase E000-E001 at E003", "Ambiguous logical")]
    public void ReportsConflicts(string ctl, string diagnostic)
    {
        byte[] bytes = [0x21, 0x00, 0x00, 0xc9, 0x55]; var result = Decode(bytes, ctl);
        Assert.False(result.Success); Assert.Contains(result.Diagnostics, d => d.Message.Contains(diagnostic));
    }

    [Fact]
    public void NamedPortsRamAndCommentsProduceXref()
    {
        byte[] bytes = [0xdb, 0x85, 0xd3, 0x87, 0x3a, 0x0f, 0xdb, 0x32, 0x0f, 0xdb, 0xc9];
        var result = Decode(bytes, "entry E000\nport 85 FDC_MSR\nport 87 FDC_DATA\nequ DB0F colpar\ncomment E000 \"driver\"");
        Assert.True(result.Success); Assert.Contains("IN A,(FDC_MSR)", result.Source);
        Assert.Contains("colpar [read]: E004", result.CrossReferences); Assert.Contains("colpar [write]: E007", result.CrossReferences);
        Assert.Contains("FDC_DATA [port]: E002", result.CrossReferences); RoundTrip(bytes, result);
    }

    [Theory]
    [InlineData(new byte[] { 0xed, 0x4c, 0xc9 })] // NEG alias
    [InlineData(new byte[] { 0xed, 0x00, 0xc9 })] // ED hole
    [InlineData(new byte[] { 0xdd, 0x00, 0xc9 })] // ignored DD
    [InlineData(new byte[] { 0xdd, 0xfd, 0x21, 0x34, 0x12, 0xc9 })] // repeated/last prefix
    [InlineData(new byte[] { 0xdd, 0xed, 0x44, 0xc9 })] // ignored before ED
    [InlineData(new byte[] { 0xdd, 0xcb, 0x7f, 0x40, 0xc9 })] // BIT alias
    [InlineData(new byte[] { 0xfd, 0xcb, 0x80 })] // truncated
    [InlineData(new byte[] { 0x21, 0x34 })]
    [InlineData(new byte[] { 0xdd, 0xdd })]
    public void NonCanonicalAndTruncatedStreamsUseExactByteFallback(byte[] bytes)
    {
        var result = Decode(bytes); Assert.True(result.Success); Assert.Contains("exact-byte fallback", result.Source); RoundTrip(bytes, result);
    }

    [Fact]
    public void EverySharedCatalogEntryRoundTripsWithoutAnotherEncodingTable()
    {
        foreach (var entry in new Opcodes().Entries)
        {
            var bytes = entry.Encoding.ToArray();
            foreach (var slot in entry.Operands)
            {
                bytes[slot.ByteOffset] = slot.Kind == OperandKind.Relative ? (byte)0 : (byte)0x81;
                if (slot.Kind == OperandKind.WordLE) bytes[slot.ByteOffset + 1] = 0x12;
            }
            // A single-instruction image avoids interpreting operands/data as independent code.
            var result = Decode(bytes); Assert.True(result.Success, entry.Pattern + ": " + string.Join(';', result.Diagnostics.Select(d => d.Message)));
            if (!entry.CanonicalForAssembly) Assert.Contains("exact-byte fallback", result.Source);
            RoundTrip(bytes, result);
        }
    }

    [Fact]
    public void CalculatorSkipsEmbeddedTerminatorBytesInNumbersAndJumpOperands()
    {
        byte[] bytes = [0xef, 0x34, 0xc1, 0x38, 0, 0, 0, 0x33, 0x38, 0x38, 0xc9];
        var result = Decode(bytes, "entry E000\nrst 28 args calc");
        Assert.True(result.Success); Assert.Equal(new DisasmStatistics(2, 9, 0), result.Statistics); RoundTrip(bytes, result);
    }

    [Fact]
    public void ExplicitDataResolvesAnIntentionalExecutableOverlap()
    {
        byte[] bytes = [0x01, 0xc1, 0x37, 0xc9];
        var result = Decode(bytes, "entry E000 E001 E002 E003\ndata E000-E002 bytes 3\nlabel E001 PopBc\nlabel E002 Carry");
        Assert.True(result.Success); Assert.Contains("PopBc:", result.Source); RoundTrip(bytes, result);
    }

    [Fact]
    public void ExplicitNamesOverrideAutomaticTableLabelsRegardlessOfOrder()
    {
        byte[] bytes = [0xc3, 0x03, 0xe0, 0xc9];
        var result = Decode(bytes, "jumptable E000-E002\nlabel E003 Routine");
        Assert.True(result.Success); Assert.Contains("JP Routine", result.Source); RoundTrip(bytes, result);
    }

    [Fact]
    public void EmptyCtlNeverGuessesCode()
    {
        byte[] bytes = [0xc3, 0x00, 0xe0, 0xff]; var result = Decode(bytes, "");
        Assert.Equal(new DisasmStatistics(0, 0, 4), result.Statistics); Assert.DoesNotContain("JP ", result.Source); RoundTrip(bytes, result);
    }
}
