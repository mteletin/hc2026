using System.Text;
using Hc2000.Disasm;

namespace Hc2000.App.Debugging;

/// <summary>UI formatting of copied state only. No dependency on live devices.</summary>
public static class DebuggerDisplay
{
    public static string Registers(DebuggerSnapshot view)
    {
        var r = view.Registers;
        return $"Boundary T={view.CpuTStates}\nPC={r.Pc:X4} SP={r.Sp:X4}\n" +
            $"AF={Hex(r.Af)} BC={Hex(r.Bc)} DE={Hex(r.De)} HL={Hex(r.Hl)}\n" +
            $"AF'={Hex(r.AfPrime)} BC'={Hex(r.BcPrime)} DE'={Hex(r.DePrime)} HL'={Hex(r.HlPrime)}\n" +
            $"IX={Hex(r.Ix)} IY={Hex(r.Iy)} WZ={Hex(r.Wz)}\n" +
            $"I={HexByte(r.I)} R={HexByte(r.R)} IM={r.Im?.ToString() ?? "unavailable"}\n" +
            $"IFF1={Bit(r.Iff1)} IFF2={Bit(r.Iff2)} HALT={Bit(r.Halted)} Q={Bit(r.Q)}\n" +
            (view.Trigger is { } t ? $"Stop trigger: {t.Kind} @{t.Address:X4} value={HexByte(t.Value)} T={t.CpuTStates}\n" : "No breakpoint trigger\n");
    }

    /// <summary>The disassembled instruction at the boundary PC. An entry pinned at the PC makes the
    /// disassembler decode it; the first content line after the ORG is that instruction. Data with no
    /// decodable opcode falls back to the byte as DB.</summary>
    public static string Instruction(DebuggerSnapshot view)
    {
        var pc = view.Registers.Pc;
        var length = Math.Min(32, 65536 - pc);
        var result = view.Disassemble(pc, length, ControlFile.Parse($"entry {pc:X4}"));
        var lines = result.Source.Split('\n');
        var org = Array.FindIndex(lines, l => l.TrimStart().StartsWith("ORG 0x", StringComparison.Ordinal));
        for (var i = org + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.Length > 0 && !line.StartsWith(";", StringComparison.Ordinal) && !line.TrimEnd().EndsWith(':')) return line.TrimEnd();
        }
        return $"DB 0x{view.Memory[pc]:X2}";
    }

    /// <summary>The active memory configuration at the boundary: the paging latches and what the PC
    /// resolves to in the current page map, with the bytes it sits on.</summary>
    public static string MemoryConfiguration(DebuggerSnapshot view)
    {
        var pc = view.Registers.Pc;
        var location = view.Locations[pc];
        var bytes = string.Join(" ", Enumerable.Range(0, 8).Select(i => view.Memory[(ushort)((pc + i) & 0xFFFF)].ToString("X2")));
        return $"Paging: 7E={view.Latches.Port7E:X2} CPM={view.Latches.Cpm} Shadow={view.Latches.Shadow}\n" +
               $"Bank selector={view.Latches.BankSelector:X2} Paged bank={view.Latches.PagedBank:X4} Control={view.Latches.PagedControl:X2} Enabled={view.Latches.PagedEnabled} Transfer={view.Latches.PagedTransfer}\n" +
               $"PC @ {pc:X4}: {location.Store}+{location.Offset:X6} {(location.Writable ? "RW" : "RO")}\n" +
               $"Bytes @ {pc:X4}: {bytes}";
    }

    public static string Fdc(DebuggerSnapshot view)
    {
        if (view.Fdc is not { } f) return "FDC unavailable (no boundary snapshot supplied)";
        return $"Phase={f.Phase} MSR={f.MainStatus:X2}\n" +
            $"Pending command: {string.Join(' ', f.Command.Select(b => b.ToString("X2")))}\n" +
            $"Last command: {string.Join(' ', f.LastCommand.Select(b => b.ToString("X2")))}\n" +
            $"Last reported ST0={HexByte(f.LastSt0)} ST1={HexByte(f.LastSt1)} ST2={HexByte(f.LastSt2)} ST3={HexByte(f.LastSt3)}\n" +
            $"Controller tracks: {string.Join(' ', f.PresentCylinders.Select((b, i) => $"{i}:{b}"))}\n";
    }

    public static string Memory(DebuggerSnapshot view, int start, int length)
    {
        if (start < 0 || start > 65535 || length < 1 || length > 65536 - start) throw new ArgumentOutOfRangeException(nameof(length));
        var text = new StringBuilder($"Paging: 7E={view.Latches.Port7E:X2} CPM={view.Latches.Cpm} Shadow={view.Latches.Shadow}\n");
        for (var a = start; a < start + length; a++)
        {
            var l = view.Locations[a];
            text.Append($"{a:X4}  {view.Memory[a]:X2}  {l.Store}+{l.Offset:X6} {(l.Writable ? "RW" : "RO")}\n");
        }
        return text.ToString();
    }

    private static string Hex(ushort? n) => n?.ToString("X4") ?? "unavailable";
    private static string HexByte(byte? n) => n?.ToString("X2") ?? "unavailable";
    private static string Bit(bool? n) => n is null ? "unavailable" : n.Value ? "1" : "0";
}
