using System.Text.RegularExpressions;
using Hc2000.Z80;

namespace Hc2000.Asm;

/// <summary>Matches source operands to the core's catalog; never defines instruction encodings.</summary>
internal sealed class InstructionEncoder
{
    private readonly Dictionary<string, OpcodeEntry[]> byMnemonic;
    private readonly HashSet<string> registers = new("A B C D E H L F AF AF' BC DE HL SP IX IY IXH IXL IYH IYL I R NZ Z NC PO PE P M".Split(' '), StringComparer.OrdinalIgnoreCase);

    public InstructionEncoder()
    {
        byMnemonic = new Opcodes().Entries.Where(e => e.CanonicalForAssembly)
            .GroupBy(e => e.Pattern.Split(' ')[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Operands.Count).ThenBy(e => e.Length).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public byte[] Encode(string mnemonic, string[] operands, Func<string, int> evaluate, int address, bool strict)
    {
        if (!byMnemonic.TryGetValue(mnemonic, out var entries)) throw new FormatException($"Unknown instruction '{mnemonic}'.");
        foreach (var entry in entries)
        {
            var space = entry.Pattern.IndexOf(' ');
            var patterns = space < 0 ? [] : Assembler.SplitArguments(entry.Pattern[(space + 1)..]);
            if (patterns.Length != operands.Length) continue;
            var captured = new Dictionary<string, string>();
            var matched = true;
            for (var i = 0; i < operands.Length && matched; i++)
                matched = Match(patterns[i], operands[i], entry, evaluate, captured);
            if (!matched) continue;
            var bytes = entry.Encoding.ToArray();
            foreach (var slot in entry.Operands)
            {
                var value = evaluate(captured[slot.PatternToken]);
                if (slot.Kind == OperandKind.Relative)
                {
                    // 16-bit modular arithmetic allows a branch over the FFFF/0000 boundary.
                    value = ((value - (address + entry.Length) + 32768) & 65535) - 32768;
                }
                if (strict)
                {
                    if (slot.Kind == OperandKind.Byte && value is < -128 or > 255)
                        throw new FormatException($"Byte operand {value} outside -128..255.");
                    if (slot.Kind is OperandKind.Displacement or OperandKind.Relative && value is < -128 or > 127)
                        throw new FormatException(slot.Kind == OperandKind.Relative
                            ? $"Relative jump out of range ({value}; expected -128..127)."
                            : $"Indexed displacement {value} outside -128..127.");
                }
                bytes[slot.ByteOffset] = unchecked((byte)value);
                if (slot.Kind == OperandKind.WordLE) bytes[slot.ByteOffset + 1] = unchecked((byte)(value >> 8));
            }
            return bytes;
        }
        throw new FormatException($"Invalid operands for {mnemonic}: {string.Join(',', operands)}.");
    }

    private bool Match(string pattern, string operand, OpcodeEntry entry, Func<string, int> evaluate, Dictionary<string, string> captured)
    {
        // Whitespace is insignificant outside quoted character constants.
        var text = Regex.Replace(operand, @"\s+(?=(?:[^']*'[^']*')*[^']*$)", "");
        foreach (var slot in entry.Operands)
        {
            var position = pattern.IndexOf(slot.PatternToken, StringComparison.Ordinal);
            if (position < 0) continue;
            var prefix = pattern[..position];
            var suffix = pattern[(position + slot.PatternToken.Length)..];
            if (slot.Kind == OperandKind.Displacement) prefix = prefix.TrimEnd('+');
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || text.Length < prefix.Length + suffix.Length) return false;
            var expression = text.Substring(prefix.Length, text.Length - prefix.Length - suffix.Length);
            if (slot.Kind == OperandKind.Displacement)
            {
                if (expression.Length == 0) expression = "0";
                else if (expression[0] is not '+' and not '-') return false;
            }
            else
            {
                if (expression.Length == 0 || registers.Contains(expression)) return false;
                if (prefix == "(" && Regex.IsMatch(expression, @"^(IX|IY)[+-]", RegexOptions.IgnoreCase)) return false;
                // Bare byte slots must not swallow an indirect-memory operand.
                if (prefix.Length == 0 && expression.StartsWith('(') && expression.EndsWith(')')) return false;
            }
            captured[slot.PatternToken] = expression;
            return true;
        }
        if (string.Equals(pattern, text, StringComparison.OrdinalIgnoreCase)) return true;
        // Concrete bit/IM/RST selectors accept any equivalent constant expression.
        if (pattern.Length > 0 && char.IsDigit(pattern[0]) && !registers.Contains(text))
            return evaluate(operand) == Expressions.Evaluate(pattern, _ => throw new FormatException("Invalid catalog selector."));
        return false;
    }
}
