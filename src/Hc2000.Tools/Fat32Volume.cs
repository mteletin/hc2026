using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Hc2000.Tools;

/// <summary>Read-only reader for a standalone 64 MiB FAT32 volume. Directory paths are image identifiers.</summary>
internal sealed class Fat32Volume(FileStream image)
{
    private const int VolumeSize = 64 * 1024 * 1024;
    private const int EntrySize = 32;
    private const uint ClusterMask = 0x0fffffff;
    private static readonly Encoding ShortNameEncoding = CodePagesEncodingProvider.Instance.GetEncoding(437)!;
    private static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, false, true);

    private readonly Layout layout = ReadLayout(image);

    internal sealed record FileEntry(string Path, string ShortName, string? LongName, int Size, byte Attributes,
        uint StartCluster)
    {
        public string? CpmName => ShortName.All(character => character is > ' ' and <= '~')
            ? ShortName.ToUpperInvariant() : null;
    }

    private sealed record Layout(int DataOffset, int ClusterSize, uint LastCluster, uint Root, byte[][] Fats);

    private sealed class DirectoryPath(uint start, string name, DirectoryPath? parent)
    {
        public uint Start { get; } = start;
        private string Name { get; } = name;
        public DirectoryPath? Parent { get; } = parent;
        public int Length { get; } = parent is null ? 0 : parent.Length + 1 + name.Length;

        public string FullPath() => string.Create(Length, this, static (buffer, path) =>
        {
            while (path.Parent is { } ancestor)
            {
                path.Name.AsSpan().CopyTo(buffer[(ancestor.Length + 1)..]);
                buffer[ancestor.Length] = '/';
                path = ancestor;
            }
        });
    }

    private static Layout ReadLayout(FileStream image)
    {
        if (!image.CanSeek) throw new InvalidDataException("FAT32 source must be a seekable image file.");
        if (image.Length != VolumeSize)
            throw new InvalidDataException("Expected an exact 64 MiB standalone FAT32 volume image.");
        Span<byte> boot = stackalloc byte[512];
        image.ReadExactly(boot);
        if (U16(boot, 510) != 0xaa55 || !(boot[0] == 0xe9 || boot[0] == 0xeb && boot[2] == 0x90))
            throw new InvalidDataException("No FAT32 boot sector at byte zero; use a standalone volume, not a partitioned disk.");

        var sectorSize = U16(boot, 11);
        var sectorsPerCluster = boot[13];
        var reservedSectors = U16(boot, 14);
        var fatCount = boot[16];
        var media = boot[21];
        var totalSectors = U32(boot, 32);
        var fatSectors = U32(boot, 36);
        var flags = U16(boot, 40);
        var root = U32(boot, 44);
        if (sectorSize is not (512 or 1024 or 2048 or 4096) || sectorsPerCluster == 0 || sectorsPerCluster > 128 ||
            (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 || reservedSectors == 0 || fatCount == 0 ||
            (media != 0xf0 && media < 0xf8) ||
            U16(boot, 17) != 0 || U16(boot, 19) != 0 || U16(boot, 22) != 0 || U16(boot, 42) != 0 ||
            totalSectors != VolumeSize / sectorSize || fatSectors == 0)
            throw new InvalidDataException("Unsupported FAT32 boot parameter block (BPB).");

        var dataStart = reservedSectors + (long)fatCount * fatSectors;
        if (dataStart >= totalSectors)
            throw new InvalidDataException("FAT32 tables extend beyond the volume.");
        var clusters = (totalSectors - dataStart) / sectorsPerCluster;
        if (clusters < 65525 || (clusters + 2) * 4 > (long)fatSectors * sectorSize || root < 2 || root > clusters + 1)
            throw new InvalidDataException("Invalid FAT32 cluster count, table length or root cluster.");

        var mirrored = (flags & 0x80) == 0;
        var activeFat = mirrored ? 0 : flags & 0x0f;
        if (activeFat >= fatCount)
            throw new InvalidDataException("The active FAT32 table index is out of range.");
        byte[] ReadTable(int index)
        {
            var table = new byte[(clusters + 2) * 4];
            image.Position = (reservedSectors + index * (long)fatSectors) * sectorSize;
            image.ReadExactly(table);
            // FAT[0] embeds the BPB media byte. FAT[1] has 26 one bits plus two volume-status bits.
            if ((U32(table, 0) & ClusterMask) != (0x0fffff00u | media) || (U32(table, 4) & 0x03ffffff) != 0x03ffffff)
                throw new InvalidDataException($"FAT32 table {index + 1} has invalid reserved entries.");
            return table;
        }
        var fats = mirrored ? Enumerable.Range(0, fatCount).Select(ReadTable).ToArray() : [ReadTable(activeFat)];
        return new Layout((int)(dataStart * sectorSize), sectorSize * sectorsPerCluster, (uint)(clusters + 1), root, fats);
    }

    public IEnumerable<FileEntry> Files()
    {
        var pending = new Stack<DirectoryPath>();
        pending.Push(new DirectoryPath(layout.Root, "", null));
        var claimed = new bool[layout.LastCluster + 1];
        while (pending.TryPop(out var directory))
            foreach (var file in ReadDirectory(directory, pending, claimed)) yield return file;
    }

    private IEnumerable<FileEntry> ReadDirectory(DirectoryPath directory, Stack<DirectoryPath> pending, bool[] claimed)
    {
        var parent = directory.FullPath();
        var block = new byte[layout.ClusterSize];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longSlots = new List<byte[]>(20);
        var ended = false;
        var bytesVisited = 0;
        foreach (var cluster in Chain(directory.Start, parent, claimed))
        {
            bytesVisited += block.Length;
            if (bytesVisited > 65536 * EntrySize) throw Invalid(parent, "directory exceeds 65536 slots");
            if (ended) continue; // Still validate and claim the rest of the allocated chain.
            ReadCluster(cluster, block);
            for (var offset = 0; offset < block.Length; offset += EntrySize)
            {
                var first = block[offset];
                if (first == 0 || first == 0xe5)
                {
                    if (longSlots.Count != 0) throw Invalid(parent, "incomplete long-name sequence");
                    if (first == 0) { ended = true; break; }
                    continue;
                }

                var attributes = block[offset + 11];
                if (attributes == 0x0f)
                {
                    if (longSlots.Count == 20) throw Invalid(parent, "long name exceeds 20 slots");
                    longSlots.Add(block.AsSpan(offset, EntrySize).ToArray());
                    continue;
                }

                var isDirectory = (attributes & 0x10) != 0;
                var alias = block.AsSpan(offset, 11);
                var isSelf = alias.SequenceEqual(".          "u8);
                var isDot = isSelf || alias.SequenceEqual("..         "u8);
                var firstCluster = (((uint)U16(block, offset + 20) << 16) | U16(block, offset + 26)) & ClusterMask;
                var size = U32(block, offset + 28);
                if ((attributes & 0xc0) != 0 || (attributes & 0x18) == 0x18 || isDot && !isDirectory)
                    throw Invalid(parent, "invalid directory-entry attributes");
                if ((attributes & 0x08) != 0 || isDot) // Volume label and dot references do not own a chain.
                {
                    if (longSlots.Count != 0) throw Invalid(parent, "long name attached to a label or dot entry");
                    if (isDot)
                    {
                        var expected = isSelf ? directory.Start : directory.Parent?.Parent is null ? 0 : directory.Parent.Start;
                        if (directory.Parent is null || cluster != directory.Start || offset != (isSelf ? 0 : EntrySize) ||
                            size != 0 || firstCluster != expected)
                            throw Invalid(parent, "invalid directory dot link");
                    }
                    continue;
                }

                var shortName = DecodeShortName(alias, parent);
                var longName = DecodeLongName(longSlots, alias, parent);
                longSlots.Clear();
                if (!names.Add(shortName) || longName is not null &&
                    !shortName.Equals(longName, StringComparison.OrdinalIgnoreCase) && !names.Add(longName))
                    throw Invalid(parent, $"duplicate source name {Quote(shortName)} / {Quote(longName)}");

                var component = longName ?? shortName;
                if (directory.Length + 1 + component.Length > 32767)
                    throw Invalid(parent, "source path exceeds 32767 UTF-16 code units");
                if (isDirectory)
                {
                    if (size != 0) throw Invalid(parent + "/" + component, "directory has a nonzero file size");
                    pending.Push(new DirectoryPath(firstCluster, component, directory));
                }
                else
                {
                    var path = parent + "/" + component;
                    if (size > VolumeSize) throw Invalid(path, "file size exceeds the volume");
                    ValidateFileChain(firstCluster, (int)size, path, claimed);
                    yield return new FileEntry(path, shortName, longName, (int)size, attributes, firstCluster);
                }
            }
        }
        if (longSlots.Count != 0) throw Invalid(parent, "incomplete long-name sequence");
    }

    private void ValidateFileChain(uint start, int size, string path, bool[] claimed)
    {
        if (size == 0 && start == 0) return;
        var expected = (size + layout.ClusterSize - 1) / layout.ClusterSize;
        var count = 0;
        foreach (var _ in Chain(start, path, claimed))
            if (++count > expected) throw Invalid(path, "cluster chain is longer than the file size");
        if (count != expected) throw Invalid(path, "cluster chain ends before the file size");
    }

    private IEnumerable<uint> Chain(uint start, string path, bool[]? claimed)
    {
        var current = start;
        for (var seen = 0u; ; seen++)
        {
            if (current < 2 || current > layout.LastCluster)
                throw Invalid(path, $"invalid cluster 0x{current:x} (free, bad, reserved or out of range)");
            if (seen >= layout.LastCluster - 1 || claimed is not null && claimed[current])
                throw Invalid(path, $"cyclic or shared cluster chain at cluster {current}");
            if (claimed is not null) claimed[current] = true;
            var offset = (int)current * 4;
            var next = U32(layout.Fats[0], offset) & ClusterMask;
            for (var index = 1; index < layout.Fats.Length; index++)
                if ((U32(layout.Fats[index], offset) & ClusterMask) != next)
                    throw Invalid(path, $"mirrored FAT32 tables disagree at cluster {current}");
            yield return current;
            if (next >= 0x0ffffff8) yield break;
            current = next;
        }
    }

    public string Hash(FileEntry file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ReadFile(file, (chunk, _) => hash.AppendData(chunk));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public byte[] Read(FileEntry file)
    {
        var bytes = new byte[file.Size];
        ReadFile(file, (chunk, offset) => chunk.CopyTo(bytes.AsSpan(offset)));
        return bytes;
    }

    private void ReadFile(FileEntry file, ReadOnlyChunk consume)
    {
        if (file.Size == 0) return;
        var buffer = new byte[layout.ClusterSize];
        var offset = 0;
        foreach (var cluster in Chain(file.StartCluster, file.Path, null))
        {
            var count = Math.Min(file.Size - offset, buffer.Length);
            var chunk = buffer.AsSpan(0, count);
            ReadCluster(cluster, chunk);
            consume(chunk, offset);
            offset += count;
        }
    }

    private delegate void ReadOnlyChunk(ReadOnlySpan<byte> chunk, int offset);

    private void ReadCluster(uint cluster, Span<byte> target)
    {
        image.Position = layout.DataOffset + (cluster - 2L) * layout.ClusterSize;
        image.ReadExactly(target);
    }

    private static string DecodeShortName(ReadOnlySpan<byte> alias, string parent)
    {
        Span<byte> field = stackalloc byte[11];
        alias.CopyTo(field);
        if (field[0] == 0x05) field[0] = 0xe5; // FAT escape for a real first byte E5.
        static bool Valid(ReadOnlySpan<byte> part)
        {
            foreach (var value in part)
                if (value <= 0x20 || "\"*+,./:;<=>?[\\]|".Contains((char)value)) return false;
            return true;
        }
        var stem = ((ReadOnlySpan<byte>)field[..8]).TrimEnd((byte)' ');
        var ext = ((ReadOnlySpan<byte>)field[8..]).TrimEnd((byte)' ');
        if (stem.IsEmpty || !Valid(stem) || !Valid(ext)) throw Invalid(parent, "invalid FAT short name");
        return ShortNameEncoding.GetString(stem) + (ext.IsEmpty ? "" : "." + ShortNameEncoding.GetString(ext));
    }

    private static string? DecodeLongName(List<byte[]> slots, ReadOnlySpan<byte> alias, string parent)
    {
        if (slots.Count == 0) return null;
        byte checksum = 0;
        foreach (var value in alias) checksum = (byte)(((checksum >> 1) | (checksum << 7)) + value);
        Span<byte> utf16 = stackalloc byte[slots.Count * 26];
        for (var index = 0; index < slots.Count; index++)
        {
            ReadOnlySpan<byte> slot = slots[index];
            var ordinal = slots.Count - index;
            if (slot[0] != (ordinal | (index == 0 ? 0x40 : 0)) || slot[12] != 0 || slot[13] != checksum || U16(slot, 26) != 0)
                throw Invalid(parent, "invalid long-name ordinal, checksum or reserved fields");
            var target = utf16.Slice((ordinal - 1) * 26, 26);
            slot.Slice(1, 10).CopyTo(target);
            slot.Slice(14, 12).CopyTo(target[10..]);
            slot.Slice(28, 4).CopyTo(target[22..]);
        }
        var length = utf16.Length / 2;
        for (var index = 0; index < length; index++)
        {
            var code = U16(utf16, index * 2);
            if (code == 0)
            {
                if (index <= (slots.Count - 1) * 13) throw Invalid(parent, "extra long-name slots");
                for (var fill = index + 1; fill < length; fill++)
                    if (U16(utf16, fill * 2) != 0xffff) throw Invalid(parent, "invalid long-name padding");
                length = index;
            }
            else if (code == 0xffff) throw Invalid(parent, "invalid long-name padding");
        }
        if (length > 255) throw Invalid(parent, "long name exceeds 255 code units");
        string name;
        try { name = StrictUtf16.GetString(utf16[..(length * 2)]); }
        catch (DecoderFallbackException) { throw Invalid(parent, "invalid long-name UTF-16"); }
        if (name is "." or ".." || name.Any(character => character < ' ' || "\"*/:<>?\\|".Contains(character)))
            throw Invalid(parent, "invalid long-name component");
        return name;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    internal static string Quote(string? value) => System.Text.Json.JsonSerializer.Serialize(value);
    private static InvalidDataException Invalid(string path, string reason) =>
        new($"Invalid or unsupported FAT32 data at {Quote(path.Length == 0 ? "/" : path)}: {reason}.");
}
