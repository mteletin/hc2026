namespace Hc2000.Machine.Storage;

/// <summary>
/// Explorer copy-in boundary (R9 cpm-rules): puts one host-side file's bytes into an admitted image
/// through the existing <see cref="CpmFs"/> allocation. CP/M 2.2 profiles take invariant-upper-case 8.3 names
/// (forbidden punctuation and non-manual characters refused, user areas 0..15, normalized-name collisions
/// refused unless replace is asked); HC header profiles keep their case-sensitive literal 11-byte names.
/// Every check runs before <see cref="CpmFs.Put"/>'s staged write, so any refusal leaves the image byte-unchanged.
/// </summary>
public sealed class ExplorerCopyIn
{
    public sealed record Copied(string Name, int User, int Records, bool Replaced);

    // DRI CP/M 2.2 manual (M1 1.2.2) excludes these from name components; '"' is refused too because the
    // directory layer (CpmFs.EncodeName) rejects it. Space is blank, not a name character.
    private const string Forbidden = "<>,;:=?*[]%|()/\"\\ ";

    private readonly CpmFs _fs;
    private readonly StringComparison _nameComparison;
    public ExplorerCopyIn(CpmFs fs)
    {
        _fs = fs; _nameComparison = fs.Parameters.HcHeader ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    // HcHeader identity is the literal 11-byte field: CpmFs pads lookups through EncodeName, so the
    // boundary compares padded fields too; CP/M identity is the canonical dotted name.
    private string Field(string name) => _fs.Parameters.HcHeader ? name.PadRight(11) : name;

    public Copied CopyIn(string name, ReadOnlySpan<byte> data, int user = 0, bool replace = false)
    {
        if (user is < 0 or > 15) throw new InvalidDataException("User must be 0..15.");
        var normalized = Normalize(name);
        var field = Field(normalized);
        var existing = _fs.List().SingleOrDefault(f => f.User == user && string.Equals(f.Name, field, _nameComparison));
        if (existing is not null && !replace)
            throw new InvalidDataException($"File already exists: {normalized}.");
        _fs.Put(normalized, data, user);
        var written = _fs.List().Single(f => f.User == user && string.Equals(f.Name, field, _nameComparison));
        return new Copied(normalized, user, written.Records, existing is not null);
    }

    private string Normalize(string name)
    {
        if (_fs.Parameters.HcHeader)
        {
            // HC header disks use case-sensitive literal names: 1..11 7-bit bytes, no 8.3 rewriting.
            if (name.Length is < 1 or > 11 || name.Any(c => c > 0x7f))
                throw new InvalidDataException("HC header names are 1..11 7-bit bytes.");
            return name;
        }
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Name must be a CP/M 8.3 disk filename.");
        // Validate the host spelling first: Unicode case folding can turn non-ASCII letters into ASCII.
        if (name.Any(c => c < 0x21 || c > 0x7e || Forbidden.Contains(c)))
            throw new InvalidDataException("Name contains a forbidden character.");
        name = name.ToUpperInvariant();
        var dot = name.LastIndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var ext = dot < 0 ? "" : name[(dot + 1)..];
        if (stem.Length is < 1 or > 8 || ext.Length is > 3 || stem.Contains('.'))
            throw new InvalidDataException("Name must be a CP/M 8.3 disk filename.");
        // Match CpmFs's directory identity even when the host spells an empty extension with a dot.
        return ext.Length == 0 ? stem : stem + "." + ext;
    }
}
