using System.Collections.Immutable;
using Hc2000.App.Settings;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;

namespace Hc2000.App.Media;

/// <summary>
/// One scanned media-home row (contract hc2000-media-home-scan-admission-v1, section 2).
/// <see cref="Kind"/> is the result of the existing inspectors, never a filename or size guess:
/// "snapshot", "floppy-dsk", "hdd-raw", "hdd-container", "tape", or "unknown".
/// </summary>
public sealed record MediaHomeRow(
    string Home,
    string Kind,
    string Name,
    string Path,
    long Size,
    string Detail,
    bool Recognized)
{
    /// <summary>Recognized rows are what the UI slice attaches actions to; unknown rows carry no action.</summary>
    public bool Selectable => Recognized && Kind != "unknown";
}

/// <summary>Per-home availability so a missing or refused home is explicit, never a fake empty list.</summary>
public sealed record MediaHomeState(string Home, bool Available, string Reason)
{
    public static MediaHomeState Configured(string home) => new(home, true, "configured directory");
    public static MediaHomeState Refused(string home, string reason) => new(home, false, reason);
}

/// <summary>
/// The whole scan result: file rows (up to <see cref="MediaHomeScan.MaxRowsPerHome"/> per home) plus
/// one explicit state per media home. <see cref="Truncated"/> is set when either per-home cap stops the scan.
/// Presentation-only like the frozen inline lists: no media is loaded beyond the bounded inspector reads.
/// </summary>
public sealed record MediaHomeListing(ImmutableArray<MediaHomeRow> Rows, ImmutableArray<MediaHomeState> Homes, bool Truncated);

/// <summary>
/// Read-only scan of the configured media homes (AppSettings diskHome / tapeHome / snapshotHome, with the
/// derived snapshot directory fallback). Each home is enumerated non-recursively; a file's kind comes from
/// the existing inspectors (Dsk, CpmFs.OpenHddVolume, HddCatalogCodec.InspectHddFooter, Tap.LoadBounded),
/// and a file that two inspectors both accept, or none accepts, is "unknown" and carries no action.
/// </summary>
public static class MediaHomeScan
{
    /// <summary>Maximum directory entries examined per home.</summary>
    public const int MaxEntriesPerHome = 1024;

    /// <summary>Maximum rows shown per home; matches the frozen inline-list page size.</summary>
    public const int MaxRowsPerHome = 128;

    /// <summary>
    /// TAP block guard for <see cref="Tap.LoadBounded"/>: far above any real tape (16 MiB of 512-byte
    /// blocks is 32,768) while bounding the degenerate tiny-block case that the byte bound alone does not.
    /// </summary>
    public const int MaxTapeBlocks = 65_536;

    /// <summary>
    /// Container footer bounds, mirroring the app's existing cold-volume loader
    /// (<c>HddColdVolume.CodecLimits</c>): the scan classifies exactly what the existing load path accepts.
    /// </summary>
    private static readonly HddLimits ContainerLimits = new(uint.MaxValue, 511, 12_276, 114_640);

    private const string HomeDisk = "disk";
    private const string HomeTape = "tape";
    private const string HomeSnapshot = "snapshot";

    private const string KindSnapshot = "snapshot";
    private const string KindFloppyDsk = "floppy-dsk";
    private const string KindHddRaw = "hdd-raw";
    private const string KindHddContainer = "hdd-container";
    private const string KindTape = "tape";
    private const string KindUnknown = "unknown";

    /// <summary>Scan all three homes from settings; the snapshot home falls back to the derived directory.</summary>
    public static Task<MediaHomeListing> ScanAsync(AppSettings settings, string? derivedSnapshotDirectory = null,
        CancellationToken cancellationToken = default)
        => ScanAsync(settings.DiskHome, settings.TapeHome, settings.SnapshotHome, derivedSnapshotDirectory, cancellationToken);

