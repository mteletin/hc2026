using System.Globalization;
using System.Text.RegularExpressions;

namespace Hc2000.Disasm;

public sealed record DisasmDiagnostic(int Line, string Message);
public sealed record ControlDirective(string Kind, int Start, int End, string Argument, int Value, int Line);

/// <summary>Addresses in ctl files are hexadecimal. Ranges are inclusive physical addresses;
/// entries, labels and equs are logical addresses after PHASE mapping.</summary>
public sealed class ControlFile
{
    public IReadOnlyList<ControlDirective> Directives { get; }
    public IReadOnlyList<DisasmDiagnostic> Diagnostics { get; }
    public bool Success => Diagnostics.Count == 0;
    private ControlFile(List<ControlDirective> directives, List<DisasmDiagnostic> diagnostics)
    {
        Directives = directives.AsReadOnly(); Diagnostics = diagnostics.AsReadOnly();
    }

    public static ControlFile Parse(string text)
    {
        var directives = new List<ControlDirective>();
        var diagnostics = new List<DisasmDiagnostic>();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var line = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            line++;
            try
            {
                var tokens = Tokenize(raw);
                if (tokens.Count == 0) continue;
                var kind = tokens[0].ToLowerInvariant();
                if (tokens.Count < 2) throw new FormatException("Missing address");
                if (kind == "entry")
                {
                    foreach (var token in tokens.Skip(1)) directives.Add(new(kind, Hex(token), Hex(token), "", 0, line));
                    continue;
                }
                var range = tokens[1].Split('-');
                if (range.Length > 2) throw new FormatException("Bad range");
                var start = Hex(range[0]); var end = range.Length == 1 ? start : Hex(range[1]);
                if (end < start) throw new FormatException("Reversed range");
                var arg = ""; var value = 0;
                void Count(int n) { if (tokens.Count != n) throw new FormatException($"{kind} expects {n - 1} arguments"); }
                switch (kind)
                {
                    case "code": case "words": case "text": case "jumptable": Count(2); break;
                    case "data":
                        if (tokens.Count == 2) { value = 8; break; }
                        Count(4);
                        if (tokens[2] != "bytes" || !int.TryParse(tokens[3], out value) || value is < 1 or > 256)
                            throw new FormatException("data expects bytes <1..256>");
                        break;
                    case "fill": Count(3); value = Hex(tokens[2]); if (value > 255) throw new FormatException("Fill is not a byte"); break;
                    case "font": Count(3); arg = tokens[2]; if (arg != "4x8" && arg != "8x8") throw new FormatException("Font must be 4x8 or 8x8"); break;
                    case "phase": Count(4); if (tokens[2] != "at") throw new FormatException("phase expects at <address>"); value = Hex(tokens[3]); if (value + end - start > 65535) throw new FormatException("Phase exceeds address space"); break;
                    case "label": case "port": case "equ":
                        Count(3); arg = tokens[2];
                        if (start != end || !Regex.IsMatch(arg, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new FormatException("Expected single address and symbol name");
                        if (names.ContainsKey(arg)) throw new FormatException($"Duplicate symbol {arg}");
                        names[arg] = start;
                        if (kind == "port" && start > 255) throw new FormatException("Immediate port is not a byte");
                        break;
                    case "comment": Count(3); arg = tokens[2]; if (start != end) throw new FormatException("comment expects a single address"); break;
                    case "rst":
                        Count(4); if (start != end || start > 0x38 || start % 8 != 0 || tokens[2] != "args") throw new FormatException("rst expects vector args <decimal count|calc>");
                        arg = tokens[3];
                        if (arg != "calc" && (!int.TryParse(arg, out value) || value < 0 || value > 65536)) throw new FormatException("Invalid RST argument count");
                        break;
                    default: throw new FormatException($"Unknown directive {kind}");
                }
                if (kind == "phase" && directives.Any(d => d.Kind == "phase" && d.Start <= end && start <= d.End))
                    throw new FormatException("Overlapping phase ranges");
                if (IsRegion(kind) && directives.Any(d => IsRegion(d.Kind) && d.Start <= end && start <= d.End))
                    throw new FormatException("Overlapping code/data regions");
                directives.Add(new(kind, start, end, arg, value, line));
            }
            catch (FormatException ex) { diagnostics.Add(new(line, ex.Message)); }
        }
        return new(directives, diagnostics);
    }

    public static int Hex(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        else if (text.StartsWith('$')) text = text[1..];
        if (text.EndsWith('h') || text.EndsWith('H')) text = text[..^1];
        if (!int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 65535)
            throw new FormatException("Invalid hexadecimal address: " + text);
        return value;
    }

    internal static bool IsRegion(string kind) => kind is "code" or "data" or "words" or "text" or "fill" or "font" or "jumptable";
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>(); var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == ';') break;
            if (line[i] == '"')
            {
                var start = ++i;
                while (i < line.Length && line[i] != '"') i++;
                if (i == line.Length) throw new FormatException("Unterminated quoted comment");
                tokens.Add(line[start..i++]);
            }
            else
            {
                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != ';') i++;
                tokens.Add(line[start..i]);
            }
        }
        return tokens;
    }
}
