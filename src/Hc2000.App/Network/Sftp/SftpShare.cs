using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network.Sftp;

/// <summary>How a drive is shared (hc2000-drive-sharing-sftp-v1 §1): read-only is served and never written; read-write
/// means the drive is off the machine for the session and the remote is the only writer of its bytes.</summary>
public enum SftpShareMode { ReadOnly, ReadWrite }

/// <summary>One shared CP/M drive: its letter, a <see cref="CpmFs"/> over in-memory bytes the share owns for the session,
/// the mode, and the user areas the profile enables (user 0 alone by default). A read-write share of a protected
/// original is refused here, at mount time, not at the first write; a directory whose structural report has issues is
/// served read-only and says why. After the session, <see cref="Changed"/> and <see cref="Bytes"/> give the caller what
/// to put back through the media path.</summary>
public sealed class SftpShare
{
    // DRI CP/M 2.2 manual (M1 1.2.2), as ExplorerCopyIn refuses them; space is blank, not a name character.
    private const string Forbidden = "<>,;:=?*[]%|()/\"\\ ";
    private readonly CpmFs fs;
    private readonly object sync = new();
    private bool changed;

    public SftpShare(char letter, CpmFs fileSystem, SftpShareMode mode, IEnumerable<int>? userAreas = null,
        bool protectedOriginal = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        letter = char.ToUpperInvariant(letter);
        if (letter is < 'A' or > 'P') throw new ArgumentOutOfRangeException(nameof(letter), letter, "A CP/M drive is A to P.");
        if (fileSystem.Parameters.HcHeader)
            throw new ArgumentException("An HC header (hcfs) disk keeps literal 11-byte names, not CP/M 8.3 names; only CP/M disks are shared.",
                nameof(fileSystem));
        var users = (userAreas ?? [0]).Order().ToArray();
        if (users.Length == 0 || users.Any(u => u is < 0 or > CpmExplorer.Cpm22MaxUser) || users.Distinct().Count() != users.Length)
            throw new ArgumentException("User areas are distinct numbers from 0 to 15, at least one.", nameof(userAreas));
        if (mode == SftpShareMode.ReadWrite && protectedOriginal)
            throw new InvalidOperationException($"{letter}: is a protected original: share it read-only, or share a private copy read-write.");
        Letter = letter;
        fs = fileSystem;
        UserAreas = users;
        ProtectedOriginal = protectedOriginal;
        Mode = mode;
        var issues = mode == SftpShareMode.ReadWrite ? fileSystem.InspectStructure().Issues : [];
        if (issues.Count == 0) return;
        Mode = SftpShareMode.ReadOnly;
        ReadOnlyReason = string.Create(CultureInfo.InvariantCulture,
            $"{letter}: is served read-only: its directory has {issues.Count} structural issue(s), the first {issues[0].Code} at entry {issues[0].Entry} ({issues[0].Detail})");
    }

    public char Letter { get; }

    /// <summary>The mode served: read-write only when asked for and the directory is sound.</summary>
    public SftpShareMode Mode { get; }

    /// <summary>Why a drive asked for read-write is served read-only; null otherwise.</summary>
    public string? ReadOnlyReason { get; }

    public IReadOnlyList<int> UserAreas { get; }

    public bool ProtectedOriginal { get; }

    /// <summary>A cold HDD (or M:) volume, handed back as its flat volume bytes; otherwise a floppy image.</summary>
    public bool IsVolume => fs.Parameters == Dpb.HddVolume() || fs.Parameters == Dpb.MVolumeV2();

    /// <summary>True once the session has changed the directory: the drive's bytes must go back and be published.</summary>
    public bool Changed
    {
        get { lock (sync) return changed; }
    }

    /// <summary>The drive's bytes as the media path takes them: <see cref="CpmFs.ExportVolumeBytes"/> for a volume
    /// (<c>HddSlot.Attach</c>), <see cref="CpmFs.ImageBytes"/> for a floppy, in the container it came in.</summary>
    public byte[] Bytes()
    {
        lock (sync) return IsVolume ? fs.ExportVolumeBytes() : fs.ImageBytes();
    }

    public override string ToString() =>
        $"{Letter}: {(Mode == SftpShareMode.ReadWrite ? "read-write" : "read-only")}, user{(UserAreas.Count == 1 ? "" : "s")} {string.Join(' ', UserAreas)}";

    /// <summary>The canonical CP/M name a client may create, upper case, or null with the rule it breaks.</summary>
    internal static string? Canonical(string name, out string? refusal)
    {
        refusal = null;
        // The host spelling is checked before upper-casing: Unicode case folding can turn other letters into ASCII.
        var bad = name.Where(c => c < 0x21 || c > 0x7e || Forbidden.Contains(c)).Select(c => (char?)c).FirstOrDefault();
        var upper = name.ToUpperInvariant();
        var dot = upper.LastIndexOf('.');
        var stem = dot < 0 ? upper : upper[..dot];
        var extension = dot < 0 ? "" : upper[(dot + 1)..];
        var why = name.Length == 0 ? "it is empty"
            : bad is { } c ? (c is < (char)0x21 or > (char)0x7e
                ? string.Create(CultureInfo.InvariantCulture, $"character U+{(int)c:X4} is not a CP/M name character")
                : $"'{c}' is not a CP/M name character")
            : stem.Contains('.') ? "it has more than one dot"
            : stem.Length is < 1 or > 8 ? "the name before the dot is not 1 to 8 characters"
            : extension.Length > 3 ? "the extension is longer than 3 characters"
            : null;
        if (why is null) return extension.Length == 0 ? stem : stem + "." + extension;
        refusal = $"bad name \"{name}\": {why}. CP/M names are 8.3: 1 to 8 characters, optionally a dot and 1 to 3 more, " +
            "stored upper case, without spaces or < > , ; : = ? * [ ] % | ( ) / \\ \"";
        return null;
    }

