using System.Collections.ObjectModel;

namespace Hc2000.Z80;

public enum PrefixFamily { Base, CB, ED, DD, FD, DDCB, FDCB }
public enum OperandKind { Byte, WordLE, Displacement, Relative }
public readonly record struct DecoderKey(PrefixFamily PrefixFamily, byte Opcode);
public readonly record struct OperandSlot(OperandKind Kind, int ByteOffset, string PatternToken);

/// <summary>One concrete encoding. All collections are immutable snapshots.</summary>
public sealed class OpcodeEntry
{
    public DecoderKey DecoderKey { get; }
    public string Pattern { get; }
    public ReadOnlyCollection<byte> Encoding { get; }
    public ReadOnlyCollection<OperandSlot> Operands { get; }
    public int Length => Encoding.Count;
    public bool CanonicalForAssembly { get; }

    internal OpcodeEntry(DecoderKey key, string pattern, byte[] encoding, OperandSlot[] operands, bool canonical)
    {
        DecoderKey = key;
        Pattern = pattern;
        Encoding = Array.AsReadOnly((byte[])encoding.Clone());
        Operands = Array.AsReadOnly((OperandSlot[])operands.Clone());
        CanonicalForAssembly = canonical;
    }
}

/// <summary>
/// Instance-owned Z80 encoding authority. Parser, expression evaluation and
/// range diagnostics belong to the assembler. Noncanonical encodings require
/// DB fallback for lossless disassembly (hc2000-shared-opcode-contract).
/// </summary>
public sealed class Opcodes
{
    private readonly Dictionary<DecoderKey, OpcodeEntry> byKey = [];
    // Same catalog entries, dense instance-owned lookup for the allocation-free CPU hot path.
    private readonly OpcodeEntry?[] byIndex = new OpcodeEntry?[7 * 256];
    public ReadOnlyCollection<OpcodeEntry> Entries { get; }

    public Opcodes()
    {
        foreach (var family in new[] { PrefixFamily.Base, PrefixFamily.DD, PrefixFamily.FD })
            for (var opcode = 0; opcode < 256; opcode++)
                AddBase(family, (byte)opcode);
        foreach (var family in new[] { PrefixFamily.CB, PrefixFamily.DDCB, PrefixFamily.FDCB })
            for (var opcode = 0; opcode < 256; opcode++)
                AddCb(family, (byte)opcode);
        for (var opcode = 0; opcode < 256; opcode++)
            AddEd((byte)opcode);
        Entries = Array.AsReadOnly(byKey.Values.ToArray());
    }

    // CPU decoding only supplies complete, valid byte/family pairs. Keep its hot
    // lookup separate from the public checked API without duplicating encoding data.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal DecoderKey Decode(PrefixFamily family, byte opcode)
        => byIndex[(int)family * 256 + opcode]!.DecoderKey;

    public bool TryGet(DecoderKey key, out OpcodeEntry? entry)
    {
        if ((uint)key.PrefixFamily >= 7) { entry = null; return false; }
        entry = byIndex[(int)key.PrefixFamily * 256 + key.Opcode];
        return entry != null;
    }
    public OpcodeEntry this[DecoderKey key] => TryGet(key, out var entry) ? entry! : throw new KeyNotFoundException($"Unknown encoding: {key}");

    private void Add(PrefixFamily family, byte opcode, string pattern, bool canonical = true)
    {
        var bytes = new List<byte>();
        if (family is PrefixFamily.DD or PrefixFamily.DDCB) bytes.Add(0xdd);
        if (family is PrefixFamily.FD or PrefixFamily.FDCB) bytes.Add(0xfd);
        if (family is PrefixFamily.CB or PrefixFamily.DDCB or PrefixFamily.FDCB) bytes.Add(0xcb);
        if (family == PrefixFamily.ED) bytes.Add(0xed);
        var slots = new List<OperandSlot>();
        // Indexed CB places its displacement before the final opcode.
        if (family is PrefixFamily.DDCB or PrefixFamily.FDCB)
        {
            slots.Add(new OperandSlot(OperandKind.Displacement, bytes.Count, "d"));
            bytes.Add(0);
        }
        bytes.Add(opcode);
        if (pattern.Contains("+d", StringComparison.Ordinal) && family is PrefixFamily.DD or PrefixFamily.FD)
        {
            slots.Add(new OperandSlot(OperandKind.Displacement, bytes.Count, "d"));
            bytes.Add(0);
        }
        // Concrete patterns reserve lowercase tokens exclusively for operands.
        if (pattern.Contains("nn", StringComparison.Ordinal))
        {
            slots.Add(new OperandSlot(OperandKind.WordLE, bytes.Count, "nn"));
            bytes.Add(0); bytes.Add(0);
        }
        else if (pattern.Contains('n'))
        {
            slots.Add(new OperandSlot(OperandKind.Byte, bytes.Count, "n"));
            bytes.Add(0);
        }
        if (pattern.EndsWith('e'))
        {
            slots.Add(new OperandSlot(OperandKind.Relative, bytes.Count, "e"));
            bytes.Add(0);
        }
        var key = new DecoderKey(family, opcode);
        var entry = new OpcodeEntry(key, pattern, bytes.ToArray(), slots.ToArray(), canonical);
        byKey.Add(key, entry);
        byIndex[(int)family * 256 + opcode] = entry;
    }

