using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

/// <summary>The inventory of every file the repository's disks hold: each distinct content once, with its hash, size,
/// kind and every disk, user area and name that holds it. The sources are the CP/M images in disks/cpm, the HC BASIC
/// images in disks/basic and the host folders of CP/M files in disks/host/&lt;collection&gt;/&lt;disk&gt;. A host file
/// is counted as CP/M stores it, padded to whole 128-byte records with ^Z, so the same program matches across sources.
/// disks/inventory.json is generated from the images and the tests compare it, so a disk that gains or changes a file
/// fails until the inventory is regenerated (hc fs inventory &lt;repo&gt; --write).</summary>
public static class DiskInventory
{
    public const string FileName = "disks/inventory.json";

    public sealed record Holder(string Disk, int User, string Name);
    public sealed record Entry(string Sha256, int Size, string Kind, IReadOnlyList<Holder> Holders);
    public sealed record Source(string Disk, string Format, string Sha256, int Files);
    public sealed record Inventory(IReadOnlyList<Source> Sources, IReadOnlyList<Entry> Entries);

    public static Inventory Build(string root)
    {
        var sources = new List<Source>();
        var found = new List<(string Sha, int Size, string Kind, Holder Holder)>();
        foreach (var path in Files(root, "disks/cpm").Concat(Files(root, "disks/basic"))
                     .Where(p => p.EndsWith(".DSK", StringComparison.OrdinalIgnoreCase)))
        {
            var disk = Relative(root, path);
            var image = File.ReadAllBytes(path);
            var basic = disk.StartsWith("disks/basic/", StringComparison.Ordinal);
            var fs = CpmFs.Open(Dsk.Load(image), basic ? "hcfs" : "auto");
            var files = fs.List();
            sources.Add(new(disk, fs.Parameters.Name, Sha(image), files.Count));
            var hc = basic ? new HcFs(fs) : null;
            foreach (var file in files)
            {
                var bytes = fs.Get(file.Name, file.User);
                found.Add((Sha(bytes), bytes.Length, hc is null ? CpmKind(file.Name, bytes) : BasicKind(hc.Inspect(file)),
                    new Holder(disk, file.User, file.Name)));
            }
        }
        var host = Path.Combine(root, "disks/host");
        foreach (var folder in (Directory.Exists(host)
                     ? Directory.EnumerateDirectories(host).SelectMany(Directory.EnumerateDirectories)
                     : Enumerable.Empty<string>()).Order(StringComparer.Ordinal))
        {
            var disk = Relative(root, folder);
            var files = Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal).ToArray();
            sources.Add(new(disk, "host", Sha(Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => Path.GetFileName(f) + " " +
                Sha(File.ReadAllBytes(f)))))), files.Length));
            foreach (var file in files)
            {
                var bytes = Stored(File.ReadAllBytes(file));
                var name = Path.GetFileName(file).ToUpperInvariant();
                found.Add((Sha(bytes), bytes.Length, CpmKind(name, bytes), new Holder(disk, 0, name)));
            }
        }
        var entries = found.GroupBy(f => f.Sha)
            .Select(g => new Entry(g.Key, g.First().Size, g.Select(f => f.Kind).Order(StringComparer.Ordinal).First(),
                g.Select(f => f.Holder).OrderBy(h => h.Disk, StringComparer.Ordinal).ThenBy(h => h.User)
                    .ThenBy(h => h.Name, StringComparer.Ordinal).ToArray()))
            .OrderBy(e => e.Holders[0].Name, StringComparer.Ordinal).ThenBy(e => e.Sha256, StringComparer.Ordinal).ToArray();
        return new(sources, entries);
    }

    /// <summary>The checked-in inventory (disks/inventory.json), as Build wrote it.</summary>
    public static Inventory Load(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, FileName)));
        var sources = document.RootElement.GetProperty("sources").EnumerateArray().Select(s => new Source(s.GetProperty("disk").GetString()!,
            s.GetProperty("format").GetString()!, s.GetProperty("sha256").GetString()!, s.GetProperty("files").GetInt32())).ToArray();
        var entries = document.RootElement.GetProperty("entries").EnumerateArray().Select(e => new Entry(e.GetProperty("sha256").GetString()!,
            e.GetProperty("size").GetInt32(), e.GetProperty("kind").GetString()!, e.GetProperty("holders").EnumerateArray()
                .Select(h => new Holder(h.GetProperty("disk").GetString()!, h.GetProperty("user").GetInt32(), h.GetProperty("name").GetString()!)).ToArray())).ToArray();
        return new(sources, entries);
    }

    /// <summary>The inventory as diff-friendly JSON: one source and one entry per line, in a fixed order.</summary>
    public static string Render(Inventory inventory)
    {
        var text = new StringBuilder();
        text.Append("{\"schema\":1,\n\"sources\":[\n");
        text.AppendJoin(",\n", inventory.Sources.Select(s => JsonSerializer.Serialize(new { disk = s.Disk, format = s.Format, sha256 = s.Sha256, files = s.Files })));
        text.Append("\n],\n\"entries\":[\n");
        text.AppendJoin(",\n", inventory.Entries.Select(e => JsonSerializer.Serialize(new
        {
            sha256 = e.Sha256, size = e.Size, kind = e.Kind,
            holders = e.Holders.Select(h => new { disk = h.Disk, user = h.User, name = h.Name }),
        })));
        text.Append("\n]}\n");
        return text.ToString();
    }

    /// <summary>The files a checked-in inventory does not know ("new"), and the ones it lists that the disks no longer
    /// hold ("gone"), as "new|gone disk user name sha256" lines in a fixed order.</summary>
    public static IReadOnlyList<string> Differences(string checkedIn, Inventory now)
    {
        static IEnumerable<string> Holders(JsonElement entries) => entries.EnumerateArray().SelectMany(e => e.GetProperty("holders")
            .EnumerateArray().Select(h => $"{h.GetProperty("disk").GetString()} {h.GetProperty("user").GetInt32()} {h.GetProperty("name").GetString()} {e.GetProperty("sha256").GetString()}"));
        var known = Holders(JsonDocument.Parse(checkedIn).RootElement.GetProperty("entries")).ToHashSet(StringComparer.Ordinal);
        var held = Holders(JsonDocument.Parse(Render(now)).RootElement.GetProperty("entries")).ToHashSet(StringComparer.Ordinal);
        return held.Except(known).Order(StringComparer.Ordinal).Select(h => "new " + h)
            .Concat(known.Except(held).Order(StringComparer.Ordinal).Select(h => "gone " + h)).ToArray();
    }

    /// <summary>A host file's bytes as CP/M stores them: whole 128-byte records, the last one padded with ^Z.</summary>
    public static byte[] Stored(byte[] bytes)
    {
        var stored = new byte[(bytes.Length + 127) / 128 * 128];
        Array.Fill(stored, (byte)0x1a);
        bytes.CopyTo(stored, 0);
        return stored;
    }

    public static string CpmKind(string name, ReadOnlySpan<byte> bytes)
    {
        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? "" : name[(dot + 1)..];
        return extension switch
        {
            "COM" when name.Contains("OVR", StringComparison.Ordinal) => "overlay",
            "COM" => "program",
            "OVR" => "overlay",
            "REL" or "RT" or "RH" => "object",
            "ASM" or "MAC" or "Z80" or "INC" or "C" or "H" or "PAS" or "SUB" => "source",
            "BAK" or "TMP" or "$$$" => "leftover",
            "TXT" or "DOC" or "CUP" or "HLP" => "document",
            _ => Text(bytes) ? "document" : "data",
        };
    }

    private static string BasicKind(HcFile file) => file.Header?.Type switch
    {
        0 => "basic-program",
        1 => "basic-numbers",
        2 => "basic-characters",
        3 => "basic-code",
        _ => "basic-untyped",
    };

    /// <summary>Printable text up to the first ^Z, with CR, LF and TAB allowed: a document rather than binary data.
    /// Bit 7 is ignored, because WordStar documents (the CP/M, BASIC and Pascal course chapters) set it on soft spaces
    /// and word ends.</summary>
    private static bool Text(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0x1a);
        var body = end < 0 ? bytes : bytes[..end];
        if (body.Length == 0) return false;
        var printable = 0;
        foreach (var b in body) if ((b & 0x7f) is >= 0x20 and < 0x7f or 0x0d or 0x0a or 0x09 or 0x0c) printable++;
        return printable >= body.Length * 95 / 100;
    }

    private static IEnumerable<string> Files(string root, string folder) =>
        Directory.Exists(Path.Combine(root, folder))
            ? Directory.EnumerateFiles(Path.Combine(root, folder)).Order(StringComparer.Ordinal)
            : [];

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