    internal IReadOnlyList<CpmFile> Files(int user)
    {
        lock (sync) return fs.List().Where(f => f.User == user && f.Name.Length > 0 && !f.Name.Contains('/')).ToArray();
    }

    /// <summary>The committed file, by the stored name (CP/M names match without regard to case).</summary>
    internal CpmFile? Find(string name, int user)
    {
        lock (sync) return fs.List().FirstOrDefault(f => f.User == user && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal byte[] Read(CpmFile file)
    {
        lock (sync) return fs.Get(file.Name, file.User);
    }

    /// <summary>One <see cref="CpmFs.Put"/>: the file's directory and allocation change at once.</summary>
    internal void Write(string name, int user, ReadOnlySpan<byte> data, bool system)
    {
        lock (sync)
        {
            fs.Put(name, data, user, readOnly: false, system);
            changed = true;
        }
    }

    internal void Delete(CpmFile file)
    {
        lock (sync)
        {
            fs.Delete(file.Name, file.User);
            changed = true;
        }
    }

    /// <summary>RENAME inside this drive as read, delete, put. The put takes back no more than the delete freed, and a
    /// put that is refused anyway puts the original back before the refusal goes on.</summary>
    internal void Move(CpmFile file, string name, int user)
    {
        lock (sync)
        {
            var data = fs.Get(file.Name, file.User);
            fs.Delete(file.Name, file.User);
            changed = true;
            try { fs.Put(name, data, user, readOnly: false, file.System); }
            catch (InvalidDataException)
            {
                fs.Put(file.Name, data, file.User, readOnly: false, file.System);
                throw;
            }
        }
    }

    /// <summary>Why <paramref name="length"/> bytes would not fit under this name, saying which (directory full or no free
    /// blocks), or null. It counts what <see cref="CpmFs.Put"/> counts at CLOSE, early, because sshfs never reads the
    /// status of a CLOSE: a WRITE refused is an error its writer sees.</summary>
    internal string? WhyNotFits(string name, int user, long length)
    {
        lock (sync)
        {
            var parameters = fs.Parameters;
            int live = 0, own = 0, used = 0;
            for (var index = 0; index < fs.DirectoryEntryCount; index++)
            {
                var entry = fs.GetDirectoryEntry(index);
                if (entry[0] == 0xe5) continue;
                live++;
                if (entry[0] == user && string.Equals(EntryName(entry), name, StringComparison.OrdinalIgnoreCase)) own++;
                else used += BlockCount(entry, parameters.WordAllocations);
            }
            var extentBytes = 16384L * (parameters.Exm + 1);
            var entriesNeeded = Math.Max(1, (length + extentBytes - 1) / extentBytes);
            var freeEntries = fs.DirectoryEntryCount - live + own;
            if (entriesNeeded > freeEntries)
                return string.Create(CultureInfo.InvariantCulture,
                    $"directory full: {Letter}: has {freeEntries} free directory entries and {name} needs {entriesNeeded}");
            var blocksNeeded = (length + parameters.BlockSize - 1) / parameters.BlockSize;
            var freeBlocks = fs.MaximumBlock + 1 - parameters.DirectoryBlocks - used;
            if (blocksNeeded > freeBlocks)
                return string.Create(CultureInfo.InvariantCulture,
                    $"disk full: no free blocks for {name} on {Letter}: ({freeBlocks * parameters.BlockSize / 1024} KiB free, {blocksNeeded * parameters.BlockSize / 1024} KiB needed)");
            return null;
        }
    }

    // CpmFs's directory name of one raw entry: attribute bits masked, STEM.EXT or STEM.
    private static string EntryName(byte[] entry)
    {
        var field = Encoding.ASCII.GetString(entry.AsSpan(1, 11).ToArray().Select(b => (byte)(b & 0x7f)).ToArray());
        var stem = field[..8].TrimEnd();
        var extension = field[8..].TrimEnd();
        return extension.Length == 0 ? stem : stem + "." + extension;
    }

    private static int BlockCount(byte[] entry, bool words)
    {
        var count = 0;
        for (var offset = 16; offset < 32; offset += words ? 2 : 1)
            if ((words ? BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(offset, 2)) : entry[offset]) != 0) count++;
        return count;
    }
}

/// <summary>The drives one session serves, one share per letter.</summary>
public sealed class SftpShareSet
{
    public SftpShareSet(IEnumerable<SftpShare> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        Shares = shares.OrderBy(s => s.Letter).ToArray();
        if (Shares.Count == 0) throw new ArgumentException("Share at least one drive.", nameof(shares));
        if (Shares.Select(s => s.Letter).Distinct().Count() != Shares.Count)
            throw new ArgumentException("Each drive letter is shared once.", nameof(shares));
    }

    public IReadOnlyList<SftpShare> Shares { get; }

    public SftpShare? Find(char letter) => Shares.FirstOrDefault(s => s.Letter == letter);

    /// <summary>The drives whose bytes the session changed: what the caller puts back and publishes.</summary>
    public IReadOnlyList<SftpShare> Changed => Shares.Where(s => s.Changed).ToArray();
}
