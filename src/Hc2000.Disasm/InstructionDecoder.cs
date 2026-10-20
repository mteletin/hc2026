using Hc2000.Z80;

namespace Hc2000.Disasm;

internal sealed record DecodedInstruction(int Offset, int Address, int Length, OpcodeEntry? Entry, int ExtraPrefixes, bool Exact)
{
    public int OperandOffset(OperandSlot slot) => Offset + ExtraPrefixes + slot.ByteOffset;
}

/// <summary>Prefix stream selection only; encoding definitions remain in Hc2000.Z80.Opcodes.</summary>
internal sealed class InstructionDecoder(Opcodes catalog)
{
    public DecodedInstruction Decode(byte[] bytes, int offset, int address, int available)
    {
        var end = Math.Min(bytes.Length, offset + available);
        var cursor = offset; var index = (byte)0; var prefixes = 0;
        while (cursor < end && bytes[cursor] is 0xdd or 0xfd) { index = bytes[cursor++]; prefixes++; }
        if (cursor == end) return new(offset, address, end - offset, null, 0, false);
        var opcode = bytes[cursor++]; var family = PrefixFamily.Base; var extra = prefixes;
        if (opcode == 0xcb)
        {
            if (index != 0)
            {
                family = index == 0xdd ? PrefixFamily.DDCB : PrefixFamily.FDCB;
                if (cursor + 1 >= end) return new(offset, address, end - offset, null, 0, false);
                cursor++; extra = prefixes - 1; // indexed CB displacement precedes opcode
            }
            else family = PrefixFamily.CB;
            if (cursor == end) return new(offset, address, end - offset, null, 0, false);
            opcode = bytes[cursor++];
        }
        else if (opcode == 0xed)
        {
            family = PrefixFamily.ED; // DD/FD are ignored before ED
            if (cursor == end) return new(offset, address, end - offset, null, 0, false);
            opcode = bytes[cursor++];
        }
        else if (index != 0) { family = index == 0xdd ? PrefixFamily.DD : PrefixFamily.FD; extra = prefixes - 1; }
        if (!catalog.TryGet(new(family, opcode), out var entry) || entry is null)
            return new(offset, address, cursor - offset, null, 0, false);
        var length = entry.Length + extra;
        if (offset + length > end) return new(offset, address, end - offset, null, 0, false);
        return new(offset, address, length, entry, extra, extra == 0 && entry.CanonicalForAssembly);
    }
}
