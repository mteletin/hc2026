using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

/// <summary>The full workstation's prepared drives, filled from disks/layout.json: every CP/M program, document and
/// source of the disk inventory (disks/inventory.json) in a named folder, one copy each, with a second copy only where a
/// program needs a companion beside it (the Turbo overlays, L80). Each drive gets its FOLDERS.CFG (the folder shell's
/// names and search path). A file
/// already on the drive with the same content (C:/bin's tools) is checked, not copied again. src-library goes where the
/// layout's "library" list sends it (D:/lib for the house's own code, E:/dis for the disassemblies), under 8.3 names
/// mapped in NAMES.TXT at the top of each library folder. The result depends only on
/// the layout, the inventory's sources and src-library, in a fixed order, so it is byte-reproducible.</summary>
public static class WorkstationLayout
{
    public const string FileName = "disks/layout.json";
    public const string FoldersConfigName = "FOLDERS.CFG";
    public const string LibraryMapName = "NAMES.TXT";

    public sealed record Drive(string Letter, IReadOnlyList<string> Folders, IReadOnlyList<string> Path);
    public sealed record Placement(string Sha256, string Name, IReadOnlyList<string> At);
    public sealed record LibraryRoute(string From, string At);
    /// <summary>A house-built file that is not from a held disk (the remade FM programs), with its repository path.</summary>
    public sealed record Extra(string From, string Name, string At);
    public sealed record Layout(IReadOnlyList<Drive> Drives, IReadOnlyList<LibraryRoute> Library, IReadOnlyList<Extra> Extras,
        IReadOnlyList<Placement> Placements);