    private void AddBase(PrefixFamily family, byte opcode)
    {
        if (opcode is 0xcb or 0xdd or 0xed or 0xfd) return; // prefix, not a complete instruction
        var x = opcode >> 6;
        var y = (opcode >> 3) & 7;
        var z = opcode & 7;
        var p = y >> 1;
        var q = y & 1;
        var indexed = family != PrefixFamily.Base;
        var index = family == PrefixFamily.FD ? "IY" : "IX";
        var hl = indexed ? index : "HL";
        var memory = indexed ? $"({index}+d)" : "(HL)";
        string Reg(int r, bool plainHl = false) => r switch
        {
            0 => "B", 1 => "C", 2 => "D", 3 => "E", 4 => indexed && !plainHl ? index + "H" : "H",
            5 => indexed && !plainHl ? index + "L" : "L", 6 => memory, _ => "A"
        };
        string Pair(int r, bool stack = false) => r switch { 0 => "BC", 1 => "DE", 2 => hl, _ => stack ? "AF" : "SP" };
        string Condition(int c) => c switch { 0 => "NZ", 1 => "Z", 2 => "NC", 3 => "C", 4 => "PO", 5 => "PE", 6 => "P", _ => "M" };
        string pattern;
        if (x == 0)
        {
            pattern = z switch
            {
                0 => y switch { 0 => "NOP", 1 => "EX AF,AF'", 2 => "DJNZ e", 3 => "JR e", _ => $"JR {Condition(y - 4)},e" },
                1 => q == 0 ? $"LD {Pair(p)},nn" : $"ADD {hl},{Pair(p)}",
                2 => y switch
                {
                    0 => "LD (BC),A", 1 => "LD A,(BC)", 2 => "LD (DE),A", 3 => "LD A,(DE)",
                    4 => $"LD (nn),{hl}", 5 => $"LD {hl},(nn)", 6 => "LD (nn),A", _ => "LD A,(nn)"
                },
                3 => $"{(q == 0 ? "INC" : "DEC")} {Pair(p)}",
                4 => $"INC {Reg(y)}",
                5 => $"DEC {Reg(y)}",
                6 => $"LD {Reg(y)},n",
                _ => y switch { 0 => "RLCA", 1 => "RRCA", 2 => "RLA", 3 => "RRA", 4 => "DAA", 5 => "CPL", 6 => "SCF", _ => "CCF" }
            };
        }
        else if (x == 1)
        {
            // DD/FD 66/6E/74/75 use ordinary H/L when paired with indexed memory.
            pattern = opcode == 0x76 ? "HALT" : $"LD {Reg(y, z == 6)},{Reg(z, y == 6)}";
        }
        else if (x == 2)
        {
            pattern = Alu(y, Reg(z));
        }
        else
        {
            pattern = z switch
            {
                0 => $"RET {Condition(y)}",
                1 => q == 0 ? $"POP {Pair(p, true)}" : p switch { 0 => "RET", 1 => "EXX", 2 => $"JP ({hl})", _ => $"LD SP,{hl}" },
                2 => $"JP {Condition(y)},nn",
                3 => y switch { 0 => "JP nn", 2 => "OUT (n),A", 3 => "IN A,(n)", 4 => $"EX (SP),{hl}", 5 => "EX DE,HL", 6 => "DI", _ => "EI" },
                4 => $"CALL {Condition(y)},nn",
                5 => q == 0 ? $"PUSH {Pair(p, true)}" : "CALL nn",
                6 => Alu(y, "n"),
                _ => $"RST {y * 8:X2}h"
            };
        }
        var changed = indexed && (pattern.Contains(index, StringComparison.Ordinal));
        Add(family, opcode, pattern, !indexed || changed);
    }

