using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

/// <summary>The pinned source catalog for the af-agent guest drive images (hc2000-af-agent-guest-preset c1):
/// C: tools + compilers + assemblers (cold-bootable), D: Pascal + BASIC + source, E: games, F: empty.
/// The catalog is the single source of truth: <see cref="BuildInto"/> reproduces the four named raw 8 MiB
/// volumes from it, and the tests list the built volumes back against it.</summary>
public static class GuestDriveImages
{
    public const string CatalogName = "disks/guest-catalog.json";
    public const string ReceiptName = "guest-drive-images.sha256";
    public const int VolumeSize = 8 * 1024 * 1024;

    public sealed record Source(string Kind, string? Path, string? Disk, int? User, string? Name)
    {
        public static Source Parse(JsonElement e) => new(
            e.GetProperty("kind").GetString()!,
            e.TryGetProperty("path", out var p) ? p.GetString() : null,
            e.TryGetProperty("disk", out var d) ? d.GetString() : null,
            e.TryGetProperty("user", out var u) ? u.GetInt32() : null,
            e.TryGetProperty("name", out var n) ? n.GetString() : null);
    }

    public sealed record CatalogFile(string Name, string Drive, string Folder, string Sha256, int Size,
        Source Source, bool System);

    public sealed record CatalogDrive(string Image, string[] Folders, string[] Path);

