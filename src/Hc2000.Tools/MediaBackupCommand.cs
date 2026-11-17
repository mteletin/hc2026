using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hc2000.Tools;

/// <summary>Detached personal media copies; no mounts, codecs or guest filesystem changes.</summary>
public static class MediaBackupCommand
{
    private const string ManifestName = "manifest.json";
    private const int MaxFiles = 10000;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    internal sealed record Item(string Path, long Size, string Sha256);
    internal sealed record Manifest(int Version, Item[] Files);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            switch (args)
            {
                case ["--help"]:
                    output.WriteLine("usage: hc backup create <archive.zip> <source-root> <relative-media-file>...");
                    output.WriteLine("       hc backup verify <archive.zip>");
                    output.WriteLine("       hc backup restore <archive.zip> <new-destination-folder>");
                    output.WriteLine("Close all selected media first. Select disk, tape and snapshot files explicitly; do not select ROMs, credentials or settings.");
                    output.WriteLine("create never overwrites; manifest.json lists relative names, byte sizes and SHA256 hashes. verify checks every file.");
                    output.WriteLine("restore validates everything before atomically publishing a new folder; an existing destination is refused, never merged or overwritten.");
                    return 0;
                case ["create", var archive, var root, .. var files] when files.Length > 0:
                    Create(archive, root, files);
                    output.WriteLine($"Backup created: {archive}");
                    return 0;
                case ["verify", var archive]:
                    using (var input = OpenArchive(archive))
                    using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
                    {
                        var manifest = ReadManifest(zip);
                        CheckContents(zip, manifest, null);
                        output.WriteLine($"Verified {manifest.Files.Length} files.");
                    }
                    return 0;
                case ["restore", var archive, var destination]:
                    Restore(archive, destination);
                    output.WriteLine($"Restored: {destination}");
                    return 0;
                default:
                    throw new ArgumentException("Use hc backup --help for create, verify and restore usage.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException or OverflowException)
        {
            error.WriteLine($"hc backup: {ex.Message}");
            return 1;
        }
    }