    /// <summary>
    /// Read-only scan. Missing, relative, non-directory or unlistable homes fail closed (explicit
    /// <see cref="MediaHomeState"/>, no rows for that home); nothing outside the homes is read or listed.
    /// </summary>
    public static async Task<MediaHomeListing> ScanAsync(string? diskHome, string? tapeHome, string? snapshotHome,
        string? derivedSnapshotDirectory = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? snapshotDirectory = string.IsNullOrWhiteSpace(snapshotHome) ? derivedSnapshotDirectory : snapshotHome;
        var disk = ResolveHome(HomeDisk, diskHome);
        var tape = ResolveHome(HomeTape, tapeHome);
        var snapshot = ResolveHome(HomeSnapshot, snapshotDirectory);

        var rows = new List<MediaHomeRow>(MaxRowsPerHome * 3);
        var truncated = false;
        foreach (var home in new[] { disk, tape, snapshot })
        {
            if (home.Full is null) continue;
            var homeRows = await ScanResolvedHomeAsync(home.State.Home, home.Full, cancellationToken);
            truncated |= homeRows.Truncated;
            rows.AddRange(homeRows.Rows);
        }
        return new MediaHomeListing(rows.OrderBy(r => r.Home, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal).ToImmutableArray(),
            new[] { disk.State, tape.State, snapshot.State }.ToImmutableArray(), truncated);
    }

