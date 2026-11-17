using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

namespace Hc2000.Tools;

public static class CpmNameVariantsCommand
{
    public sealed record FileOccurrence(
        string ImagePath,
        int User,
        string FileName,
        int StoredBytes,
        int Records,
        string ContentHash
    );

    public sealed record ContentVariant(
        int StoredBytes,
        int Records,
        string ContentHash,
        IReadOnlyList<FileOccurrence> Members
    );

    public sealed record NameFamily(
        string FileName,
        IReadOnlyList<ContentVariant> Variants,
        IReadOnlyList<FileOccurrence> AllOccurrences
    );

    public static void PrintHelp(TextWriter output)
    {
        output.WriteLine("usage: hc fs variants <folder> [--recursive] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
        output.WriteLine("       hc fs vars <folder> [--recursive] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
        output.WriteLine("       hc fs name-variants <folder> [--recursive] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
        output.WriteLine("Finds canonical CP/M filenames with multiple stored-content variants across a folder of supported HC40/HC80 data DSK images without mounting or modifying them.");
        output.WriteLine();
        output.WriteLine("Arguments:");
        output.WriteLine("  <folder>          Host folder containing CP/M disk images (.dsk, or .raw with --geometry).");
        output.WriteLine("                    Subfolders are searched only when --recursive is specified.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --recursive, -r   Search subfolders recursively (default: top-level folder only).");
        output.WriteLine("  --format <name>   Disk format preset (default: auto; supported: cpm-hc, cpm-hc40, cpm-hc80).");
        output.WriteLine("  --geometry <geom> Geometry (cylinders,sides,sectors,sector-size) for raw sector images.");
        output.WriteLine("  --help, -h        Show this help message and exit.");
        output.WriteLine();
        output.WriteLine("Supported Media:");
        output.WriteLine("  - HC40 and HC80 CP/M data DSK images (standard or extended DSK).");
        output.WriteLine("  - Raw sector images require explicit --geometry.");
        output.WriteLine("  - Multi-volume HDD containers and non-CP/M filesystems (e.g. HCFS) are reported as uncheckable/unsupported.");
        output.WriteLine("  - Non-image files in the folder are skipped.");
        output.WriteLine();
        output.WriteLine("Same-Name Content Families and Variants:");
        output.WriteLine("  - A same-name content family exists when a canonical CP/M filename appears with at least two distinct stored-content variants across the inspected images or user areas.");
        output.WriteLine("  - Single occurrences and identical-only copies across images or user areas do not form a differing variant family and are omitted.");
        output.WriteLine("  - Different filenames with identical contents are not combined into a name family.");
        output.WriteLine("  - Different user areas on the same or different disks are tracked as distinct occurrence identities.");
        output.WriteLine();
        output.WriteLine("Stored-Record Equality and Limits:");
        output.WriteLine("  - Compares full stored CP/M record bytes and lengths across all extents (128-byte record-aligned).");
        output.WriteLine("  - Original application EOF is not inferred; trailing padding (0x1A or 0x00) is preserved as stored content.");
        output.WriteLine("  - Valid empty files (0 records, 0 bytes) are handled explicitly and form a distinct 0-byte variant when non-empty copies of the same filename exist.");
        output.WriteLine("  - File attributes (read-only, system), directory order, and physical block allocation do not create content variants.");
        output.WriteLine("  - The report does not identify a newest or preferred copy, nor does it infer common history, whole-disk equivalence, or permission to discard media.");
        output.WriteLine();
        output.WriteLine("Read-Only Safety:");
        output.WriteLine("  - Uses supported offline read admission; no media is mounted or altered.");
        output.WriteLine("  - Never modifies source images, creates no index or sidecar files, and executes no guest code.");
        output.WriteLine();
        output.WriteLine("Exit status:");
        output.WriteLine("  0: Complete search, same-name content variant families found.");
        output.WriteLine("  1: Complete search, no same-name content variant families found.");
        output.WriteLine("  2: Invalid invocation, missing/unreadable folder, refused cold read, or incomplete search");
        output.WriteLine("     (one or more candidate images were unreadable, damaged, unsupported, or refused).");
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["variants", "--help"] or ["vars", "--help"] or ["name-variants", "--help"] or ["variants", "-h"] or ["vars", "-h"] or ["name-variants", "-h"] ||
            args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp(output);
            return 0;
        }

        NativeImageAdmission? admission = null;
        try
        {
            var options = new StorageCommand.Options(args);
            if (options.Positional.Count != 2)
            {
                throw new ArgumentException("usage: hc fs variants <folder> [--recursive] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            }
            options.Allow("--recursive", "-r", "--format", "--geometry");

            var folder = options.Positional[1];
            bool recursive = options.Values.ContainsKey("--recursive") || options.Values.ContainsKey("-r");
            var format = options.Get("--format", "auto");
            var geometry = StorageCommand.Geometry(options);

            var fullFolder = Path.GetFullPath(folder);
            if (!Directory.Exists(fullFolder))
            {
                if (File.Exists(fullFolder) || StorageCommand.PathExistsOrLink(fullFolder))
                    throw new ArgumentException($"Input folder '{folder}' is not a directory.");
                throw new DirectoryNotFoundException($"Input folder does not exist: '{folder}'.");
            }

            try
            {
                admission = StorageCommand.AcquireColdRead();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AdmissionException)
            {
                error.WriteLine("could not run: cold read refused (" + ex.Message + ").");
                return 2;
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(fullFolder, "*", searchOption)
                    .Where(f => File.Exists(f) && (f.EndsWith(".dsk", StringComparison.OrdinalIgnoreCase) || (geometry is not null && f.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))))
                    .OrderBy(f => Path.GetRelativePath(fullFolder, f).Replace('\\', '/'), StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"could not run: unreadable folder '{folder}': {ex.Message}");
                return 2;
            }

            var allOccurrences = new List<FileOccurrence>();
            var incompletes = new List<(string Path, string Reason)>();

            foreach (var file in files)
            {
                var relPath = Path.GetRelativePath(fullFolder, file).Replace('\\', '/');
                var (cpmFs, issueReason) = CpmFileLocatorCommand.InspectCandidateImage(file, format, geometry, admission);
                if (issueReason is not null)
                {
                    incompletes.Add((relPath, issueReason));
                }
                else if (cpmFs is not null)
                {
                    var imageOccurrences = new List<FileOccurrence>();
                    bool imageError = false;
                    foreach (var cpmFile in cpmFs.List())
                    {
                        try
                        {
                            var contentBytes = cpmFs.Get(cpmFile.Name, cpmFile.User);
                            var hash = Convert.ToHexStringLower(SHA256.HashData(contentBytes));
                            imageOccurrences.Add(new FileOccurrence(
                                relPath,
                                cpmFile.User,
                                cpmFile.Name,
                                contentBytes.Length,
                                cpmFile.Records,
                                hash
                            ));
                        }
                        catch (Exception ex) when (ex is InvalidDataException or IOException)
                        {
                            incompletes.Add((relPath, $"could not read file '{cpmFile.Name}' (user {cpmFile.User}): {ex.Message}"));
                            imageError = true;
                            break;
                        }
                    }

                    if (!imageError)
                    {
                        allOccurrences.AddRange(imageOccurrences);
                    }
                }
            }

            var families = new List<NameFamily>();
            var nameGroups = allOccurrences
                .GroupBy(o => o.FileName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.First().FileName, StringComparer.Ordinal);

            foreach (var group in nameGroups)
            {
                var variantGroups = group
                    .GroupBy(o => (o.StoredBytes, o.ContentHash))
                    .Select(vg => new ContentVariant(
                        StoredBytes: vg.Key.StoredBytes,
                        Records: vg.First().Records,
                        ContentHash: vg.Key.ContentHash,
                        Members: vg.OrderBy(m => m.ImagePath, StringComparer.Ordinal)
                                   .ThenBy(m => m.User)
                                   .ToList()
                    ))
                    .ToList();

                if (variantGroups.Count >= 2)
                {
                    var orderedVariants = variantGroups
                        .OrderBy(v => v.Members[0].ImagePath, StringComparer.Ordinal)
                        .ThenBy(v => v.Members[0].User)
                        .ThenBy(v => v.StoredBytes)
                        .ThenBy(v => v.ContentHash, StringComparer.Ordinal)
                        .ToList();

                    var allFamilyOccurrences = orderedVariants
                        .SelectMany(v => v.Members)
                        .ToList();

                    families.Add(new NameFamily(
                        FileName: group.First().FileName,
                        Variants: orderedVariants,
                        AllOccurrences: allFamilyOccurrences
                    ));
                }
            }

            bool hasIncompletes = incompletes.Count > 0;
            output.WriteLine("status " + (hasIncompletes ? "incomplete" : "complete"));
            output.WriteLine("scope record-aligned stored bytes (128-byte CP/M records); original application EOF is not inferred");

            foreach (var (incPath, reason) in incompletes.OrderBy(i => i.Path, StringComparer.Ordinal))
            {
                output.WriteLine($"incomplete image={JsonSerializer.Serialize(incPath)} reason={JsonSerializer.Serialize(reason)}");
            }

            for (int fi = 0; fi < families.Count; fi++)
            {
                var family = families[fi];
                int familyId = fi + 1;
                output.WriteLine($"family id={familyId} file={JsonSerializer.Serialize(family.FileName)} variants={family.Variants.Count} occurrences={family.AllOccurrences.Count}");
                for (int vi = 0; vi < family.Variants.Count; vi++)
                {
                    var variant = family.Variants[vi];
                    int variantId = vi + 1;
                    output.WriteLine($"variant family={familyId} variant_id={variantId} stored_bytes={variant.StoredBytes} stored_length={variant.StoredBytes} records={variant.Records} sha256={JsonSerializer.Serialize(variant.ContentHash)} occurrences={variant.Members.Count}");
                    foreach (var occurrence in variant.Members)
                    {
                        output.WriteLine($"occurrence family={familyId} variant_id={variantId} image={JsonSerializer.Serialize(occurrence.ImagePath)} user={occurrence.User} file={JsonSerializer.Serialize(occurrence.FileName)} stored_bytes={occurrence.StoredBytes} stored_length={occurrence.StoredBytes} records={occurrence.Records}");
                    }
                }
            }

            if (families.Count == 0)
            {
                if (files.Length == 0)
                    output.WriteLine("no supported CP/M images found in folder");
                else if (!hasIncompletes)
                    output.WriteLine("no same-name content variant families found");
                else
                    output.WriteLine("no same-name content variant families found on readable images");
            }

            int totalVariants = families.Sum(f => f.Variants.Count);
            int totalOccurrences = families.Sum(f => f.AllOccurrences.Count);
            output.WriteLine($"summary name_families={families.Count} content_variants={totalVariants} total_occurrences={totalOccurrences} images_checked={files.Length} incomplete_images={incompletes.Count}");

            if (hasIncompletes)
                return 2;
            if (families.Count == 0)
                return 1;
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException or AdmissionException)
        {
            error.WriteLine("could not run: " + ex.Message);
            return 2;
        }
        finally
        {
            admission?.Dispose();
        }
    }
}
