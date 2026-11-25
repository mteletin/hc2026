using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;
using Microsoft.Win32.SafeHandles;

namespace Hc2000.Tools;

internal static class FromFat32Command
{
    public static void PrintUsage(TextWriter output, string prefix)
    {
        output.WriteLine($"{prefix}hc fs from-fat32 preview <image>");
        output.WriteLine($"{prefix}hc fs from-fat32 import <image> <destination> --user <0..15> --select <path> [--select <path> ...]");
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["from-fat32", "--help"])
        {
            PrintUsage(output, "usage: ");
            output.WriteLine("Input: an exact 64 MiB standalone FAT32 volume image. Output: a new flat 8 MiB CP/M 2.2 HDD image.");
            output.WriteLine("Use exact case-sensitive preview paths (a leading /, long components where present) for --select.");
            output.WriteLine("Preview names and paths are JSON-quoted; pass the decoded path as one shell argument.");
            output.WriteLine("Short names are the on-disk aliases (OEM bytes displayed as CP437); only ASCII aliases map to CP/M.");
            output.WriteLine("FAT attributes are shown as a hex byte (01 read-only, 02 hidden, 04 system, 20 archive), not copied.");
            output.WriteLine("The source is read-only; the destination must not exist. CP/M pads final records with 0x1A.");
            if (!OperatingSystem.IsWindows()) output.WriteLine("The destination filesystem must support hard links for atomic publication.");
            return 0;
        }

        try
        {
            if (args is ["from-fat32", "preview", var source])
            {
                using var image = OpenSource(source);
                var volume = new Fat32Volume(image);
                var count = 0;
                foreach (var file in volume.Files())
                {
                    output.WriteLine(FormattableString.Invariant(
                        $"path={Fat32Volume.Quote(file.Path)} short={Fat32Volume.Quote(file.ShortName)} long={Fat32Volume.Quote(file.LongName)} size={file.Size} attributes=0x{file.Attributes:X2} sha256={volume.Hash(file)} cpm={(file.CpmName is { } name ? Fat32Volume.Quote(name) : "unmappable")}"));
                    count++;
                }
                output.WriteLine(FormattableString.Invariant($"Files: {count}"));
                return 0;
            }

            if (args.Length >= 4 && args[1] == "import")
                return Import(args, output, error);
            PrintUsage(error, "usage: ");
            return 2;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"hc fs from-fat32: {exception.Message}");
            return 1;
        }
    }

    private static int Import(string[] args, TextWriter output, TextWriter error)
    {
        int? user = null;
        var requested = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 4; index < args.Length; index += 2)
        {
            var option = args[index];
            if (index == args.Length - 1) throw new ArgumentException($"Missing value for {Fat32Volume.Quote(option)}.");
            var value = args[index + 1];
            switch (option)
            {
                case "--user":
                    if (user is not null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 15)
                        throw new ArgumentException("Specify one --user in the range 0..15.");
                    user = parsed;
                    break;
                case "--select":
                    if (!value.StartsWith('/') || value.Length == 1)
                        throw new ArgumentException("--select requires an exact file path from preview, starting with /.");
                    if (!requested.Add(value)) throw new ArgumentException($"Duplicate selection {Fat32Volume.Quote(value)}.");
                    break;
                default: throw new ArgumentException($"Unknown option {Fat32Volume.Quote(option)}.");
            }
        }
        if (requested.Count == 0)
        {
            output.WriteLine("Nothing to import: no files selected. No output created.");
            return 0;
        }
        if (user is null) throw new ArgumentException("Specify one --user in the range 0..15.");

        var destination = Path.GetFullPath(args[3]);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"Destination already exists: {Fat32Volume.Quote(destination)}.");

        using var image = OpenSource(args[2]);
        var volume = new Fat32Volume(image);
        var selected = new Dictionary<string, Fat32Volume.FileEntry>(StringComparer.Ordinal);
        var dpb = Dpb.HddVolume();
        var allocationBlocks = 0;
        var directoryEntries = 0;
        var extentBytes = 16384 * (dpb.Exm + 1);
        foreach (var file in volume.Files())
        {
            if (!requested.Remove(file.Path)) continue;
            var name = file.CpmName ?? throw new InvalidDataException(
                $"Unmappable short name {Fat32Volume.Quote(file.ShortName)} for {Fat32Volume.Quote(file.Path)}: not a CP/M 8.3 name.");
            if (!selected.TryAdd(name, file))
                throw new InvalidDataException($"CP/M name collision for {name}: {Fat32Volume.Quote(selected[name].Path)} and {Fat32Volume.Quote(file.Path)}.");
            allocationBlocks += (file.Size + dpb.BlockSize - 1) / dpb.BlockSize;
            directoryEntries += Math.Max(1, (file.Size + extentBytes - 1) / extentBytes);
            if (allocationBlocks > dpb.Dsm + 1 - dpb.DirectoryBlocks || directoryEntries > dpb.Drm + 1)
                throw new InvalidDataException($"Selection does not fit on an 8 MiB CP/M disk (at {Fat32Volume.Quote(file.Path)}).");
        }
        if (requested.Count != 0)
            throw new InvalidDataException($"No regular file at selected path {Fat32Volume.Quote(requested.Min(StringComparer.Ordinal))}. Use exact preview paths.");

        var disk = CpmFs.OpenHddVolume(CpmFs.FormatHddVolume());
        foreach (var (name, file) in selected)
            disk.Put(name, volume.Read(file), user.Value);
        PublishNew(destination, disk.ExportVolumeBytes(), error);
        output.WriteLine(FormattableString.Invariant($"Imported {selected.Count} file(s) into user {user} of {Fat32Volume.Quote(destination)}."));
        return 0;
    }

    private static FileStream OpenSource(string path)
    {
        path = Path.GetFullPath(path);
        if (!OperatingSystem.IsLinux()) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // O_PATH pins the inode without opening a device or FIFO for I/O.
        const int atCurrentDirectory = -100, pathOnly = 0x200000, closeExec = 0x80000;
        var descriptor = OpenAt(atCurrentDirectory, path, pathOnly | closeExec, 0);
        if (descriptor < 0)
            throw new IOException($"Cannot open source {Fat32Volume.Quote(path)}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        var facts = LinuxImageAdmissionNative.Probe(handle);
        if ((facts.Mode & 0xf000) != 0x8000)
            throw new InvalidDataException("FAT32 source must be a regular image file.");
        // Use the pinned inode, not another lookup of the caller's path.
        return new FileStream(FormattableString.Invariant($"/proc/self/fd/{descriptor}"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    internal static void PublishNew(string destination, byte[] bytes, TextWriter error)
        => NewDiskPublication.Publish("hc fs from-fat32", ".hc-fat32-", destination, bytes, error);
}