    private static async Task<(List<MediaHomeRow> Rows, bool Truncated, int Examined, bool More)> ScanResolvedHomeAsync(
        string home, string full, CancellationToken ct)
    {
        List<string> files;
        try
        {
            // Bound enumeration before sorting; directories and links consume the entry budget too.
            files = Directory.EnumerateFileSystemEntries(full).Take(MaxEntriesPerHome).ToList();
            files.Sort(StringComparer.Ordinal);
        }
        catch
        {
            // Fail closed: an unlistable directory produces no rows for that home, never a partial list.
            return ([], false, 0, false);
        }
        var rows = new List<MediaHomeRow>();
        // Do not probe a 1025th entry just to prove there is more. Reaching the cap is conservative.
        var examined = 0; var more = files.Count == MaxEntriesPerHome;
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            examined++;
            var row = await ClassifyAsync(home, path, ct);
            if (row is null) continue; // non-regular entry (directory, symlink, junction): not a media row
            if (rows.Count == MaxRowsPerHome) { more = true; break; }
            rows.Add(row);
        }
        if (examined < files.Count) more = true;
        return (rows, more, examined, more);
    }

    /// <summary>Fail-closed home resolution: absent / relative / non-directory paths never get scanned.</summary>
    private static (MediaHomeState State, string? Full) ResolveHome(string home, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return (MediaHomeState.Refused(home, "not configured"), null);
        if (!Path.IsPathRooted(configured))
            return (MediaHomeState.Refused(home, "relative path; fail-closed, not scanned"), null);
        string full;
        try { full = Path.GetFullPath(configured); }
        catch { return (MediaHomeState.Refused(home, "invalid local path; fail-closed, not scanned"), null); }
        try
        {
            for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
            {
                if (directory.LinkTarget is not null)
                    return (MediaHomeState.Refused(home, "symbolic link or junction; fail-closed, not scanned"), null);
                // Preserve the existing missing/non-directory diagnostics below.
                if (!directory.Exists) break;
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return (MediaHomeState.Refused(home, "symbolic link or junction; fail-closed, not scanned"), null);
            }
            if (Directory.Exists(full)) return (MediaHomeState.Configured(home), full);
            if (File.Exists(full)) return (MediaHomeState.Refused(home, "not a directory; fail-closed, not scanned"), null);
            return (MediaHomeState.Refused(home, "directory missing on the host; fail-closed, not scanned"), null);
        }
        catch { return (MediaHomeState.Refused(home, "directory not reachable; fail-closed, not scanned"), null); }
    }

    /// <summary>
    /// One file's kind from the existing inspectors. Regular files only (symlinks and junctions are never
    /// followed). Zero-byte files are a blank tape under the tape home and unknown everywhere else;
    /// a file accepted by two inspectors, or by none, is unknown. Unreadable files are unknown rows.
    /// </summary>
    private static async Task<MediaHomeRow?> ClassifyAsync(string home, string path, CancellationToken ct)
    {
        FileInfo info;
        long length;
        try
        {
            info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.Directory) != 0) return null;
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            if (OperatingSystem.IsLinux())
            {
                var facts = LinuxFileFacts.Inspect(path);
                if (!facts.Known || !facts.Regular || facts.Link) return null;
            }
            length = info.Length;
        }
        catch { return null; }

        var name = Path.GetFileName(path);
        if (length == 0)
            return home == HomeTape
                ? new MediaHomeRow(HomeTape, KindTape, name, path, 0, "blank tape (0 B)", true)
                : new MediaHomeRow(home, KindUnknown, name, path, 0, "zero-byte file; not a blank tape here", false);

        var accepts = new List<(string Kind, string Detail)>();
        // Snapshot: only under the snapshot home (or derived fallback), name ends in .hcs, within the
        // existing load guard. Host metadata only - .hcs content is never read or decoded here.
        if (home == HomeSnapshot && name.EndsWith(".hcs", StringComparison.Ordinal) && length <= MediaFiles.MaxSnapshotBytes)
            accepts.Add((KindSnapshot, $"{length} B snapshot image (.hcs)"));

        // Whole-file inspectors share one bounded read (each gate implies length <= MaxTapeBytes).
        if (length <= MediaFiles.MaxTapeBytes)
        {
            ImmutableArray<byte> bytes;
            try { bytes = await MediaFiles.ReadAsync(path, MediaFiles.MaxTapeBytes); }
            catch
            {
                return new MediaHomeRow(home, KindUnknown, name, path, length, "unreadable file; inspector not run", false);
            }
            if (length <= MediaFiles.MaxDiskBytes)
            {
                try { Dsk.Load(bytes.AsSpan()); accepts.Add((KindFloppyDsk, $"DSK image, {length} B (standard or extended)")); }
                catch { /* not a DSK image */ }
            }
            if (length == HddMediaAdapter.ImageLength)
            {
                try { CpmFs.OpenHddVolume(bytes.AsSpan()); accepts.Add((KindHddRaw, $"cold HDD volume, {length} B (16384 x 512)")); }
                catch { /* not a valid cold volume */ }
            }
            var tape = Tap.LoadBounded(bytes.AsSpan(), MediaFiles.MaxTapeBytes, MaxTapeBlocks);
            if (tape.Success) accepts.Add((KindTape, $"TAP tape, {length} B"));
        }

        // Container footer: the last 64 bytes only; the arena and index are never read here.
        if (length >= 64)
        {
            byte[] footer;
            try { footer = await ReadTailAsync(path, 64, ct); }
            catch
            {
                return new MediaHomeRow(home, KindUnknown, name, path, length, "unreadable file; footer not inspected", false);
            }
            var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, ContainerLimits);
            if (inspected.Success)
                accepts.Add((KindHddContainer, $"HDD container, revision {inspected.Value.Revision}, {inspected.Value.Entries} entries"));
        }

        if (accepts.Count == 1)
            return new MediaHomeRow(home, accepts[0].Kind, name, path, length, accepts[0].Detail, true);
        if (accepts.Count == 0)
            return new MediaHomeRow(home, KindUnknown, name, path, length, "no existing inspector accepts this file", false);
        return new MediaHomeRow(home, KindUnknown, name, path, length,
            $"ambiguous: {accepts.Count} inspectors accept; no action without an explicit choice", false);
    }

    private static Task<byte[]> ReadTailAsync(string path, int count, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < count) throw new IOException("File shorter than the footer.");
            stream.Seek(-count, SeekOrigin.End);
            var tail = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(tail, read, count - read);
                if (n <= 0) throw new IOException("Short read of the footer.");
                read += n;
            }
            return tail;
        }, ct);
    }
}
