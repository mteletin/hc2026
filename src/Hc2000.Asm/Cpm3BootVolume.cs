using System.Buffers.Binary;

namespace Hc2000.Asm;

/// <summary>Prepares the opt-in CP/M 3 reset media with the linked system, pinned CCP, and dated directory support.</summary>
public static class Cpm3BootVolume
{
    private const int BlockSize = 0x4000;

    public static byte[] Build(ReadOnlySpan<byte> system, ReadOnlySpan<byte> ccp, ReadOnlySpan<byte> dirCom)
    {
        if (system.Length != 0x10000) throw new ArgumentException("Expected CPM3-RAM.BIN.", nameof(system));
        if (ccp.IsEmpty || ccp.Length > BlockSize || ccp.Length % 128 != 0)
            throw new ArgumentException("Expected a record-aligned CCP.COM of at most one block.", nameof(ccp));
        if (dirCom.IsEmpty || dirCom.Length > BlockSize || dirCom.Length % 128 != 0)
            throw new ArgumentException("Expected a record-aligned DIR.COM of at most one block.", nameof(dirCom));
        var volume = new byte[8 * 1024 * 1024];
        volume.AsSpan(0, BlockSize).Fill(0xe5);
        for (var slot = 3; slot < BlockSize / 32; slot += 4)
        {
            var sfcb = volume.AsSpan(slot * 32, 32);
            sfcb.Clear();
            sfcb[0] = 0x21;
        }
        var label = volume.AsSpan(0, 32);
        label.Clear();
        label[0] = 0x20;
        "HC2000CPM3 "u8.CopyTo(label[1..]);
        label[12] = 0x31; // Enable creation and update stamps, with a valid label.
        var payload = volume.AsSpan(BlockSize, 3 * BlockSize);
        "HC2KCPM3"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[10..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[12..], 0x6660);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[16..], Checksum(payload[..16]));
        ReadOnlySpan<byte> table = [0x00, 0x78, 0x00, 0x63, 0xa0, 0xdb, 0x60, 0x03];
        table.CopyTo(payload[0x40..]);
        system[0x7800..0xdb00].CopyTo(payload[0x4000..]);
        system[0xdba0..0xdf00].CopyTo(payload[0xa300..]);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[18..], Checksum(payload[0x4000..0xa660]));
        // One physical extent (EXM=7), ending in logical extent 2 with 128 records.
        Entry(volume.AsSpan(32, 32), "CPM3IMG 000"u8, 2, 128, [1, 2, 3]);
        Entry(volume.AsSpan(64, 32), "CCP     COM"u8, 0, (byte)(ccp.Length / 128), [4]);
        ccp.CopyTo(volume.AsSpan(4 * BlockSize));
        // Run the unmodified DRI DIR.COM as LST, avoiding the CCP built-in DIR.
        Entry(volume.AsSpan(4 * 32, 32), "LST     COM"u8, 0, (byte)(dirCom.Length / 128), [5]);
        dirCom.CopyTo(volume.AsSpan(5 * BlockSize));
        return volume;
    }

    private static void Entry(Span<byte> entry, ReadOnlySpan<byte> name, byte extent, byte records, ReadOnlySpan<ushort> blocks)
    {
        entry.Clear();
        name.CopyTo(entry[1..]);
        entry[12] = extent;
        entry[15] = records;
        for (var i = 0; i < blocks.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(entry[(16 + i * 2)..], blocks[i]);
    }

    private static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        byte first = 0, second = 0;
        foreach (var value in bytes)
        {
            first = unchecked((byte)(first + value));
            second = unchecked((byte)(second + first));
        }
        return (ushort)(first | (second << 8));
    }
}
