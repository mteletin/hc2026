using System.Buffers.Binary;
using System.Text;

namespace Hc2000.Machine.Storage;

/// <summary>Published HC parameters from hcfsv1; OFF counts sides, not cylinders.</summary>
public sealed record Dpb(string Name, DiskGeometry Geometry, int Spt, int Bsh, int Blm, int Exm,
    int Dsm, int Drm, byte Al0, byte Al1, int Cks, int Off, bool WordAllocations, bool HcHeader)
{
    public int BlockSize => 128 << Bsh;
    public int DirectoryBlocks => System.Numerics.BitOperations.PopCount((uint)((Al0 << 8) | Al1));
    public IReadOnlyList<int> Skew => Enumerable.Range(1, Geometry.Sectors).Where(n => n % 2 == 1)
        .Concat(Enumerable.Range(1, Geometry.Sectors).Where(n => n % 2 == 0)).ToArray();
    public static IReadOnlyList<Dpb> Presets() => new Dpb[]
    {
        new("cpm-hc80", new(80,2,9,512),36,4,15,0,351,127,0xc0,0,32,4,true,false),
        new("cpm-hc40", new(40,2,9,512),36,4,15,1,174,63,0x80,0,16,2,false,false),
        new("hcfs80", new(80,2,16,256),32,4,15,0,320,127,0xc0,0,32,0,true,true),
        new("hcfs40", new(40,2,16,256),32,4,15,0,160,127,0xc0,0,32,0,true,true)
    };
    /// <summary>Accepted logical DPB v1 for the cold C:-F: HDD volumes the HDD epic delivers
    /// (KB hc2000-cpm22-large-drive-dpb-v1 / hc2000-hdd-transport-v1): 4 KiB blocks, DSM 2047,
    /// DRM 1023, EXM 1, CKS 256, OFF 0 over a flat 16384x512 raw image. Geometry describes
    /// 1024 logical tracks of 16 physical sectors (SPT 64 in 128-byte records), not DSK CHS;
    /// Skew is not applied to a cold volume.</summary>
    public static Dpb HddVolume() => new("hdd-volume-8m", new(1024,1,16,512), 64, 5, 31, 1, 2047, 1023, 0xff, 0, 256, 0, true, false);
    /// <summary>Accepted logical DPB for the resident M: v2 8 KiB drive (KB hc2000-m-base-view-v1,
    /// doc amendment-m-v2-geometry-8kib): 8 KiB blocks (Bsh 6), EXM 1 with 16-bit allocation, DSM 1023,
    /// DRM 255, ALV 128 (AL0 0x80), CKS/OFF 0. SPT 128 records is logical, not DSK CHS; Geometry is a
    /// flat 512x16x1024 = 8 MiB raw store (1024 blocks, DSM 1023); Skew is not applied to a cold drive.</summary>
    public static Dpb MVolumeV2() => new("m-volume-8m-v2", new(512, 1, 16, 1024), 128, 6, 63, 1, 1023, 255, 0x80, 0x00, 0, 0, true, false);
}
public sealed record CpmFile(int User, string Name, string CatalogName, bool ReadOnly, bool System,
    int Records, int AllocatedBytes, int DirectoryIndex);

