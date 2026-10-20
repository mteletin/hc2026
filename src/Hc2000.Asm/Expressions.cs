using System.Globalization;

namespace Hc2000.Asm;

/// <summary>C-precedence integer expressions used by the source language.</summary>
public static class Expressions
{
    public static int Evaluate(string expression, Func<string, int> symbol, int address = 0)
        => new Parser(expression, symbol, address).Parse();

    private sealed class Parser(string text, Func<string, int> symbol, int address)
    {
        private int pos;
        private int depth;
        private string token = "";
        private bool started;
        private readonly Dictionary<string, int> Precedence = new()
        {
            ["||"] = 1, ["&&"] = 2, ["|"] = 3, ["^"] = 4, ["&"] = 5,
            ["=="] = 6, ["!="] = 6, ["<"] = 7, ["<="] = 7, [">"] = 7, [">="] = 7,
            ["<<"] = 8, [">>"] = 8, ["+"] = 9, ["-"] = 9, ["*"] = 10, ["/"] = 10, ["%"] = 10
        };
        public int Parse()
        {
            Next();
            var value = Binary(1);
            if (token.Length != 0) throw new FormatException($"Unexpected '{token}' in expression.");
            return value;
        }
        private int Binary(int min)
        {
            var left = Unary();
            while (Precedence.TryGetValue(token, out var precedence) && precedence >= min)
            {
                var op = token;
                Next();
                var right = Binary(precedence + 1);
                left = op switch
                {
                    "+" => unchecked(left + right), "-" => unchecked(left - right), "*" => unchecked(left * right),
                    "/" => right == 0 ? throw new FormatException("Division by zero.") : left / right,
                    "%" => right == 0 ? throw new FormatException("Division by zero.") : left % right,
                    "<<" => left << right, ">>" => left >> right, "&" => left & right, "|" => left | right, "^" => left ^ right,
                    "==" => left == right ? 1 : 0, "!=" => left != right ? 1 : 0,
                    "<" => left < right ? 1 : 0, "<=" => left <= right ? 1 : 0,
                    ">" => left > right ? 1 : 0, ">=" => left >= right ? 1 : 0,
                    "&&" => left != 0 && right != 0 ? 1 : 0, "||" => left != 0 || right != 0 ? 1 : 0,
                    _ => throw new FormatException("Invalid operator.")
                };
            }
            return left;
        }
        private int Unary()
        {
            if (++depth > 128) throw new FormatException("Expression nesting exceeds 128.");
            try { return UnaryCore(); }
            finally { depth--; }
        }
        private int UnaryCore()
        {
            var value = token;
            Next();
            switch (value)
            {
                case "+": return Unary();
                case "-": return unchecked(-Unary());
                case "~": return ~Unary();
                case "!": return Unary() == 0 ? 1 : 0;
                case "(":
                    var inner = Binary(1);
                    Expect(")");
                    return inner;
                case "$": return address;
                case "": throw new FormatException("Expected an expression.");
            }
            if ((value.Equals("HIGH", StringComparison.OrdinalIgnoreCase) || value.Equals("LOW", StringComparison.OrdinalIgnoreCase)) && token == "(")
            {
                Next();
                var inner = Binary(1);
                Expect(")");
                return value.Equals("HIGH", StringComparison.OrdinalIgnoreCase) ? (inner >> 8) & 255 : inner & 255;
            }
            if (value.Length == 3 && value[0] == '\'' && value[2] == '\'') return value[1];
            if (char.IsDigit(value[0]) || value[0] is '$' or '#' or '%')
            {
                // Numeric temporary labels are resolved by the assembler, not parsed as binary numbers.
                if (value.Length == 2 && char.IsDigit(value[0]) && value[1] is 'B' or 'F') return symbol(value);
                var radix = 10;
                var digits = value;
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { radix = 16; digits = value[2..]; }
                else if (value.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { radix = 2; digits = value[2..]; }
                else if (value[0] is '$' or '#') { radix = 16; digits = value[1..]; }
                else if (value[0] == '%') { radix = 2; digits = value[1..]; }
                else if (value.EndsWith('h') || value.EndsWith('H')) { radix = 16; digits = value[..^1]; }
                else if (value.EndsWith('b') || value.EndsWith('B')) { radix = 2; digits = value[..^1]; }
                try { return radix == 10 ? int.Parse(digits, CultureInfo.InvariantCulture) : Convert.ToInt32(digits, radix); }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                { throw new FormatException($"Invalid number '{value}'."); }
            }
            return symbol(value);
        }
        private void Expect(string expected)
        {
            if (token != expected) throw new FormatException($"Expected '{expected}'.");
            Next();
        }
        private void Next()
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos == text.Length) { token = ""; return; }
            var start = pos++;
            var c = text[start];
            if (c == '\'')
            {
                if (pos + 1 >= text.Length || text[pos + 1] != '\'') throw new FormatException("Expected a single quoted character.");
                pos += 2;
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '.' or '#' || (c == '$' && pos < text.Length && Uri.IsHexDigit(text[pos])) || (c == '%' && !started && pos < text.Length && text[pos] is '0' or '1'))
            {
                while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] is '_' or '.')) pos++;
            }
            else if (pos < text.Length && (text[start..(pos + 1)] is "<<" or ">>" or "<=" or ">=" or "==" or "!=" or "&&" or "||")) pos++;
            token = text[start..pos];
            // Percent is a binary prefix where an operand is expected, modulo otherwise.
            started = token != "(" && !Precedence.ContainsKey(token) && token is not "!" and not "~";
        }
    }
}
