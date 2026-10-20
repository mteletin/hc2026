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
}
public sealed record CpmFile(int User, string Name, string CatalogName, bool ReadOnly, bool System,
    int Records, int AllocatedBytes, int DirectoryIndex);

/// <summary>CP/M 2.2 extents. Get returns record-rounded data, never guessed trimming.</summary>
public sealed class CpmFs
{
    private readonly Dsk disk;
    private readonly byte[] logical;
    private readonly int dataStart;
    private readonly int maxBlock;
    public Dpb Parameters { get; }
    private StringComparison NameComparison => Parameters.HcHeader ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private StringComparer NameComparer => Parameters.HcHeader ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    public int MaximumBlock => maxBlock;
    public IReadOnlyList<string> Warnings { get; }
    public CpmFs(Dsk disk, Dpb parameters)
    {
        this.disk = disk; Parameters = parameters;
        if (disk.UniformGeometry() != parameters.Geometry) throw new InvalidDataException("Geometry does not match DPB.");
        if (parameters.Bsh is < 3 or > 7 || parameters.Blm != (1 << parameters.Bsh) - 1 || parameters.Exm is < 0 or > 31 ||
            (parameters.Exm & (parameters.Exm + 1)) != 0 || parameters.Dsm < 0 || parameters.Drm is < 0 or > 65535 ||
            parameters.Spt * 128 != parameters.Geometry.Sectors * parameters.Geometry.SectorSize || parameters.Off < 0)
            throw new InvalidDataException("Invalid DPB.");
        var directoryMask = (parameters.Al0 << 8) | parameters.Al1;
        if (parameters.DirectoryBlocks == 0 || directoryMask != ((0xffff << (16 - parameters.DirectoryBlocks)) & 0xffff) ||
            (!parameters.HcHeader && parameters.WordAllocations != (parameters.Dsm > 255)) ||
            parameters.BlockSize * (parameters.WordAllocations ? 8 : 16) != 16384 * (parameters.Exm + 1))
            throw new InvalidDataException("Unsupported DPB directory/allocation/extent layout.");
        logical = disk.LogicalBytes(parameters.Skew); dataStart = checked(parameters.Off * parameters.Spt * 128);
        maxBlock = Math.Min(parameters.Dsm, (logical.Length - dataStart) / parameters.BlockSize - 1);
        if (maxBlock < parameters.DirectoryBlocks || (parameters.Drm + 1) * 32 > parameters.DirectoryBlocks * parameters.BlockSize)
            throw new InvalidDataException("DPB directory exceeds physical capacity.");
        Warnings = maxBlock == parameters.Dsm ? Array.Empty<string>() : new[] { $"Published DSM {parameters.Dsm} exceeds physical maximum {maxBlock}; allocation and reads are bounded." };
        ValidateDirectory();
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
        if (freeEntries.Length != entriesNeeded || freeBlocks.Length != blocksNeeded) throw new InvalidDataException("Disk or directory is full.");
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
        next.CopyTo(logical, 0); disk.WriteLogicalBytes(logical, Parameters.Skew);
    }
    public void Delete(string name, int user = 0)
    {
        var offsets = Offsets(name, user);
        if (offsets.Any(o => (logical[o + 9] & 128) != 0)) throw new InvalidDataException("File is read-only.");
        foreach (var offset in offsets) logical[offset] = 0xe5;
        disk.WriteLogicalBytes(logical, Parameters.Skew);
    }
}