/// <summary>CP/M 2.2 extents. Get returns record-rounded data, never guessed trimming.</summary>
public sealed class CpmFs
{
    private readonly Dsk? disk;
    private readonly byte[] logical;
    private readonly int dataStart;
    private readonly int maxBlock;
    private readonly bool volume;
    private readonly bool readOnly;
    public Dpb Parameters { get; }
    private StringComparison NameComparison => Parameters.HcHeader ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private StringComparer NameComparer => Parameters.HcHeader ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    public int MaximumBlock => maxBlock;
    public IReadOnlyList<string> Warnings { get; }
    public CpmFs(Dsk disk, Dpb parameters)
    {
        this.disk = disk; Parameters = parameters;
        if (disk.UniformGeometry() != parameters.Geometry) throw new InvalidDataException("Geometry does not match DPB.");
        (logical, dataStart, maxBlock, Warnings) = Initialize(parameters, disk.LogicalBytes(parameters.Skew), checked(parameters.Off * parameters.Spt * 128));
        ValidateDirectory();
    }
    private CpmFs(byte[] volume, Dpb parameters, bool readOnly = false)
    {
        Parameters = parameters; this.volume = true; this.readOnly = readOnly;
        if (volume.Length != parameters.Geometry.Length)
            throw new InvalidDataException($"Cold HDD volume must be a flat {parameters.Geometry.Length}-byte raw image; got {volume.Length}.");
        (logical, dataStart, maxBlock, Warnings) = Initialize(parameters, volume, 0);
        ValidateDirectory();
    }
    private static (byte[], int, int, IReadOnlyList<string>) Initialize(Dpb parameters, byte[] logical, int dataStart)
    {
        if (parameters.Bsh is < 3 or > 7 || parameters.Blm != (1 << parameters.Bsh) - 1 || parameters.Exm is < 0 or > 31 ||
            (parameters.Exm & (parameters.Exm + 1)) != 0 || parameters.Dsm < 0 || parameters.Drm is < 0 or > 65535 ||
            parameters.Spt * 128 != parameters.Geometry.Sectors * parameters.Geometry.SectorSize || parameters.Off < 0)
            throw new InvalidDataException("Invalid DPB.");
        var directoryMask = (parameters.Al0 << 8) | parameters.Al1;
        var extentBytes = 16384 * (parameters.Exm + 1); // one extent is a whole number of blocks within the allocation words
        if (parameters.DirectoryBlocks == 0 || directoryMask != ((0xffff << (16 - parameters.DirectoryBlocks)) & 0xffff) ||
            (!parameters.HcHeader && parameters.WordAllocations != (parameters.Dsm > 255)) ||
            extentBytes % parameters.BlockSize != 0 || extentBytes / parameters.BlockSize > (parameters.WordAllocations ? 8 : 16))
            throw new InvalidDataException("Unsupported DPB directory/allocation/extent layout.");
        var maxBlock = Math.Min(parameters.Dsm, (logical.Length - dataStart) / parameters.BlockSize - 1);
        if (maxBlock < parameters.DirectoryBlocks || (parameters.Drm + 1) * 32 > parameters.DirectoryBlocks * parameters.BlockSize)
            throw new InvalidDataException("DPB directory exceeds physical capacity.");
        var warnings = maxBlock == parameters.Dsm ? Array.Empty<string>() : new[] { $"Published DSM {parameters.Dsm} exceeds physical maximum {maxBlock}; allocation and reads are bounded." };
        return (logical, dataStart, maxBlock, warnings);
    }
    /// <summary>Host-side read model over a cold C:-F: HDD volume: one flat 8,388,608-byte
    /// (16384x512) raw image with the explicit DPB v1 geometry (OFF 0, no skew). Any other
    /// length, or a directory that fails validation, is a refusal, never an empty disk.</summary>
    public static CpmFs OpenHddVolume(ReadOnlySpan<byte> raw)
    {
        var parameters = Dpb.HddVolume();
        if (raw.Length != parameters.Geometry.Length)
            throw new InvalidDataException($"Cold HDD volume must be exactly {parameters.Geometry.Length} bytes (16384 sectors x 512); got {raw.Length}.");
        return new CpmFs(raw.ToArray(), parameters);
    }
    /// <summary>Host-side read model over the resident M: v2 8 KiB drive: one flat 8,388,608-byte
    /// (1024 blocks x 8 KiB) raw image with the accepted v2 geometry (OFF 0, no skew). Any other
    /// length, or a directory that fails validation, is a refusal, never an empty drive.</summary>
    public static CpmFs OpenMVolumeV2(ReadOnlySpan<byte> raw)
    {
        var parameters = Dpb.MVolumeV2();
        if (raw.Length != parameters.Geometry.Length)
            throw new InvalidDataException($"M: v2 volume must be exactly {parameters.Geometry.Length} bytes (1024 blocks x 8 KiB); got {raw.Length}.");
        return new CpmFs(raw.ToArray(), parameters, readOnly: true);
    }
    public static CpmFs Open(Dsk disk, string format = "auto")
    {
        var geometry = disk.UniformGeometry();
        var matches = new List<CpmFs>(); var failures = new List<string>();
        foreach (var profile in Dpb.Presets().Where(p => p.Geometry == geometry &&
            (format == "auto" || format == p.Name || format == (p.HcHeader ? "hcfs" : "cpm-hc"))))
        {
            try { matches.Add(new(disk, profile)); }
            catch (InvalidDataException ex) { failures.Add(profile.Name + ": " + ex.Message); }
        }
        return matches.Count == 1 ? matches[0] : throw new InvalidDataException(matches.Count == 0 ? "No valid directory/DPB preset. " + string.Join("; ", failures) : "Ambiguous filesystem presets.");
    }
    public static CpmFs Format(Dpb parameters, DiskFormat format = DiskFormat.Extended)
        => new(Dsk.Create(parameters.Geometry, format), parameters);
    /// <summary>Shared blank cold-volume writer (KB hc2000-media-home-scan-admission-v1 sec 4): fills exactly
    /// 8,388,608 bytes with 0xe5 and returns the flat raw image CpmFs.OpenHddVolume accepts through the
    /// volume constructor (data start 0, no skew). Not the zero fill of hc hdd create; the Dpb.HddVolume()
    /// geometry is not a Dsk geometry, so Format(Dpb.HddVolume()) is structurally never on this path.</summary>
    public static byte[] FormatHddVolume()
    {
        var raw = new byte[Dpb.HddVolume().Geometry.Length];
        Array.Fill(raw, (byte)0xe5);
        OpenHddVolume(raw); // the volume constructor validates this exact image
        return raw;
    }
    /// <summary>Container bytes this filesystem holds: the floppy image or the flat cold-volume raw.</summary>
    public byte[] ImageBytes() => volume ? logical.ToArray() : disk!.Save();
    /// <summary>Export the flat 8,388,608-byte cold volume bytes as a copy that OpenHddVolume
    /// accepts. Volume constructor only (data start 0, no skew); floppy images save through
    /// their Dsk container. The §4 shared formatter sibling (no-skew 0xe5 blank) belongs to
    /// hc2000-hdd-volume-format-cli and is not this method.</summary>
    public byte[] ExportVolumeBytes()
    {
        if (!volume) throw new InvalidOperationException("Volume export is only valid on a cold HDD volume.");
        return logical.ToArray();
    }

