using System.Text;
using System.Text.RegularExpressions;

namespace Hc2000.Asm;

/// <summary>Deterministic multipass Zilog assembler. Source errors are returned as diagnostics.</summary>
public static class Assembler
{
    public static AssemblyResult Assemble(string sourcePath, AssemblerOptions? options = null)
    {
        options ??= new AssemblerOptions();
        try
        {
            var path = Path.GetFullPath(sourcePath);
            return AssembleLines(ReadLines(path), options);
        }
        catch (Exception ex) when (IsSourceException(ex))
        {
            return Failure(sourcePath, ex.Message, options.Fill);
        }
    }

    /// <summary>Assemble in-memory source; includes are relative to the supplied file name.</summary>
    public static AssemblyResult AssembleText(string source, AssemblerOptions? options = null, string file = "source.asm")
    {
        options ??= new AssemblerOptions();
        return AssembleLines(ToLines(source, Path.GetFullPath(file)), options);
    }

    private static AssemblyResult Failure(string file, string message, byte fill) => new()
    {
        Fill = fill, Diagnostics = [new(file, 1, 1, DiagnosticSeverity.Error, message)]
    };

    private static AssemblyResult AssembleLines(SourceLine[] lines, AssemblerOptions options)
    {
        var previous = new Pass(options, null, false);
        for (var i = 0; i < 16; i++)
        {
            var pass = new Pass(options, previous, false);
            pass.Run(lines);
            if (i > 0 && pass.SameLayout(previous))
            {
                var final = new Pass(options, pass, true);
                final.Run(lines);
                if (final.Errors.Count == 0 && !final.SameLayout(pass)) final.Errors.Add(new(lines.FirstOrDefault()?.File ?? "source.asm", 1, 1, DiagnosticSeverity.Error, "Source layout changed during the final pass."));
                return final.Result();
            }
            previous = pass;
        }
        var last = new Pass(options, previous, true);
        last.Run(lines);
        last.Errors.Add(new(lines.FirstOrDefault()?.File ?? "source.asm", 1, 1, DiagnosticSeverity.Error, "Assembly did not converge after 16 passes."));
        return last.Result();
    }

    private sealed record SourceLine(string File, int Number, string Text);
    private sealed record Macro(string[] Parameters, SourceLine[] Body);
    private sealed record Temporary(string Name, int Order, int Address);
    private static SourceLine[] ReadLines(string path) => ToLines(File.ReadAllText(path), path);
    private static SourceLine[] ToLines(string source, string file) => source.Replace("\r", "").Split('\n')
        .Select((text, i) => new SourceLine(file, i + 1, text)).ToArray();
    private static bool IsSourceException(Exception ex) => ex is IOException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException;

    private sealed class Pass
    {
        private readonly InstructionEncoder encoder;
        private readonly AssemblerOptions options;
        private readonly Pass? previous;
        private readonly bool strict;
        private readonly Dictionary<string, Macro> macros = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> immutable = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> symbols = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Temporary> temporaries = [];
        private readonly SortedDictionary<int, byte> memory = [];
        private readonly List<ListingLine> listing = [];
        private readonly List<BinaryOutput> outputs = [];
        private readonly Dictionary<string, (int Start, int End)> outputRanges = [];
        private readonly Stack<int> phaseOffsets = [];
        private string scope = "";
        private int physical;
        private int logical;
        private int order;
        private string? outputPath;
        public List<Diagnostic> Errors { get; } = [];

        public Pass(AssemblerOptions options, Pass? previous, bool strict)
        {
            this.options = options;
            encoder = previous?.encoder ?? new InstructionEncoder();
            this.previous = previous;
            this.strict = strict;
            foreach (var (name, value) in options.Defines) { symbols[name] = value; immutable.Add(name); }
        }

        public bool SameLayout(Pass other) => symbols.Count == other.symbols.Count && symbols.All(s => other.symbols.TryGetValue(s.Key, out var value) && value == s.Value)
            && memory.Keys.SequenceEqual(other.memory.Keys) && temporaries.SequenceEqual(other.temporaries);

