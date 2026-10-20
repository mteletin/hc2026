using System.Text;

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
