using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hc2000.Z80.Tests;

public class AssetIntegrityTests
{
    [Z80AssetFact]
    public void PinnedManifestMatchesEveryFixtureAndCompleteJsonInventory()
    {
        var root = Z80AssetFactAttribute.AssetRoot()!;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var files = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(1617, files.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in files)
        {
            var relative = row.GetProperty("local").GetString()!;
            Assert.True(seen.Add(relative));
            var path = Path.Combine(root, relative);
            using var stream = File.OpenRead(path);
            Assert.Equal(row.GetProperty("size").GetInt64(), stream.Length);
            Assert.Equal(row.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(stream)));
            if (row.TryGetProperty("git_blob_sha1", out var gitHash))
            {
                stream.Position = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                hash.AppendData(Encoding.ASCII.GetBytes($"blob {stream.Length}\0"));
                var buffer = new byte[65536];
                int n;
                while ((n = stream.Read(buffer)) != 0) hash.AppendData(buffer, 0, n);
                Assert.Equal(gitHash.GetString(), Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
        }
        var jsonFiles = files.Where(r => r.GetProperty("local").GetString()!.StartsWith("singlestep/v1/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(1604, jsonFiles.Length);
        Assert.Equal(1374635394L, jsonFiles.Sum(r => r.GetProperty("size").GetInt64()));
        Assert.Equal(1604, Directory.EnumerateFiles(Path.Combine(root, "singlestep", "v1"), "*.json").Count());
        using var tree = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "singlestep", "upstream-tree.json")));
        Assert.False(tree.RootElement.GetProperty("truncated").GetBoolean());
        var upstream = tree.RootElement.GetProperty("tree").EnumerateArray()
            .Where(r => r.GetProperty("type").GetString() == "blob" && r.GetProperty("path").GetString()!.StartsWith("v1/", StringComparison.Ordinal)
                && r.GetProperty("path").GetString()!.EndsWith(".json", StringComparison.Ordinal))
            .ToDictionary(r => r.GetProperty("path").GetString()!, StringComparer.Ordinal);
        Assert.Equal(1604, upstream.Count);
        foreach (var row in jsonFiles)
        {
            var expected = upstream[row.GetProperty("upstream").GetString()!];
            Assert.Equal(expected.GetProperty("sha").GetString(), row.GetProperty("git_blob_sha1").GetString());
            Assert.Equal(expected.GetProperty("size").GetInt64(), row.GetProperty("size").GetInt64());
        }
    }
}

public sealed class Z80AssetFactAttribute : FactAttribute
{
    public Z80AssetFactAttribute()
    {
        var root = AssetRoot();
        if (root == null || !File.Exists(Path.Combine(root, "manifest.json")))
        {
            Skip = "Pinned Z80 fixtures are missing; suite acceptance remains open.";
            return;
        }
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            foreach (var row in manifest.RootElement.GetProperty("files").EnumerateArray())
                if (!File.Exists(Path.Combine(root, row.GetProperty("local").GetString()!)))
                {
                    Skip = "Pinned Z80 fixture bundle is incomplete; suite acceptance remains open.";
                    break;
                }
        }
        catch (JsonException) { /* Malformed metadata must fail validation, not skip. */ }
    }
    public static string? AssetRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Hc2000.slnx")))
                return Path.Combine(dir.FullName, "tests", "assets", "z80");
        return null;
    }
}
