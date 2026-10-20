using System.Collections.Immutable;
using Hc2000.App.Library;

namespace Hc2000.App.Media;

/// <summary>Typed availability of one inline listing row (frozen app-tabs contract, media projection).</summary>
public enum InlineMediaStatus { Ready, Unavailable, Refused }

/// <summary>
/// One inline media row. Presentation-only: it projects a canonical catalog entry or an
/// existing inspection result (stable handle, kind, name, path, size, detail) and never owns
/// media bytes. Selection dispatches to the existing guarded actions; the handle is
/// revalidated at action time.
/// </summary>
public sealed record InlineMediaRow(
    string Handle,
    string Kind,
    string Name,
    string? Path,
    long Size,
    string Detail,
    InlineMediaStatus Status,
    string? Reason = null)
{
    public bool Selectable => Status == InlineMediaStatus.Ready && Path is not null;

    public static InlineMediaRow Unavailable(string kind, string reason) =>
        new($"{kind}:unavailable", kind, "—", null, 0, reason, InlineMediaStatus.Unavailable, reason);
}

/// <summary>
/// Presentation-only adapter projecting canonical catalog/inspection results into inline rows
/// for the machine UI. No guest readers, no new media model, no mutation: missing providers
/// are explicitly unavailable rows, not fake empty lists, and no HCS content is decoded here
/// (host file metadata only).
/// </summary>
public static class InlineMediaListing
{
    /// <summary>Maximum rows per page (frozen app-tabs contract).</summary>
    public const int MaxRowsPerPage = 128;

    // Floppy disks: canonical disk-library catalog entries (registered v1 floppy data; raw
    // configuration rows remain unmanaged by the library). A missing provider is a single
    // explicitly typed Unavailable row.
    public static ImmutableArray<InlineMediaRow> Floppies(IReadOnlyList<DiskLibraryEntry>? entries, string? providerReason = null)
    {
        if (entries is null)
            return [InlineMediaRow.Unavailable("disk", providerReason ?? "Disk library not configured for this machine.")];
        var rows = new List<InlineMediaRow>(Math.Min(entries.Count, MaxRowsPerPage) + 1);
        for (int i = 0; i < Math.Min(entries.Count, MaxRowsPerPage); i++)
        {
            var entry = entries[i];
            (InlineMediaStatus status, string? reason) = EntryStatus(entry);
            rows.Add(new($"disk:{entry.Id}", "disk", entry.Name, entry.SourcePath, entry.Length,
                $"{entry.Format} · source {entry.SourcePath}", status, reason));
        }
        if (entries.Count > MaxRowsPerPage)
            rows.Add(InlineMediaRow.Unavailable("disk", $"{entries.Count - MaxRowsPerPage} more catalog entries not listed (page limit {MaxRowsPerPage})."));
        return rows.ToImmutableArray();
    }

    // HDD: existing cold-prepared selections, named read-only volumes, and runtime
    // inspection views. Never a filesystem browser or a second media model.
    public static ImmutableArray<InlineMediaRow> Hdd(
        bool enabled,
        ImmutableArray<HddDriveView> runtimeViews,
        ImmutableArray<HddInitialMedia>? prepared,
        (string Path, string Name, byte Drive)? namedSelection = null,
        ImmutableArray<(string Path, string Name, byte Drive)> namedMap = default,
        IReadOnlySet<byte>? writableNamedDrives = null)
    {
        if (!enabled)
            return [InlineMediaRow.Unavailable("hdd", "HDD profile off for this machine; choose New machine, then enable HDD in its hardware settings before Start.")];
        var rows = new List<InlineMediaRow>();
        var names = namedMap.IsDefault
            ? (namedSelection is { } single ? ImmutableArray.Create(single) : []) : namedMap;
        foreach (var named in names)
            rows.Add(new($"hdd:volume:{named.Path}:{named.Drive}", "hdd", named.Name, named.Path, HddMediaAdapter.ImageLength,
                $"named {(writableNamedDrives?.Contains(named.Drive) == true ? "writable (opt-in)" : "read-only")} volume on {Drive(named.Drive)} · 8 MiB container · prepared, not attached", InlineMediaStatus.Ready));
        if (prepared is not null)
            foreach (var image in prepared)
                rows.Add(new($"hdd:prepared:{image.Drive}", "hdd", image.Name ?? $"{Drive(image.Drive)}: raw image", null, image.Bytes.Length,
                    $"prepared raw image on {Drive(image.Drive)} · exact {image.Bytes.Length} bytes · {(image.WriteProtected ? "write-protected" : "writable")} · not attached",
                    InlineMediaStatus.Ready));
        foreach (var view in runtimeViews)
        {
            string letter = Drive(view.Drive);
            rows.Add(new($"hdd:{view.Drive}", "hdd",
                view.Present ? view.Name ?? $"{letter}: mounted" : $"{letter}: empty", null, 0,
                $"runtime observation · generation {view.Generation} · {(view.WriteProtected ? "protected" : "writable")} · {(view.Dirty ? "dirty" : "clean")}",
                view.Present ? InlineMediaStatus.Ready : InlineMediaStatus.Unavailable,
                view.Present ? null : "No media on this drive."));
        }
        if (rows.Count == 0)
            return [InlineMediaRow.Unavailable("hdd", "No HDD media prepared or mounted.")];
        return rows.ToImmutableArray();
    }