    private static FileStream OpenArchive(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static void Create(string archive, string root, string[] files)
    {
        ValidatePaths(files);
        root = Path.GetFullPath(root);
        archive = Path.GetFullPath(archive);
        RequireAbsent(archive);
        var parent = Path.GetDirectoryName(archive)!;
        if (!Directory.Exists(parent)) throw new IOException("Archive parent folder does not exist.");
        var temporary = Path.Combine(parent, $".hc-backup-{Guid.NewGuid():N}.tmp");
        var sources = new List<FileStream>();
        try
        {
            foreach (var name in files)
            {
                var source = Path.Combine(root, name);
                RejectLinks(root, name);
                sources.Add(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None));
            }
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(target, ZipArchiveMode.Create, true))
                {
                    var items = new List<Item>();
                    for (var index = 0; index < sources.Count; index++)
                    {
                        using (var entry = zip.CreateEntry("media/" + files[index], CompressionLevel.NoCompression).Open())
                            items.Add(CopySource(sources[index], entry, files[index]));
                    }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, items.ToArray()), new JsonSerializerOptions { WriteIndented = true });
                    if (bytes.Length > MaxManifestBytes) throw new IOException("Manifest is too large.");
                    using var manifest = zip.CreateEntry(ManifestName).Open();
                    manifest.Write(bytes);
                }
                target.Flush(true);
            }
            using (var check = OpenArchive(temporary))
            using (var zip = new ZipArchive(check, ZipArchiveMode.Read))
            {
                var manifest = ReadManifest(zip);
                CheckContents(zip, manifest, null);
                for (var index = 0; index < sources.Count; index++)
                {
                    sources[index].Position = 0;
                    if (sources[index].Length != manifest.Files[index].Size
                        || Convert.ToHexStringLower(SHA256.HashData(sources[index])) != manifest.Files[index].Sha256)
                        throw new IOException($"Source changed during backup: {files[index]}");
                }
            }
            File.Move(temporary, archive, false);
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static Item CopySource(Stream source, Stream target, string name)
    {
        var size = source.Length;
        var hash = Convert.ToHexStringLower(SHA256.HashData(source));
        source.Position = 0;
        source.CopyTo(target);
        source.Position = 0;
        if (source.Length != size || Convert.ToHexStringLower(SHA256.HashData(source)) != hash)
            throw new IOException($"Source changed during backup: {name}");
        return new(name, size, hash);
    }

    private static void Restore(string archive, string destination)
    {
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        RequireAbsent(destination);
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Choose a new destination folder.");
        if (!Directory.Exists(parent)) throw new IOException("Destination parent folder does not exist.");
        using var input = OpenArchive(archive);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var manifest = ReadManifest(zip);
        CheckContents(zip, manifest, null);
        var stage = Path.Combine(parent, $".hc-restore-{Guid.NewGuid():N}");
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stage);
            else Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            CheckContents(zip, manifest, stage);
            Directory.Move(stage, destination);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    private static Manifest ReadManifest(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxFiles + 1) throw new InvalidDataException("Too many archive entries.");
        var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Missing manifest.json.");
        if (entry.Length > MaxManifestBytes) throw new InvalidDataException("Manifest is too large.");
        using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        stream.ReadExactly(bytes);
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes) ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.Version != 1 || manifest.Files is null || manifest.Files.Any(item => item is null))
            throw new InvalidDataException("Unsupported or invalid manifest.");
        ValidatePaths(manifest.Files.Select(item => item.Path).ToArray());
        var expected = manifest.Files.Select(item => "media/" + item.Path).Append(ManifestName).ToHashSet(StringComparer.Ordinal);
        if (zip.Entries.Count != expected.Count || zip.Entries.Any(item => !expected.Remove(item.FullName)))
            throw new InvalidDataException("Archive entries do not match the manifest.");
        foreach (var item in manifest.Files)
        {
            if (item.Size < 0 || item.Sha256 is null || item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit)
                || zip.GetEntry("media/" + item.Path)!.Length != item.Size)
                throw new InvalidDataException($"Invalid manifest size or hash: {item.Path}");
        }
        return manifest;
    }

    private static void CheckContents(ZipArchive zip, Manifest manifest, string? destination)
    {
        var buffer = new byte[65536];
        foreach (var item in manifest.Files)
        {
            using var input = zip.GetEntry("media/" + item.Path)!.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream? output = null;
            try
            {
                if (destination is not null)
                {
                    var path = Path.Combine(destination, item.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                long count = 0;
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    count = checked(count + read);
                    if (count > item.Size) throw new InvalidDataException($"Entry exceeds declared size: {item.Path}");
                    hash.AppendData(buffer, 0, read);
                    output?.Write(buffer, 0, read);
                }
                if (count != item.Size || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Content hash or size mismatch: {item.Path}");
                output?.Flush(true);
            }
            finally { output?.Dispose(); }
        }
    }

    private static void ValidatePaths(string[] paths)
    {
        if (paths.Length is 0 or > MaxFiles) throw new InvalidDataException("Select between 1 and 10000 files.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Any(character => character < 32 || "\\:<>\"|?*".Contains(character)))
                throw new InvalidDataException($"Unsafe relative path: {path}");
            foreach (var part in path.Split('/'))
            {
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                    || stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3])))
                    throw new InvalidDataException($"Unsafe relative path: {path}");
            }
            if (!names.Add(path)) throw new InvalidDataException($"Duplicate relative path: {path}");
        }
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            for (var count = 1; count < parts.Length; count++)
                if (names.Contains(string.Join('/', parts.Take(count)))) throw new InvalidDataException($"File/folder conflict: {path}");
        }
    }

    private static void RejectLinks(string root, string relative)
    {
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException($"Symbolic links are not supported: {current}");
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException($"Symbolic links are not supported: {current}");
        }
    }

    private static void RequireAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException($"Destination already exists; refusing to overwrite: {path}");
    }
}