        public AssemblyResult Result()
        {
            var segments = new List<Segment>();
            var buffer = new List<byte>();
            var start = -1;
            foreach (var (address, value) in memory)
            {
                if (start < 0) start = address;
                if (address != start + buffer.Count)
                {
                    segments.Add(new(start, buffer.ToArray()));
                    buffer.Clear();
                    start = address;
                }
                buffer.Add(value);
            }
            if (buffer.Count > 0) segments.Add(new(start, buffer.ToArray()));
            foreach (var (path, range) in outputRanges.Reverse()) outputs.Insert(0, new(path, range.Start, range.End - range.Start));
            return new() { Diagnostics = Errors.ToArray(), Segments = segments, Symbols = new Dictionary<string, int>(symbols, StringComparer.OrdinalIgnoreCase), Listing = listing, Outputs = outputs, Fill = options.Fill };
        }

        public void Run(SourceLine[] lines)
        {
            Process(lines, 0);
            if (phaseOffsets.Count != 0) Error(lines.LastOrDefault() ?? new("source.asm", 1, ""), "Unclosed PHASE block.");
        }

        private int Eval(string text) => Expressions.Evaluate(text, Resolve, logical);
        private int Resolve(string name)
        {
            if (Regex.IsMatch(name, @"^[0-9][BF]$"))
            {
                var candidates = name[1] == 'B' ? temporaries : previous?.temporaries ?? [];
                var match = name[1] == 'B'
                    ? candidates.LastOrDefault(t => t.Name == name[..1] && t.Order <= order)
                    : candidates.FirstOrDefault(t => t.Name == name[..1] && t.Order > order);
                if (match != null) return match.Address;
            }
            else
            {
                var key = Scoped(name);
                if (symbols.TryGetValue(key, out var value) || (previous != null && previous.symbols.TryGetValue(key, out value))) return value;
            }
            if (strict) throw new FormatException($"Undefined label '{name}'.");
            return 0;
        }
        private string Scoped(string name)
        {
            if (!name.StartsWith('.')) return name;
            if (scope.Length == 0) throw new FormatException("Local label requires a preceding global label.");
            return scope + name;
        }
        private void Define(string name, int value, bool redefine = false)
        {
            if (Regex.IsMatch(name, @"^[0-9]$"))
            {
                temporaries.Add(new(name, order, value));
                return;
            }
            if (!Regex.IsMatch(name, @"^[A-Za-z_.][A-Za-z0-9_.]*$")) throw new FormatException($"Invalid label '{name}'.");
            var key = Scoped(name);
            if (immutable.Contains(key) || (!redefine && symbols.ContainsKey(key))) throw new FormatException($"Duplicate label '{name}'.");
            symbols[key] = value;
            if (!redefine) immutable.Add(key);
            if (!name.StartsWith('.')) scope = name;
        }
        private void Error(SourceLine line, string message)
        {
            if (strict) Errors.Add(new(line.File, line.Number, Math.Max(1, line.Text.Length - line.Text.TrimStart().Length + 1), DiagnosticSeverity.Error, message));
        }
        private readonly HashSet<string> Directives = new(StringComparer.OrdinalIgnoreCase)
        {
            "ORG", "EQU", "=", "DB", "DEFB", "BYTE", "DW", "DEFW", "WORD", "DS", "DEFS", "BLOCK", "DM", "DEFM",
            "INCLUDE", "INCBIN", "IF", "ELSE", "ENDIF", "MACRO", "ENDM", "REPT", "ENDR", "ASSERT", "PHASE", "DEPHASE", "OUTPUT", "SAVEBIN"
        };
        // Reserved Zilog mnemonics disambiguate column-one instructions from colonless labels.
        private readonly HashSet<string> Mnemonics = new("ADC ADD AND BIT CALL CCF CP CPD CPDR CPI CPIR CPL DAA DEC DI DJNZ EI EX EXX HALT IM IN INC IND INDR INI INIR JP JR LD LDD LDDR LDI LDIR NEG NOP OR OTDR OTIR OUT OUTD OUTI POP PUSH RES RET RETI RETN RL RLA RLC RLCA RLD RR RRA RRC RRCA RRD RST SBC SCF SET SLA SLL SRA SRL SUB XOR".Split(' '), StringComparer.OrdinalIgnoreCase);