    public static Layout Load(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, FileName)));
        var drives = document.RootElement.GetProperty("drives").EnumerateArray().Select(d => new Drive(
            d.GetProperty("drive").GetString()!,
            d.GetProperty("folders").EnumerateArray().Select(f => f.GetString()!).ToArray(),
            d.GetProperty("path").EnumerateArray().Select(f => f.GetString()!).ToArray())).ToArray();
        var library = document.RootElement.GetProperty("library").EnumerateArray()
            .Select(r => new LibraryRoute(r.GetProperty("from").GetString()!, r.GetProperty("at").GetString()!)).ToArray();
        var extras = document.RootElement.TryGetProperty("extras", out var listed)
            ? listed.EnumerateArray().Select(e => new Extra(e.GetProperty("from").GetString()!, e.GetProperty("name").GetString()!,
                e.GetProperty("at").GetString()!)).ToArray()
            : [];
        var placements = document.RootElement.GetProperty("placements").EnumerateArray().Select(p => new Placement(
            p.GetProperty("sha256").GetString()!, p.GetProperty("name").GetString()!,
            p.GetProperty("at").EnumerateArray().Select(a => a.GetString()!).ToArray())).ToArray();
        foreach (var drive in drives)
        {
            if (drive.Folders.Count != 16 || drive.Folders[0] != "/")
                throw new InvalidDataException($"{drive.Letter}: needs 16 folders, / first.");
            if (drive.Path.Count > 8 || drive.Path.Any(p => !drive.Folders.Contains(p)))
                throw new InvalidDataException($"{drive.Letter}: the path names at most 8 of its folders.");
        }
        return new(drives, library, extras, placements);
    }

    /// <summary>A drive's FOLDERS.CFG: record 0 "FOLDCFG", version 1 and the path (user numbers, 0xFF after the last);
    /// records 1 and 2 the 16 names, 16 bytes each, zero-terminated (folders.layout.inc). An unused area keeps the
    /// shell's default name, /uN.</summary>
    public static byte[] FoldersConfig(Drive drive)
    {
        var file = new byte[3 * 128];
        Encoding.ASCII.GetBytes("FOLDCFG").CopyTo(file, 0);
        file[7] = 1;
        for (var i = 0; i < 8; i++)
            file[8 + i] = i < drive.Path.Count ? (byte)IndexOf(drive, drive.Path[i]) : (byte)0xff;
        for (var user = 0; user < 16; user++)
        {
            var name = drive.Folders[user] is { Length: > 0 } folder ? folder : "/u" + user;
            if (name.Length > 15 || name.Any(c => c is < '!' or > '~')) throw new InvalidDataException("Bad folder name: " + name);
            Encoding.ASCII.GetBytes(name).CopyTo(file, 128 + user * 16);
        }
        return file;
    }

    /// <summary>Puts the layout on the given volumes ("C" to "F"): each drive's FOLDERS.CFG, every placement read from
    /// the first of its holders (disks/inventory.json) that the repository has, and src-library on its routes. A placement
    /// none of whose source disks is present is left out and counted: the public export carries only the distributed
    /// disks, so its drives hold exactly what it may share. A present source whose content changed fails.</summary>
    public static int Apply(string root, IReadOnlyDictionary<string, CpmFs> volumes, bool includePrivateLibrary = true)
    {
        var layout = Load(root);
        var inventory = DiskInventory.Load(root).Entries.ToDictionary(e => e.Sha256, StringComparer.Ordinal);
        var absent = 0;
        var drives = layout.Drives.ToDictionary(d => d.Letter, StringComparer.Ordinal);
        foreach (var drive in layout.Drives) volumes[drive.Letter].Put(FoldersConfigName, FoldersConfig(drive), system: true);
        foreach (var placement in layout.Placements)
        {
            if (!inventory.TryGetValue(placement.Sha256, out var entry))
                throw new InvalidDataException($"{FileName}: {placement.Name} {placement.Sha256} is not in the inventory.");
            if (!entry.Holders.Any(h => Path.Exists(Path.Combine(root, h.Disk)))) { absent++; continue; }
            var bytes = Content(root, entry);
            foreach (var at in placement.At)
            {
                var (letter, user) = Locate(drives, at);
                Put(volumes[letter], placement.Name, bytes, user, at);
            }
        }
        foreach (var extra in layout.Extras)
        {
            var path = Path.Combine(root, extra.From);
            if (!File.Exists(path)) { absent++; continue; }
            var (letter, user) = Locate(drives, extra.At);
            Put(volumes[letter], extra.Name, File.ReadAllBytes(path), user, extra.At);
        }
        if (includePrivateLibrary) PutLibrary(root, layout, drives, volumes);
        return absent;
    }

    /// <summary>The bytes of an inventory entry as CP/M stores them, read from its first holder the repository has.</summary>
    public static byte[] Content(string root, DiskInventory.Entry entry)
    {
        var holder = entry.Holders.First(h => Path.Exists(Path.Combine(root, h.Disk)));
        var path = Path.Combine(root, holder.Disk);
        byte[] bytes;
        if (Directory.Exists(path))
        {
            var file = Directory.EnumerateFiles(path).Single(f => Path.GetFileName(f).ToUpperInvariant() == holder.Name);
            bytes = DiskInventory.Stored(File.ReadAllBytes(file));
        }
        else
        {
            var basic = holder.Disk.StartsWith("disks/basic/", StringComparison.Ordinal);
            bytes = CpmFs.Open(Dsk.Load(File.ReadAllBytes(path)), basic ? "hcfs" : "auto").Get(holder.Name, holder.User);
        }
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != entry.Sha256)
            throw new InvalidDataException($"{holder.Disk} {holder.Name} no longer matches the inventory.");
        return bytes;
    }

    /// <summary>src-library's files with the folder and 8.3 name each gets: the longest matching "library" route picks
    /// the folder; per folder, the file name's letters and digits cut to 8 and 3, upper case, and a ~N ending where two
    /// would meet, in ordinal path order.</summary>
    public static IReadOnlyList<(string At, string Name, string Source)> Library(string root, Layout layout)
    {
        var result = new List<(string, string, string)>();
        var taken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src-library"), "*", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal))
        {
            var route = layout.Library.Where(r => file.StartsWith(r.From, StringComparison.Ordinal)).MaxBy(r => r.From.Length)
                ?? throw new InvalidDataException($"{FileName}: no library route for {file}.");
            var leaf = file[(file.LastIndexOf('/') + 1)..];
            var dot = leaf.LastIndexOf('.');
            static string Clean(string s) => new(s.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());
            var stem = Clean(dot > 0 ? leaf[..dot] : leaf);
            var extension = dot > 0 ? Clean(leaf[(dot + 1)..]) : "";
            if (stem.Length == 0) stem = "FILE";
            extension = extension.Length > 3 ? extension[..3] : extension;
            var suffix = extension.Length > 0 ? "." + extension : "";
            if (!taken.TryGetValue(route.At, out var names))
                taken[route.At] = names = new(StringComparer.Ordinal) { FoldersConfigName, LibraryMapName };
            var name = Short(stem, 8) + suffix;
            for (var n = 1; names.Contains(name); n++) name = Short(stem, 8 - 1 - n.ToString().Length) + "~" + n + suffix;
            names.Add(name);
            result.Add((route.At, name, file));
        }
        return result;
    }

    private static void PutLibrary(string root, Layout layout, IReadOnlyDictionary<string, Drive> drives, IReadOnlyDictionary<string, CpmFs> volumes)
    {
        var maps = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);
        foreach (var (at, name, source) in Library(root, layout))
        {
            var (letter, user) = Locate(drives, at);
            Put(volumes[letter], name, File.ReadAllBytes(Path.Combine(root, source)), user, at);
            var top = at[..2] + "/" + at[3..].Split('/')[0];
            if (!maps.TryGetValue(top, out var map))
                maps[top] = map = new StringBuilder("src-library in 8.3 names: folder, name, then the file in the repository.\r\n\r\n");
            map.Append($"{at[2..],-14} {name,-12} {source}\r\n");
        }
        foreach (var (top, map) in maps)
        {
            var (letter, user) = Locate(drives, top);
            Put(volumes[letter], LibraryMapName, Encoding.ASCII.GetBytes(map.ToString()), user, top);
        }
    }

    private static void Put(CpmFs volume, string name, byte[] bytes, int user, string at)
    {
        if (volume.List().Any(f => f.User == user && f.Name == name))
        {
            if (!volume.Get(name, user).AsSpan().SequenceEqual(DiskInventory.Stored(bytes)))
                throw new InvalidDataException($"{at}/{name} is already there with other content.");
            return;
        }
        volume.Put(name, bytes, user);
    }

    private static (string Letter, int User) Locate(IReadOnlyDictionary<string, Drive> drives, string at)
    {
        var colon = at.IndexOf(':');
        if (colon != 1 || !drives.TryGetValue(at[..1], out var drive))
            throw new InvalidDataException($"{FileName}: {at} names no configured drive.");
        return (drive.Letter, IndexOf(drive, at[2..]));
    }

    private static int IndexOf(Drive drive, string folder) =>
        drive.Folders.ToList().IndexOf(folder) is var i and >= 0 ? i : throw new InvalidDataException($"{drive.Letter}: no folder {folder}.");

    private static string Short(string s, int length) => s.Length > length ? s[..Math.Max(1, length)] : s;
}
