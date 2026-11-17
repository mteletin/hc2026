using Hc2000.App.Media;

namespace Hc2000.App.Explorer;

internal sealed record InstalledMediaEntry(string Name, string Path, bool IsDirectory, MediaHomeRow? Media);
internal sealed record InstalledMediaListing(string Path, IReadOnlyList<InstalledMediaEntry> Entries, bool Truncated, bool Available, string Notice);

internal static class InstalledMediaDirectory
{
    internal static IReadOnlyList<string> Roots(string? assetRoot = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = InstalledLayout.ConfigDirectories().Select(path => Path.Combine(path, "disks"))
            .Append(Path.Combine(AppContext.BaseDirectory, "disks"));
        if (!string.IsNullOrWhiteSpace(assetRoot) && Path.IsPathFullyQualified(assetRoot))
            roots = roots.Append(Path.Combine(assetRoot, "disks"));
        return roots.Select(Path.GetFullPath).Distinct(comparer).ToArray();
    }

    internal static bool Contains(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison);
    }

    internal static async Task<InstalledMediaListing> ListAsync(string root, string path, CancellationToken cancellation = default)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(path);
            if (!Contains(fullRoot, fullPath))
                throw new IOException("Location is outside the installed media root.");
            for (DirectoryInfo? directory = new(fullPath); directory is not null; directory = directory.Parent)
            {
                if (!directory.Exists) throw new IOException("Directory is unavailable.");
                if (directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Links and junctions are not followed.");
            }
            var paths = Directory.EnumerateFileSystemEntries(fullPath).Take(MediaHomeScan.MaxEntriesPerHome).ToArray();
            Array.Sort(paths, StringComparer.Ordinal);
            var entries = new List<InstalledMediaEntry>();
            var truncated = paths.Length == MediaHomeScan.MaxEntriesPerHome;
            foreach (var entryPath in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                var info = new FileInfo(entryPath);
                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                InstalledMediaEntry? entry;
                if ((info.Attributes & FileAttributes.Directory) != 0)
                    entry = new(Path.GetFileName(entryPath), entryPath, true, null);
                else
                {
                    var media = await MediaHomeScan.ClassifyAsync("installed", entryPath, cancellation);
                    entry = media is null ? null : new(media.Name, media.Path, false, media);
                }
                if (entry is null) continue;
                if (entries.Count == MediaHomeScan.MaxRowsPerHome) { truncated = true; break; }
                entries.Add(entry);
            }
            return new(fullPath, entries, truncated, true,
                entries.Count == 0 ? "No entries in this installed media folder." : $"{entries.Count} item(s)." );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(path, [], false, false, "Installed media unavailable: " + exception.Message);
        }
    }
}