        private (string? Label, string Op, string Args) Parse(SourceLine line)
        {
            var text = StripComment(line.Text);
            if (string.IsNullOrWhiteSpace(text)) return (null, "", "");
            var head = Regex.Match(text, @"^\s*([A-Za-z_.][A-Za-z0-9_.]*|[0-9])\s*:");
            string? label = null;
            if (head.Success) { label = head.Groups[1].Value; text = text[head.Length..]; }
            else
            {
                var definition = Regex.Match(text, @"^\s*([A-Za-z_.][A-Za-z0-9_.]*)(?:\s+(EQU\b|MACRO\b)|\s*(=))", RegexOptions.IgnoreCase);
                if (definition.Success)
                {
                    label = definition.Groups[1].Value;
                    var operation = definition.Groups[2].Success ? definition.Groups[2] : definition.Groups[3];
                    text = text[operation.Index..];
                    if (text[0] == '=') text = "= " + text[1..];
                }
                else if (!char.IsWhiteSpace(text[0]))
                {
                    var first = Regex.Match(text, @"^[A-Za-z_.][A-Za-z0-9_.]*");
                    if (first.Success && !Directives.Contains(first.Value) && !Mnemonics.Contains(first.Value) && !macros.ContainsKey(first.Value))
                    { label = first.Value; text = text[first.Length..]; }
                }
            }
            text = text.Trim();
            if (text.Length == 0) return (label, "", "");
            var end = text.IndexOfAny([' ', '\t']);
            return end < 0 ? (label, text.ToUpperInvariant(), "") : (label, text[..end].ToUpperInvariant(), text[(end + 1)..].Trim());
        }

        private void Process(SourceLine[] lines, int depth)
        {
            if (depth > 32) { Error(lines.FirstOrDefault() ?? new("source.asm", 1, ""), "Include/macro nesting exceeds 32."); return; }
            var conditions = new Stack<(bool Parent, bool Condition, bool Else)>();
            var active = true;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (++order > 1_000_000) { Error(line, "Expanded source exceeds 1000000 lines."); return; }
                try
                {
                    var (label, op, args) = Parse(line);
                    if (op == "IF")
                    {
                        var condition = active && Eval(args) != 0;
                        conditions.Push((active, condition, false));
                        active = condition;
                        continue;
                    }
                    if (op == "ELSE")
                    {
                        if (conditions.Count == 0) throw new FormatException("ELSE without IF.");
                        var condition = conditions.Pop();
                        if (condition.Else) throw new FormatException("Duplicate ELSE.");
                        conditions.Push((condition.Parent, condition.Condition, true));
                        active = condition.Parent && !condition.Condition;
                        continue;
                    }
                    if (op == "ENDIF")
                    {
                        if (conditions.Count == 0) throw new FormatException("ENDIF without IF.");
                        active = conditions.Pop().Parent;
                        continue;
                    }
                    if (op is "MACRO" or "REPT")
                    {
                        var endOp = op == "MACRO" ? "ENDM" : "ENDR";
                        var nesting = 1;
                        var end = i + 1;
                        for (; end < lines.Length; end++)
                        {
                            var childOp = Parse(lines[end]).Op;
                            if (childOp == op) nesting++;
                            if (childOp == endOp) nesting--;
                            if (nesting == 0) break;
                        }
                        if (end == lines.Length) throw new FormatException($"Unclosed {op} block.");
                        var body = lines[(i + 1)..end];
                        i = end;
                        if (!active) continue;
                        if (op == "MACRO")
                        {
                            var name = label;
                            if (name == null)
                            {
                                var split = args.IndexOfAny([' ', '\t', ',']);
                                name = split < 0 ? args : args[..split];
                                args = split < 0 ? "" : args[(split + 1)..].Trim();
                            }
                            if (string.IsNullOrWhiteSpace(name)) throw new FormatException("MACRO requires a name.");
                            if (!macros.TryAdd(name, new(SplitArguments(args), body))) throw new FormatException($"Duplicate macro '{name}'.");
                        }
                        else
                        {
                            var count = Eval(args);
                            if (count < 0 || count > 65536) throw new FormatException("REPT count outside 0..65536.");
                            for (var n = 0; n < count && order <= 1_000_000; n++) Process(body, depth + 1);
                        }
                        continue;
                    }
                    if (!active) continue;
                    if (op is "ENDM" or "ENDR") throw new FormatException($"Unexpected {op}.");
                    if (label != null && op is "EQU" or "=") { Define(label, Eval(args), op == "="); continue; }
                    if (label != null) Define(label, logical);
                    if (op.Length == 0) continue;
                    if (macros.TryGetValue(op, out var macro))
                    {
                        var values = SplitArguments(args);
                        if (values.Length != macro.Parameters.Length) throw new FormatException($"Macro '{op}' expects {macro.Parameters.Length} arguments.");
                        var body = macro.Body.Select(l => l with { Text = Substitute(l.Text, macro.Parameters, values) }).ToArray();
                        Process(body, depth + 1);
                        continue;
                    }
                    var address = logical;
                    var bytes = Execute(op, args, line, depth);
                    if (bytes.Length > 0)
                    {
                        if (physical < 0 || (long)physical + bytes.Length > 65536 || logical < 0 || (long)logical + bytes.Length > 65536) throw new FormatException("Emission exceeds the 16-bit address space.");
                        for (var n = 0; n < bytes.Length; n++)
                        {
                            if (memory.ContainsKey(physical + n)) throw new FormatException($"Overlapping output at 0x{physical + n:X4}.");
                        }
                        for (var n = 0; n < bytes.Length; n++) memory[physical + n] = bytes[n];
                        if (outputPath != null)
                        {
                            var range = outputRanges.GetValueOrDefault(outputPath, (physical, physical));
                            outputRanges[outputPath] = (Math.Min(range.Item1, physical), Math.Max(range.Item2, physical + bytes.Length));
                        }
                        physical += bytes.Length;
                        logical += bytes.Length;
                    }
                    if (strict) listing.Add(new(line.File, line.Number, address, bytes, line.Text));
                }
                catch (Exception ex) when (IsSourceException(ex)) { Error(line, ex.Message); }
            }
            if (conditions.Count != 0) Error(lines.LastOrDefault() ?? new("source.asm", 1, ""), "Unclosed IF block.");
        }

