using System.Collections.Immutable;
using Hc2000.App.Media;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Explorer;

/// <summary>
/// One-level disk-home listing for the two-pane copy view (hc2000-media-home-scan-admission-v1
/// section 3). At most 1024 names are examined before anything is sorted, at most 128 rows are
/// listed, and <see cref="TwoPaneScanResult.Truncated"/> says when either cap stopped the walk.
/// Floppy DSK rows are the only openable kind. An 8 MiB file is hdd-raw only when the cold-volume
/// reader accepts it; a zero-filled 8 MiB file is unknown, never a volume. Larger files are
/// classified from their size and are not read. The scan reads only; the scanned path is never written.
/// </summary>
internal static class TwoPaneCopyListing
{
    internal const int MaxListedRows = 128;
    internal const int MaxExaminedFiles = 1024;

    internal sealed record TwoPaneScanResult(ImmutableArray<ExplorerCopyRow> Rows, string Notice, bool Truncated)
    {
        public override string ToString() => Notice;
    }

    internal static TwoPaneScanResult Scan(string? home)
    {
        if (!OperatingSystem.IsLinux())
            return new(ImmutableArray<ExplorerCopyRow>.Empty, "Disk home scan is unavailable: trustworthy Linux file facts are required.", false);
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home) || !Directory.Exists(home))
            return new(ImmutableArray<ExplorerCopyRow>.Empty, "Disk home is missing, relative, or not a directory; refusing to scan.", false);
        var homeFacts = LinuxFileFacts.Inspect(home);
        if (!homeFacts.Known || homeFacts.Link)
            return new(ImmutableArray<ExplorerCopyRow>.Empty, "Disk home is missing, relative, or not a directory; refusing to scan.", false);

        // Examine cap first: collect up to 1024 names before any sort, so a huge home never
        // buffers and orders an unbounded file list.
        List<string> examined;
        var moreFiles = false;
        try
        {
            examined = new List<string>(MaxExaminedFiles);
            foreach (var path in Directory.EnumerateFiles(home))
            {
                if (examined.Count >= MaxExaminedFiles) { moreFiles = true; break; }
                examined.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(ImmutableArray<ExplorerCopyRow>.Empty, "Disk home could not be listed; refusing to scan. " + ex.Message, false);
        }

        var listed = new List<ExplorerCopyRow>();
        var skipped = 0;
        var rowCapped = false;
        foreach (var path in examined.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            if (listed.Count >= MaxListedRows) { rowCapped = true; break; }
            ExplorerCopyRow? row;
            try { row = Classify(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
            if (row is null) { skipped++; continue; }
            listed.Add(row);
        }
        var notice = listed.Count + " files listed.";
        if (skipped > 0) notice += " " + skipped + " skipped.";
        if (moreFiles) notice += " Examine cap 1024 reached; files beyond it are not examined.";
        if (rowCapped) notice += " Listing capped at 128 files.";
        return new([.. listed], notice, moreFiles || rowCapped);
    }

    private static ExplorerCopyRow? Classify(string path)
    {
        var full = Path.GetFullPath(path);
        var facts = LinuxFileFacts.Inspect(full);
        if (!facts.Known || !facts.Regular || facts.Link) return null;
        var length = new FileInfo(full).Length;
        var name = Path.GetFileName(full);
        if (length == HddMediaAdapter.ImageLength)
        {
            // HDD-sized: read it once and ask the cold-volume reader. A directory that fails
            // validation (zero-fill, garbage) is not a volume.
            try
            {
                CpmFs.OpenHddVolume(File.ReadAllBytes(full));
                return new(name, full, "hdd-raw", length, "HDD raw image; open refused");
            }
            catch (InvalidDataException)
            {
                return new(name, full, "unknown", length, "8 MiB but not a valid cold HDD volume; open refused");
            }
        }
        if (length > MediaFiles.MaxDiskBytes)
            return new(name, full, "unknown", length, "larger than 4 MiB; not read");
        var bytes = File.ReadAllBytes(full);
        try
        {
            var disk = Dsk.Load(bytes);
            if (disk.Format is DiskFormat.Standard or DiskFormat.Extended)
                return new(name, full, "floppy-dsk", length, disk.Format == DiskFormat.Extended ? "extended DSK" : "standard DSK");
        }
        catch (InvalidDataException) { }
        return new(name, full, "unknown", length, "not a floppy DSK; open refused");
    }
}
