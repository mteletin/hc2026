using System.Text;

namespace Hc2000.Machine.Storage;

/// <summary>One directory file (or a refused entry) from a read-only CP/M listing.</summary>
public sealed record CpmEntry(
    string Name, int User, int Records, long Size,
    bool ReadOnly, bool System, int DirectoryIndex, bool Refused, string? Refusal);

/// <summary>
/// Host-side read-only listing over the four CP/M presets (hc2000-explorer-list-fdd).
/// Enforces the CP/M 2.2 policy the library does not: user areas 0-15 only - entries in user
/// areas above 15 are refused, never hidden or rewritten; canonical 8.3 names - entries with
/// high-bit or DRI-forbidden characters are refused rather than high-bit masked; R/O and
/// system attributes are surfaced; sizes are record-rounded and keep the 0x1A text end.
/// Fail-closed on the same corruption <see cref="CpmFs"/> refuses. No write path, no mount.
/// </summary>
public sealed class CpmExplorer
{
    public const int Cpm22MaxUser = 15;
    private static readonly char[] Forbidden = ['<', '>', '.', ',', ';', ':', '=', '?', '*', '[', ']', '%', '|', '(', ')', '/', '\\'];

    public Dpb Parameters { get; }
    private readonly byte[] logical;
    private readonly int dataStart;
    private readonly int maxBlock;

    private CpmExplorer(Dsk disk, Dpb parameters)
        : this(FloppyBytes(disk, parameters), parameters, checked(parameters.Off * parameters.Spt * 128)) { }

    private static byte[] FloppyBytes(Dsk disk, Dpb parameters)
    {
        if (disk.UniformGeometry() != parameters.Geometry) throw new InvalidDataException("Geometry does not match DPB.");
        return disk.LogicalBytes(parameters.Skew);
    }

    private CpmExplorer(byte[] bytes, Dpb parameters, int start)
    {
        Parameters = parameters;
        var directoryMask = (parameters.Al0 << 8) | parameters.Al1;
        if (parameters.DirectoryBlocks == 0 || directoryMask != ((0xffff << (16 - parameters.DirectoryBlocks)) & 0xffff) ||
            parameters.BlockSize * (parameters.WordAllocations ? 8 : 16) != 16384 * (parameters.Exm + 1))
            throw new InvalidDataException("Unsupported DPB directory/allocation/extent layout.");
        logical = bytes; dataStart = start;
        maxBlock = Math.Min(parameters.Dsm, (logical.Length - dataStart) / parameters.BlockSize - 1);
        if (maxBlock < parameters.DirectoryBlocks || (parameters.Drm + 1) * 32 > parameters.DirectoryBlocks * parameters.BlockSize)
            throw new InvalidDataException("DPB directory exceeds physical capacity.");
        var extents = new HashSet<(int User, string Name, int Base)>();
        var allocated = new HashSet<int>();
        foreach (var offset in Offsets().Where(Live))
        {
            if (logical[offset] > 31 || logical[offset + 15] > 128)
                throw new InvalidDataException($"Invalid directory entry {(offset - dataStart) / 32}.");
            if (!extents.Add(((int)logical[offset], NameKey(offset), Extent(offset) & ~parameters.Exm)))
                throw new InvalidDataException("Duplicate extent.");
            var blocks = Blocks(offset).ToArray();
            if (blocks.Length < (Records(offset) * 128 + parameters.BlockSize - 1) / parameters.BlockSize)
                throw new InvalidDataException("Extent has insufficient allocated blocks.");
            foreach (var block in blocks)
                if (block > maxBlock || Reserved(block) || !allocated.Add(block))
                    throw new InvalidDataException($"Corrupt allocation block {block}, physical maximum {maxBlock}.");
        }
    }

    /// <summary>Opens an image whose geometry matches exactly one of the four presets.</summary>
    public static CpmExplorer Open(Dsk disk)
    {
        var geometry = disk.UniformGeometry();
        var matches = Dpb.Presets().Where(p => p.Geometry == geometry).ToArray();
        return matches.Length == 1 ? new CpmExplorer(disk, matches[0]) : throw new InvalidDataException("Geometry is not a supported CP/M preset.");
    }

    /// <summary>Lists an explicit flat 8 MiB C:-F: volume using the accepted HDD DPB,
    /// with the same CP/M 2.2 refusal policy as a floppy. No CHS guessing or skew.</summary>
    public static CpmExplorer OpenHddVolume(ReadOnlySpan<byte> raw)
    {
        var parameters = Dpb.HddVolume();
        if (raw.Length != parameters.Geometry.Length)
            throw new InvalidDataException($"Cold HDD volume must be exactly {parameters.Geometry.Length} bytes.");
        return new CpmExplorer(raw.ToArray(), parameters, 0);
    }