    // Snapshots: host metadata of the machine's scoped directory only. No .hcs content is
    // read or decoded; explicit load goes through the existing guarded load action, and an
    // oversized file is refused here by the same bound the load action enforces.
    public static ImmutableArray<InlineMediaRow> Snapshots(string? directory, string? lastSnapshot = null,
        Hc2000.Machine.Snapshots.HcsLimits? limits = null)
    {
        limits ??= new();
        if (directory is null)
            return [InlineMediaRow.Unavailable("snapshot", "No snapshot directory configured for this machine.")];
        string full;
        try { full = Path.GetFullPath(directory); }
        catch { return [InlineMediaRow.Unavailable("snapshot", "Snapshot directory is not a valid local path.")]; }
        if (!Directory.Exists(full))
            return [InlineMediaRow.Unavailable("snapshot", "Snapshot directory missing on the host.")];
        ImmutableArray<string> files;
        try { files = Directory.EnumerateFiles(full, "*.hcs").OrderBy(path => path, StringComparer.Ordinal).ToImmutableArray(); }
        catch (Exception ex)
        { return [InlineMediaRow.Unavailable("snapshot", "Snapshot directory cannot be listed: " + ex.Message)]; }
        var rows = new List<InlineMediaRow>(Math.Min(files.Length, MaxRowsPerPage) + 1);
        for (int i = 0; i < Math.Min(files.Length, MaxRowsPerPage); i++)
        {
            string path = files[i];
            (long size, string detail, InlineMediaStatus status, string? reason) = SnapshotMetadata(path, limits);
            bool isLast = lastSnapshot is not null && PathEquals(Path.GetFullPath(lastSnapshot), path);
            rows.Add(new($"hcs:{path}", "snapshot", Path.GetFileName(path) + (isLast ? " (last selection)" : ""), path, size,
                detail, status, reason));
        }
        if (files.Length > MaxRowsPerPage)
            rows.Add(InlineMediaRow.Unavailable("snapshot", $"{files.Length - MaxRowsPerPage} more snapshots not listed (page limit {MaxRowsPerPage})."));
        if (rows.Count == 0)
            rows.Add(InlineMediaRow.Unavailable("snapshot", "No .hcs snapshots in this machine's snapshot directory."));
        return rows.ToImmutableArray();
    }

    /// <summary>Revalidate a listed disk handle at action time: the entry is still listed and its source file still exists.</summary>
    public static string? RevalidateDiskHandle(IReadOnlyList<DiskLibraryEntry>? entries, string handle, string? path)
    {
        if (entries is null) return "Disk library no longer configured; refresh the disk library list.";
        string id = handle.Length > 5 && handle.StartsWith("disk:", StringComparison.Ordinal) ? handle[5..] : "";
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry is null || !string.Equals(entry.SourcePath, path, StringComparison.Ordinal))
            return "Listing changed; refresh the disk library list.";
        try { if (!File.Exists(entry.SourcePath)) return "Catalog source file missing; refresh the disk library list."; }
        catch { return "Catalog source file unreadable."; }
        return null;
    }

    /// <summary>Revalidate a listed snapshot handle at action time: same host metadata as listed.</summary>
    public static string? RevalidateSnapshot(string path, long listedSize, Hc2000.Machine.Snapshots.HcsLimits? limits = null)
    {
        limits ??= new();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "Snapshot file no longer exists; refresh the list.";
            if (!limits.AllowsFileLength(info.Length))
                return $"Snapshot exceeds {limits.TotalBytes} bytes; the existing load guard applies.";
            if (info.Length != listedSize)
                return "Snapshot changed since listing; refresh the list.";
            return null;
        }
        catch (Exception ex) { return "Snapshot metadata unavailable: " + ex.Message; }
    }

    private static (InlineMediaStatus, string?) EntryStatus(DiskLibraryEntry entry)
    {
        try { if (File.Exists(entry.SourcePath)) return (InlineMediaStatus.Ready, null); }
        catch { }
        return (InlineMediaStatus.Unavailable, "Catalog source file missing or unreadable.");
    }

    private static (long Size, string Detail, InlineMediaStatus Status, string? Reason) SnapshotMetadata(string path,
        Hc2000.Machine.Snapshots.HcsLimits limits)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return (0, "unreadable host metadata", InlineMediaStatus.Unavailable, "Snapshot file missing.");
            if (!limits.AllowsFileLength(info.Length))
                return (info.Length, $"{info.Length} B", InlineMediaStatus.Refused,
                    $"Snapshot exceeds {limits.TotalBytes} bytes; the existing load guard applies.");
            return (info.Length, $"{info.Length} B · saved {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC", InlineMediaStatus.Ready, null);
        }
        catch (Exception ex)
        { return (0, "unreadable host metadata", InlineMediaStatus.Unavailable, "Snapshot metadata unavailable: " + ex.Message); }
    }

    private static string Drive(byte drive) => ((char)('C' + (drive - 2))) + ":";

    private static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal); }
        catch { return false; }
    }
}
