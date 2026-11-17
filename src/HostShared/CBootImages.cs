using System.Security.Cryptography;
using System.Text;

namespace Hc2000.HostFiles;

/// <summary>
/// Host-side C:-boot payload (BOOTIMG.000) builder and verifier (hc2000-direct-c-boot).
/// The file layout is byte-for-byte the one in src-library/extended/bios-upgrade/hdd/cboot.layout.inc:
/// the guest loader refuses a payload whose region table differs from its built-in table,
/// so this table and that include must stay in lockstep. The payload carries the CP/M
/// system state (vectors, low CP/M, BIOS/DPB area, bootstrap, resident, system high area)
/// captured from a real guest run; the C: user files are separate (the factory volume).
/// </summary>
public static class CBootImages
{
    public const string AssetName = "BOOTIMG.000";
    public const string AssetPath = "disks/cpm/BOOTIMG.000";
    public const int FileLength = 0x8000;
    public const ushort Version = 1;
    public const int DataLength = 0x6100;
    public const int MagicOffset = 0x0000;
    public const int VersionOffset = 0x0008;
    public const int RegionCountOffset = 0x000A;
    public const int DataLengthOffset = 0x000C;
    public const int HdrCrcOffset = 0x0010;
    public const int DataCrcOffset = 0x0012;
    public const int TableOffset = 0x0040;
    public const int DataOffset = 0x1000;
    private static readonly byte[] Magic = "HC2KBOOT"u8.ToArray();

    /// <summary>
    /// The seven canonical payload regions {dest, len}, in restore order (cboot.layout.inc R0-R6).
    /// All lens are multiples of 512; the sum is DataLength.
    /// </summary>
    public static readonly (int Dest, int Len)[] Regions =
    [
        (0x0000, 0x0100), // vectors + low CP/M ((0000) stays JP D303, the BIOS WBOOT entry)
        (0x0100, 0x0800), // CP/M low system area
        (0x2700, 0x0200), // BIOS/DPB area
        (0x3000, 0x0100), // bootstrap region (warm trampoline at 0x3080)
        (0x5800, 0x0500), // mid system area
        (0x9000, 0x2800), // HDINST resident
        (0xB800, 0x2800), // system high area (warm patch: the BIOS WBOOT entry D303 -> JP 0x3080)
    ];

    public static bool ChecksumsMatch(ReadOnlySpan<byte> file)
        => Fletcher16(file[0..16]) == (file[HdrCrcOffset] | file[HdrCrcOffset + 1] << 8)
        && Fletcher16(file[DataOffset..(DataOffset + DataLength)]) == (file[DataCrcOffset] | file[DataCrcOffset + 1] << 8);

    public static int Fletcher16(ReadOnlySpan<byte> data)
    {
        byte sum1 = 0, sum2 = 0;
        foreach (var value in data)
        {
            sum1 = (byte)(sum1 + value);
            sum2 = (byte)(sum2 + sum1);
        }
        return (sum2 << 8) | sum1;
    }

    /// <summary>Builds the 0x8000-byte payload from the seven captured region byte arrays (restore order).</summary>
    public static byte[] Build(IReadOnlyList<byte[]> regionData)
    {
        if (regionData.Count != Regions.Length)
            throw new InvalidDataException($"Expected {Regions.Length} regions, got {regionData.Count}.");
        var data = new byte[DataLength];
        var offset = 0;
        for (var i = 0; i < Regions.Length; i++)
        {
            if (regionData[i].Length != Regions[i].Len)
                throw new InvalidDataException($"Region {i}: expected {Regions[i].Len} bytes, got {regionData[i].Length}.");
            regionData[i].CopyTo(data, offset);
            offset += Regions[i].Len;
        }
        var file = new byte[FileLength];
        Magic.CopyTo(file, MagicOffset);
        file[VersionOffset] = (byte)Version;
        file[VersionOffset + 1] = (byte)(Version >> 8);
        file[RegionCountOffset] = (byte)Regions.Length;
        file[RegionCountOffset + 1] = 0;
        file[DataLengthOffset] = unchecked((byte)DataLength);
        file[DataLengthOffset + 1] = unchecked((byte)(DataLength >> 8));
        var hdrCrc = Fletcher16(file.AsSpan(MagicOffset, 16));
        file[HdrCrcOffset] = (byte)hdrCrc;
        file[HdrCrcOffset + 1] = (byte)(hdrCrc >> 8);
        var dataCrc = Fletcher16(data);
        file[DataCrcOffset] = (byte)dataCrc;
        file[DataCrcOffset + 1] = (byte)(dataCrc >> 8);
        for (var i = 0; i < Regions.Length; i++)
        {
            var table = TableOffset + i * 4;
            file[table] = (byte)Regions[i].Dest;
            file[table + 1] = (byte)(Regions[i].Dest >> 8);
            file[table + 2] = (byte)Regions[i].Len;
            file[table + 3] = (byte)(Regions[i].Len >> 8);
        }
        data.CopyTo(file, DataOffset);
        return file;
    }

    /// <summary>
    /// Validates the payload header and region table exactly as the guest loader does, and
    /// returns the seven region byte arrays in restore order. Throws on any mismatch.
    /// </summary>
    public static byte[][] Verify(byte[] file)
    {
        if (file.Length != FileLength)
            throw new InvalidDataException($"Payload must be {FileLength} bytes, got {file.Length}.");
        for (var i = 0; i < Magic.Length; i++)
            if (file[MagicOffset + i] != Magic[i])
                throw new InvalidDataException("Bad payload magic.");
        if ((file[VersionOffset] | (file[VersionOffset + 1] << 8)) != Version)
            throw new InvalidDataException("Unsupported payload version.");
        if ((file[RegionCountOffset] | (file[RegionCountOffset + 1] << 8)) != Regions.Length)
            throw new InvalidDataException("Bad payload region count.");
        var dataLength = file[DataLengthOffset] | (file[DataLengthOffset + 1] << 8) | (file[DataLengthOffset + 2] << 16) | (file[DataLengthOffset + 3] << 24);
        if (dataLength != DataLength)
            throw new InvalidDataException("Bad payload data length.");
        for (var i = 0x0014; i < TableOffset; i++)
            if (file[i] != 0)
                throw new InvalidDataException("Payload reserved area must be zero.");
        for (var i = 0; i < Regions.Length; i++)
        {
            var table = TableOffset + i * 4;
            var dest = file[table] | file[table + 1] << 8;
            var len = file[table + 2] | file[table + 3] << 8;
            if (dest != Regions[i].Dest || len != Regions[i].Len)
                throw new InvalidDataException($"Payload region table {i} = ({dest:X4},{len:X4}), built-in = ({Regions[i].Dest:X4},{Regions[i].Len:X4}).");
        }
        if (!ChecksumsMatch(file))
            throw new InvalidDataException("Payload checksum mismatch.");
        var regions = new byte[Regions.Length][];
        var offset = 0;
        for (var i = 0; i < Regions.Length; i++)
        {
            regions[i] = file.AsSpan(DataOffset + offset, Regions[i].Len).ToArray();
            offset += Regions[i].Len;
        }
        return regions;
    }
}