    /// <summary>Lists the files of one user area, or every area with <paramref name="user"/> negative.
    /// User areas must be 0..15 under CP/M 2.2; a query outside the range is refused, not wrapped.
    /// Refused entries (user above 15, non-canonical names) are reported, never hidden.</summary>
    public IReadOnlyList<CpmEntry> List(int user = -1)
    {
        if (user > Cpm22MaxUser)
            throw new ArgumentException($"User area must be 0..{Cpm22MaxUser} under CP/M 2.2.", nameof(user));
        return Offsets().Where(Live)
            .GroupBy(offset => ((int)logical[offset], NameKey(offset)))
            .Select(g => Build(g.Key.Item1, g.OrderBy(Extent).ToArray()))
            .Where(e => user < 0 || e.User == user)
            .OrderBy(e => e.User).ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.DirectoryIndex)
            .ToArray();
    }

    private CpmEntry Build(int user, int[] offsets)
    {
        var first = offsets[0];
        var records = offsets.Sum(Records);
        var refusal = offsets.Select(offset => Refusal(user, offset)).FirstOrDefault(reason => reason is not null);
        return new CpmEntry(DisplayName(first), user, records, records * 128L,
            (logical[first + 9] & 0x80) != 0, (logical[first + 10] & 0x80) != 0,
            (first - dataStart) / 32, refusal is not null, refusal);
    }

    private string? Refusal(int user, int offset)
    {
        if (user > Cpm22MaxUser) return $"User area {user} is outside the CP/M 2.2 range 0-{Cpm22MaxUser}.";
        // Name characters are the low 7 bits; the high bits of ext bytes 0/1 are the R/O and system attributes, not name data.
        if (Enumerable.Range(1, 11).Where(k => k != 9 && k != 10).Any(k => logical[offset + k] >= 0x80))
            return Parameters.HcHeader
                ? "Name has high-bit characters; HC BASIC names are literal 7-bit."
                : "Name has high-bit characters; refused rather than masked.";
        if (Parameters.HcHeader) return null;
        var field = NameField(offset);
        var stem = field[..8].TrimEnd(' ');
        var extension = field[8..].TrimEnd(' ');
        if (stem.Length == 0) return "Name field has an empty filename component.";
        if (field.Any(c => c < ' ' || c == '\u007f')) return "Name contains a control character.";
        var forbidden = field.FirstOrDefault(c => Forbidden.Contains(c));
        if (forbidden != '\0') return $"Name contains DRI-forbidden character '{forbidden}'.";
        if (stem.Contains(' ') || extension.Contains(' ')) return "Name components must be contiguous and space-padded.";
        if (field.Any(c => c is >= 'a' and <= 'z')) return "Name is not canonical upper-case CP/M 8.3.";
        return null;
    }

    private IEnumerable<int> Offsets() => Enumerable.Range(0, Parameters.Drm + 1).Select(i => dataStart + i * 32);
    private bool Live(int offset) => logical[offset] != 0xe5;
    private string NameField(int offset) => Encoding.ASCII.GetString(logical.AsSpan(offset + 1, 11).ToArray().Select(b => (byte)(b & 0x7f)).ToArray());
    private string NameKey(int offset) => Parameters.HcHeader ? NameField(offset) : NameField(offset).ToUpperInvariant();
    private string DisplayName(int offset)
    {
        if (Parameters.HcHeader) return NameField(offset).TrimEnd(' ');
        var raw = NameField(offset).PadRight(11);
        var stem = raw[..8].TrimEnd(); var extension = raw[8..].TrimEnd();
        return extension.Length == 0 ? stem : stem + "." + extension;
    }
    private int Extent(int offset) => ((logical[offset + 14] & 0x3f) << 5) | (logical[offset + 12] & 0x1f);
    private int Records(int offset) => (Extent(offset) & Parameters.Exm) * 128 + logical[offset + 15];
    private IEnumerable<int> Blocks(int offset)
    {
        for (var j = 16; j < 32; j += Parameters.WordAllocations ? 2 : 1)
        {
            var value = Parameters.WordAllocations
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(logical.AsSpan(offset + j, 2))
                : logical[offset + j];
            if (value != 0) yield return value;
        }
    }
    private bool Reserved(int block) => block < 16 && (((Parameters.Al0 << 8) | Parameters.Al1) & (0x8000 >> block)) != 0;
}
