using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace Hc2000.HostFiles;

// Public builds consume compiled guest assets, not the private assembly-source tree.
// Verify the complete group before writing any output; corrupt or missing assets fail the build.
internal static class PublicGuestPayload
{
    internal static void Copy(string root, string group, string output)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        if (manifest.RootElement.GetProperty("schema").GetInt32() != 1)
            throw new InvalidDataException("Unsupported compiled guest payload schema.");
        if (group is not ("utilities" or "cpnet" or "workstation"))
            throw new InvalidDataException("Unknown compiled guest payload group.");
        var repository = Path.GetFullPath(Path.Combine(root, "../.."));
        if (File.Exists(Path.Combine(repository, "Hc2000.slnx")) && Directory.Exists(Path.Combine(repository, "src-library")))
        {
            var actual = SourceDigest(repository);
            if (!string.Equals(actual, manifest.RootElement.GetProperty("privateSourceSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Compiled guest payload is stale; refresh it from the private guest sources.");
        }
        var files = new List<(string Path, byte[] Bytes)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.RootElement.GetProperty("groups").GetProperty(group).EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString()!;
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
                relative.Split('/').Any(part => part is "" or "." or "..") || !names.Add(relative))
                throw new InvalidDataException("Invalid compiled guest payload path.");
            var bytes = File.ReadAllBytes(Path.Combine(root, group, relative));
            if (bytes.LongLength != entry.GetProperty("bytes").GetInt64() ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Compiled guest payload checksum mismatch: " + relative);
            files.Add((relative, bytes));
        }
        if (files.Count == 0) throw new InvalidDataException("Empty compiled guest payload group.");
        foreach (var (relative, bytes) in files)
        {
            var path = Path.Combine(output, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }
    internal static string SourceDigest(string repository)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var folder in new[] { "src-library/extended", "src-library/shared", "bios/src/hc2000/upgrade", "software/tools/cpm-stress" })
            foreach (var source in Directory.EnumerateFiles(Path.Combine(repository, folder), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (Path.GetExtension(source) is not (".asm" or ".inc")) continue;
                digest.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repository, source).Replace('\\', '/')));
                digest.AppendData(new byte[] { 0 });
                digest.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(source).Replace("\r\n", "\n"))));
            }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

}
