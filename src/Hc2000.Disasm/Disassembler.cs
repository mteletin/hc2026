using System.Globalization;
using System.Text;
using Hc2000.Z80;

namespace Hc2000.Disasm;

public sealed record DisasmStatistics(int CodeBytes, int DataBytes, int UnknownBytes);
public sealed class DisassemblyResult
{
    public string Source { get; internal init; } = "";
    public string CrossReferences { get; internal init; } = "";
    public DisasmStatistics Statistics { get; internal init; } = new(0, 0, 0);
    public IReadOnlyList<DisasmDiagnostic> Diagnostics { get; internal init; } = [];
    public bool Success => Diagnostics.Count == 0;
}

public sealed class Disassembler
{
    private readonly InstructionDecoder decoder = new(new Opcodes());

    public DisassemblyResult Disassemble(byte[] binary, int origin, ControlFile? control = null, ExecutionMap? map = null, string? mapConfig = null)
    {
        ArgumentNullException.ThrowIfNull(binary);
        if (origin < 0 || origin > 65535 || binary.Length > 65536 - origin) throw new ArgumentOutOfRangeException(nameof(origin));
        return new Analysis(decoder, (byte[])binary.Clone(), origin, control ?? ControlFile.Parse(""), map, mapConfig).Run();
    }

    private sealed class Analysis
    {
        private readonly InstructionDecoder decoder;
        private readonly byte[] bytes;
        private readonly int origin;
        private readonly ControlFile control;
        private readonly List<DisasmDiagnostic> diagnostics;
        private readonly int[] logical;
        private readonly int[] contiguous;
        private readonly Dictionary<int, int> physical = [];
        private readonly ControlDirective?[] regions;
        private readonly bool[] data;
        private readonly int[] owner;
        private readonly ExecutionMap? map;
        private readonly string? mapConfig;
        private readonly ExecutionFlags[] flags;
        private readonly bool[] mapCode;
        private readonly Dictionary<int, DecodedInstruction> instructions = [];
        private readonly Dictionary<int, string> labels = [];
        private readonly Dictionary<int, string> ports = [];
        private readonly Dictionary<int, string> equs = [];
        private readonly Dictionary<int, string> comments = [];
        private readonly Dictionary<string, SortedSet<int>> references = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<int> pending = [];
        private readonly HashSet<int> scheduled = [];

        public Analysis(InstructionDecoder decoder, byte[] bytes, int origin, ControlFile control, ExecutionMap? map, string? mapConfig)
        {
            this.decoder = decoder; this.bytes = bytes; this.origin = origin; this.control = control;
            this.map = map; this.mapConfig = mapConfig;
            flags = new ExecutionFlags[bytes.Length]; mapCode = new bool[bytes.Length];
            diagnostics = control.Diagnostics.ToList();
            logical = new int[bytes.Length]; contiguous = new int[bytes.Length]; regions = new ControlDirective?[bytes.Length];
            data = new bool[bytes.Length]; owner = Enumerable.Repeat(-1, bytes.Length).ToArray();
        }

