using Xunit;

namespace Hc2000.Asm.Tests;

public class ExpressionTests
{
    [Theory]
    [InlineData("0x1F", 31)]
    [InlineData("1Fh", 31)]
    [InlineData("$1F", 31)]
    [InlineData("#1F", 31)]
    [InlineData("%1010", 10)]
    [InlineData("0b1010", 10)]
    [InlineData("1010b", 10)]
    [InlineData("'A'", 65)]
    [InlineData("$", 0x8123)]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("17 % 5", 2)]
    [InlineData("1 + %1010", 11)]
    [InlineData("16 >> 1 + 1", 4)]
    [InlineData("3 << 2", 12)]
    [InlineData("HIGH(0xABCD)", 0xAB)]
    [InlineData("low(0xABCD)", 0xCD)]
    [InlineData("!0 + ~0", 0)]
    [InlineData("3 & 1 | 4 ^ 2", 7)]
    [InlineData("1 < 2 == 1", 1)]
    [InlineData("3 >= 3 && 0 != 1", 1)]
    [InlineData("1 && %1010", 1)]
    [InlineData("0 || %10", 1)]
    [InlineData("-3 + +1", -2)]
    [InlineData("start + .local", 0x1100)]
    [InlineData("1B + 1F", 9)]
    public void EvaluatesNumbersAndPrecedence(string source, int expected)
    {
        Assert.Equal(expected, Expressions.Evaluate(source, name => name switch
        {
            "start" => 0x1000, ".local" => 0x100, "1B" => 4, "1F" => 5,
            _ => throw new FormatException("Undefined symbol")
        }, 0x8123));
    }

    [Fact]
    public void ExcessiveNestingIsASourceError()
    {
        Assert.Throws<FormatException>(() => Expressions.Evaluate(new string('(', 256) + "1" + new string(')', 256), _ => 0));
        Assert.Throws<FormatException>(() => Expressions.Evaluate(new string('~', 256) + "1", _ => 0));
    }

    [Theory]
    [InlineData("(1 + 2")]
    [InlineData("1 / 0")]
    [InlineData("0xZZ")]
    [InlineData("'ab'")]
    [InlineData("1 2")]
    [InlineData("")]
    public void MalformedExpressionsAreRejected(string source)
        => Assert.Throws<FormatException>(() => Expressions.Evaluate(source, _ => 0));
}
