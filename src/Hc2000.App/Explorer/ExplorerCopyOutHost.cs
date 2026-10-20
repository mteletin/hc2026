using Hc2000.Machine.Storage;

namespace Hc2000.App.Explorer;

/// <summary>
/// Mapping returned by the explorer export lane: the on-disk CP/M identity of the exported
/// entry (name and user area, kept unsanitised) and the exact host path where the stored
/// records landed.
/// </summary>
public sealed record CpmExportMapping(string CpmName, int User, string HostPath, int Records, int Bytes)
{
    public string HostName => Path.GetFileName(HostPath);
}

/// <summary>
/// Explorer export-lane host layer (hc2000-explorer-copy-out): reads one named entry out of an
/// admitted image and writes the stored records to a host path, byte-faithful to the records as
/// stored (terminal 0x00 and 0x1A kept, no trimming). The source image is opened read-only and
/// never mutated. The host file name is sanitised to a legal host name; the CP/M identity is
/// kept in the returned mapping. A host collision is refused unless replace is explicit.
/// </summary>
public static class ExplorerCopyOutHost
{
    /// <summary>
    /// Reads one entry out of the image at imagePath (opened read-only, shared for reading) and
    /// writes the stored records to hostPath. If hostPath names a directory (empty file name or
    /// an existing directory), the sanitised CP/M name is used as the host file name. Refuses to
    /// overwrite an existing host file unless replace is explicit.
    /// </summary>
    public static CpmExportMapping CopyOut(string imagePath, string cpmName, int user, string hostPath,
        bool replace = false, string format = "auto", DiskGeometry? rawGeometry = null)
    {
        if (imagePath is null) throw new ArgumentNullException(nameof(imagePath));
        using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = checked((int)source.Length);
        if (length == 0) throw new InvalidDataException("Image is empty.");
        var image = new byte[length]; source.ReadExactly(image);
        return CopyOutCore(CpmFs.Open(Dsk.Load(image, rawGeometry), format), cpmName, user, hostPath, replace, imagePath);
    }

    /// <summary>
    /// Export-lane host core: reads one entry out of an already-opened filesystem (the image is
    /// never reopened or mutated) and writes the stored records to hostPath, following the same
    /// directory/name and collision rules as the image-path overload.
    /// </summary>
    public static CpmExportMapping CopyOut(CpmFs fs, string cpmName, int user, string hostPath, bool replace = false)
        => CopyOutCore(fs, cpmName, user, hostPath, replace, null);

    private static CpmExportMapping CopyOutCore(CpmFs fs, string cpmName, int user, string hostPath, bool replace, string? imagePath)
    {
        var entry = ExplorerCopyOut.Extract(fs, cpmName, user);
        if (hostPath is null) throw new ArgumentNullException(nameof(hostPath));
        var target = Path.GetFullPath(hostPath);
        if (Path.GetFileName(target).Length == 0 || Directory.Exists(target)) target = Path.Combine(target, ExplorerCopyOut.HostName(entry.CpmName));
        if (imagePath is not null)
        {
            // Rename-based replacement can overwrite a read-only, open image on Unix. Resolve
            // parent links too, and protect both a source link's entry and its actual image.
            var destination = CanonicalPath(target, resolveLeaf: false);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(destination, CanonicalPath(imagePath, resolveLeaf: false), comparison)
                || string.Equals(destination, CanonicalPath(imagePath, resolveLeaf: true), comparison))
                throw new IOException("Refusing to replace the source image with its exported contents.");
        }
        if (File.Exists(target) && !replace)
            throw new IOException($"Refusing to overwrite existing host path {target}; re-export with replace to overwrite.");
        WriteAtomic(target, entry.Data, replace);
        return new CpmExportMapping(entry.CpmName, entry.User, target, entry.Records, entry.Data.Length);
    }

    private static string CanonicalPath(string path, bool resolveLeaf)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var parts = full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (i == parts.Length - 1 && !resolveLeaf) break;
            FileSystemInfo entry = i == parts.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
            current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    private static void WriteAtomic(string target, byte[] bytes, bool overwrite)
    {
        var temporary = target + ".hc-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