        public DisassemblyResult Run()
        {
            if (!control.Success) return new() { Diagnostics = diagnostics.AsReadOnly() };
            for (var i = 0; i < bytes.Length; i++)
            {
                var address = origin + i;
                var phase = control.Directives.FirstOrDefault(d => d.Kind == "phase" && d.Start <= address && address <= d.End);
                logical[i] = phase is null ? address : phase.Value + address - phase.Start;
                if (!physical.TryAdd(logical[i], i)) Error(phase?.Line ?? 0, $"Ambiguous logical address {logical[i]:X4}");
            }
            for (var i = bytes.Length - 1; i >= 0; i--)
                contiguous[i] = 1 + (i + 1 < bytes.Length && logical[i + 1] == ((logical[i] + 1) & 65535) ? contiguous[i + 1] : 0);
            // Read explicit symbols first; table-derived automatic labels must never win by line order.
            foreach (var directive in control.Directives)
            {
                switch (directive.Kind)
                {
                    case "label":
                        if (labels.TryGetValue(directive.Start, out var name) && name != directive.Argument) Error(directive.Line, "Multiple names for label address");
                        else labels[directive.Start] = directive.Argument;
                        break;
                    case "port": ports[directive.Start] = directive.Argument; break;
                    case "equ": equs[directive.Start] = directive.Argument; break;
                    case "comment": comments[directive.Start] = directive.Argument; break;
                }
            }
            foreach (var directive in control.Directives)
            {
                if (ControlFile.IsRegion(directive.Kind) || directive.Kind == "phase")
                {
                    if (directive.Start < origin || directive.End >= origin + bytes.Length) { Error(directive.Line, "Range outside binary"); continue; }
                    if (directive.Kind == "phase") continue;
                    for (var i = directive.Start - origin; i <= directive.End - origin; i++)
                    { regions[i] = directive; data[i] = directive.Kind is not ("code" or "jumptable"); }
                    if (directive.Kind == "fill" && Enumerable.Range(directive.Start - origin, directive.End - directive.Start + 1).Any(i => bytes[i] != directive.Value))
                        Error(directive.Line, "Fill range contains a different byte");
                    if (directive.Kind == "words" && (directive.End - directive.Start + 1) % 2 != 0) Error(directive.Line, "Word range has odd length");
                    if (directive.Kind == "jumptable")
                    {
                        if ((directive.End - directive.Start + 1) % 3 != 0) Error(directive.Line, "JP table length is not divisible by three");
                        for (var i = directive.Start - origin; i + 2 <= directive.End - origin; i += 3)
                        {
                            if (bytes[i] != 0xc3) { Error(directive.Line, $"JP table entry at {origin + i:X4} is not JP"); continue; }
                            Schedule(logical[i]);
                            Target(bytes[i + 1] | bytes[i + 2] << 8, logical[i]);
                        }
                    }
                }
            }
            ApplyMap();
            foreach (var d in control.Directives.Where(d => d.Kind == "entry")) Schedule(d.Start);
            // An explicit code range is linear code, including bytes after returns.
            foreach (var d in control.Directives.Where(d => d.Kind == "code" && d.Start >= origin && d.End < origin + bytes.Length))
            {
                for (var i = d.Start - origin; i <= d.End - origin;)
                {
                    Schedule(logical[i]);
                    i += decoder.Decode(bytes, i, logical[i], Math.Min(d.End - origin - i + 1, Contiguous(i))).Length;
                }
            }
            while (pending.TryDequeue(out var address)) Trace(address);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (data[i] && (flags[i] & (ExecutionFlags.M1 | ExecutionFlags.Target)) != 0)
                    Error(0, $"Control-file data conflicts with execution-map code at {logical[i]:X4}");
                if (owner[i] < 0 && regions[i] is null && flags[i] == ExecutionFlags.Read) data[i] = true;
            }
            // Xref is semantic even when an alias/prefix stream must be emitted as DB.
            foreach (var instruction in instructions.Values.Where(d => d.Entry is not null)) Format(instruction);
            var source = EmitSource();
            foreach (var name in labels.Values) references.TryAdd(name, []);
            foreach (var name in ports.Values) references.TryAdd(name + " [port]", []);
            foreach (var name in equs.Values)
            {
                references.TryAdd(name + " [read]", []);
                references.TryAdd(name + " [write]", []);
            }
            var code = Enumerable.Range(0, bytes.Length).Count(i => owner[i] >= 0 || (mapCode[i] && !data[i]));
            var knownData = Enumerable.Range(0, bytes.Length).Count(i => data[i] && owner[i] < 0);
            return new()
            {
                Source = source,
                CrossReferences = string.Join(Environment.NewLine, references.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => $"{p.Key}: {string.Join(", ", p.Value.Select(a => a.ToString("X4", CultureInfo.InvariantCulture)))}")) + Environment.NewLine,
                Statistics = new(code, knownData, bytes.Length - code - knownData),
                Diagnostics = diagnostics.AsReadOnly()
            };
        }

        private void ApplyMap()
        {
            if (map is null)
            {
                if (mapConfig is not null) Error(0, "--map-config requires an execution map");
                return;
            }
            var config = map.Select(bytes, mapConfig, diagnostics);
            if (config is null) return;
            // Validate the complete selected layout before applying any evidence.
            foreach (var window in config.Windows)
                for (var j = 0; j < window.Length; j++)
                    if (logical[window.ImageOffset + j] != window.CpuStart + j)
                    { Error(0, "Execution-map window disagrees with origin/PHASE layout"); return; }
            foreach (var window in config.Windows)
            for (var j = 0; j < window.Length; j++)
            {
                var i = window.ImageOffset + j; var address = window.CpuStart + j;
                var observed = config.FlagsAt(address); flags[i] = observed;
                var mismatched = (observed & ExecutionFlags.Mismatch) != 0;
                mapCode[i] = !mismatched && (observed & ExecutionFlags.M1) != 0;
                if (!mismatched && (observed & (ExecutionFlags.Start | ExecutionFlags.Target)) != 0) Schedule(address);
                if (!mismatched && (observed & ExecutionFlags.Target) != 0) Label(address);
                if (mismatched && (observed & (ExecutionFlags.M1 | ExecutionFlags.Start | ExecutionFlags.Target)) != 0
                    && regions[i]?.Kind is not ("code" or "jumptable")) Error(0, $"Execution-map byte mismatch at {address:X4}; retained as DB");
            }
        }

        private void Schedule(int address)
        {
            if (physical.ContainsKey(address) && scheduled.Add(address)) pending.Enqueue(address);
        }
        private string Label(int address)
        {
            if (labels.TryGetValue(address, out var name)) return name;
            name = $"L_{address:X4}";
            var used = labels.Values.Concat(ports.Values).Concat(equs.Values).ToHashSet(StringComparer.OrdinalIgnoreCase);
            while (used.Contains(name)) name += "_";
            labels[address] = name; return name;
        }
        private void Reference(string name, int caller)
        {
            if (!references.TryGetValue(name, out var callers)) references[name] = callers = [];
            callers.Add(caller);
        }
        private void Target(int address, int caller) { Reference(Label(address), caller); Schedule(address); }
        private void Error(int line, string message)
        {
            if (!diagnostics.Any(d => d.Line == line && d.Message == message)) diagnostics.Add(new(line, message));
        }
        private int Contiguous(int i) => contiguous[i];
        private int Value(DecodedInstruction instruction, OperandSlot slot)
        {
            var i = instruction.OperandOffset(slot);
            return slot.Kind switch
            {
                OperandKind.WordLE => bytes[i] | bytes[i + 1] << 8,
                OperandKind.Relative => (instruction.Address + instruction.Length + (sbyte)bytes[i]) & 65535,
                OperandKind.Displacement => (sbyte)bytes[i],
                _ => bytes[i]
            };
        }
        private void Trace(int address)
        {
            if (!physical.TryGetValue(address, out var i)) return;
            while (i < bytes.Length)
            {
                if (owner[i] >= 0)
                {
                    if (owner[i] != i) Error(0, $"Overlapping instruction at {logical[i]:X4} inside {logical[owner[i]]:X4}");
                    return;
                }
                // An explicit data block is authoritative, including intentional overlapping
                // executable byte streams that the ctl chooses to preserve as DB.
                if (data[i]) return;
                var instruction = decoder.Decode(bytes, i, logical[i], Contiguous(i));
                if (Enumerable.Range(i, instruction.Length).Any(p => data[p] || owner[p] >= 0))
                { Error(0, $"Overlapping instruction/data at {logical[i]:X4}"); return; }
                if (Enumerable.Range(i, instruction.Length).Any(p => (flags[p] & ExecutionFlags.Mismatch) != 0 && regions[p]?.Kind is not ("code" or "jumptable")))
                { Error(0, $"Execution-map byte mismatch in instruction at {logical[i]:X4}; retained as DB"); return; }
                instructions[i] = instruction;
                for (var p = i; p < i + instruction.Length; p++) owner[p] = i;
                var pattern = instruction.Entry?.Pattern ?? "";
                var branch = pattern.StartsWith("JP ", StringComparison.Ordinal) || pattern.StartsWith("JR ", StringComparison.Ordinal)
                    || pattern.StartsWith("CALL ", StringComparison.Ordinal) || pattern.StartsWith("DJNZ ", StringComparison.Ordinal);
                var targetSlot = instruction.Entry?.Operands.FirstOrDefault(s => s.Kind is OperandKind.WordLE or OperandKind.Relative);
                if (branch && targetSlot is { } slot && slot.PatternToken is not null) Target(Value(instruction, slot), instruction.Address);
                var next = i + instruction.Length;
                if (pattern.StartsWith("RST ", StringComparison.Ordinal))
                {
                    var vector = ControlFile.Hex(pattern[4..]); Target(vector, instruction.Address);
                    var args = control.Directives.FirstOrDefault(d => d.Kind == "rst" && d.Start == vector);
                    if (args is not null)
                    {
                        var count = args.Value;
                        if (args.Argument == "calc")
                        {
                            count = CalculatorLength(next, args.Line);
                        }
                        if (next + count > bytes.Length) { Error(args.Line, "Truncated RST arguments"); count = bytes.Length - next; }
                        for (var p = next; p < next + count; p++)
                        {
                            if (owner[p] >= 0) Error(args.Line, "RST arguments overlap code");
                            data[p] = true;
                        }
                        next += count;
                    }
                }
                if (pattern is "RET" or "RETI" or "RETN" or "HALT" || pattern.StartsWith("JP (", StringComparison.Ordinal)
                    || pattern is "JP nn" or "JR e") return;
                if (next >= bytes.Length || logical[next] != ((instruction.Address + next - i) & 65535)) return;
                i = next;
            }
        }

        private int CalculatorLength(int start, int line)
        {
            var cursor = start;
            while (cursor < bytes.Length)
            {
                var operation = bytes[cursor++];
                if (operation == 0x38) return cursor - start; // END-CALC
                if (operation is 0x00 or 0x33 or 0x35) cursor++; // jump-true, jump, dec-jr-nz operand
                else if (operation == 0x34) // STK-DATA: descriptor, optional exponent, compressed mantissa
                {
                    if (cursor >= bytes.Length) break;
                    var descriptor = bytes[cursor++];
                    cursor += (descriptor >> 6) + 1 + ((descriptor & 63) == 0 ? 1 : 0);
                }
                if (cursor > bytes.Length) break;
            }
            Error(line, "Unterminated or truncated RST calculator arguments");
            return bytes.Length - start;
        }

        private string Format(DecodedInstruction instruction)
        {
            var pattern = instruction.Entry!.Pattern;
            foreach (var slot in instruction.Entry.Operands.OrderByDescending(s => s.PatternToken.Length))
            {
                var value = Value(instruction, slot); string operand;
                var branch = pattern.StartsWith("JP ", StringComparison.Ordinal) || pattern.StartsWith("CALL ", StringComparison.Ordinal) || slot.Kind == OperandKind.Relative;
                if (branch && slot.Kind is OperandKind.WordLE or OperandKind.Relative) operand = Label(value);
                else if (slot.Kind == OperandKind.WordLE && equs.TryGetValue(value, out var equ))
                {
                    operand = equ;
                    if (pattern.Contains("(nn)", StringComparison.Ordinal)) Reference(equ + (pattern.StartsWith("LD (", StringComparison.Ordinal) ? " [write]" : " [read]"), instruction.Address);
                }
                else if (slot.Kind == OperandKind.Byte && (pattern.StartsWith("IN ", StringComparison.Ordinal) || pattern.StartsWith("OUT ", StringComparison.Ordinal)) && ports.TryGetValue(value, out var port))
                { operand = port; Reference(port + " [port]", instruction.Address); }
                else operand = slot.Kind switch
                {
                    OperandKind.Displacement => value < 0 ? value.ToString(CultureInfo.InvariantCulture) : "+" + value.ToString(CultureInfo.InvariantCulture),
                    OperandKind.WordLE or OperandKind.Relative => $"0x{value:X4}",
                    _ => $"0x{value:X2}"
                };
                pattern = slot.Kind == OperandKind.Displacement ? pattern.Replace("+d", operand, StringComparison.Ordinal)
                    : pattern.Replace(slot.PatternToken, operand, StringComparison.Ordinal);
            }
            return pattern;
        }

        private string EmitSource()
        {
            var output = new StringBuilder("; Generated by hc disasm; edit the adjacent .ctl, not this file.\n");
            // Targets outside the image are equates, never additional output segments.
            foreach (var pair in labels.Where(p => !physical.ContainsKey(p.Key)).Concat(ports).Concat(equs).DistinctBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
                output.AppendLine($"{pair.Value} EQU 0x{pair.Key:X4}");
            output.AppendLine($"ORG 0x{origin:X4}");
            var phased = false; ControlDirective? activePhase = null;
            for (var i = 0; i < bytes.Length;)
            {
                var phase = control.Directives.FirstOrDefault(d => d.Kind == "phase" && d.Start <= origin + i && origin + i <= d.End);
                if (phase != activePhase)
                {
                    if (phased) output.AppendLine("DEPHASE");
                    if (phase is not null) output.AppendLine($"PHASE 0x{logical[i]:X4}");
                    phased = phase is not null; activePhase = phase;
                }
                if (labels.TryGetValue(logical[i], out var label)) output.AppendLine(label + ":");
                if (comments.TryGetValue(logical[i], out var comment)) output.AppendLine("; " + comment);
                var count = 1;
                if (instructions.TryGetValue(i, out var instruction) && instruction.Exact
                    && !Enumerable.Range(i + 1, instruction.Length - 1).Any(p => labels.ContainsKey(logical[p]) || comments.ContainsKey(logical[p])))
                { output.AppendLine("    " + Format(instruction)); count = instruction.Length; }
                else
                {
                    var region = regions[i]; var max = region?.Kind == "data" ? region.Value : 8;
                    if (max <= 0) max = 8;
                    if (region?.Kind == "fill") max = region.End - (origin + i) + 1;
                    while (count < max && i + count < bytes.Length && regions[i + count] == region && owner[i + count] == owner[i]
                        && data[i + count] == data[i] && mapCode[i + count] == mapCode[i] && logical[i + count] == logical[i] + count
                        && !labels.ContainsKey(logical[i + count]) && !comments.ContainsKey(logical[i + count])
                        && (activePhase is null || origin + i + count <= activePhase.End)
                        && !control.Directives.Any(d => d.Kind == "phase" && d.Start == origin + i + count)) count++;
                    var slice = bytes.AsSpan(i, count).ToArray();
                    string line;
                    if (region?.Kind == "fill" && slice.All(b => b == region.Value)) line = $"DS {count},0x{region.Value:X2}";
                    else if (region?.Kind == "words" && count >= 2)
                    {
                        count -= count % 2;
                        line = "DW " + string.Join(',', Enumerable.Range(0, count / 2).Select(n => $"0x{(bytes[i + 2 * n] | bytes[i + 2 * n + 1] << 8):X4}"));
                    }
                    else if (region?.Kind == "text" && slice.All(b => b is >= 32 and <= 126 && b != '"' && b != '\\'))
                        line = "DM \"" + Encoding.ASCII.GetString(slice) + "\"";
                    else line = "DB " + string.Join(',', slice.Select(b => $"0x{b:X2}"));
                    var note = owner[i] >= 0 || mapCode[i] ? "exact-byte fallback" : data[i] ? region?.Kind ?? "data" : "unreached";
                    if (region?.Kind == "font") note += " " + string.Join(' ', slice.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
                    output.AppendLine($"    {line} ; {note}");
                }
                i += count;
            }
            if (phased) output.AppendLine("DEPHASE");
            return output.ToString();
        }
    }
}