    private void AddCb(PrefixFamily family, byte opcode)
    {
        var x = opcode >> 6;
        var y = (opcode >> 3) & 7;
        var z = opcode & 7;
        var indexed = family != PrefixFamily.CB;
        var register = PlainReg(z);
        var operand = indexed ? family == PrefixFamily.DDCB ? "(IX+d)" : "(IY+d)" : register;
        var operation = x == 0 ? RotateName(y) : x switch { 1 => $"BIT {y}", 2 => $"RES {y}", _ => $"SET {y}" };
        var pattern = x == 0 ? $"{operation} {operand}" : $"{operation},{operand}";
        if (indexed && x != 1 && z != 6) pattern += $",{register}";
        Add(family, opcode, pattern, !indexed || x != 1 || z == 6);
    }

    private void AddEd(byte opcode)
    {
        var x = opcode >> 6;
        var y = (opcode >> 3) & 7;
        var z = opcode & 7;
        var p = y >> 1;
        var q = y & 1;
        string Pair(int r) => r switch { 0 => "BC", 1 => "DE", 2 => "HL", _ => "SP" };
        var pattern = "NOP";
        var canonical = false;
        if (x == 1)
        {
            pattern = z switch
            {
                0 => y == 6 ? "IN F,(C)" : $"IN {PlainReg(y)},(C)",
                1 => y == 6 ? "OUT (C),0" : $"OUT (C),{PlainReg(y)}",
                2 => $"{(q == 0 ? "SBC" : "ADC")} HL,{Pair(p)}",
                3 => q == 0 ? $"LD (nn),{Pair(p)}" : $"LD {Pair(p)},(nn)",
                4 => "NEG",
                5 => opcode == 0x4d ? "RETI" : "RETN",
                6 => $"IM {(y is 2 or 6 ? 1 : y is 3 or 7 ? 2 : 0)}",
                _ => y switch { 0 => "LD I,A", 1 => "LD R,A", 2 => "LD A,I", 3 => "LD A,R", 4 => "RRD", 5 => "RLD", _ => "NOP" }
            };
            canonical = z switch
            {
                3 => p != 2, // HL forms canonically use the shorter base encoding
                4 => opcode == 0x44,
                5 => opcode is 0x45 or 0x4d,
                6 => opcode is 0x46 or 0x56 or 0x5e,
                7 => y < 6,
                _ => true
            };
        }
        else if (x == 2 && y >= 4 && z < 4)
        {
            pattern = (y, z) switch
            {
                (4, 0) => "LDI", (4, 1) => "CPI", (4, 2) => "INI", (4, 3) => "OUTI",
                (5, 0) => "LDD", (5, 1) => "CPD", (5, 2) => "IND", (5, 3) => "OUTD",
                (6, 0) => "LDIR", (6, 1) => "CPIR", (6, 2) => "INIR", (6, 3) => "OTIR",
                (7, 0) => "LDDR", (7, 1) => "CPDR", (7, 2) => "INDR", _ => "OTDR"
            };
            canonical = true;
        }
        Add(PrefixFamily.ED, opcode, pattern, canonical);
    }

    internal static string PlainReg(int r) => r switch { 0 => "B", 1 => "C", 2 => "D", 3 => "E", 4 => "H", 5 => "L", 6 => "(HL)", _ => "A" };
    internal static string RotateName(int y) => y switch { 0 => "RLC", 1 => "RRC", 2 => "RL", 3 => "RR", 4 => "SLA", 5 => "SRA", 6 => "SLL", _ => "SRL" };
    private static string Alu(int y, string operand) => y switch
    {
        0 => $"ADD A,{operand}", 1 => $"ADC A,{operand}", 2 => $"SUB {operand}", 3 => $"SBC A,{operand}",
        4 => $"AND {operand}", 5 => $"XOR {operand}", 6 => $"OR {operand}", _ => $"CP {operand}"
    };
}
