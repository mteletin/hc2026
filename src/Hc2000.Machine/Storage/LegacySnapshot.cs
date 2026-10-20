using System.Buffers.Binary;

namespace Hc2000.Machine.Storage;

/// <summary>Architectural legacy registers only, not a core continuation state.</summary>
public sealed record LegacyRegisters(
    ushort AF, ushort BC, ushort DE, ushort HL,
    ushort AFPrime, ushort BCPrime, ushort DEPrime, ushort HLPrime,
    ushort IX, ushort IY, ushort PC, ushort SP,
    byte I, byte R, bool IFF1, bool IFF2, byte IM)
{
    public byte A => (byte)(AF >> 8);
    public byte F => (byte)AF;
    public byte APrime => (byte)(AFPrime >> 8);
    public byte FPrime => (byte)AFPrime;
}

[Flags]
public enum LegacySnapshotWarnings
{
    None = 0,
    IgnoredZ80HardwareHints = 1,
    IgnoredSnaInterruptBits = 2
}

public enum LegacySnapshotError
{
    InvalidLength, InputTooLarge, UnsupportedVersion, UnsupportedHardware,
    InvalidHeader, InvalidStackPointer, InvalidCompression
}

public sealed class LegacySnapshotException(LegacySnapshotError error, string message) : FormatException(message)
{
    public LegacySnapshotError Error { get; } = error;
}

/// <summary>Detached 48K layout data; decoding never mutates or creates a Machine.</summary>
public sealed class LegacySnapshot
{
    private readonly byte[] ram;
    internal LegacySnapshot(LegacyRegisters registers, byte border, byte[] ram, LegacySnapshotWarnings warnings)
    {
        Registers = registers;
        Border = border;
        this.ram = ram;
        Warnings = warnings;
    }
    public LegacyRegisters Registers { get; }
    public byte Border { get; }
    public LegacySnapshotWarnings Warnings { get; }
    /// <summary>Copy of RAM mapped at 4000..FFFF; no writable storage is shared.</summary>
    public byte[] CopyRam() => (byte[])ram.Clone();
}

/// <summary>Only .z80 v1 48K and exact-length 48K .sna. Not live import or .hcs.</summary>
public static class LegacySnapshotDecoder
{
    public const int RamBytes = 49152;
    public const int MaximumZ80Bytes = 30 + 4 * RamBytes + 4;

    public static LegacySnapshot DecodeZ80(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumZ80Bytes)
            throw Error(LegacySnapshotError.InputTooLarge, "Z80 input exceeds the bounded v1 layout.");
        if (bytes.Length < 30)
            throw Error(LegacySnapshotError.InvalidLength, "Truncated Z80 header.");
        var pc = Word(bytes, 6);
        if (pc == 0)
            throw Error(LegacySnapshotError.UnsupportedVersion, "Extended Z80 versions are unsupported (header PC is zero).");
        var flags = bytes[12] == 0xff ? 1 : bytes[12];
        if ((flags & 0x10) != 0)
            throw Error(LegacySnapshotError.UnsupportedHardware, "SamRom is unsupported.");
        var im = (byte)(bytes[29] & 3);
        if (im == 3)
            throw Error(LegacySnapshotError.InvalidHeader, "Invalid Z80 interrupt mode.");
        byte[] ram;
        if ((flags & 0x20) != 0)
            ram = ExpandZ80(bytes[30..]);
        else
        {
            if (bytes.Length != 30 + RamBytes)
                throw Error(LegacySnapshotError.InvalidLength, "Raw Z80 v1 requires exactly 48K RAM and EOF.");
            ram = bytes[30..].ToArray();
        }
        var registers = new LegacyRegisters(
            (ushort)((bytes[0] << 8) | bytes[1]), Word(bytes, 2), Word(bytes, 13), Word(bytes, 4),
            (ushort)((bytes[21] << 8) | bytes[22]), Word(bytes, 15), Word(bytes, 17), Word(bytes, 19),
            Word(bytes, 25), Word(bytes, 23), pc, Word(bytes, 8), bytes[10],
            (byte)((bytes[11] & 0x7f) | ((flags & 1) << 7)), bytes[27] != 0, bytes[28] != 0, im);
        return new(registers, (byte)((flags >> 1) & 7), ram,
            (bytes[29] & 0xfc) == 0 ? LegacySnapshotWarnings.None : LegacySnapshotWarnings.IgnoredZ80HardwareHints);
    }

    public static LegacySnapshot DecodeSna(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 27 + RamBytes)
            throw Error(LegacySnapshotError.InvalidLength, "Only exact-length 48K SNA is supported.");
        if (bytes[25] > 2 || bytes[26] > 7)
            throw Error(LegacySnapshotError.InvalidHeader, "Invalid SNA interrupt mode or border.");
        var sp = Word(bytes, 23);
        if (sp < 0x4000 || sp == 0xffff)
            throw Error(LegacySnapshotError.InvalidStackPointer, "Both SNA PC stack bytes must be in RAM.");
        var pc = Word(bytes[27..], sp - 0x4000);
        var iff = (bytes[19] & 4) != 0;
        var registers = new LegacyRegisters(Word(bytes, 21), Word(bytes, 13), Word(bytes, 11), Word(bytes, 9),
            Word(bytes, 7), Word(bytes, 5), Word(bytes, 3), Word(bytes, 1),
            Word(bytes, 17), Word(bytes, 15), pc, unchecked((ushort)(sp + 2)), bytes[0], bytes[20], iff, iff, bytes[25]);
        return new(registers, bytes[26], bytes[27..].ToArray(),
            (bytes[19] & ~4) == 0 ? LegacySnapshotWarnings.None : LegacySnapshotWarnings.IgnoredSnaInterruptBits);
    }

    private static byte[] ExpandZ80(ReadOnlySpan<byte> input)
    {
        ReadOnlySpan<byte> terminator = [0x00, 0xed, 0xed, 0x00];
        var ram = new byte[RamBytes];
        var source = 0;
        var target = 0;
        while (target < ram.Length)
        {
            if (source >= input.Length || input[source..].StartsWith(terminator))
                throw Error(LegacySnapshotError.InvalidCompression, "Short RAM or premature terminator.");
            if (input[source] == 0xed && source + 1 < input.Length && input[source + 1] == 0xed)
            {
                if (input.Length - source < 4)
                    throw Error(LegacySnapshotError.InvalidCompression, "Truncated RLE token.");
                var count = input[source + 2];
                if (count == 0 || count > ram.Length - target)
                    throw Error(LegacySnapshotError.InvalidCompression, "Zero or overflowing RLE count.");
                ram.AsSpan(target, count).Fill(input[source + 3]);
                source += 4;
                target += count;
            }
            else
                ram[target++] = input[source++]; // a solitary ED is a literal
        }
        if (!input[source..].SequenceEqual(terminator))
            throw Error(LegacySnapshotError.InvalidCompression, "Exact 48K output must be followed by the terminator and EOF.");
        return ram;
    }

    private static ushort Word(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static LegacySnapshotException Error(LegacySnapshotError error, string message) => new(error, message);
}
