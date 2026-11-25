using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Hc2000.Machine.Storage;
using Hc2000.HostFiles;

namespace Hc2000.Tools;

/// <summary>Host I/O adapter. Libraries take bytes only; updates use same-directory atomic replacement.</summary>
public static class StorageCommand
{
    public static int Run(string command, string[] args, TextWriter output, TextWriter error)
    {
        if (command.Equals("fs", StringComparison.OrdinalIgnoreCase) && args.FirstOrDefault() == "from-fat32")
            return FromFat32Command.Run(args, output, error);

        if (command.Equals("fs", StringComparison.OrdinalIgnoreCase) && args.FirstOrDefault() == "to-hdd")
            return FloppyToHddCommand.Run(args, output, error);

        if (command.Equals("fs", StringComparison.OrdinalIgnoreCase) &&
            (args is ["--help"] or ["check", "--help"] or ["survey", "--help"] or ["locate", "--help"] or ["find", "--help"] or ["duplicates", "--help"] or ["dups", "--help"] or ["variants", "--help"] or ["vars", "--help"] or ["name-variants", "--help"] or ["compare", "--help"] or ["export", "--help"] or ["from-folder", "--help"] or ["repack", "--help"] or ["copy", "--help"] or ["restore", "--help"] or ["inventory", "--help"] || args.FirstOrDefault() is "check" or "survey" or "locate" or "find" or "duplicates" or "dups" or "variants" or "vars" or "name-variants" or "compare" or "export" or "from-folder" or "repack" or "copy" or "restore" or "inventory"))
        {
            if (args.FirstOrDefault() is "variants" or "vars" or "name-variants") return CpmNameVariantsCommand.Run(args, output, error);
            if (args.FirstOrDefault() is "locate" or "find") return CpmFileLocatorCommand.Run(args, output, error);
            if (args.FirstOrDefault() is "duplicates" or "dups") return CpmDuplicatesCommand.Run(args, output, error);
            if (args.FirstOrDefault() == "survey") return SurveyCommand(args, output, error);
            if (args.FirstOrDefault() == "compare") return CompareCommand(args, output, error);
            if (args.FirstOrDefault() == "export") return ExportCommand(args, output, error);
            if (args.FirstOrDefault() == "from-folder") return FromFolderCommand(args, output, error);
            if (args.FirstOrDefault() is "repack" or "copy") return RepackCommand(args, output, error);
            if (args.FirstOrDefault() == "restore") return RestoreCommand(args, output, error);
            if (args.FirstOrDefault() == "inventory") return InventoryCommand(args, output, error);
            return CheckFilesystem(args, output, error);
        }
        try
        {
            var options = new Options(args);
            if (options.Positional.Count < 2) throw new ArgumentException($"usage: hc {command} <operation> <image> [options]");
            switch (command.ToLowerInvariant())
            {
                case "hdd": return HardDisk(options, output, error);
                case "dsk": Disk(options, output); break;
                case "fs": Filesystem(options, output, error); break;
                case "tap": Tape(options, output, error); break;
                case "rom": return Rom(options, output);
                default: throw new ArgumentException("Unknown storage command.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            error.WriteLine($"hc {command}: {ex.Message}"); return 1;
        }
    }
    /// <summary>hc fs inventory &lt;repo&gt; [--write]: every file on every held disk (DiskInventory). Without --write it
    /// compares the generated inventory with disks/inventory.json and fails on any difference; --write regenerates it.</summary>
    private static int InventoryCommand(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["inventory", "--help"] || args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--write")
        {
            (args is ["inventory", "--help"] ? output : error).WriteLine("usage: hc fs inventory <repo> [--write]");
            return args is ["inventory", "--help"] ? 0 : 2;
        }
        var root = args[1];
        var inventory = DiskInventory.Build(root);
        var text = DiskInventory.Render(inventory);
        var path = Path.Combine(root, DiskInventory.FileName);
        output.WriteLine($"sources {inventory.Sources.Count} files {inventory.Sources.Sum(s => s.Files)} distinct {inventory.Entries.Count}");
        if (args.Length == 3)
        {
            File.WriteAllText(path, text);
            output.WriteLine($"wrote {DiskInventory.FileName}");
            return 0;
        }
        if (File.Exists(path) && File.ReadAllText(path) == text) { output.WriteLine("inventory current"); return 0; }
        error.WriteLine($"hc fs inventory: {DiskInventory.FileName} differs from the disks; regenerate it with --write");
        if (File.Exists(path)) foreach (var line in DiskInventory.Differences(File.ReadAllText(path), inventory)) error.WriteLine(line);
        return 1;
    }

    // Spec 11: flat bytes only, no header, partition table or CP/M formatting.
    private static int HardDisk(Options o, TextWriter output, TextWriter error)
    {
        const int imageBytes = 8_388_608;
        if (o.Positional[0] == "replace-volume")
        {
            if (o.Positional.Count != 4)
                throw new ArgumentException("usage: hc hdd replace-volume <container> <volume-name> <raw-image>");
            o.Allow();
            return HddOfflineReplaceCommand.Run(o.Positional[1], o.Positional[2], o.Positional[3], output, error);
        }
        o.Count(2); o.Allow();
        var path = o.Positional[1];
        switch (o.Positional[0])
        {
            case "create":
                if (File.Exists(path) || Directory.Exists(path))
                    throw new IOException("Refusing to replace an existing path with hdd create.");
                // The final no-overwrite move also refuses a destination created during the write.
                AtomicWrite(path, new byte[imageBytes], false);
                break;
            case "identify":
                using (var image = File.OpenRead(path))
                {
                    if (image.Length != imageBytes)
                        throw new InvalidDataException($"Raw HDD image must be exactly {imageBytes} bytes; found {image.Length}.");
                    var hash = Convert.ToHexStringLower(SHA256.HashData(image));
                    if (image.Length != imageBytes || image.Position != imageBytes)
                        throw new InvalidDataException("Raw HDD image length changed during identification.");
                    output.WriteLine($"raw-hdd bytes=8388608 sectors=16384 sector-size=512 offset=0 sha256={hash}");
                }
                break;
            case "inspect":
                InspectHardDiskContainer(path, output);
                break;
            case "factory":
                FactoryHardDisks(path, output);
                break;
            default: throw new ArgumentException("hdd supports create/identify/inspect/factory/replace-volume; usage: hc hdd replace-volume <container> <volume-name> <raw-image> (local, unbound, caller-owned disposable container only).");
        }
        return 0;
    }
    private static void FactoryHardDisks(string directory, TextWriter output)
    {
        var paths = HddFactoryImages.GenerateInto(directory);
        foreach (var path in paths) InspectHardDiskContainer(path, output);
        output.Write(File.ReadAllText(Path.Combine(directory, HddFactoryImages.ReceiptName)));
    }
    // Spec 11 sibling: read-only inspection of a multivolume HDD container (footer + index only, per the accepted
    // shared catalog metadata). Reads exactly the 64-byte trailing footer and then the validated index; never arena
    // or payload bytes, never the whole image. The input is opened read-only and never mutated; no mount, selection,
    // replacement or repair. Identity is container ID/revision plus per-row name/slot ordinal (no per-volume GUID);
    // integrity is metadata-only (storedImageCrc is stored, not verified). No CAS/snapshot promise: a file changed
    // mid-read may refuse. The complete bounded report is validated, decoded and rendered before any line is emitted.
    private static void InspectHardDiskContainer(string path, TextWriter output)
    {
        // Fixed caller admission (no CLI knobs): 511 is the largest entry count whose arena+index+64-byte footer stays
        // within uint file lengths; the index and metadata maxima derive from that cap, not from separate options.
        var limits = new HddLimits(uint.MaxValue, 511, 12_276, 114_640);
        using var image = File.OpenRead(path);
        var length = image.Length;
        if (length < 76)
            throw new InvalidDataException($"HDD container image must be at least 76 bytes; found {length}.");
        var footer = new byte[64];
        image.Position = length - 64;
        image.ReadExactly(footer);
        var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, limits);
        if (!inspected.Success)
            throw new InvalidDataException(inspected.Failure!.Detail);
        var view = inspected.Value;
        var index = new byte[view.IndexBytes];
        image.Position = (long)view.IndexStart;
        image.ReadExactly(index);
        var decoded = HddCatalogCodec.DecodeHdd(footer, index, (ulong)length, limits);
        if (!decoded.Success)
            throw new InvalidDataException(decoded.Failure!.Detail);
        if (image.Length != length)
            throw new InvalidDataException("HDD container length changed during inspection.");
        var invariant = CultureInfo.InvariantCulture;
        var entries = decoded.Value.Entries;
        var lines = new string[entries.Length + 1];
        lines[0] = "hdd-container version=1 id=" + Convert.ToHexStringLower(view.ContainerId)
            + " revision=" + view.Revision.ToString(invariant)
            + " entries=" + view.Entries.ToString(invariant)
            + " arenaBytes=" + view.ArenaBytes.ToString(invariant)
            + " indexBytes=" + view.IndexBytes.ToString(invariant)
            + " indexCrc=" + view.IndexCrc.ToString("x8", invariant)
            + " indexStart=" + view.IndexStart.ToString(invariant)
            + " indexEnd=" + view.IndexEnd.ToString(invariant)
            + " fileBytes=" + length.ToString(invariant)
            + " integrity=metadata-only";
        for (var row = 0; row < entries.Length; row++)
        {
            var entry = entries[row];
            var ordinal = entry.SlotOrdinal.Value;
            lines[row + 1] = "hdd-volume row=" + row.ToString(invariant)
                + " ordinal=" + ordinal.ToString(invariant)
                + " name=" + JsonSerializer.Serialize(entry.Name)
                + " protection=" + (entry.ReadOnly ? "ro" : "rw")
                + " offset=" + (ordinal * VolumeCatalogMetadata.SlotBytes).ToString(invariant)
                + " bytes=" + entry.ImageBytes.ToString(invariant)
                + " storedImageCrc=" + entry.ImageCrc.ToString("x8", invariant);
        }
        foreach (var line in lines) output.WriteLine(line);
    }
    internal static DiskGeometry? Geometry(Options o)
    {
        if (!o.Values.TryGetValue("--geometry", out var value)) return null;
        var fields = value.Split(',');
        if (fields.Length != 4) throw new ArgumentException("--geometry requires cylinders,sides,sectors,sector-size.");
        var n = fields.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var geometry = new DiskGeometry(n[0], n[1], n[2], n[3]); geometry.Validate(); return geometry;
    }
    private static Dsk Load(string path, Options o) => Dsk.Load(File.ReadAllBytes(path), Geometry(o));
    public sealed record CpmFormatSelection(CpmStructuralReport? Report, string? Unsupported, string? Ambiguous);
    public static CpmFormatSelection SelectFormat(IReadOnlyList<CpmStructuralReport> candidates)
    {
        if (candidates.Count == 0) return new(null, "No supported format matches the image geometry and selection.", null);
        var healthy = candidates.Where(candidate => candidate.Issues.Count == 0).ToArray();
        if (healthy.Length > 1 || (healthy.Length == 0 && candidates.Count > 1))
            return new(null, null, "Ambiguous filesystem formats: " + string.Join(", ", candidates.Select(candidate => candidate.Parameters.Name)) + "; select --format.");
        return new(healthy.Length == 1 ? healthy[0] : candidates[0], null, null);
    }
        private static int CheckFilesystem(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["check", "--help"])
        {
            output.WriteLine("usage: hc fs check <image> [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            output.WriteLine("       hc fs from-folder <folder> <destination> [--preset cpm-hc80|cpm-hc40] [--user <0..15>]");
            FromFat32Command.PrintUsage(output, "       ");
            FloppyToHddCommand.PrintUsage(output, "       ");
            output.WriteLine("       hc fs repack <source> <destination> --target cpm-hc80|cpm-hc40 [--format auto|cpm-hc80|cpm-hc40] [--geometry <geometry>]");
            output.WriteLine("       hc fs restore <export-folder> <destination> [--preset cpm-hc80|cpm-hc40]");
            output.WriteLine("       hc fs locate <folder> <pattern> [--recursive] [--user <0..31>] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            output.WriteLine("Checks supported standard/extended DSK images locally; raw images require --geometry. Auto selects a valid supported preset; ambiguous images require --format.");
            output.WriteLine("Reports directory, extent and allocation structure, not file contents. No mount, write or automatic repair is performed.");
            output.WriteLine("Exit status: 0 healthy, 1 structural issues found, 2 could not check (including ambiguity, unreadable or unsupported input and invalid arguments).");
            return 0;
        }
        try
        {
            var options = new Options(args); options.Count(2); options.Allow("--format", "--geometry");
            var path = options.Positional[1]; var format = options.Get("--format", "auto");
            NativeImageAdmission? admission = AcquireColdRead();
            try
            {
                var result = CheckImageOnce(path, format, Geometry(options), admission);
                if (result.Outcome == CheckOutcome.Uncheckable)
                {
                    error.WriteLine("status uncheckable: " + result.Reason); return 2;
                }
                var fs = result.Report!; var issues = fs.Issues;
                output.WriteLine("format " + fs.Parameters.Name);
                output.WriteLine("status " + (issues.Count == 0 ? "healthy" : "issues"));
                output.WriteLine("scope structural; file contents are not verified; no repair performed");
                foreach (var warning in fs.Warnings) output.WriteLine("warning " + warning);
                foreach (var issue in issues)
                    output.WriteLine($"issue {issue.Code} entry={issue.Entry} user={issue.User} file={JsonSerializer.Serialize(issue.File)} extent={issue.Extent}" +
                        (issue.Block is int block ? $" block={block}" : "") + ": " + issue.Detail);
                return issues.Count == 0 ? 0 : 1;
            }
            finally { admission?.Dispose(); }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            error.WriteLine("status uncheckable: " + ex.Message); return 2;
        }
    }
    internal static NativeImageAdmission? AcquireColdRead()
    {
        if (!NativeImageAdmission.IsGuarded) return null;
        var acquired = NativeImageAdmission.TryAcquireColdRead(default);
        return acquired.RequireAdmission();
    }
    internal enum CheckOutcome { Healthy, Issues, Uncheckable }
    internal sealed record ImageCheckResult(CheckOutcome Outcome, CpmStructuralReport? Report, string? Reason);
    /// <summary>One guarded read-only check of a single image: the shared core of hc fs check and
    /// hc fs survey, so both commands report the same findings for the same file.</summary>
    internal static ImageCheckResult CheckImageOnce(string path, string format, DiskGeometry? geometry, NativeImageAdmission? admission)
    {
        try
        {
            using var source = admission?.OpenColdSource(path) ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var facts = admission?.ValidateOpenedSource(source, path);
            if (source.Length > 16 * 1024 * 1024) throw new InvalidDataException("Image exceeds supported floppy size.");
            var bytes = new byte[checked((int)source.Length)]; source.ReadExactly(bytes);
            if (source.Length != bytes.Length || source.ReadByte() != -1) throw new InvalidDataException("Image changed while reading.");
            if (facts is not null) admission!.ValidateSourceStillCurrent(source, facts);
            var disk = Dsk.Load(bytes, geometry); var uniform = disk.UniformGeometry();
            var candidates = Dpb.Presets().Where(p => p.Geometry == uniform &&
                (format == "auto" || format == p.Name || format == (p.HcHeader ? "hcfs" : "cpm-hc")))
                .Select(p => CpmFs.InspectStructure(disk, p)).ToArray();
            var selection = SelectFormat(candidates);
            if (selection.Report is null) return new(CheckOutcome.Uncheckable, null, selection.Unsupported ?? selection.Ambiguous);
            return new(selection.Report.Issues.Count == 0 ? CheckOutcome.Healthy : CheckOutcome.Issues, selection.Report, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            return new(CheckOutcome.Uncheckable, null, ex.Message);
        }
    }
    private static int SurveyCommand(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["survey", "--help"])
        {
            output.WriteLine("usage: hc fs survey <folder> [--recursive] [--report <path>] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            output.WriteLine("Checks every supported CP/M disk image in a folder: files ending in .dsk (standard or extended DSK) or .raw (raw sector image, requires --geometry). Other files are skipped.");
            output.WriteLine("Prints one line per image (path, recognised format, healthy / problem count / uncheckable with its reason) and a summary; --report writes the full per-image detail (the findings hc fs check prints) to the chosen path. --recursive includes subfolders.");
            output.WriteLine("Read-only: no mount, write or automatic repair is performed; no file is created except the requested report.");
            output.WriteLine("Exit status: 0 all checked images healthy or no supported images found, 1 at least one problem or uncheckable image, 2 could not run (invalid arguments, missing or unreadable folder, refused cold read or report not writable).");
            return 0;
        }
        NativeImageAdmission? admission = null;
        try
        {
            try { admission = AcquireColdRead(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine("could not run: cold read refused (" + ex.Message + ")."); return 2;
            }
            try
            {
                var options = new Options(args); options.Count(2); options.Allow("--recursive", "--report", "--format", "--geometry");
                var folder = options.Positional[1];
                if (!Directory.Exists(folder)) throw new InvalidDataException("Folder not found: " + folder);
                var files = (options.Values.ContainsKey("--recursive")
                    ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    : Directory.EnumerateFiles(folder, "*"))
                    .Where(f => File.Exists(f) && (f.EndsWith(".dsk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".raw", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
                var format = options.Get("--format", "auto"); var geometry = Geometry(options);
                var results = files.Select(f => (Path: f, Check: CheckImageOnce(f, format, geometry, admission))).ToArray();
                // The report write needs the publication lease, which the cold-read lease excludes: release it once every read is done.
                admission?.Dispose(); admission = null;
                var healthy = 0; var problems = 0; var uncheckable = 0;
                foreach (var (path, check) in results)
                {
                    if (check.Outcome == CheckOutcome.Healthy)
                    {
                        healthy++; output.WriteLine($"healthy {path} format={check.Report!.Parameters.Name}");
                    }
                    else if (check.Outcome == CheckOutcome.Issues)
                    {
                        problems++; output.WriteLine($"problems {path} format={check.Report!.Parameters.Name} count={check.Report.Issues.Count}");
                    }
                    else
                    {
                        uncheckable++; output.WriteLine($"uncheckable {path} reason={check.Reason}");
                    }
                }
                if (results.Length == 0) output.WriteLine("no supported CP/M images found");
                output.WriteLine($"total={results.Length} healthy={healthy} problems={problems} uncheckable={uncheckable}");
                if (options.Values.TryGetValue("--report", out var reportPath))
                {
                    try { AtomicWrite(reportPath, Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, ReportLines(folder, results)) + Environment.NewLine), false); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
                    {
                        error.WriteLine("could not write report: " + ex.Message); return 2;
                    }
                }
                return problems == 0 && uncheckable == 0 ? 0 : 1;
            }
            finally { admission?.Dispose(); }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            error.WriteLine("could not run: " + ex.Message); return 2;
        }
    }
    private static IEnumerable<string> ReportLines(string folder, (string Path, ImageCheckResult Check)[] results)
    {
        yield return "hc fs survey report";
        yield return "folder " + folder;
        var healthy = results.Count(r => r.Check.Outcome == CheckOutcome.Healthy);
        var problems = results.Count(r => r.Check.Outcome == CheckOutcome.Issues);
        var uncheckable = results.Count(r => r.Check.Outcome == CheckOutcome.Uncheckable);
        yield return $"total={results.Length} healthy={healthy} problems={problems} uncheckable={uncheckable}";
        yield return string.Empty;
        foreach (var (path, check) in results)
        {
            yield return path;
            if (check.Outcome == CheckOutcome.Uncheckable)
            {
                yield return "status uncheckable";
                yield return "reason: " + check.Reason;
            }
            else
            {
                var fs = check.Report!;
                yield return "format " + fs.Parameters.Name;
                yield return "status " + (fs.Issues.Count == 0 ? "healthy" : "issues");
                yield return "scope structural; file contents are not verified; no repair performed";
                foreach (var warning in fs.Warnings) yield return "warning " + warning;
                foreach (var issue in fs.Issues)
                    yield return $"issue {issue.Code} entry={issue.Entry} user={issue.User} file={JsonSerializer.Serialize(issue.File)} extent={issue.Extent}" +
                        (issue.Block is int block ? $" block={block}" : "") + ": " + issue.Detail;
            }
        }
    }
    private static int CompareCommand(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["compare", "--help"])
        {
            output.WriteLine("usage: hc fs compare <left-image> <right-image> [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            output.WriteLine("Compares stored CP/M files between two closed supported single-filesystem images locally; raw images require --geometry. Auto selects a valid supported preset; ambiguous images require --format.");
            output.WriteLine("Reports files only on the left, only on the right, changed and unchanged by CP/M user area (0..31) and filename. Files are compared by their stored 128-byte CP/M records; padding is not stripped to infer original host-file length.");
            output.WriteLine("Comparison verifies file-content equality only, not whole-image, disk-layout, bootability or file-attribute equivalence. Read-only: no mount, repair or write is performed.");
            output.WriteLine("Exit status: 0 equal file contents, 1 differences found, 2 could not compare (including missing, unreadable, unsupported, ambiguous, structurally damaged or writer-fenced inputs).");
            return 0;
        }
        NativeImageAdmission? admission = null;
        try
        {
            try { admission = AcquireColdRead(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine("status uncheckable: cold read refused (" + ex.Message + ")."); return 2;
            }

            var options = new Options(args);
            if (options.Positional.Count != 3)
                throw new ArgumentException("usage: hc fs compare <left-image> <right-image> [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            options.Allow("--format", "--geometry");
            var leftPath = options.Positional[1];
            var rightPath = options.Positional[2];
            var format = options.Get("--format", "auto");
            var geometry = Geometry(options);

            var left = LoadFilesystemForCompare(leftPath, format, geometry, admission, "left");
            if (left.Error is not null) { error.WriteLine("status uncheckable: " + left.Error); return 2; }

            var right = LoadFilesystemForCompare(rightPath, format, geometry, admission, "right");
            if (right.Error is not null) { error.WriteLine("status uncheckable: " + right.Error); return 2; }

            var leftFs = left.Fs!;
            var rightFs = right.Fs!;

            var (leftOnly, rightOnly, changed, unchanged, rows) = CompareFilesystems(leftFs, rightFs);
            var hasDifferences = leftOnly > 0 || rightOnly > 0 || changed > 0;

            output.WriteLine("left-format " + leftFs.Parameters.Name);
            output.WriteLine("right-format " + rightFs.Parameters.Name);
            output.WriteLine("status " + (hasDifferences ? "differences" : "equal"));
            output.WriteLine("scope file-content equality; not whole-image or bootability equality; padding is not stripped");
            foreach (var warning in leftFs.Warnings) output.WriteLine("warning left: " + warning);
            foreach (var warning in rightFs.Warnings) output.WriteLine("warning right: " + warning);
            foreach (var row in rows)
            {
                output.WriteLine($"{row.State} user={row.User} file={JsonSerializer.Serialize(row.Name)}");
            }
            output.WriteLine($"summary left-only={leftOnly} right-only={rightOnly} changed={changed} unchanged={unchanged} total={rows.Count}");
            return hasDifferences ? 1 : 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            error.WriteLine("status uncheckable: " + ex.Message); return 2;
        }
        finally
        {
            admission?.Dispose();
        }
    }

    private sealed record CompareFsResult(CpmFs? Fs, string? Error);
    private static CompareFsResult LoadFilesystemForCompare(string path, string format, DiskGeometry? geometry, NativeImageAdmission? admission, string side)
        => LoadFilesystem(path, format, geometry, admission, side);

    private static CompareFsResult LoadFilesystem(string path, string format, DiskGeometry? geometry, NativeImageAdmission? admission, string side)
    {
        try
        {
            var prefix = string.IsNullOrEmpty(side) ? "" : side + " ";
            if (!File.Exists(path))
                return new(null, $"{prefix}image not found: {path}");
            using var source = admission?.OpenColdSource(path) ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var facts = admission?.ValidateOpenedSource(source, path);
            var length = source.Length;
            if (length >= 76)
            {
                var footer = new byte[64];
                source.Position = length - 64;
                source.ReadExactly(footer);
                var limits = new HddLimits(uint.MaxValue, 511, 12_276, 114_640);
                var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, limits);
                if (inspected.Success)
                    return new(null, $"{prefix}image is a multi-volume container; multi-volume containers requiring volume selection are unsupported.");
            }
            if (length > 16 * 1024 * 1024)
                return new(null, $"{prefix}image exceeds supported floppy size.");
            var bytes = new byte[checked((int)length)];
            source.Position = 0;
            source.ReadExactly(bytes);
            if (source.Length != bytes.Length || source.ReadByte() != -1)
                return new(null, $"{prefix}image changed while reading.");
            if (facts is not null) admission!.ValidateSourceStillCurrent(source, facts);

            var disk = Dsk.Load(bytes, geometry);
            var uniform = disk.UniformGeometry();
            var candidates = Dpb.Presets().Where(p => p.Geometry == uniform &&
                (format == "auto" || format == p.Name || format == (p.HcHeader ? "hcfs" : "cpm-hc")))
                .Select(p => CpmFs.InspectStructure(disk, p)).ToArray();
            var selection = SelectFormat(candidates);
            if (selection.Report is null)
                return new(null, selection.Unsupported ?? selection.Ambiguous);
            if (selection.Report.Issues.Count > 0)
                return new(null, $"{prefix}image is structurally damaged: {selection.Report.Issues[0].Detail}");
            return new(new CpmFs(disk, selection.Report.Parameters), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            return new(null, ex.Message);
        }
    }

    private sealed record ExportRow(int User, string CpmName, string RelPath, int Length, string Sha256, bool ReadOnly, bool System);

    private static int ExportCommand(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["export", "--help"])
        {
            output.WriteLine("usage: hc fs export <image> <destination> [--replayable] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            output.WriteLine("Exports every file from a supported closed single-filesystem CP/M disk image across all user areas into a new host folder.");
            output.WriteLine("Supported inputs: standard/extended DSK images; raw sector images require --geometry. Multi-volume containers and non-CP/M filesystems are refused. Auto selects a valid supported preset; ambiguous images require --format.");
            output.WriteLine("Replayable: with --replayable, writes a versioned cpm-export.json inventory into the exported folder recording guest user/name, relative path, stored bytes, SHA-256 and read-only/system attributes for safe restoration without the original image. Refuses HCFS disks, unrepresentable logical names and unsupported attributes.");
            output.WriteLine("Destination: the destination folder must not exist as a file, folder or link; existing host data is never overwritten.");
            output.WriteLine("Mapping: files are placed into user-area subdirectories (user0, user1, ...) to preserve identity across user areas. Host-unsafe, reserved or colliding filenames receive safe, distinct mappings.");
            output.WriteLine("Output: stable-order report with original user/name, destination-relative path, stored byte length and SHA-256, followed by totals.");
            output.WriteLine("Limits: stored CP/M 128-byte record padding is preserved; original host-file length is not guessed; file extraction is not a bootable or whole-image backup.");
            output.WriteLine("Exit status: 0 complete export, 1 could not export (including missing, unreadable, unsupported, ambiguous, structurally damaged, writer-fenced input, existing destination or invalid arguments).");
            return 0;
        }

        NativeImageAdmission? admission = null;
        try
        {
            var options = new Options(args);
            if (options.Positional.Count != 3)
                throw new ArgumentException("usage: hc fs export <image> <destination> [--replayable] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            options.Allow("--format", "--geometry", "--replayable");
            var imagePath = options.Positional[1];
            var destination = options.Positional[2];
            var format = options.Get("--format", "auto");
            var geometry = Geometry(options);
            var replayable = options.Has("--replayable");

            var fullDest = Path.GetFullPath(destination);
            if (ExportPathExistsOrLink(fullDest))
                throw new IOException("Refusing to replace existing destination path: " + destination);

            var fullImage = Path.GetFullPath(imagePath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(fullDest, fullImage, comparison))
                throw new IOException("Refusing to replace source image with export destination.");

            try { admission = AcquireColdRead(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine("could not export: cold read refused (" + ex.Message + ").");
                return 1;
            }

            var loaded = LoadFilesystem(imagePath, format, geometry, admission, "");
            if (loaded.Error is not null)
            {
                error.WriteLine("could not export: " + loaded.Error);
                return 1;
            }

            var fs = loaded.Fs!;
            if (replayable)
            {
                if (fs.Parameters.HcHeader)
                    throw new InvalidDataException("HCFS disk images are not supported for replayable CP/M export.");
                var unrecognized = fs.UnrecognizedAttributeFiles();
                if (unrecognized.Count > 0)
                    throw new InvalidDataException("Image contains files with unsupported CP/M attribute bits: " + string.Join(", ", unrecognized));
            }

            var files = fs.List().OrderBy(f => f.User).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
            if (replayable)
            {
                foreach (var file in files)
                {
                    if (file.User is < 0 or > 31)
                        throw new InvalidDataException($"File '{file.Name}' has unsupported user area {file.User}; must be 0..31.");
                    if (!CanRoundTripCpmName(file.Name))
                        throw new InvalidDataException($"File '{file.Name}' cannot round-trip through CP/M 8.3 filename encoding.");
                }
            }

            var parent = Path.GetDirectoryName(fullDest) ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(parent))
                throw new DirectoryNotFoundException("Destination parent folder not found: " + parent);

            var tempStaging = Path.Combine(parent, ".export-tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempStaging);
            bool committed = false;
            try
            {
                var allocatedByUser = new Dictionary<int, HashSet<string>>();
                var rows = new List<ExportRow>();
                long totalBytes = 0;

                foreach (var file in files)
                {
                    if (!allocatedByUser.TryGetValue(file.User, out var allocated))
                    {
                        allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        allocatedByUser[file.User] = allocated;
                    }

                    var safeName = SafeHostName(file.Name, allocated);
                    allocated.Add(safeName);

                    var userDir = Path.Combine(tempStaging, $"user{file.User}");
                    Directory.CreateDirectory(userDir);

                    var safeRelPath = $"user{file.User}/{safeName}";
                    var fullFilePath = Path.GetFullPath(Path.Combine(userDir, safeName));
                    if (!fullFilePath.StartsWith(userDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException("Path traversal prevented.");

                    byte[] data = fs.Get(file.Name, file.User);
                    File.WriteAllBytes(fullFilePath, data);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(fullFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                    var hash = Convert.ToHexStringLower(SHA256.HashData(data));
                    rows.Add(new(file.User, file.Name, safeRelPath, data.Length, hash, file.ReadOnly, file.System));
                    totalBytes += data.Length;
                }

                if (replayable)
                {
                    var inventoryPath = Path.Combine(tempStaging, "cpm-export.json");
                    var inventoryObj = new
                    {
                        format = "hc2000-cpm-export",
                        version = 1,
                        files = rows.Select(r => new
                        {
                            user = r.User,
                            name = r.CpmName,
                            path = r.RelPath,
                            length = r.Length,
                            sha256 = r.Sha256,
                            readOnly = r.ReadOnly,
                            system = r.System
                        }).ToArray()
                    };
                    var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(inventoryObj, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllBytes(inventoryPath, jsonBytes);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(inventoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                if (ExportPathExistsOrLink(fullDest))
                    throw new IOException("Refusing to replace destination created during export: " + destination);

                Directory.Move(tempStaging, fullDest);
                committed = true;

                output.WriteLine("format " + fs.Parameters.Name);
                output.WriteLine("status complete");
                output.WriteLine("destination " + destination);
                foreach (var row in rows)
                {
                    output.WriteLine($"user={row.User} file={JsonSerializer.Serialize(row.CpmName)} path={JsonSerializer.Serialize(row.RelPath)} length={row.Length} sha256={row.Sha256}");
                }
                output.WriteLine($"total files={rows.Count} bytes={totalBytes}");
                return 0;
            }
            finally
            {
                if (!committed && Directory.Exists(tempStaging))
                {
                    try { Directory.Delete(tempStaging, recursive: true); }
                    catch (Exception ex)
                    {
                        error.WriteLine("warning: failed to clean up temporary staging directory: " + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            error.WriteLine("could not export: " + ex.Message);
            return 1;
        }
        finally
        {
            admission?.Dispose();
        }
    }

    private static bool ExportPathExistsOrLink(string path)
    {
        if (Path.Exists(path) || File.Exists(path) || Directory.Exists(path)) return true;
        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null) return true;
        }
        catch { }
        try
        {
            var di = new DirectoryInfo(path);
            if (di.LinkTarget is not null) return true;
        }
        catch { }
        return false;
    }

    private static string SafeHostName(string cpmName, HashSet<string> allocated)
    {
        var baseName = ExplorerCopyOut.HostName(cpmName);
        var dot = baseName.LastIndexOf('.');
        var stem = dot < 0 ? baseName : baseName[..dot];
        var ext = dot < 0 ? "" : baseName[dot..];
        var candidate = baseName;
        int counter = 2;
        while (allocated.Contains(candidate))
        {
            candidate = $"{stem}_{counter}{ext}";
            counter++;
        }
        return candidate;
    }

    private sealed record CompareRow(string State, int User, string Name);
    private static (int LeftOnly, int RightOnly, int Changed, int Unchanged, IReadOnlyList<CompareRow>) CompareFilesystems(CpmFs leftFs, CpmFs rightFs)
    {
        var leftFiles = leftFs.List();
        var rightFiles = rightFs.List();

        bool hcHeaders = leftFs.Parameters.HcHeader && rightFs.Parameters.HcHeader;

        var leftMap = new Dictionary<(int User, string Key), CpmFile>();
        foreach (var file in leftFiles)
        {
            var key = (file.User, hcHeaders ? file.Name : file.Name.ToUpperInvariant());
            leftMap[key] = file;
        }

        var rightMap = new Dictionary<(int User, string Key), CpmFile>();
        foreach (var file in rightFiles)
        {
            var key = (file.User, hcHeaders ? file.Name : file.Name.ToUpperInvariant());
            rightMap[key] = file;
        }

        var allKeys = leftMap.Keys.Union(rightMap.Keys).OrderBy(k => k.User).ThenBy(k => k.Key, StringComparer.Ordinal).ToArray();
        var rows = new List<CompareRow>();
        int leftOnly = 0, rightOnly = 0, changed = 0, unchanged = 0;

        foreach (var key in allKeys)
        {
            bool inLeft = leftMap.TryGetValue(key, out var leftFile);
            bool inRight = rightMap.TryGetValue(key, out var rightFile);
            string state;
            string displayName = inLeft ? leftFile!.Name : rightFile!.Name;

            if (inLeft && !inRight)
            {
                state = "left-only";
                leftOnly++;
            }
            else if (!inLeft && inRight)
            {
                state = "right-only";
                rightOnly++;
            }
            else
            {
                if (leftFile!.Records != rightFile!.Records)
                {
                    state = "changed";
                    changed++;
                }
                else if (leftFile.Records == 0)
                {
                    state = "unchanged";
                    unchanged++;
                }
                else
                {
                    byte[] leftBytes = leftFs.Get(leftFile.Name, leftFile.User);
                    byte[] rightBytes = rightFs.Get(rightFile.Name, rightFile.User);
                    if (leftBytes.AsSpan().SequenceEqual(rightBytes))
                    {
                        state = "unchanged";
                        unchanged++;
                    }
                    else
                    {
                        state = "changed";
                        changed++;
                    }
                }
            }
            rows.Add(new CompareRow(state, key.User, displayName));
        }

        return (leftOnly, rightOnly, changed, unchanged, rows);
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxAt(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxImageAdmissionNative.Facts facts);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    internal static bool PathExistsOrLink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return true;
        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null) return true;
            var di = new DirectoryInfo(path);
            if (di.LinkTarget is not null) return true;
        }
        catch { }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (StatxAt(-100, path, 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x7ff, out _) == 0) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null || (fi.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            var di = new DirectoryInfo(path);
            if (di.LinkTarget is not null || (di.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch { }
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (StatxAt(-100, path, 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x7ff, out var facts) == 0)
                {
                    if ((facts.Mode & 0xf000) == 0xa000) return true; // S_IFLNK
                }
            }
            catch { }
        }
        return false;
    }

    private static void PrintFromFolderHelp(TextWriter output)
    {
        output.WriteLine("usage: hc fs from-folder <folder> <destination> [--preset cpm-hc80|cpm-hc40] [--user <0..15>]");
        output.WriteLine("Creates a new supported HC2000 CP/M data-disk image from a flat host folder of files.");
        output.WriteLine("Each regular input file is copied into the selected CP/M user area.");
        output.WriteLine("Stored files preserve input bytes followed only by standard 128-byte record padding (0x1A).");
        output.WriteLine();
        output.WriteLine("Arguments:");
        output.WriteLine("  <folder>          Flat host folder containing regular files. Subdirectories, symlinks,");
        output.WriteLine("                    and non-regular entries are strictly refused.");
        output.WriteLine("  <destination>     Path for the new DSK image. Refuses existing files, folders, or links.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --preset <name>   Disk DPB/geometry preset: cpm-hc80 (default, 80 tracks, 720 KiB)");
        output.WriteLine("                    or cpm-hc40 (40 tracks, 360 KiB).");
        output.WriteLine("  --format <name>   Alias for --preset.");
        output.WriteLine("  --user <0..15>    Target CP/M user area (default: 0).");
        output.WriteLine("  --help            Show this help message and exit.");
        output.WriteLine();
        output.WriteLine("Rules and Limitations:");
        output.WriteLine("  - The resulting disk is a CP/M data disk, not a bootable OS/system image.");
        output.WriteLine("  - Does not import into existing disks, synchronize, or repair guest files.");
        output.WriteLine("  - Filenames must fit standard CP/M 8.3 rules (1-8 char stem, 0-3 char ext).");
        output.WriteLine("  - Case normalization to uppercase is disclosed; case-folding collisions are refused.");
        output.WriteLine("  - Atomic temporary staging ensures incomplete or failed builds are never exposed.");
        output.WriteLine();
        output.WriteLine("Exit status: 0 on success; 1 on failure or refusal.");
    }

    private sealed record FolderInputFile(string HostName, string CpmName, byte[] Data, int StoredLength, string Sha256);

    private static int FromFolderCommand(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Contains("--help"))
        {
            PrintFromFolderHelp(output);
            return 0;
        }

        string? tempPath = null;
        try
        {
            var options = new Options(args);
            if (options.Positional.Count != 3)
                throw new ArgumentException("usage: hc fs from-folder <folder> <destination> [--preset cpm-hc80|cpm-hc40] [--user <0..15>]");
            options.Allow("--preset", "--format", "--user", "--container");

            var folder = options.Positional[1];
            var destination = options.Positional[2];

            var preset = options.Get("--preset", options.Get("--format", "cpm-hc80"));
            if (preset is not ("cpm-hc80" or "cpm-hc40"))
                throw new ArgumentException($"Preset '{preset}' is not supported. Supported presets: cpm-hc80, cpm-hc40.");

            var user = options.Number("--user", 0, 15);
            var containerName = options.Get("--container", "extended");
            var container = Container(containerName);

            if (PathExistsOrLink(destination))
                throw new IOException($"Destination already exists: '{destination}'. Refusing to overwrite existing host data.");

            var fullDest = Path.GetFullPath(destination);
            var destDir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                throw new DirectoryNotFoundException($"Destination parent directory does not exist: '{destDir}'.");

            var fullFolder = Path.GetFullPath(folder);
            if (fullDest.Equals(fullFolder, StringComparison.OrdinalIgnoreCase) ||
                fullDest.StartsWith(fullFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullDest.StartsWith(fullFolder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Destination '{destination}' cannot be inside input folder '{folder}'.");
            }

            if (!Directory.Exists(fullFolder))
            {
                if (File.Exists(fullFolder) || PathExistsOrLink(fullFolder))
                    throw new ArgumentException($"Input folder '{folder}' is not a directory.");
                throw new DirectoryNotFoundException($"Input folder does not exist: '{folder}'.");
            }

            if (IsSymbolicLink(fullFolder))
                throw new InvalidDataException($"Input folder '{folder}' is a symbolic link; symbolic links are not supported.");

            var entries = Directory.GetFileSystemEntries(fullFolder);
            var processedFiles = new List<FolderInputFile>();
            var cpmNameToOrig = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var entryPath in entries)
            {
                if (IsSymbolicLink(entryPath))
                    throw new InvalidDataException($"Offending path '{entryPath}': symbolic links are not supported; folder must contain regular files only.");

                if (Directory.Exists(entryPath) || (File.GetAttributes(entryPath) & FileAttributes.Directory) != 0)
                    throw new InvalidDataException($"Offending path '{entryPath}': subdirectories are not supported; folder must be flat.");

                if (OperatingSystem.IsLinux())
                {
                    const int atCurrentDirectory = -100, nonblock = 0x800, closeExec = 0x80000, noFollow = 0x20000;
                    int fd = OpenAt(atCurrentDirectory, entryPath, nonblock | closeExec | noFollow, 0);
                    if (fd < 0)
                        throw new IOException($"Offending path '{entryPath}': could not open input file.");
                    using var handle = new SafeFileHandle(fd, ownsHandle: true);
                    var facts = LinuxImageAdmissionNative.Probe(handle);
                    if ((facts.Mode & 0xf000) != 0x8000)
                        throw new InvalidDataException($"Offending path '{entryPath}': non-regular files (FIFOs, devices, sockets) are not supported.");
                }

                var fileName = Path.GetFileName(entryPath);
                if (string.IsNullOrEmpty(fileName))
                    throw new InvalidDataException($"Offending path '{entryPath}': filename is empty.");

                var dot = fileName.LastIndexOf('.');
                var stem = dot < 0 ? fileName : fileName[..dot];
                var ext = dot < 0 ? "" : fileName[(dot + 1)..];

                if (stem.Contains('.'))
                    throw new InvalidDataException($"Offending path '{entryPath}': filename contains multiple dots; not a valid CP/M 8.3 filename.");
                if (fileName.Any(c => c is <= ' ' or > '~' || "<>/\\:*?\"|".Contains(c)))
                    throw new InvalidDataException($"Offending path '{entryPath}': filename contains invalid characters; not a valid CP/M 8.3 filename.");
                if (stem.Length < 1)
                    throw new InvalidDataException($"Offending path '{entryPath}': filename stem is empty; not a valid CP/M 8.3 filename.");
                if (stem.Length > 8)
                    throw new InvalidDataException($"Offending path '{entryPath}': filename stem '{stem}' exceeds 8 characters; not a valid CP/M 8.3 filename.");
                if (ext.Length > 3)
                    throw new InvalidDataException($"Offending path '{entryPath}': filename extension '{ext}' exceeds 3 characters; not a valid CP/M 8.3 filename.");

                var cpmName = (stem + (ext.Length > 0 ? "." + ext : "")).ToUpperInvariant();
                if (cpmNameToOrig.TryGetValue(cpmName, out var existingOrig))
                {
                    throw new InvalidDataException($"Offending path '{entryPath}': case-folding collision with '{existingOrig}' mapping to same CP/M name '{cpmName}'.");
                }
                cpmNameToOrig[cpmName] = fileName;

                byte[] data;
                using (var stream = new FileStream(entryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    data = new byte[stream.Length];
                    stream.ReadExactly(data);
                }

                var sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                var storedLength = data.Length == 0 ? 0 : ((data.Length + 127) / 128 * 128);

                processedFiles.Add(new FolderInputFile(fileName, cpmName, data, storedLength, sha256));
            }

            var profile = Dpb.Presets().FirstOrDefault(p => p.Name == preset)
                ?? throw new ArgumentException($"Preset '{preset}' is not supported.");

            var extentBytes = 16384 * (profile.Exm + 1);
            if (extentBytes > profile.BlockSize * (profile.WordAllocations ? 8 : 16))
                throw new InvalidDataException("Unsupported DPB extent capacity.");

            int totalEntriesNeeded = 0;
            int totalBlocksNeeded = 0;

            foreach (var file in processedFiles)
            {
                if (file.Data.Length > 0 && checked(file.Data.Length + extentBytes - 1) / extentBytes * (profile.Exm + 1) > 2048)
                    throw new InvalidDataException($"File '{file.HostName}' exceeds CP/M extent range.");

                var entriesForFile = Math.Max(1, checked(file.Data.Length + extentBytes - 1) / extentBytes);
                var blocksForFile = checked(file.Data.Length + profile.BlockSize - 1) / profile.BlockSize;

                totalEntriesNeeded += entriesForFile;
                totalBlocksNeeded += blocksForFile;
            }

            var maxDirectoryEntries = profile.Drm + 1;
            var availableDataBlocks = profile.Dsm + 1 - profile.DirectoryBlocks;
            var maxDataBytes = (long)availableDataBlocks * profile.BlockSize;
            var requestedDataBytes = (long)totalBlocksNeeded * profile.BlockSize;

            if (totalEntriesNeeded > maxDirectoryEntries)
            {
                throw new InvalidDataException($"Selected disk preset '{preset}' cannot hold the requested files: required {totalEntriesNeeded} directory entries, but preset directory capacity is {maxDirectoryEntries} entries.");
            }
            if (totalBlocksNeeded > availableDataBlocks)
            {
                throw new InvalidDataException($"Selected disk preset '{preset}' cannot hold the requested files: required {totalBlocksNeeded} blocks ({requestedDataBytes} bytes), but disk data capacity is {availableDataBlocks} blocks ({maxDataBytes} bytes).");
            }

            var fs = CpmFs.Format(profile, container);
            foreach (var file in processedFiles)
            {
                fs.Put(file.CpmName, file.Data, user);
            }

            tempPath = Path.Combine(destDir ?? ".", $".cpm-disk-tmp-{Guid.NewGuid():N}");
            var imageBytes = fs.ImageBytes();
            File.WriteAllBytes(tempPath, imageBytes);
            File.Move(tempPath, destination);

            long totalInputBytes = 0;
            long totalStoredBytes = 0;

            foreach (var file in processedFiles.OrderBy(f => f.CpmName, StringComparer.Ordinal))
            {
                totalInputBytes += file.Data.Length;
                totalStoredBytes += file.StoredLength;
                output.WriteLine($"file={file.HostName} cpm={file.CpmName} user={user} input_length={file.Data.Length} stored_length={file.StoredLength} sha256={file.Sha256}");
            }

            output.WriteLine($"format={preset} container={containerName}");
            output.WriteLine($"total files={processedFiles.Count} input_bytes={totalInputBytes} stored_bytes={totalStoredBytes}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException or System.Security.SecurityException)
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            error.WriteLine($"hc fs from-folder: {ex.Message}");
            return 1;
        }
    }

    private static void PrintRepackHelp(TextWriter output)
    {
        output.WriteLine("usage: hc fs repack <source> <destination> --target cpm-hc80|cpm-hc40 [--format auto|cpm-hc80|cpm-hc40] [--geometry <geometry>]");
        output.WriteLine("       hc fs copy <source> <destination> --target cpm-hc80|cpm-hc40 [--format auto|cpm-hc80|cpm-hc40] [--geometry <geometry>]");
        output.WriteLine("Safely moves/copies all files from a supported CP/M disk image into a new extended DSK image.");
        output.WriteLine("Preserves guest filenames, user areas, exact stored bytes and padding, and read-only/system attributes.");
        output.WriteLine();
        output.WriteLine("Arguments:");
        output.WriteLine("  <source>          Existing closed CP/M disk image. Input is opened read-only and never modified.");
        output.WriteLine("  <destination>     Path for the new extended DSK image. Refuses existing files, folders, or links.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --target <preset> Target DPB preset (explicit choice required):");
        output.WriteLine("                    cpm-hc80 (80 tracks, 2 sides, 720 KiB) or cpm-hc40 (40 tracks, 2 sides, 360 KiB).");
        output.WriteLine("  --preset <preset> Alias for --target.");
        output.WriteLine("  --format <format> Source filesystem format (default: auto). Explicit format required if ambiguous.");
        output.WriteLine("  --geometry <geom> Source geometry (cylinders,sides,sectors,sector-size) for raw floppy images.");
        output.WriteLine("  --help            Show this help message and exit.");
        output.WriteLine();
        output.WriteLine("Rules and Boundaries:");
        output.WriteLine("  - Target must be explicitly specified (no implicit default geometry).");
        output.WriteLine("  - Generates a CP/M data-disk image, not a bootable OS/system image.");
        output.WriteLine("  - Does not preserve boot tracks, perform in-place resizing, or repair damaged source disks.");
        output.WriteLine("  - Destination cannot exist or alias the source image.");
        output.WriteLine("  - Atomic temporary staging ensures incomplete or failed builds are never exposed.");
        output.WriteLine();
        output.WriteLine("Exit status: 0 on success; 1 on failure or refusal.");
    }

    private sealed record RepackFileReport(int User, string Name, int StoredLength, string Sha256, bool ReadOnly, bool System);

    private static int RepackCommand(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Contains("--help"))
        {
            PrintRepackHelp(output);
            return 0;
        }

        string? tempPath = null;
        NativeImageAdmission? admission = null;
        try
        {
            var options = new Options(args);
            if (options.Positional.Count != 3)
                throw new ArgumentException("usage: hc fs repack <source> <destination> --target cpm-hc80|cpm-hc40 [--format auto|cpm-hc80|cpm-hc40] [--geometry <geometry>]");
            options.Allow("--target", "--preset", "--format", "--geometry");

            var sourcePath = options.Positional[1];
            var destination = options.Positional[2];

            if (!options.Values.ContainsKey("--target") && !options.Values.ContainsKey("--preset"))
                throw new ArgumentException("Explicit target choice is required: specify --target cpm-hc80 or --target cpm-hc40.");

            var targetPreset = options.Get("--target", options.Get("--preset", ""));
            if (targetPreset is not ("cpm-hc80" or "cpm-hc40"))
                throw new ArgumentException($"Target preset '{targetPreset}' is not supported. Supported target presets: cpm-hc80, cpm-hc40.");

            var targetProfile = Dpb.Presets().FirstOrDefault(p => p.Name == targetPreset)
                ?? throw new ArgumentException($"Target preset '{targetPreset}' is not supported.");

            var format = options.Get("--format", "auto");
            var geometry = Geometry(options);

            var fullDest = Path.GetFullPath(destination);
            var destDir = Path.GetDirectoryName(fullDest);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                throw new DirectoryNotFoundException($"Destination parent directory does not exist: '{destDir}'.");

            if (!File.Exists(sourcePath) && !PathExistsOrLink(sourcePath))
                throw new FileNotFoundException($"Source disk image does not exist: '{sourcePath}'.");

            var fullSource = Path.GetFullPath(sourcePath);
            if (string.Equals(fullSource, fullDest, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Destination '{destination}' is an alias of source '{sourcePath}'. Refusing to copy onto source.");

            if (PathExistsOrLink(destination))
                throw new IOException($"Destination already exists: '{destination}'. Refusing to overwrite existing host data.");

            admission = AcquireColdRead();
            using var sourceStream = admission?.OpenColdSource(sourcePath) ?? new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var facts = admission?.ValidateOpenedSource(sourceStream, sourcePath);
            if (sourceStream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Source image exceeds supported floppy size.");
            var sourceBytes = new byte[checked((int)sourceStream.Length)];
            sourceStream.ReadExactly(sourceBytes);
            if (sourceStream.Length != sourceBytes.Length || sourceStream.ReadByte() != -1)
                throw new InvalidDataException("Source image changed while reading.");
            if (facts is not null) admission!.ValidateSourceStillCurrent(sourceStream, facts);

            var initialSourceSha = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));

            Dsk sourceDisk;
            try
            {
                sourceDisk = Dsk.Load(sourceBytes, geometry);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Could not load source disk image: {ex.Message}", ex);
            }

            var uniform = sourceDisk.UniformGeometry();
            var candidates = Dpb.Presets().Where(p => p.Geometry == uniform &&
                (format == "auto" || format == p.Name || format == (p.HcHeader ? "hcfs" : "cpm-hc")))
                .Select(p => CpmFs.InspectStructure(sourceDisk, p)).ToArray();
            var selection = SelectFormat(candidates);
            if (selection.Report is null)
                throw new InvalidDataException(selection.Unsupported ?? selection.Ambiguous ?? "Unsupported or ambiguous source format.");
            if (selection.Report.Issues.Count > 0)
                throw new InvalidDataException($"Source disk has structural damage ({selection.Report.Issues.Count} issue(s) found); refusing repack.");
            if (selection.Report.Parameters.HcHeader)
                throw new InvalidDataException($"Source disk format '{selection.Report.Parameters.Name}' is an HCFS native disk, not a CP/M filesystem; repack supports CP/M source disks only.");

            var sourceFs = new CpmFs(sourceDisk, selection.Report.Parameters);

            var unrecognized = sourceFs.UnrecognizedAttributeFiles();
            if (unrecognized.Count > 0)
                throw new InvalidDataException($"Source disk contains unrecognized file attributes for: {string.Join(", ", unrecognized)}; cannot transfer without promised metadata preservation.");

            var sourceFiles = sourceFs.List();

            var extentBytes = 16384 * (targetProfile.Exm + 1);
            if (extentBytes > targetProfile.BlockSize * (targetProfile.WordAllocations ? 8 : 16))
                throw new InvalidDataException("Unsupported target DPB extent capacity.");

            int totalEntriesNeeded = 0;
            int totalBlocksNeeded = 0;

            foreach (var file in sourceFiles)
            {
                var storedBytes = file.Records * 128;
                if (storedBytes > 0 && checked(storedBytes + extentBytes - 1) / extentBytes * (targetProfile.Exm + 1) > 2048)
                    throw new InvalidDataException($"File '{file.Name}' in user {file.User} exceeds CP/M extent range on target disk.");

                var entriesForFile = Math.Max(1, checked(storedBytes + extentBytes - 1) / extentBytes);
                var blocksForFile = checked(storedBytes + targetProfile.BlockSize - 1) / targetProfile.BlockSize;

                totalEntriesNeeded += entriesForFile;
                totalBlocksNeeded += blocksForFile;
            }

            var maxDirectoryEntries = targetProfile.Drm + 1;
            var availableDataBlocks = targetProfile.Dsm + 1 - targetProfile.DirectoryBlocks;
            var maxDataBytes = (long)availableDataBlocks * targetProfile.BlockSize;
            var requestedDataBytes = (long)totalBlocksNeeded * targetProfile.BlockSize;

            if (totalEntriesNeeded > maxDirectoryEntries)
            {
                throw new InvalidDataException($"Selected target disk preset '{targetPreset}' cannot hold the source files: required {totalEntriesNeeded} directory entries, but preset directory capacity is {maxDirectoryEntries} entries.");
            }
            if (totalBlocksNeeded > availableDataBlocks)
            {
                throw new InvalidDataException($"Selected target disk preset '{targetPreset}' cannot hold the source files: required {totalBlocksNeeded} blocks ({requestedDataBytes} bytes), but disk data capacity is {availableDataBlocks} blocks ({maxDataBytes} bytes).");
            }

            var targetFs = CpmFs.Format(targetProfile, DiskFormat.Extended);
            var reportRows = new List<RepackFileReport>();
            long totalStoredBytes = 0;

            var orderedFiles = sourceFiles.OrderBy(f => f.User).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
            foreach (var file in orderedFiles)
            {
                byte[] data = sourceFs.Get(file.Name, file.User);
                targetFs.Put(file.Name, data, file.User, readOnly: file.ReadOnly, system: file.System);
                var sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
                totalStoredBytes += data.Length;
                reportRows.Add(new RepackFileReport(file.User, file.Name, data.Length, sha256, file.ReadOnly, file.System));
            }

            tempPath = Path.Combine(destDir ?? ".", $".cpm-repack-tmp-{Guid.NewGuid():N}");
            var targetImageBytes = targetFs.ImageBytes();
            File.WriteAllBytes(tempPath, targetImageBytes);
            File.Move(tempPath, destination);

            var postSourceSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath)));
            if (initialSourceSha != postSourceSha)
                throw new InvalidDataException("Source image was modified during operation.");

            foreach (var row in reportRows)
            {
                output.WriteLine($"user={row.User} file={row.Name} stored_length={row.StoredLength} sha256={row.Sha256} read_only={row.ReadOnly.ToString().ToLowerInvariant()} system={row.System.ToString().ToLowerInvariant()}");
            }

            output.WriteLine($"source_format={sourceFs.Parameters.Name} target_format={targetPreset}");
            output.WriteLine($"total files={reportRows.Count} bytes={totalStoredBytes}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException or System.Security.SecurityException)
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            error.WriteLine($"hc fs repack: {ex.Message}");
            return 1;
        }
        finally
        {
            admission?.Dispose();
        }
    }

    private static readonly HashSet<string> ReservedDeviceStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static bool CanRoundTripCpmName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var dot = name.LastIndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var ext = dot < 0 ? "" : name[(dot + 1)..];
        if (stem.Length is < 1 or > 8 || ext.Length > 3) return false;
        if (name.Any(c => c is < ' ' or > '~' || "<>/\\:*?\"|".Contains(c)) || stem.Contains('.')) return false;
        if (name.EndsWith('.') || stem.EndsWith(' ') || ext.EndsWith(' ')) return false;
        try
        {
            var encoded = Encoding.ASCII.GetBytes(stem.PadRight(8) + ext.PadRight(3));
            var field = Encoding.ASCII.GetString(encoded);
            var decodedStem = field[..8].TrimEnd(' ');
            var decodedExt = field[8..].TrimEnd(' ');
            var decoded = decodedExt.Length == 0 ? decodedStem : decodedStem + "." + decodedExt;
            return string.Equals(name, decoded, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var current = full;
        while (!string.IsNullOrEmpty(current))
        {
            if (IsLinkOrReparse(current))
                throw new IOException($"Symbolic links or reparse points are not supported: {current}");
            var parent = Path.GetDirectoryName(current);
            if (parent == current || string.IsNullOrEmpty(parent)) break;
            current = parent;
        }
    }

    private static bool IsLinkOrReparse(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.LinkTarget is not null) return true;
            if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        catch { }
        try
        {
            var di = new DirectoryInfo(path);
            if (di.LinkTarget is not null) return true;
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        catch { }
        return false;
    }

    private static bool IsInsideOrEqual(string targetPath, string rootPath)
    {
        var targetFull = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootFull = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(targetFull, rootFull, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = rootFull + Path.DirectorySeparatorChar;
        return targetFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StorageUserNameComparer(StringComparer comparer) : IEqualityComparer<(int User, string Name)>
    {
        public bool Equals((int User, string Name) x, (int User, string Name) y) => x.User == y.User && comparer.Equals(x.Name, y.Name);
        public int GetHashCode((int User, string Name) obj) => HashCode.Combine(obj.User, comparer.GetHashCode(obj.Name));
    }

    private sealed record InventoryEntry(int User, string Name, string Path, int Length, string Sha256, bool ReadOnly, bool System);
    private sealed record RestoredFileSnapshot(int User, string Name, byte[] Data, int Length, string Sha256, bool ReadOnly, bool System);

    private static (List<InventoryEntry> Files, string? Error) ParseInventory(byte[] jsonBytes)
    {
        if (jsonBytes.Length > 1024 * 1024)
            return ([], "cpm-export.json exceeds maximum size of 1 MiB.");

        if (jsonBytes.Length >= 3 && jsonBytes[0] == 0xef && jsonBytes[1] == 0xbb && jsonBytes[2] == 0xbf)
            return ([], "cpm-export.json must not have a byte-order mark (BOM).");

        JsonDocument doc;
        try
        {
            var options = new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            };
            doc = JsonDocument.Parse(jsonBytes, options);
        }
        catch (JsonException ex)
        {
            return ([], "cpm-export.json is malformed JSON: " + ex.Message);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return ([], "cpm-export.json root must be a JSON object.");

            var rootProps = new HashSet<string>(StringComparer.Ordinal);
            string? format = null;
            int? version = null;
            JsonElement? filesElement = null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!rootProps.Add(prop.Name))
                    return ([], $"cpm-export.json contains duplicate property '{prop.Name}'.");

                switch (prop.Name)
                {
                    case "format":
                        if (prop.Value.ValueKind != JsonValueKind.String)
                            return ([], "cpm-export.json property 'format' must be a string.");
                        format = prop.Value.GetString();
                        break;
                    case "version":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var v))
                            return ([], "cpm-export.json property 'version' must be an integer.");
                        version = v;
                        break;
                    case "files":
                        if (prop.Value.ValueKind != JsonValueKind.Array)
                            return ([], "cpm-export.json property 'files' must be an array.");
                        filesElement = prop.Value;
                        break;
                    default:
                        return ([], $"cpm-export.json contains unknown property '{prop.Name}'.");
                }
            }

            if (!rootProps.Contains("format") || !rootProps.Contains("version") || !rootProps.Contains("files"))
                return ([], "cpm-export.json is missing required root properties.");

            if (format != "hc2000-cpm-export")
                return ([], $"Unsupported export format '{format}'; expected 'hc2000-cpm-export'.");

            if (version != 1)
                return ([], $"Unsupported export version {version}; expected 1.");

            var files = new List<InventoryEntry>();
            var seenIdentities = new HashSet<(int, string)>(new StorageUserNameComparer(StringComparer.OrdinalIgnoreCase));
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;

            int fileCount = filesElement!.Value.GetArrayLength();
            if (fileCount > 128)
                return ([], $"cpm-export.json exceeds maximum file count of 128; found {fileCount}.");

            foreach (var item in filesElement.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return ([], "cpm-export.json files element must contain objects.");

                var itemProps = new HashSet<string>(StringComparer.Ordinal);
                int? user = null;
                string? name = null;
                string? path = null;
                int? length = null;
                string? sha256 = null;
                bool? readOnly = null;
                bool? system = null;

                foreach (var prop in item.EnumerateObject())
                {
                    if (!itemProps.Add(prop.Name))
                        return ([], $"cpm-export.json file entry contains duplicate property '{prop.Name}'.");

                    switch (prop.Name)
                    {
                        case "user":
                            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var u))
                                return ([], "File entry property 'user' must be an integer.");
                            user = u;
                            break;
                        case "name":
                            if (prop.Value.ValueKind != JsonValueKind.String)
                                return ([], "File entry property 'name' must be a string.");
                            name = prop.Value.GetString();
                            break;
                        case "path":
                            if (prop.Value.ValueKind != JsonValueKind.String)
                                return ([], "File entry property 'path' must be a string.");
                            path = prop.Value.GetString();
                            break;
                        case "length":
                            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var l))
                                return ([], "File entry property 'length' must be an integer.");
                            length = l;
                            break;
                        case "sha256":
                            if (prop.Value.ValueKind != JsonValueKind.String)
                                return ([], "File entry property 'sha256' must be a string.");
                            sha256 = prop.Value.GetString();
                            break;
                        case "readOnly":
                            if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                return ([], "File entry property 'readOnly' must be a boolean.");
                            readOnly = prop.Value.GetBoolean();
                            break;
                        case "system":
                            if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                return ([], "File entry property 'system' must be a boolean.");
                            system = prop.Value.GetBoolean();
                            break;
                        default:
                            return ([], $"File entry contains unknown property '{prop.Name}'.");
                    }
                }

                if (itemProps.Count != 7 || user is null || name is null || path is null || length is null || sha256 is null || readOnly is null || system is null)
                    return ([], "File entry is missing one or more required properties.");

                if (user.Value is < 0 or > 31)
                    return ([], $"File entry user {user.Value} is out of range; must be 0..31.");

                if (!CanRoundTripCpmName(name))
                    return ([], $"File entry name '{name}' is not a valid representable CP/M 8.3 filename.");

                if (!seenIdentities.Add((user.Value, name)))
                    return ([], $"Duplicate CP/M identity (user={user.Value}, name='{name}').");

                var pathParts = path.Split('/');
                if (pathParts.Length != 2)
                    return ([], $"File entry path '{path}' must have exactly two slash-separated components.");

                if (pathParts[0] != $"user{user.Value}")
                    return ([], $"File entry path directory component '{pathParts[0]}' does not match user{user.Value}.");

                var hostFileName = pathParts[1];
                if (hostFileName.Length is 0 or > 255)
                    return ([], $"File entry host filename component '{hostFileName}' must be 1..255 bytes.");

                if (hostFileName.Any(c => c < 32 || c > 126 || "<>:\"/\\|?*".Contains(c)))
                    return ([], $"File entry host filename component '{hostFileName}' contains forbidden characters.");

                if (hostFileName is "." or ".." || hostFileName.EndsWith('.') || hostFileName.EndsWith(' '))
                    return ([], $"File entry host filename component '{hostFileName}' has invalid format.");

                var dotIdx = hostFileName.IndexOf('.');
                var fileStem = dotIdx < 0 ? hostFileName : hostFileName[..dotIdx];
                if (ReservedDeviceStems.Contains(fileStem))
                    return ([], $"File entry host filename '{hostFileName}' uses a reserved device stem.");

                if (!seenPaths.Add(path))
                    return ([], $"Duplicate relative path '{path}' in export inventory.");

                if (length.Value < 0 || length.Value % 128 != 0)
                    return ([], $"File entry length {length.Value} must be a non-negative multiple of 128.");

                if (length.Value > 714752)
                    return ([], $"File entry length {length.Value} exceeds maximum supported capacity of 714752 bytes.");

                totalBytes += length.Value;
                if (totalBytes > 714752)
                    return ([], $"Total stored byte length {totalBytes} exceeds maximum supported capacity of 714752 bytes.");

                if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                    return ([], $"File entry sha256 '{sha256}' must be a 64-character hexadecimal string.");

                files.Add(new(user.Value, name, path, length.Value, sha256.ToLowerInvariant(), readOnly.Value, system.Value));
            }

            return (files, null);
        }
    }

    private static void ValidateExactSet(string fullExport, List<InventoryEntry> inventoryFiles)
    {
        var rootEntries = Directory.GetFileSystemEntries(fullExport);
        var expectedUserDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedFilesByDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in inventoryFiles)
        {
            var parts = file.Path.Split('/');
            var userDirName = parts[0];
            var fileName = parts[1];
            expectedUserDirs.Add(userDirName);
            if (!expectedFilesByDir.TryGetValue(userDirName, out var fileSet))
            {
                fileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                expectedFilesByDir[userDirName] = fileSet;
            }
            fileSet.Add(fileName);
        }

        bool seenExportJson = false;

        foreach (var entryPath in rootEntries)
        {
            if (IsLinkOrReparse(entryPath))
                throw new IOException("Symbolic links or reparse points are not supported in export tree: " + entryPath);

            var entryName = Path.GetFileName(entryPath);
            if (Directory.Exists(entryPath))
            {
                if (!expectedUserDirs.Contains(entryName))
                    throw new InvalidDataException("Unexpected directory in export: " + entryName);

                var subEntries = Directory.GetFileSystemEntries(entryPath);
                var expectedFiles = expectedFilesByDir[entryName];
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var subEntry in subEntries)
                {
                    if (IsLinkOrReparse(subEntry))
                        throw new IOException("Symbolic links or reparse points are not supported in export tree: " + subEntry);

                    if (Directory.Exists(subEntry))
                        throw new InvalidDataException("Unexpected subdirectory in user directory: " + subEntry);

                    var subName = Path.GetFileName(subEntry);
                    if (!expectedFiles.Contains(subName))
                        throw new InvalidDataException($"Unexpected file in {entryName}: {subName}");

                    seenFiles.Add(subName);
                }

                foreach (var expectedFile in expectedFiles)
                {
                    if (!seenFiles.Contains(expectedFile))
                        throw new InvalidDataException($"Missing payload file in {entryName}: {expectedFile}");
                }
            }
            else
            {
                if (string.Equals(entryName, "cpm-export.json", StringComparison.Ordinal))
                    seenExportJson = true;
                else
                    throw new InvalidDataException("Unexpected file at export root: " + entryName);
            }
        }

        if (!seenExportJson)
            throw new InvalidDataException("Export folder does not contain a replayable inventory (cpm-export.json). Ordinary folders and non-replayable exports cannot be restored.");

        foreach (var userDirName in expectedUserDirs)
        {
            var userDirPath = Path.Combine(fullExport, userDirName);
            if (!Directory.Exists(userDirPath))
                throw new InvalidDataException("Missing expected user directory: " + userDirName);
        }
    }

    private static int RestoreCommand(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["restore", "--help"])
        {
            output.WriteLine("usage: hc fs restore <export-folder> <destination> [--preset cpm-hc80|cpm-hc40]");
            output.WriteLine("Restores a replayable CP/M file export to a new 40- or 80-track data-disk image without needing the original disk.");
            output.WriteLine("Input: the export folder must contain a valid cpm-export.json inventory and exact matching payload files. Ordinary folders, modified files or earlier exports without an inventory are refused.");
            output.WriteLine("Presets: --preset cpm-hc80 (default, 80-track, 714752 bytes data capacity) or cpm-hc40 (40-track, 356352 bytes data capacity). Produces an extended DSK image.");
            output.WriteLine("Destination: the destination image must not exist as a file, folder or link, and cannot be inside the export folder; existing files are never overwritten.");
            output.WriteLine("Integrity: guest identities, user areas (0..31), exact stored bytes, record padding and read-only/system attributes are verified and restored.");
            output.WriteLine("Limits: restores logical CP/M files only; not a bootable or whole-image backup; does not infer historical authenticity or unpadded host EOF.");
            output.WriteLine("Exit status: 0 complete restoration, 1 could not restore (including missing/invalid inventory, tampered or missing payloads, capacity exceeded, existing destination or invalid arguments).");
            return 0;
        }

        string? tempPath = null;
        bool committed = false;
        try
        {
            var options = new Options(args);
            if (options.Positional.Count != 3)
                throw new ArgumentException("usage: hc fs restore <export-folder> <destination> [--preset cpm-hc80|cpm-hc40]");
            options.Allow("--preset");
            var exportFolder = options.Positional[1];
            var destination = options.Positional[2];
            var preset = options.Get("--preset", "cpm-hc80");

            if (preset is not ("cpm-hc80" or "cpm-hc40"))
                throw new ArgumentException($"Target preset '{preset}' is not supported. Supported presets: cpm-hc80, cpm-hc40.");

            var fullExport = Path.GetFullPath(exportFolder);
            if (!Directory.Exists(fullExport))
                throw new DirectoryNotFoundException("Export folder not found: " + exportFolder);

            RejectLinks(fullExport);

            var fullDest = Path.GetFullPath(destination);
            if (IsInsideOrEqual(fullDest, fullExport))
                throw new IOException("Destination path cannot be inside or equal to export folder: " + destination);

            if (ExportPathExistsOrLink(fullDest))
                throw new IOException("Refusing to replace existing destination path: " + destination);

            var destParent = Path.GetDirectoryName(fullDest) ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(destParent))
                throw new DirectoryNotFoundException("Destination parent folder not found: " + destParent);

            RejectLinks(destParent);

            var inventoryPath = Path.Combine(fullExport, "cpm-export.json");
            if (!File.Exists(inventoryPath))
                throw new InvalidDataException("Export folder does not contain a replayable inventory (cpm-export.json). Ordinary folders and non-replayable exports cannot be restored.");

            if (IsLinkOrReparse(inventoryPath))
                throw new IOException("Symbolic links or reparse points are not supported for inventory: " + inventoryPath);

            byte[] inventoryBytes;
            using (var invStream = new FileStream(inventoryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (invStream.Length > 1024 * 1024)
                    throw new InvalidDataException("cpm-export.json exceeds maximum size of 1 MiB.");
                inventoryBytes = new byte[invStream.Length];
                invStream.ReadExactly(inventoryBytes);
                if (invStream.ReadByte() != -1)
                    throw new InvalidDataException("cpm-export.json changed while reading.");
            }

            var (inventoryFiles, parseError) = ParseInventory(inventoryBytes);
            if (parseError is not null)
                throw new InvalidDataException(parseError);

            ValidateExactSet(fullExport, inventoryFiles);

            var snapshots = new List<RestoredFileSnapshot>();
            foreach (var item in inventoryFiles)
            {
                var payloadPath = Path.Combine(fullExport, item.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(payloadPath))
                    throw new InvalidDataException("Missing payload file: " + item.Path);
                if (IsLinkOrReparse(payloadPath))
                    throw new IOException("Symbolic links or reparse points are not supported for payload: " + payloadPath);

                byte[] payloadBytes;
                using (var stream = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length != item.Length)
                        throw new InvalidDataException($"Payload length mismatch for {item.Path}: expected {item.Length}, found {stream.Length}.");
                    payloadBytes = new byte[item.Length];
                    stream.ReadExactly(payloadBytes);
                    if (stream.ReadByte() != -1)
                        throw new InvalidDataException($"Payload changed while reading: {item.Path}.");
                }
                var actualSha = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
                if (!string.Equals(actualSha, item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Payload SHA-256 mismatch for {item.Path}: expected {item.Sha256}, actual {actualSha}.");

                snapshots.Add(new(item.User, item.Name, payloadBytes, item.Length, actualSha, item.ReadOnly, item.System));
            }

            var targetProfile = Dpb.Presets().FirstOrDefault(p => p.Name == preset)
                ?? throw new ArgumentException($"Target preset '{preset}' is not supported. Supported presets: cpm-hc80, cpm-hc40.");

            var targetFs = CpmFs.Format(targetProfile, DiskFormat.Extended);
            int usableBlocks = targetFs.MaximumBlock + 1 - targetProfile.DirectoryBlocks;
            int totalDirCapacity = targetProfile.Drm + 1;
            int extentBytes = 16384 * (targetProfile.Exm + 1);

            int totalBlocksNeeded = 0;
            int totalDirEntriesNeeded = 0;
            foreach (var file in snapshots)
            {
                int blocks = (file.Data.Length + targetProfile.BlockSize - 1) / targetProfile.BlockSize;
                int entries = Math.Max(1, (file.Data.Length + extentBytes - 1) / extentBytes);
                if (entries * (targetProfile.Exm + 1) > 2048)
                    throw new InvalidDataException($"File '{file.Name}' exceeds CP/M extent range.");
                totalBlocksNeeded += blocks;
                totalDirEntriesNeeded += entries;
            }

            if (totalBlocksNeeded > usableBlocks)
                throw new InvalidDataException($"Export exceeds target {targetProfile.Name} data capacity: requires {totalBlocksNeeded} blocks, but preset has {usableBlocks} usable blocks.");

            if (totalDirEntriesNeeded > totalDirCapacity)
                throw new InvalidDataException($"Export exceeds target {targetProfile.Name} directory capacity: requires {totalDirEntriesNeeded} entries, but preset has {totalDirCapacity} entries.");

            var orderedSnapshots = snapshots.OrderBy(s => s.User).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
            foreach (var snapshot in orderedSnapshots)
            {
                targetFs.Put(snapshot.Name, snapshot.Data, snapshot.User, snapshot.ReadOnly, snapshot.System);
            }

            tempPath = Path.Combine(destParent, $".restore-tmp-{Guid.NewGuid():N}.dsk");
            var imageBytes = targetFs.ImageBytes();
            using (var tempStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                tempStream.Write(imageBytes);
                tempStream.Flush(true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var readBackDsk = Dsk.Load(File.ReadAllBytes(tempPath));
            var report = CpmFs.InspectStructure(readBackDsk, targetProfile);
            if (report.Issues.Count > 0)
                throw new InvalidDataException("Restored image failed structural check: " + report.Issues[0].Detail);

            var readBackFs = new CpmFs(readBackDsk, targetProfile);
            var readBackFiles = readBackFs.List().ToDictionary(f => (f.User, f.Name), new StorageUserNameComparer(StringComparer.OrdinalIgnoreCase));
            if (readBackFiles.Count != orderedSnapshots.Length)
                throw new InvalidDataException("Restored image file count mismatch.");

            foreach (var snapshot in orderedSnapshots)
            {
                if (!readBackFiles.TryGetValue((snapshot.User, snapshot.Name), out var readBackFile))
                    throw new InvalidDataException($"Restored file missing on readback: user={snapshot.User} name={snapshot.Name}");
                if (readBackFile.ReadOnly != snapshot.ReadOnly)
                    throw new InvalidDataException($"Restored file ReadOnly attribute mismatch: user={snapshot.User} name={snapshot.Name}");
                if (readBackFile.System != snapshot.System)
                    throw new InvalidDataException($"Restored file System attribute mismatch: user={snapshot.User} name={snapshot.Name}");

                var readBackData = readBackFs.Get(snapshot.Name, snapshot.User);
                if (readBackData.Length != snapshot.Data.Length)
                    throw new InvalidDataException($"Restored file length mismatch on readback: user={snapshot.User} name={snapshot.Name}");
                if (!readBackData.AsSpan().SequenceEqual(snapshot.Data))
                    throw new InvalidDataException($"Restored file bytes mismatch on readback: user={snapshot.User} name={snapshot.Name}");
            }

            if (ExportPathExistsOrLink(fullDest))
                throw new IOException("Refusing to replace existing destination path: " + destination);

            File.Move(tempPath, fullDest, overwrite: false);
            committed = true;

            output.WriteLine("preset " + targetProfile.Name);
            output.WriteLine("status complete");
            output.WriteLine("destination " + destination);
            long totalBytes = 0;
            foreach (var row in orderedSnapshots)
            {
                totalBytes += row.Data.Length;
                output.WriteLine($"user={row.User} file={row.Name} stored_length={row.Data.Length} sha256={row.Sha256} read_only={row.ReadOnly.ToString().ToLowerInvariant()} system={row.System.ToString().ToLowerInvariant()}");
            }
            output.WriteLine($"total files={orderedSnapshots.Length} bytes={totalBytes}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException or System.Security.SecurityException)
        {
            error.WriteLine("could not restore: " + ex.Message);
            return 1;
        }
        finally
        {
            if (!committed && tempPath is not null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (Exception cleanupEx)
                {
                    error.WriteLine($"warning: failed to clean up temporary staging file '{tempPath}': {cleanupEx.Message}");
                }
            }
        }
    }

    private static void Disk(Options o, TextWriter output)
    {
        var verb = o.Positional[0]; o.Count(2);
        if (verb == "info")
        {
            o.Allow("--geometry"); var disk = Load(o.Positional[1], o);
            output.WriteLine($"{disk.Format}: {disk.Cylinders} cylinders, {disk.Sides} sides");
            foreach (var track in disk.Tracks)
                output.WriteLine($"track {track.Cylinder}/{track.Side} gap={track.Gap:X2} filler={track.Filler:X2}: " +
                    string.Join(' ', track.Sectors.Select(s => $"{s.C}/{s.H}/{s.R}/{s.N}:{s.Data.Length}[{s.ST1:X2},{s.ST2:X2}]")));
        }
        else if (verb == "convert")
        {
            o.Allow("--geometry", "-o", "--container"); var disk = Load(o.Positional[1], o); var path = o.Required("-o");
            var format = o.Get("--container", Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase) ? "raw" : "extended");
            AtomicWrite(path, disk.ToContainer(Container(format)));
        }
        else throw new ArgumentException("dsk supports info/convert.");
    }
    private static DiskFormat Container(string value) => value switch
    {
        "extended" => DiskFormat.Extended, "standard" => DiskFormat.Standard, "raw" => DiskFormat.Raw,
        _ => throw new ArgumentException("--container must be extended, standard or raw.")
    };
    private static void Filesystem(Options o, TextWriter output, TextWriter error)
    {
        var verb = o.Positional[0]; var path = o.Positional[1];
        var user = o.Number("--user", 0, 31);
        if (verb == "new")
        {
            o.Count(2); o.Allow("--format", "--geometry", "--container");
            if (File.Exists(path)) throw new IOException("Refusing to replace an existing image with fs new.");
            var format = o.Required("--format"); var geometry = Geometry(o);
            var profiles = Dpb.Presets().Where(p => (p.Name == format || format == (p.HcHeader ? "hcfs" : "cpm-hc")) && (geometry is null || p.Geometry == geometry)).ToArray();
            var profile = profiles.FirstOrDefault() ?? throw new ArgumentException("No preset matches requested format/geometry.");
            AtomicWrite(path, Dsk.Create(profile.Geometry, Container(o.Get("--container", "extended"))).Save(), false); return;
        }
        var disk = Load(path, o); var fs = CpmFs.Open(disk, o.Get("--format", "auto"));
        foreach (var warning in fs.Warnings) error.WriteLine("warning: " + warning);
        var hc = fs.Parameters.HcHeader ? new HcFs(fs) : null;
        switch (verb)
        {
            case "ls":
                o.Count(2); o.Allow("--format", "--geometry", "--user"); output.WriteLine("format " + fs.Parameters.Name);
                var listed = fs.List().Where(f => !o.Values.ContainsKey("--user") || f.User == user).ToArray();
                foreach (var collision in ExportCollisions(listed)) error.WriteLine("warning: " + collision);
                foreach (var file in listed)
                {
                    var inspection = hc?.Inspect(file); var h = inspection?.Header;
                    var metadata = h is null ? "" : $" type={h.Type} length={h.Length} start={h.Start} variables={h.Variables} autostart={h.Autostart}";
                    if (inspection?.IsUntyped == true) metadata += " untyped/headerless";
                    else if (inspection?.Diagnostic is not null) { error.WriteLine($"warning: {JsonSerializer.Serialize(file.Name)}: {inspection.Diagnostic}"); metadata += " invalid-header"; }
                    var presentedName = file.Name.TrimEnd(' ');
                    var displayName = CatalogName(presentedName);
                    if (displayName != presentedName || (presentedName.Length == 0 && file.Name.Length > 0)) metadata += " literal=" + JsonSerializer.Serialize(file.Name);
                    output.WriteLine($"{file.User}\t{displayName}\t{file.Records * 128}\tallocated={file.AllocatedBytes}\t{(file.ReadOnly ? 'R' : '-')}{(file.System ? 'S' : '-')}" + metadata);
                }
                break;
            case "get":
                o.Count(3); o.Allow("--format", "--geometry", "--user", "-o", "--tap");
                var name = o.Positional[2]; var bytes = hc?.Get(name, user) ?? fs.Get(name, user);
                if (o.Values.ContainsKey("--tap"))
                {
                    if (hc is null) throw new ArgumentException("--tap export requires HCFS metadata.");
                    var h = hc.Header(name, user); var tape = Tap.Load([]); tape.Put(FromHc(name, h, bytes)); bytes = tape.Save();
                }
                if (!o.Values.ContainsKey("-o"))
                {
                    var collisions = ExportCollisions(fs.List().Where(f => f.User == user && HostComparer.Equals(HostName(f.Name), HostName(name)))).ToArray();
                    if (collisions.Length > 0) throw new InvalidDataException(collisions[0] + "; specify -o explicitly.");
                }
                Export(o, name, bytes); break;
            case "put":
                o.Count(3); o.Allow("--format", "--geometry", "--user", "--name", "--type", "--start", "--variables", "--autostart", "--tap-name");
                var data = File.ReadAllBytes(o.Positional[2]); var targetName = o.Get("--name", Path.GetFileName(o.Positional[2]));
                if (o.Values.TryGetValue("--tap-name", out var tapeName))
                {
                    if (hc is null) throw new ArgumentException("Tape metadata import requires HCFS.");
                    var file = Tap.Load(data).Get(tapeName); var h = file.HcHeader;
                    targetName = o.Get("--name", tapeName); hc.Put(targetName, file.Data, user, h.Type, h.Start, h.Variables, h.Autostart);
                }
                else if (hc is not null) hc.Put(targetName, data, user, (byte)o.Number("--type", 3, 255), (ushort)o.Number("--start", 0, 65535),
                    (ushort)o.Number("--variables", 0, 65535), (ushort)o.Number("--autostart", 0x8000, 65535));
                else
                {
                    if (o.Values.Keys.Any(k => k is "--type" or "--start" or "--variables" or "--autostart")) throw new ArgumentException("Header options require HCFS.");
                    fs.Put(targetName.ToUpperInvariant(), data, user);
                }
                AtomicWrite(path, disk.Save()); break;
            case "rm":
                o.Count(3); o.Allow("--format", "--geometry", "--user"); fs.Delete(o.Positional[2], user); AtomicWrite(path, disk.Save()); break;
            default: throw new ArgumentException("fs supports ls/get/put/rm/new.");
        }
    }
    private static TapeFile FromHc(string name, HcFileHeader header, byte[] bytes)
        => new(name.TrimEnd(' '), header.Type <= 3 ? header.Type : throw new InvalidDataException("HC file type cannot be represented on tape."),
            header.Type == 0 ? header.Autostart : header.Start, header.Variables, bytes);
    private static void Tape(Options o, TextWriter output, TextWriter error)
    {
        var verb = o.Positional[0]; var path = o.Positional[1];
        if (verb == "create")
        {
            // A shared blank tape: zero bytes, never one of the guest's tape formats.
            o.Count(2); o.Allow();
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Refusing to replace an existing path with tap create.");
            AtomicWrite(path, MediaBlanks.TapeBlank(), false, error);
            return;
        }
        var tape = Tap.Load(File.Exists(path) ? File.ReadAllBytes(path) : verb == "put" ? [] : throw new FileNotFoundException("Tape not found.", path));
        switch (verb)
        {
            case "ls":
                o.Count(2); o.Allow();
                foreach (var f in tape.List()) output.WriteLine($"{f.Name}\t{f.Data.Length}\ttype={f.Type} start={f.Start} variables={f.Variables}"); break;
            case "get":
                o.Count(3); o.Allow("-o"); Export(o, o.Positional[2], tape.Get(o.Positional[2]).Data); break;
            case "put":
                o.Count(3); o.Allow("--name", "--type", "--start", "--variables", "--disk-name", "--format", "--geometry", "--user");
                if (o.Values.TryGetValue("--disk-name", out var name))
                {
                    var hc = new HcFs(CpmFs.Open(Load(o.Positional[2], o), o.Get("--format", "hcfs"))); var user = o.Number("--user", 0, 31);
                    tape.Put(FromHc(o.Get("--name", name), hc.Header(name, user), hc.Get(name, user)));
                }
                else tape.Put(new(o.Get("--name", Path.GetFileNameWithoutExtension(o.Positional[2])), (byte)o.Number("--type", 3, 3),
                    (ushort)o.Number("--start", 0, 65535), (ushort)o.Number("--variables", 0, 65535), File.ReadAllBytes(o.Positional[2])));
                AtomicWrite(path, tape.Save(), true, error); break;
            default: throw new ArgumentException("tap supports create/ls/get/put.");
        }
    }
    private static int Rom(Options o, TextWriter output)
    {
        o.Count(2); var verb = o.Positional[0]; var tools = new RomTools(); var bytes = File.ReadAllBytes(o.Positional[1]);
        if (verb == "id")
        {
            o.Allow("--manifest"); var manifestPath = o.Get("--manifest", FindManifest(o.Positional[1]));
            var manifest = File.ReadAllText(manifestPath); var matches = tools.Identify(bytes, manifest);
            foreach (var match in matches) output.WriteLine(match);
            if (matches.Count != 0) return 0;
            if (bytes.Length == 65536)
                foreach (var slice in tools.Split(bytes)) output.WriteLine($"{slice.Offset:X4} {slice.Name}: " + string.Join(",", tools.Identify(slice.Data, manifest)));
            output.WriteLine("unknown sha256=" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()); return 1;
        }
        if (verb == "split")
        {
            o.Allow("-o"); var slices = tools.Split(bytes); var folder = o.Get("-o", Path.GetFileNameWithoutExtension(o.Positional[1]) + "-split");
            if (slices.Any(s => File.Exists(Path.Combine(folder, s.Name)))) throw new IOException("Refusing to overwrite existing ROM slices.");
            Directory.CreateDirectory(folder);
            foreach (var slice in slices) { AtomicWrite(Path.Combine(folder, slice.Name), slice.Data, false); output.WriteLine($"{slice.Offset:X4} {slice.Data.Length} {slice.Name}"); }
            return 0;
        }
        throw new ArgumentException("rom supports id/split.");
    }
    private static string FindManifest(string input)
    {
        foreach (var start in new[] { Path.GetDirectoryName(Path.GetFullPath(input))!, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var path = Path.Combine(dir.FullName, "MANIFEST.sha256"); if (File.Exists(path)) return path; dir = dir.Parent;
            }
        }
        return "MANIFEST.sha256";
    }
    private static StringComparer HostComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string CatalogName(string name) => new(name.Select(c => char.IsControl(c) || "/\\\\*?".Contains(c) ? '_' : c).ToArray());
    private static IEnumerable<string> ExportCollisions(IEnumerable<CpmFile> files) => files.GroupBy(f => HostName(f.Name), HostComparer)
        .Where(g => g.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() > 1)
        .Select(g => "Export name collision " + JsonSerializer.Serialize(g.Key) + ": " + string.Join(", ", g.Select(f => JsonSerializer.Serialize(f.Name))));
    private static void Export(Options options, string name, byte[] bytes)
    {
        var path = options.Get("-o", HostName(name));
        if (!options.Values.ContainsKey("-o") && File.Exists(path)) throw new IOException("Refusing to overwrite implicit export path; specify -o explicitly.");
        AtomicWrite(path, bytes, options.Values.ContainsKey("-o"));
    }
    private static string HostName(string name)
    {
        name = name.TrimEnd(' '); // presentation only; disk identity stays the full field
        var safe = new string(name.Select(c => char.IsControl(c) || "<>/\\:*?\"|".Contains(c) ? '_' : c).ToArray());
        return safe.Length == 0 || safe is "." or ".." ? "hc-file.bin" : safe;
    }
    private static void AtomicWrite(string path, byte[] bytes, bool overwrite = true, TextWriter? error = null)
    {
        var full = Path.GetFullPath(path); var temporary = full + ".hc-" + Guid.NewGuid().ToString("N");
        var admission = NativeImageAdmission.BeginExistingPublication(full);
        if (admission?.TightenedStateDirectory is { } tightened) error?.WriteLine($"hc tap: tightened {tightened} to 0700.");
        bool moveStarted = false, moved = false;
        try
        {
            File.WriteAllBytes(temporary, bytes);
            // A builder unit's UMask=0002 would otherwise leave the image group-writable (0664),
            // which admission refuses as Unsupported on the next publication; the writer
            // tightens its own output instead of relying on the process umask.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            admission?.ValidatePublicationTargetStillCurrent();
            moveStarted = true;
            File.Move(temporary, full, overwrite);
            moved = true;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally
            {
                if (moved || !moveStarted) admission?.CompleteKnownPublicationAfterDrain(
                    moved ? KnownImagePublication.KnownNew : KnownImagePublication.Unpublished);
                else admission?.RetainUnknownPublication();
            }
        }
    }
    // Compatibility facade: the implementation and its admission/quarantine state live in HostShared.
    public enum OfflineOutcome { Refused, KnownOld, KnownNew, Unknown }
    public enum OfflineError {
        None, InvalidRequest, UnsupportedStore, Busy, AbandonedLease,
        ReadOnly, Protected, StaleIdentity, InvalidImageSize,
        InvalidContainer, ResourceLimit, Cancelled, IoFailure,
        VerificationFailed, ReconciliationFailed,
        AlreadyInFlight, OwnerAbandoned, QuarantinePoisoned, ReconcileCapExhausted
    }
    public enum OfflineIoPoint {
        SourceOpen, FooterRead, IndexRead, SourceValidationRead,
        TempCreate, TempWrite, TempVerifyRead, SourceRecheckRead,
        BeforeMove, AfterMove, TargetReadback, TempDelete
    }
    public readonly record struct OfflineIoEvent(OfflineIoPoint Point, ulong Offset, int Count);
    public sealed record OfflineOwnership(bool LocalDisposable, bool ExclusivelyOwned, bool Unbound,
        bool SameHostSecurityDomain);
    public sealed record OfflineReplaceRequest(string TargetPath, HddVolumeIdentity ExpectedVolume,
        Guid OwnerEpoch, Guid AttemptId, OfflineOwnership Ownership, HddSelectionLimits Limits, HddSelectionUsage Usage);
    public sealed record OfflineCharge(ulong MetadataBytes, ulong CopyWorkBytes, ulong TotalLiveBytes,
        ulong TransactionBytes, ulong ReservedIoBytes, ulong ActualIoBytes, ulong TransferBufferBytes);
    public sealed record OfflineReceipt(string TargetPath, Guid OwnerEpoch, Guid AttemptId,
        HddVolumeIdentity ExpectedVolume, HddVolumeIdentity? IntendedVolume,
        ImmutableArray<byte> ExpectedFileSha256, ImmutableArray<byte> IntendedFileSha256,
        bool RenameMayHaveStarted, bool WriterDrained, OfflineOutcome Outcome, OfflineError Error,
        OfflineCharge Charge, Guid? BlockingAttemptId);
    public sealed record OfflineAbandonReceipt(Guid AttemptId, bool Abandoned, OfflineError Fault, DateTimeOffset Utc);

    public sealed class OfflineAttempt : IDisposable
    {
        private readonly HddContainerPublication.OfflineAttempt attempt;
        internal OfflineAttempt(HddContainerPublication.OfflineAttempt attempt) => this.attempt = attempt;
        public OfflineReceipt Receipt => FromPublication(attempt.Receipt);
        public OfflineReceipt Reconcile() => FromPublication(attempt.Reconcile());
        public OfflineAbandonReceipt Abandon(OfflineError fault)
        {
            var receipt = attempt.Abandon((HddContainerPublication.OfflineError)fault);
            return new(receipt.AttemptId, receipt.Abandoned, (OfflineError)receipt.Fault, receipt.Utc);
        }
        public void Dispose() => attempt.Dispose();
    }

    public static OfflineAttempt ReplaceOffline(OfflineReplaceRequest request, ReadOnlySpan<byte> replacement,
        CancellationToken cancellationToken = default, Action<OfflineIoEvent>? fault = null)
    {
        var limits = request?.Limits;
        var usage = request?.Usage;
        var ownership = request?.Ownership;
        var converted = request is null ? null! : new HddContainerPublication.OfflineReplaceRequest(request.TargetPath,
            ToPublication(request.ExpectedVolume), request.OwnerEpoch, request.AttemptId,
            ownership is null ? null! : new(ownership.LocalDisposable, ownership.ExclusivelyOwned, ownership.Unbound, ownership.SameHostSecurityDomain),
            limits is null ? null! : new(limits.MaxFileBytes, limits.MaxEntries, limits.MaxIndexBytes, limits.MaxMetadataBytes,
                limits.MaxSelectedBytes, limits.MaxResidentBytes, limits.MaxSnapshotBytes, limits.MaxCopyWorkBytes,
                limits.MaxTotalLiveBytes, limits.MaxTransactionBytes, limits.MaxIoBytes),
            usage is null ? null! : new(usage.ConcurrentSelectedImages, usage.ResidentBytes, usage.SnapshotBytes, usage.TransferBufferBytes));
        return new(HddContainerPublication.ReplaceOffline(converted, replacement, cancellationToken,
            fault is null ? null : e => fault(new((OfflineIoPoint)e.Point, e.Offset, e.Count))));
    }
    private static HddContainerPublication.VolumeIdentity ToPublication(HddVolumeIdentity? identity) => identity is null ? null!
        : new(identity.Store is null ? null! : new(identity.Store.ContainerId, identity.Store.Revision, identity.Store.MetadataSha256),
            identity.Name, identity.PayloadSha256);
    private static HddVolumeIdentity FromPublication(HddContainerPublication.VolumeIdentity identity) => new(
        new(identity.Store.ContainerId, identity.Store.Revision, identity.Store.MetadataSha256), identity.Name, identity.PayloadSha256);
    private static OfflineReceipt FromPublication(HddContainerPublication.OfflineReceipt r) => new(r.TargetPath,
        r.OwnerEpoch, r.AttemptId, FromPublication(r.ExpectedVolume), r.IntendedVolume is null ? null : FromPublication(r.IntendedVolume),
        r.ExpectedFileSha256, r.IntendedFileSha256, r.RenameMayHaveStarted, r.WriterDrained,
        (OfflineOutcome)r.Outcome, (OfflineError)r.Error, new(r.Charge.MetadataBytes, r.Charge.CopyWorkBytes,
            r.Charge.TotalLiveBytes, r.Charge.TransactionBytes, r.Charge.ReservedIoBytes, r.Charge.ActualIoBytes,
            r.Charge.TransferBufferBytes), r.BlockingAttemptId);

    internal sealed class Options
    {
        public List<string> Positional { get; } = new();
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Options(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith('-')) { Positional.Add(args[i]); continue; }
                var name = args[i];
                if (Values.ContainsKey(name)) throw new ArgumentException("Duplicate option " + name);
                if (name is "--tap" or "--recursive" or "-r" or "--replayable") { Values[name] = "true"; continue; }
                if (name is not ("-o" or "-u" or "--format" or "--geometry" or "--report" or "--container" or "--user" or "--name" or "--type" or "--start" or "--variables" or "--autostart" or "--tap-name" or "--disk-name" or "--manifest" or "--preset" or "--target"))
                    throw new ArgumentException("Unknown option " + name);
                if (++i >= args.Length || args[i].StartsWith('-')) throw new ArgumentException("Missing value for " + name);
                Values[name] = args[i];
            }
        }
        public bool Has(string name) => Values.ContainsKey(name);
        public void Count(int count) { if (Positional.Count != count) throw new ArgumentException("Wrong number of operands."); }
        public void Allow(params string[] names)
        {
            foreach (var key in Values.Keys) if (!names.Contains(key)) throw new ArgumentException("Option is not valid for this operation: " + key);
        }
        public string Get(string name, string fallback) => Values.GetValueOrDefault(name, fallback);
        public string Required(string name) => Values.TryGetValue(name, out var value) ? value : throw new ArgumentException("Missing " + name);
        public int Number(string name, int fallback, int max)
        {
            var text = Get(name, fallback.ToString(CultureInfo.InvariantCulture));
            var value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(text, CultureInfo.InvariantCulture);
            return value is >= 0 && value <= max ? value : throw new ArgumentException(name + " out of range.");
        }
    }
}