        private byte[] Execute(string op, string args, SourceLine line, int depth)
        {
            var operands = SplitArguments(args);
            switch (op)
            {
                case "ORG":
                    RequireCount(operands, 1);
                    physical = Address(Eval(args));
                    logical = physical + (phaseOffsets.Count == 0 ? 0 : phaseOffsets.Peek());
                    return [];
                case "EQU": case "=": throw new FormatException($"{op} requires a label.");
                case "DB": case "DEFB": case "BYTE": case "DM": case "DEFM":
                    if (operands.Length == 0) throw new FormatException($"{op} requires data.");
                    var data = new List<byte>();
                    foreach (var operand in operands)
                    {
                        if (operand.StartsWith('"')) data.AddRange(StringBytes(operand));
                        else data.Add(Byte(Eval(operand)));
                    }
                    return data.ToArray();
                case "DW": case "DEFW": case "WORD":
                    if (operands.Length == 0) throw new FormatException($"{op} requires data.");
                    var words = new List<byte>();
                    foreach (var operand in operands)
                    {
                        var value = Eval(operand);
                        words.Add(unchecked((byte)value)); words.Add(unchecked((byte)(value >> 8)));
                    }
                    return words.ToArray();
                case "DS": case "DEFS": case "BLOCK":
                    if (operands.Length is < 1 or > 2) throw new FormatException($"{op} expects count[,fill].");
                    var count = Eval(operands[0]);
                    if (count < 0 || count > 65536) throw new FormatException("Reserve count outside 0..65536.");
                    var fill = operands.Length == 2 ? Byte(Eval(operands[1])) : (byte)0;
                    var reserved = new byte[count]; Array.Fill(reserved, fill); return reserved;
                case "ASSERT":
                    RequireCount(operands, 1);
                    if (strict && Eval(args) == 0) throw new FormatException($"Assertion failed: {args}");
                    return [];
                case "PHASE":
                    RequireCount(operands, 1);
                    logical = Address(Eval(args)); phaseOffsets.Push(logical - physical); return [];
                case "DEPHASE":
                    RequireCount(operands, 0);
                    if (phaseOffsets.Count == 0) throw new FormatException("DEPHASE without PHASE.");
                    phaseOffsets.Pop(); logical = physical + (phaseOffsets.Count == 0 ? 0 : phaseOffsets.Peek()); return [];
                case "INCLUDE":
                    RequireCount(operands, 1);
                    Process(ReadLines(FindFile(operands[0], line.File)), depth + 1); return [];
                case "INCBIN":
                    if (operands.Length is < 1 or > 3) throw new FormatException("INCBIN expects file[,offset[,length]].");
                    var input = File.ReadAllBytes(FindFile(operands[0], line.File));
                    var offset = operands.Length > 1 ? Eval(operands[1]) : 0;
                    var size = operands.Length > 2 ? Eval(operands[2]) : input.Length - offset;
                    if (offset < 0 || size < 0 || (long)offset + size > input.Length) throw new FormatException("INCBIN range outside file.");
                    return input.AsSpan(offset, size).ToArray();
                case "OUTPUT":
                    RequireCount(operands, 1); outputPath = OutputFile(operands[0], line.File); return [];
                case "SAVEBIN":
                    RequireCount(operands, 3);
                    var start = Address(Eval(operands[1])); var length = Eval(operands[2]);
                    if (length < 0 || (long)start + length > 65536) throw new FormatException("SAVEBIN range outside address space.");
                    outputs.Add(new(OutputFile(operands[0], line.File), start, length)); return [];
                default:
                    return encoder.Encode(op, operands, Eval, logical, strict);
            }
        }