    private IEnumerable<int> DirectoryOffsets() => Enumerable.Range(0, Parameters.Drm + 1).Select(i => dataStart + i * 32);
    private bool Live(int offset) => logical[offset] != 0xe5;
    private string NameField(int offset) => Encoding.ASCII.GetString(logical.AsSpan(offset + 1, 11).ToArray().Select(b => (byte)(b & 0x7f)).ToArray());
    private string CatalogName(int offset) => NameField(offset).TrimEnd(' ');
    private string Name(int offset)
    {
        if (Parameters.HcHeader) return NameField(offset);
        var raw = CatalogName(offset).PadRight(11);
        var stem = raw[..8].TrimEnd(); var extension = raw[8..].TrimEnd();
        return extension.Length == 0 ? stem : stem + "." + extension;
    }
    private int Extent(int offset) => ((logical[offset + 14] & 0x3f) << 5) | (logical[offset + 12] & 0x1f);
    private int Records(int offset) => (Extent(offset) & Parameters.Exm) * 128 + logical[offset + 15];
    private IEnumerable<int> Blocks(int offset)
    {
        for (var j = 16; j < 32; j += Parameters.WordAllocations ? 2 : 1)
        {
            var value = Parameters.WordAllocations ? BinaryPrimitives.ReadUInt16LittleEndian(logical.AsSpan(offset + j, 2)) : logical[offset + j];
            if (value != 0) yield return value;
        }
    }
    private bool Reserved(int block) => block < 16 && (((Parameters.Al0 << 8) | Parameters.Al1) & (0x8000 >> block)) != 0;
    private void ValidateDirectory()
    {
        var allocated = new HashSet<int>(); var extents = new HashSet<string>(NameComparer);
        foreach (var offset in DirectoryOffsets().Where(Live))
        {
            if (logical[offset] > 31 || logical[offset + 15] > 128 || (!Parameters.HcHeader && logical.AsSpan(offset + 1, 11).ToArray().Any(b => (b & 0x7f) is < 32 or > 126)) || (!Parameters.HcHeader && Name(offset).Length == 0))
                throw new InvalidDataException($"Invalid directory entry {(offset - dataStart) / 32}.");
            var key = $"{logical[offset]}:{Name(offset)}:{Extent(offset) & ~Parameters.Exm}";
            if (!extents.Add(key)) throw new InvalidDataException("Duplicate extent.");
            var blocks = Blocks(offset).ToArray();
            if (blocks.Length < (Records(offset) * 128 + Parameters.BlockSize - 1) / Parameters.BlockSize)
                throw new InvalidDataException("Extent has insufficient allocated blocks.");
            foreach (var block in blocks)
                if (block > maxBlock || Reserved(block) || !allocated.Add(block))
                    throw new InvalidDataException($"Corrupt allocation block {block}, physical maximum {maxBlock}.");
        }
    }
    private int[] Offsets(string name, int user)
    {
        if (Parameters.HcHeader) name = Encoding.ASCII.GetString(EncodeName(name));
        var offsets = DirectoryOffsets().Where(o => Live(o) && logical[o] == user && string.Equals(Name(o), name, NameComparison))
            .OrderBy(o => Extent(o) & ~Parameters.Exm).ToArray();
        if (offsets.Length == 0) throw new FileNotFoundException("File is not in the disk directory: " + name);
        return offsets;
    }
    public IReadOnlyList<CpmFile> List()
    {
        return DirectoryOffsets().Where(Live).GroupBy(o => (User: (int)logical[o], Name: Name(o)), new UserNameComparer(NameComparer))
            .Select(g => new CpmFile(g.Key.User, g.Key.Name, CatalogName(g.First()), (logical[g.First() + 9] & 128) != 0,
                (logical[g.First() + 10] & 128) != 0, g.Sum(Records), g.Sum(o => Blocks(o).Count() * Parameters.BlockSize), (g.OrderBy(Extent).First() - dataStart) / 32))
            .OrderBy(f => f.DirectoryIndex).ToArray();
    }
    private sealed class UserNameComparer(StringComparer comparer) : IEqualityComparer<(int User, string Name)>
    {
        public bool Equals((int User, string Name) x, (int User, string Name) y) => x.User == y.User && comparer.Equals(x.Name, y.Name);
        public int GetHashCode((int User, string Name) obj) => HashCode.Combine(obj.User, comparer.GetHashCode(obj.Name));
    }
    public byte[] Get(string name, int user = 0)
    {
        using var output = new MemoryStream(); var expectedExtent = 0;
        var offsets = Offsets(name, user);
        foreach (var offset in offsets)
        {
            if ((Extent(offset) & ~Parameters.Exm) != expectedExtent) throw new InvalidDataException("Missing file extent.");
            if (offset != offsets[^1] && Records(offset) != 128 * (Parameters.Exm + 1))
                throw new InvalidDataException("Short intermediate file extent.");
            var remaining = Records(offset) * 128;
            foreach (var block in Blocks(offset))
            {
                var count = Math.Min(remaining, Parameters.BlockSize);
                output.Write(logical, dataStart + block * Parameters.BlockSize, count); remaining -= count;
                if (remaining == 0) break;
            }
            expectedExtent += Parameters.Exm + 1;
        }
        return output.ToArray();
    }
    private byte[] EncodeName(string name)
    {
        if (Parameters.HcHeader)
        {
            if (name.Length > 11 || name.Any(c => c > 0x7f))
                throw new InvalidDataException("HC filenames are case-sensitive, literal 11-byte 7-bit names.");
            return Encoding.ASCII.GetBytes(name.PadRight(11));
        }
        var dot = name.LastIndexOf('.'); var stem = dot < 0 ? name : name[..dot]; var ext = dot < 0 ? "" : name[(dot + 1)..];
        if (stem.Length is < 1 or > 8 || ext.Length > 3 || name.Any(c => c is < ' ' or > '~' || "<>/\\:*?\"|".Contains(c)) || stem.Contains('.'))
            throw new InvalidDataException("Name must be an 8.3 disk filename.");
        return Encoding.ASCII.GetBytes(stem.PadRight(8) + ext.PadRight(3));
    }
    public void Put(string name, ReadOnlySpan<byte> data, int user = 0)
    {
        if (readOnly) throw new InvalidDataException("M: v2 host view is read-only.");
        if (user is < 0 or > 31) throw new InvalidDataException("User must be 0..31.");
        var encodedName = EncodeName(name);
        var field = Encoding.ASCII.GetString(encodedName);
        name = Parameters.HcHeader ? field : field[..8].TrimEnd(' ') +
            (field[8..].TrimEnd(' ') is var extension && extension.Length > 0 ? "." + extension : "");
        var old = DirectoryOffsets().Where(o => Live(o) && logical[o] == user && string.Equals(Name(o), name, NameComparison)).ToArray();
        if (old.Any(o => (logical[o + 9] & 128) != 0)) throw new InvalidDataException("File is read-only.");
        var records = checked(data.Length + 127) / 128;
        var extentBytes = Parameters.BlockSize * (Parameters.WordAllocations ? 8 : 16);
        if (extentBytes != 16384 * (Parameters.Exm + 1)) throw new InvalidDataException("Unsupported DPB extent capacity.");
        var entriesNeeded = Math.Max(1, checked(data.Length + extentBytes - 1) / extentBytes);
        if (entriesNeeded * (Parameters.Exm + 1) > 2048) throw new InvalidDataException("File exceeds CP/M extent range.");
        var freeEntries = DirectoryOffsets().Where(o => !Live(o) || old.Contains(o)).Take(entriesNeeded).ToArray();
        var used = DirectoryOffsets().Where(o => Live(o) && !old.Contains(o)).SelectMany(Blocks).ToHashSet();
        var blocksNeeded = checked(data.Length + Parameters.BlockSize - 1) / Parameters.BlockSize;
        var freeBlocks = Enumerable.Range(0, maxBlock + 1).Where(b => !Reserved(b) && !used.Contains(b)).Take(blocksNeeded).ToArray();
        if (freeEntries.Length != entriesNeeded) throw new InvalidDataException("Directory is full.");
        if (freeBlocks.Length != blocksNeeded) throw new InvalidDataException("Disk is full.");
        var next = logical.ToArray();
        foreach (var offset in old) next[offset] = 0xe5;
        var copied = 0; var blockIndex = 0;
        for (var e = 0; e < entriesNeeded; e++)
        {
            var offset = freeEntries[e]; Array.Clear(next, offset, 32); next[offset] = (byte)user; encodedName.CopyTo(next, offset + 1);
            var recordCount = Math.Min(records, extentBytes / 128);
            var extent = e * (Parameters.Exm + 1) + (recordCount == 0 ? 0 : (recordCount - 1) / 128);
            next[offset + 12] = (byte)(extent & 31); next[offset + 14] = (byte)(extent >> 5);
            next[offset + 15] = recordCount == 0 ? (byte)0 : (byte)((recordCount - 1) % 128 + 1);
            for (var j = 0; j < (recordCount * 128 + Parameters.BlockSize - 1) / Parameters.BlockSize; j++)
            {
                var block = freeBlocks[blockIndex++]; var target = dataStart + block * Parameters.BlockSize;
                Array.Fill(next, (byte)0x1a, target, Parameters.BlockSize);
                var count = Math.Min(data.Length - copied, Parameters.BlockSize); data.Slice(copied, count).CopyTo(next.AsSpan(target)); copied += count;
                if (Parameters.WordAllocations) BinaryPrimitives.WriteUInt16LittleEndian(next.AsSpan(offset + 16 + j * 2, 2), (ushort)block);
                else next[offset + 16 + j] = (byte)block;
            }
            records -= recordCount;
        }
        next.CopyTo(logical, 0); if (volume) return; disk!.WriteLogicalBytes(logical, Parameters.Skew);
    }
    public void Delete(string name, int user = 0)
    {
        if (readOnly) throw new InvalidDataException("M: v2 host view is read-only.");
        var offsets = Offsets(name, user);
        if (offsets.Any(o => (logical[o + 9] & 128) != 0)) throw new InvalidDataException("File is read-only.");
        foreach (var offset in offsets) logical[offset] = 0xe5;
        if (volume) return; disk!.WriteLogicalBytes(logical, Parameters.Skew);
    }
}
