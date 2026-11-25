using System.Security.Cryptography;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

namespace Hc2000.Tools;

internal static class FloppyToHddCommand
{
    private sealed record Entry(string Source, CpmFile File, byte[] Bytes);

    public static void PrintUsage(TextWriter output, string prefix)
    {
        output.WriteLine($"{prefix}hc fs to-hdd preview <floppy> [<floppy> ...] [--format <format>] [--geometry <geometry>]");
        output.WriteLine($"{prefix}hc fs to-hdd create <new.hdd> <floppy> [<floppy> ...] [--format <format>] [--geometry <geometry>]");
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["to-hdd", "--help"])
        {
            PrintUsage(output, "usage: ");
            output.WriteLine("Example: hc fs to-hdd create collection.hdd tools-hc80.dsk games-hc40.dsk");
            output.WriteLine("Explicit HC40/HC80 CP/M DSK images are read cold; raw images require --geometry cylinders,sides,sectors,sector-size.");
            output.WriteLine("--format defaults to auto and applies to every source. Preview creates no output; create never overwrites a destination.");
            output.WriteLine("User areas, stored record bytes, and read-only/system attributes are preserved. Duplicate user/name identities are refused.");
            return 0;
        }
        try
        {
            var options = new StorageCommand.Options(args[1..]);
            options.Allow("--format", "--geometry");
            bool create = options.Positional.FirstOrDefault() == "create";
            if (!(create || options.Positional.FirstOrDefault() == "preview") || options.Positional.Count < (create ? 3 : 2))
            {
                PrintUsage(error, "usage: ");
                return 2;
            }
            var format = options.Get("--format", "auto");
            if (format is not ("auto" or "cpm-hc" or "cpm-hc40" or "cpm-hc80" or "hcfs" or "hcfs40" or "hcfs80"))
                throw new ArgumentException($"Unsupported format {NewDiskPublication.Quote(format)}.");
            var geometry = StorageCommand.Geometry(options);
            var destination = create ? Path.GetFullPath(options.Positional[1]) : null;
            if (destination is not null && (File.Exists(destination) || Directory.Exists(destination)))
                throw new IOException($"Destination already exists: {NewDiskPublication.Quote(destination)}.");
            var sources = options.Positional.Skip(create ? 2 : 1).ToArray();
            return Execute(sources, destination, format, geometry, output, error);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException or System.Security.SecurityException)
        {
            error.WriteLine($"hc fs to-hdd: {exception.Message}");
            return 1;
        }
    }

    private static int Execute(string[] sources, string? destination, string format, DiskGeometry? geometry, TextWriter output, TextWriter error)
    {
        var entries = new List<Entry>();
        var volume = CpmFs.OpenHddVolume(CpmFs.FormatHddVolume());
        var dpb = volume.Parameters;
        var extentBytes = 16384 * (dpb.Exm + 1);
        long blocks = 0, directoryEntries = 0;
        string? capacityError = null;
        using var admission = StorageCommand.AcquireColdRead();
        foreach (var source in sources)
        {
            CpmFs fs;
            try { fs = ReadSource(source, format, geometry, admission); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
            { throw new InvalidDataException($"Source {NewDiskPublication.Quote(source)}: {exception.Message}", exception); }

            foreach (var file in fs.List().OrderBy(file => file.User).ThenBy(file => file.Name, StringComparer.Ordinal))
            {
                try { volume.ValidateName(file.Name); }
                catch (InvalidDataException exception)
                { throw new InvalidDataException($"Source {NewDiskPublication.Quote(source)}, user {file.User}, file {NewDiskPublication.Quote(file.Name)}: {exception.Message}", exception); }
                var storedLength = checked(file.Records * 128);
                blocks += (storedLength + (long)dpb.BlockSize - 1) / dpb.BlockSize;
                directoryEntries += Math.Max(1, (storedLength + (long)extentBytes - 1) / extentBytes);
                if (capacityError is null && directoryEntries > dpb.Drm + 1)
                    capacityError = $"Directory capacity exceeded at {NewDiskPublication.Quote(source)}: {directoryEntries} entries needed, maximum {dpb.Drm + 1}.";
                if (capacityError is null && blocks > dpb.Dsm + 1 - dpb.DirectoryBlocks)
                    capacityError = $"Data block capacity exceeded at {NewDiskPublication.Quote(source)}: {blocks} blocks needed, maximum {dpb.Dsm + 1 - dpb.DirectoryBlocks}.";
                byte[] bytes;
                try { bytes = fs.Get(file.Name, file.User); }
                catch (Exception exception) when (exception is InvalidDataException or IOException)
                { throw new InvalidDataException($"Source {NewDiskPublication.Quote(source)}, user {file.User}, {NewDiskPublication.Quote(file.Name)}: {exception.Message}", exception); }
                if (bytes.Length != storedLength)
                    throw new InvalidDataException($"Source {NewDiskPublication.Quote(source)}, user {file.User}, {NewDiskPublication.Quote(file.Name)}: incomplete stored records.");
                entries.Add(new(source, file, capacityError is null ? bytes : []));
            }
        }

        if (capacityError is null)
            foreach (var entry in entries)
                output.WriteLine($"source={NewDiskPublication.Quote(entry.Source)} user={entry.File.User} file={NewDiskPublication.Quote(entry.File.Name)} dest_user={entry.File.User} dest_file={NewDiskPublication.Quote(entry.File.Name)} stored_length={entry.Bytes.Length} sha256={Convert.ToHexStringLower(SHA256.HashData(entry.Bytes))} read_only={entry.File.ReadOnly.ToString().ToLowerInvariant()} system={entry.File.System.ToString().ToLowerInvariant()}");

        var conflicts = entries.GroupBy(entry => (entry.File.User, Name: entry.File.Name.ToUpperInvariant()))
            .Where(group => group.Count() > 1).ToArray();
        foreach (var conflict in conflicts)
            error.WriteLine($"conflict user={conflict.Key.User} file={NewDiskPublication.Quote(conflict.Key.Name)} sources={string.Join(" ", conflict.Select(entry => NewDiskPublication.Quote(entry.Source)))}");
        if (conflicts.Length != 0)
        {
            error.WriteLine("Repeated user/name identities are refused; no output created.");
            return 1;
        }
        if (capacityError is not null) throw new InvalidDataException(capacityError);

        if (entries.Count == 0)
        {
            output.WriteLine("Nothing to transfer: the selected disks contain no files. No output created.");
            return 0;
        }
        if (destination is null)
        {
            output.WriteLine($"Preview: {entries.Count} file(s) from {sources.Length} source(s); no output created.");
            return 0;
        }

        foreach (var entry in entries)
            volume.Put(entry.File.Name, entry.Bytes, entry.File.User, entry.File.ReadOnly, entry.File.System);
        NewDiskPublication.Publish("hc fs to-hdd", ".hc-to-hdd-", destination, volume.ExportVolumeBytes(), error);
        output.WriteLine($"Created {NewDiskPublication.Quote(destination)} with {entries.Count} file(s) from {sources.Length} source(s).");
        return 0;
    }

    private static CpmFs ReadSource(string path, string format, DiskGeometry? geometry, NativeImageAdmission? admission)
    {
        using var stream = admission?.OpenColdSource(path) ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var facts = admission?.ValidateOpenedSource(stream, path);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Source image exceeds supported floppy size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Length != bytes.Length || stream.ReadByte() != -1) throw new InvalidDataException("Source image changed while reading.");
        if (facts is not null) admission!.ValidateSourceStillCurrent(stream, facts);
        var disk = Dsk.Load(bytes, geometry);
        var uniform = disk.UniformGeometry();
        var candidates = Dpb.Presets().Where(preset => preset.Geometry == uniform &&
            (format == "auto" || format == preset.Name || format == (preset.HcHeader ? "hcfs" : "cpm-hc")))
            .Select(preset => CpmFs.InspectStructure(disk, preset)).ToArray();
        var selection = StorageCommand.SelectFormat(candidates);
        if (selection.Report is null)
            throw new InvalidDataException(selection.Unsupported ?? selection.Ambiguous ?? "Unsupported or ambiguous source format.");
        if (selection.Report.Issues.Count != 0)
            throw new InvalidDataException($"Source disk has structural damage ({selection.Report.Issues.Count} issue(s)).");
        if (selection.Report.Parameters.HcHeader)
            throw new InvalidDataException("HCFS native disks are not supported; source must be a CP/M disk.");
        var fs = new CpmFs(disk, selection.Report.Parameters);
        var unrecognized = fs.UnrecognizedAttributeFiles();
        if (unrecognized.Count != 0)
            throw new InvalidDataException($"Unrecognized file attributes: {string.Join(", ", unrecognized)}.");
        return fs;
    }
}