        private string FindFile(string operand, string source)
        {
            var name = StringValue(operand);
            var paths = new[] { Path.GetDirectoryName(source)! }.Concat(options.IncludePaths);
            foreach (var directory in paths)
            {
                var path = Path.GetFullPath(Path.Combine(directory, name));
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException($"Include file '{name}' not found.");
        }
        private static string OutputFile(string operand, string source) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, StringValue(operand)));
        private static int Address(int value) => value is < 0 or > 65535 ? throw new FormatException("Address outside 0..65535.") : value;
        private byte Byte(int value) => strict && (value < -128 || value > 255) ? throw new FormatException($"Byte operand {value} outside -128..255.") : unchecked((byte)value);
        private static void RequireCount(string[] args, int count)
        { if (args.Length != count) throw new FormatException($"Expected {count} operands."); }
    }

    private static string Substitute(string text, string[] parameters, string[] values)
    {
        var replacements = parameters.Zip(values).ToDictionary(p => p.First.Trim(), p => p.Second, StringComparer.OrdinalIgnoreCase);
        return Regex.Replace(text, "\"(?:[^\"]|\"\")*\"|'[^']'|;.*|[A-Za-z_][A-Za-z0-9_]*", m =>
            (char.IsLetter(m.Value[0]) || m.Value[0] == '_') && replacements.TryGetValue(m.Value, out var value) ? value : m.Value);
    }

    internal static string StripComment(string text)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            if (!quoted && text[i] == '\'' && i + 2 < text.Length && text[i + 2] == '\'') { i += 2; continue; }
            if (!quoted && text[i] == ';') return text[..i];
        }
        return text;
    }

    internal static string[] SplitArguments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<string>();
        var start = 0; var level = 0; var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c == '\'' && i + 2 < text.Length && text[i + 2] == '\'') { i += 2; continue; }
            if (c == '(') level++;
            if (c == ')') level--;
            if (c == ',' && level == 0) { result.Add(text[start..i].Trim()); start = i + 1; }
        }
        if (quoted || level != 0) throw new FormatException("Unclosed string or parentheses.");
        result.Add(text[start..].Trim());
        if (result.Any(string.IsNullOrEmpty)) throw new FormatException("Empty operand.");
        return result.ToArray();
    }

    private static byte[] StringBytes(string value)
    {
        var text = StringValue(value);
        if (text.Any(c => c > 255)) throw new FormatException("String contains a character outside byte range.");
        return text.Select(c => (byte)c).ToArray();
    }
    private static string StringValue(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') throw new FormatException("Expected a double quoted string.");
        return value[1..^1].Replace("\"\"", "\"");
    }
}
