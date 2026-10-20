using Xunit;

namespace Hc2000.Asm.Tests;

public sealed class DirectiveTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hcasm-" + Guid.NewGuid().ToString("N"));
    public DirectiveTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private AssemblyResult Assemble(string source) => Assembler.AssembleText(source, file: Path.Combine(directory, "test.asm"));
    private static void Success(AssemblyResult result) => Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));

    [Fact]
    public void ForwardGlobalLocalAndTemporaryLabels()
    {
        var result = Assemble("ORG $8000\nStart:\n DW End, .local, 1F\n.local: DB 1\n1: DW 1B\nEnd:\n DW Start, Start.local");
        Success(result);
        Assert.Equal(new byte[] { 9, 128, 6, 128, 7, 128, 1, 7, 128, 0, 128, 6, 128 }, result.ToBinary());
        Assert.Equal(0x8006, result.Symbols["Start.local"]);
        Assert.Equal(0x8009, result.Symbols["End"]);
    }

    [Fact]
    public void DataAliasesCharsStringsCommentsAndWrappedWords()
    {
        var result = Assemble("ORG 16\n DB 'A',-128,255\nDEFB 2\nBYTE 3\nDM \"a;b,c\" ; comment\nDEFM \"Z\"\nDW -1,65536\nDEFW 0xABCD\nWORD 1\nDS 2,3\nDEFS 1\nBLOCK 1,4");
        Success(result);
        Assert.Equal(new byte[] { 65, 128, 255, 2, 3, 97, 59, 98, 44, 99, 90, 255, 255, 0, 0, 205, 171, 1, 0, 3, 3, 0, 4 }, result.ToBinary());
    }

    [Fact]
    public void EquRedefinableAndColumnOneLabel()
    {
        var result = Assemble("BASE EQU 10\nvalue = 1\n DB value\nvalue = value+1\nlabel DB value,BASE\n ASSERT label == 1\n");
        Success(result);
        Assert.Equal(new byte[] { 1, 2, 10 }, result.ToBinary());
    }

    [Fact]
    public void DefinesDriveNestedConditionals()
    {
        var result = Assembler.AssembleText("IF mode == 2\n IF 0\n DB missing\n ELSE\n DB mode\n ENDIF\nELSE\n DB 9\nENDIF", new() { Defines = new Dictionary<string, int> { ["mode"] = 2 } });
        Success(result);
        Assert.Equal(new byte[] { 2 }, result.ToBinary());
    }

    [Fact]
    public void ForwardEquControlsDataSizeOverSeveralPasses()
    {
        var result = Assemble(" DS count,7\ncount EQU more\nmore EQU 3\n ASSERT $ == 3");
        Success(result);
        Assert.Equal(new byte[] { 7, 7, 7 }, result.ToBinary());
    }

    [Fact]
    public void MacroParametersReptAndInactiveMacros()
    {
        var result = Assemble("pair MACRO a,b\n DB a,b\nENDM\nIF 0\nbad MACRO\n DB undefined\nENDM\nENDIF\nREPT 2\n pair 1,2\n REPT 2\n DB 3\n ENDR\nENDR");
        Success(result);
        Assert.Equal(new byte[] { 1, 2, 3, 3, 1, 2, 3, 3 }, result.ToBinary());
    }

    [Fact]
    public void MacroDirectiveAlternativeSyntax()
    {
        var result = Assemble("MACRO byte x\n DB x\nENDM\n byte 3");
        Success(result);
        Assert.Equal(new byte[] { 3 }, result.ToBinary());
    }

    [Fact]
    public void PhaseChangesLabelsAndDollarButNotEmissionAddress()
    {
        var result = Assemble("ORG 0xE000\nPHASE 0xDB00\nlabel: DW label,$\nPHASE 0x9000\n DW $\nDEPHASE\n DW $\nDEPHASE\n DW $");
        Success(result);
        Assert.Equal(0xDB00, result.Symbols["label"]);
        Assert.Equal(0xE000, Assert.Single(result.Segments).Address);
        Assert.Equal(new byte[] { 0, 219, 0, 219, 0, 144, 6, 219, 8, 224 }, result.ToBinary());
    }

    [Fact]
    public void IncludeAndIncbinResolveFromSourceDirectory()
    {
        File.WriteAllText(Path.Combine(directory, "inc.asm"), " DB 4\n");
        File.WriteAllBytes(Path.Combine(directory, "inc.bin"), [10, 11, 12, 13]);
        File.WriteAllText(Path.Combine(directory, "test.asm"), "INCLUDE \"inc.asm\"\nINCBIN \"inc.bin\",1,2");
        var result = Assembler.Assemble(Path.Combine(directory, "test.asm"));
        Success(result);
        Assert.Equal(new byte[] { 4, 11, 12 }, result.ToBinary());
        Assert.EndsWith("inc.asm", result.Listing[0].File);
    }

    [Fact]
    public void IncludeSearchPathAndIncludedDiagnosticProvenance()
    {
        var includes = Path.Combine(directory, "include"); Directory.CreateDirectory(includes);
        File.WriteAllText(Path.Combine(includes, "bad.asm"), "; first line\n DB missing");
        var result = Assembler.AssembleText("INCLUDE \"bad.asm\"", new() { IncludePaths = [includes] }, Path.Combine(directory, "test.asm"));
        var error = Assert.Single(result.Diagnostics);
        Assert.EndsWith("bad.asm", error.File);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void GapFillSegmentsHexListingAndSymbols()
    {
        var result = Assemble("ORG 0x100\nname: DB 1,2\nORG 0x104\n DB 3");
        Success(result);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(new byte[] { 1, 2, 255, 255, 3 }, result.ToBinary());
        Assert.Equal(":020100000102FA\n:0101040003F7\n:00000001FF\n", result.ToIntelHex().Replace("\r", ""));
        Assert.Contains("name = 0x0100", result.ToSymbolFile());
        Assert.Contains("0100 0102", result.ToListing());
        Assert.Equal(new byte[] { 2, 255, 255 }, result.ToBinary(0x101, 3));
    }

    [Fact]
    public void CustomGapFill()
    {
        var result = Assembler.AssembleText(" DB 1\nORG 3\n DB 2", new() { Fill = 0xAA });
        Success(result);
        Assert.Equal(new byte[] { 1, 170, 170, 2 }, result.ToBinary());
    }

    [Fact]
    public void OutputAndSavebinDescribeFilesWithoutApiSideEffects()
    {
        var result = Assemble("OUTPUT \"out.bin\"\nORG 10\n DB 1,2,3\nSAVEBIN \"piece.bin\",11,2");
        Success(result);
        Assert.Equal(2, result.Outputs.Count);
        Assert.Equal(10, result.Outputs[0].Start);
        Assert.Equal(3, result.Outputs[0].Length);
        Assert.Equal(new byte[] { 2, 3 }, result.ToBinary(result.Outputs[1].Start, result.Outputs[1].Length));
        Assert.False(File.Exists(Path.Combine(directory, "out.bin")));
    }

    [Theory]
    [InlineData(" DB missing", 1, "Undefined label")]
    [InlineData(" DB 256", 1, "Byte operand")]
    [InlineData(" DB -129", 1, "Byte operand")]
    [InlineData("name:\nname:", 2, "Duplicate label")]
    [InlineData(" ASSERT 0", 1, "Assertion failed")]
    [InlineData("ORG 65536", 1, "Address outside")]
    [InlineData(" DS -1", 1, "Reserve count")]
    [InlineData("DEPHASE", 1, "without PHASE")]
    [InlineData("ELSE", 1, "without IF")]
    [InlineData("ENDIF", 1, "without IF")]
    [InlineData("ENDM", 1, "Unexpected ENDM")]
    [InlineData("PHASE 0", 1, "Unclosed PHASE")]
    [InlineData("IF 1", 1, "Unclosed IF")]
    [InlineData("REPT 1", 1, "Unclosed REPT")]
    [InlineData("MACRO x", 1, "Unclosed MACRO")]
    [InlineData(" DB 1\nORG 0\n DB 2", 3, "Overlapping")]
    [InlineData("ORG 65535\n DW 0", 2, "Emission exceeds")]
    [InlineData(".local: DB 1", 1, "Local label requires")]
    public void SourceErrorsProduceSingleLocatedDiagnostic(string source, int line, string message)
    {
        var result = Assemble(source);
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(line, error.Line);
        Assert.True(error.Column >= 1);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void MissingFilesProduceDiagnosticRatherThanThrowing()
    {
        Assert.Single(Assembler.Assemble(Path.Combine(directory, "absent.asm")).Diagnostics);
        Assert.Single(Assemble("INCLUDE \"absent.asm\"").Diagnostics);
        Assert.Single(Assemble("INCBIN \"absent.bin\"").Diagnostics);
    }

    [Fact]
    public void RecursiveIncludeTerminates()
    {
        File.WriteAllText(Path.Combine(directory, "loop.asm"), "INCLUDE \"loop.asm\"");
        var result = Assembler.Assemble(Path.Combine(directory, "loop.asm"));
        Assert.Contains(Assert.Single(result.Diagnostics).Message, new[] { "Include/macro nesting exceeds 32." });
    }

    [Fact]
    public void MultipleOutputDirectivesCaptureTheirOwnEmittedRanges()
    {
        var result = Assemble("OUTPUT \"one.bin\"\nORG 100\n DB 1,2\nOUTPUT \"two.bin\"\nORG 200\n DB 3,4");
        Success(result);
        Assert.Equal(2, result.Outputs.Count);
        Assert.Equal(100, result.Outputs[0].Start);
        Assert.Equal(200, result.Outputs[1].Start);
        Assert.All(result.Outputs, output => Assert.Equal(2, output.Length));
    }

    [Fact]
    public void RedefinableAssignmentNeedsNoWhitespace()
    {
        var result = Assemble("value=1\n DB value\nvalue=value+1\n DB value");
        Success(result);
        Assert.Equal(new byte[] { 1, 2 }, result.ToBinary());
    }

    [Fact]
    public void MacroSubstitutionDoesNotReplaceQuotedTextOrCharacters()
    {
        var result = Assemble("text MACRO x\n DB x,\"x\",'x' ; x\nENDM\n text 1");
        Success(result);
        Assert.Equal(new byte[] { 1, 120, 120 }, result.ToBinary());
    }

    [Fact]
    public void LayoutOscillationIsDiagnosed()
    {
        var result = Assemble("IF end == 0\n DB 1\nENDIF\nend:");
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("did not converge"));
    }
}