    public sealed record Catalog(int Schema, int Bytes, int DirectoryEntries, IReadOnlyList<CatalogDrive> Drives,
        IReadOnlyList<CatalogFile> Files)
    {
        public static Catalog Load(string root)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, CatalogName)));
            var r = document.RootElement;
            var drives = r.GetProperty("drives").EnumerateObject().Select(e =>
                new CatalogDrive(e.Value.GetProperty("image").GetString()!,
                    e.Value.GetProperty("folders").EnumerateArray().Select(f => f.GetString()!).ToArray(),
                    e.Value.GetProperty("path").EnumerateArray().Select(f => f.GetString()!).ToArray())).ToArray();
            var files = r.GetProperty("files").EnumerateArray().Select(f => new CatalogFile(
                f.GetProperty("name").GetString()!, f.GetProperty("drive").GetString()!,
                f.GetProperty("folder").GetString()!, f.GetProperty("sha256").GetString()!,
                f.GetProperty("size").GetInt32(), Source.Parse(f.GetProperty("source")),
                f.TryGetProperty("system", out var s) && s.GetBoolean())).ToArray();
            return new(r.GetProperty("schema").GetInt32(), r.GetProperty("media").GetProperty("bytes").GetInt32(),
                r.GetProperty("media").GetProperty("directoryEntries").GetInt32(), drives, files);
        }
    }

    /// <summary>Builds the four named raw 8 MiB guest volumes from the pinned catalog into <paramref name="output"/>
    /// (a receipt of the image hashes goes with them) and returns them keyed by drive letter. C: is the
    /// cold-bootable CP/M system volume: the pinned workstation boot files, the folder shell's FOLDERS.CFG and
    /// the catalog's tools. D: and E: carry their catalog content plus FOLDERS.CFG. F: is the empty formatted
    /// volume, left free as the prepared workstation leaves F:. Every source is verified against the catalog by
    /// its sha256 before it is placed.</summary>
    public static IReadOnlyDictionary<string, byte[]> BuildInto(string root, string output, bool overwrite = false)
    {
        var catalog = Catalog.Load(root);
        Directory.CreateDirectory(output);
        var volumes = new Dictionary<string, CpmFs>();
        foreach (var drive in catalog.Drives)
        {
            var raw = CpmFs.FormatHddVolume();
            if (raw.Length != catalog.Bytes)
                throw new InvalidDataException("The catalog does not match the released media contract.");
            volumes.Add(DriveLetter(drive.Image), CpmFs.OpenHddVolume(raw));
        }
        foreach (var drive in catalog.Drives)
        {
            var letter = DriveLetter(drive.Image);
            // The folder shell's FOLDERS.CFG, generated from the drive's declared folders and search path:
            // the same configuration format the prepared workstation drives carry (WorkstationLayout.FoldersConfig).
            // A drive the catalog leaves without files keeps no FOLDERS.CFG: F: stays the empty free volume.
            if (catalog.Files.All(f => f.Drive != letter)) continue;
            volumes[letter].Put(WorkstationLayout.FoldersConfigName,
                WorkstationLayout.FoldersConfig(new WorkstationLayout.Drive(letter, drive.Folders, drive.Path)),
                system: true);
        }
        foreach (var file in catalog.Files)
        {
            var volume = volumes[file.Drive];
            var user = UserFor(catalog, file.Drive, file.Folder);
            var bytes = Content(root, file);
            if (bytes.Length != file.Size || Hash(bytes) != file.Sha256)
                throw new InvalidDataException($"Catalog source for {file.Drive}:{file.Folder}/{file.Name} no longer matches the catalog.");
            volume.Put(file.Name, bytes, user, system: file.System);
        }
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var receipt = new List<string>();
        foreach (var drive in catalog.Drives)
        {
            var image = volumes[DriveLetter(drive.Image)].ExportVolumeBytes();
            if (image.Length != VolumeSize) throw new InvalidDataException(drive.Image + " is not the released 8 MiB media.");
            var path = Path.Combine(output, drive.Image);
            if (File.Exists(path) && !overwrite) throw new InvalidDataException(drive.Image + " already exists.");
            File.WriteAllBytes(path, image);
            receipt.Add(Hash(image).ToUpperInvariant() + "  " + drive.Image);
            images.Add(DriveLetter(drive.Image), image);
        }
        if (File.Exists(Path.Combine(output, ReceiptName)) && !overwrite) throw new InvalidDataException(ReceiptName + " already exists.");
        File.WriteAllLines(Path.Combine(output, ReceiptName), receipt.OrderBy(l => l, StringComparer.Ordinal));
        return images;
    }

    private static string DriveLetter(string image)
    {
        // HC2000-GUEST-C.DSK
        var letter = image[13].ToString();
        if (letter.Length != 1 || letter[0] is < 'C' or > 'F')
            throw new InvalidDataException("Guest image names must be HC2000-GUEST-<C..F>.DSK: " + image);
        return letter;
    }

    private static int UserFor(Catalog catalog, string drive, string folder)
    {
        var folders = catalog.Drives.Single(d => DriveLetter(d.Image) == drive).Folders;
        var user = Array.FindIndex(folders, f => f == folder);
        if (user < 0) throw new InvalidDataException($"{drive}:{folder} is not one of the drive's declared folders.");
        return user;
    }

    /// <summary>A catalog file's bytes from its declared source, the way CP/M stores them (whole 128-byte
    /// records): a repository file (the pinned system set and the 1998 host disk), or an inventory holder's
    /// file (a CP/M disk or a host-format directory), matching <see cref="DiskInventory"/> and
    /// <see cref="WorkstationLayout.Content"/> conventions.</summary>
    private static byte[] Content(string root, CatalogFile file)
    {
        byte[] bytes;
        switch (file.Source.Kind)
        {
            case "file":
                if (file.Source.Path is not { } path)
                    throw new InvalidDataException(file.Name + " names a file source without a path.");
                bytes = DiskInventory.Stored(File.ReadAllBytes(Path.Combine(root, path)));
                break;
            case "holder":
                if (file.Source.Disk is not { } disk || file.Source.User is not { } user || file.Source.Name is not { } name)
                    throw new InvalidDataException(file.Name + " names a holder source without disk/user/name.");
                var holder = Path.Combine(root, disk);
                if (Directory.Exists(holder))
                {
                    var inHost = Directory.EnumerateFiles(holder).Single(f =>
                        Path.GetFileName(f).ToUpperInvariant() == name.ToUpperInvariant());
                    bytes = DiskInventory.Stored(File.ReadAllBytes(inHost));
                }
                else
                {
                    var basic = disk.StartsWith("disks/basic/", StringComparison.Ordinal);
                    bytes = CpmFs.Open(Dsk.Load(File.ReadAllBytes(holder)), basic ? "hcfs" : "auto").Get(name, user);
                }
                break;
            default:
                throw new InvalidDataException(file.Name + " names an unknown source kind " + file.Source.Kind);
        }
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
