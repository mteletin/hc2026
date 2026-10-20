using Hc2000.Z80;
using Xunit;

namespace Hc2000.Asm.Tests;

public class InstructionTests
{
    [Theory]
    [InlineData("nop", "00")]
    [InlineData("ex af,af' ; quote is not a character", "08")]
    [InlineData("LD BC,$1234", "013412")]
    [InlineData("ld a,-1", "3EFF")]
    [InlineData("LD (IX+7),42", "DD36072A")]
    [InlineData("LD H,(IX-128)", "DD6680")]
    [InlineData("LD (IY),L", "FD7500")]
    [InlineData("LD IYH,IYL", "FD65")]
    [InlineData("JP (IY)", "FDE9")]
    [InlineData("SLL A", "CB37")]
    [InlineData("RLC (IX-3),B", "DDCBFD00")]
    [InlineData("SET 1+2,(IY+127),A", "FDCB7FDF")]
    [InlineData("IN F,(C)", "ED70")]
    [InlineData("OUT (C),0", "ED71")]
    [InlineData("OUT (123),A", "D37B")]
    [InlineData("RST 0x28", "EF")]
    [InlineData("BIT 1+2,A", "CB5F")]
    [InlineData("IM 1+1", "ED5E")]
    [InlineData("NEG", "ED44")]
    public void IndependentEncodingExamples(string source, string expected)
    {
        var result = Assembler.AssembleText(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(expected, Convert.ToHexString(result.ToBinary()));
    }

    [Fact]
    public void EveryCanonicalCatalogEntryAssemblesWithSampleOperands()
    {
        foreach (var entry in new Opcodes().Entries.Where(e => e.CanonicalForAssembly))
        {
            var source = entry.Pattern;
            var expected = entry.Encoding.ToArray();
            foreach (var slot in entry.Operands.OrderByDescending(s => s.PatternToken.Length))
            {
                var value = slot.Kind switch
                {
                    OperandKind.Byte => 0xA5, OperandKind.WordLE => 0x1234,
                    OperandKind.Displacement => 5, OperandKind.Relative => 0x1000 + entry.Length + 5,
                    _ => throw new InvalidOperationException()
                };
                source = source.Replace(slot.PatternToken, value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
                expected[slot.ByteOffset] = slot.Kind == OperandKind.Relative ? (byte)5 : unchecked((byte)value);
                if (slot.Kind == OperandKind.WordLE) expected[slot.ByteOffset + 1] = (byte)(value >> 8);
            }
            var result = Assembler.AssembleText("ORG 0x1000\n" + source);
            Assert.True(result.Success, $"{entry.DecoderKey}: {source}: {string.Join(';', result.Diagnostics.Select(d => d.Message))}");
            Assert.Equal(expected, result.ToBinary());
        }
    }

    [Theory]
    [InlineData("ORG 0x100\n JR 0x82", "1880")]
    [InlineData("ORG 0x100\n JR 0x181", "187F")]
    [InlineData("ORG 0xFFFE\n JR 0", "1800")]
    [InlineData("ORG 0\n JR 0xFFFF", "18FD")]
    [InlineData("ORG 0xE000\nPHASE 0xDB00\n JR $+5\nDEPHASE", "1803")]
    [InlineData("ORG 0xE000\nPHASE 0xDB00\n DJNZ target\n NOP\ntarget: RET\nDEPHASE", "100100C9")]
    public void RelativeRangeWrapAndPhase(string source, string expected)
    {
        var result = Assembler.AssembleText(source);
        Assert.True(result.Success, string.Join(';', result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(expected, Convert.ToHexString(result.ToBinary()));
    }

    [Theory]
    [InlineData(" JR 130", "Relative jump out of range")]
    [InlineData(" JR -127", "Relative jump out of range")]
    [InlineData(" LD A,256", "Byte operand")]
    [InlineData(" LD A,missing", "Undefined label")]
    [InlineData(" LD A,(IX+128)", "Indexed displacement")]
    [InlineData(" LD (IY-129),1", "Indexed displacement")]
    [InlineData(" LD F,A", "Invalid operands")]
    [InlineData(" LD A,BC", "Invalid operands")]
    public void InstructionErrorsAreSingleLocatedDiagnostics(string source, string expected)
    {
        var result = Assembler.AssembleText("; first line\n" + source);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(2, error.Line);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ForwardLabelsAndIndexedDisplacementExpressions()
    {
        var result = Assembler.AssembleText("ORG 0x8000\n LD A,LOW(target)\n LD IX,target\n JR target\n LD (IX+(target-$)),1\ntarget: RET");
        Assert.True(result.Success, string.Join(';', result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("3E0CDD210C801804DD360401C9", Convert.ToHexString(result.ToBinary()));
    }
}
