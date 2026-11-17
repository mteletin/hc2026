using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

namespace Hc2000.Tools;

public static class CpmDuplicatesCommand
{
    public sealed record FileOccurrence(
        string ImagePath,
        int User,
        string FileName,
        int StoredBytes,
        int Records,
        string ContentHash
    );

    public sealed record DuplicateGroup(
        int StoredBytes,
        int Records,
        string ContentHash,
        IReadOnlyList<FileOccurrence> Members
    );

    public static void PrintHelp(TextWriter output)
    {
        output.WriteLine("usage: hc fs duplicates <folder> [--recursive] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
        output.WriteLine("       hc fs dups <folder> [--recursive] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
        output.WriteLine("Finds groups of identical stored CP/M files across a folder of supported HC40/HC80 data DSK images without mounting or modifying them.");
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
        output.WriteLine("Stored-Record Equality and Limits:");
        output.WriteLine("  - Compares full stored CP/M record bytes and lengths across all extents (128-byte record-aligned).");
        output.WriteLine("  - Original application EOF is not inferred; trailing padding (0x1A or 0x00) is preserved as stored content.");
        output.WriteLine("  - Differently named files and files in different user areas match if stored contents match.");
        output.WriteLine("  - Distinct files on the same disk image can match and form a group.");
        output.WriteLine("  - Valid empty files (0 records, 0 bytes) form an explicitly zero-length group when two or more exist.");
        output.WriteLine("  - File attributes (read-only, system) and physical block allocation do not affect content equality.");
        output.WriteLine("  - Duplicate file content does NOT assert whole-image equivalence or imply that a disk is safe to delete.");
        output.WriteLine();
        output.WriteLine("Read-Only Safety:");
        output.WriteLine("  - Uses supported offline read admission; no media is mounted or altered.");
        output.WriteLine("  - Never modifies source images, creates no index or sidecar files, and executes no guest code.");
        output.WriteLine();
        output.WriteLine("Exit status:");
        output.WriteLine("  0: Complete search, duplicate groups found.");
        output.WriteLine("  1: Complete search, no duplicate groups found.");
        output.WriteLine("  2: Invalid invocation, missing/unreadable folder, refused cold read, or incomplete search");
        output.WriteLine("     (one or more candidate images were unreadable, damaged, unsupported, or refused).");
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["duplicates", "--help"] or ["dups", "--help"] or ["duplicates", "-h"] or ["dups", "-h"] ||
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
                throw new ArgumentException("usage: hc fs duplicates <folder> [--recursive] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
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

            // The report order is a pure function of the input: group over an explicitly sorted
            // occurrence list so group order never comes from scan order or hash order.
            var orderedOccurrences = allOccurrences
                .OrderBy(o => o.ImagePath, StringComparer.Ordinal)
                .ThenBy(o => o.User)
                .ThenBy(o => o.FileName, StringComparer.Ordinal)
                .ThenBy(o => o.StoredBytes)
                .ToArray();

            var allGroups = orderedOccurrences
                .GroupBy(o => (o.StoredBytes, o.ContentHash))
                .Select(g => new DuplicateGroup(
                    StoredBytes: g.Key.StoredBytes,
                    Records: g.First().Records,
                    ContentHash: g.Key.ContentHash,
                    Members: g.OrderBy(m => m.ImagePath, StringComparer.Ordinal)
                              .ThenBy(m => m.User)
                              .ThenBy(m => m.FileName, StringComparer.Ordinal)
                              .ToList()
                ))
                .ToList();

            var duplicateGroups = allGroups
                .Where(g => g.Members.Count >= 2)
                .OrderBy(g => g.Members[0].ImagePath, StringComparer.Ordinal)
                .ThenBy(g => g.Members[0].User)
                .ThenBy(g => g.Members[0].FileName, StringComparer.Ordinal)
                .ToList();

            bool hasIncompletes = incompletes.Count > 0;
            output.WriteLine("status " + (hasIncompletes ? "incomplete" : "complete"));
            output.WriteLine("scope record-aligned stored bytes (128-byte CP/M records); original application EOF is not inferred");

            foreach (var (incPath, reason) in incompletes.OrderBy(i => i.Path, StringComparer.Ordinal))
            {
                output.WriteLine($"incomplete image={JsonSerializer.Serialize(incPath)} reason={JsonSerializer.Serialize(reason)}");
            }

            for (int i = 0; i < duplicateGroups.Count; i++)
            {
                var group = duplicateGroups[i];
                int groupId = i + 1;
                output.WriteLine($"group id={groupId} files={group.Members.Count} stored_bytes={group.StoredBytes} stored_length={group.StoredBytes} records={group.Records} sha256={JsonSerializer.Serialize(group.ContentHash)}");
                foreach (var member in group.Members)
                {
                    output.WriteLine($"member group={groupId} image={JsonSerializer.Serialize(member.ImagePath)} user={member.User} file={JsonSerializer.Serialize(member.FileName)} stored_bytes={member.StoredBytes} stored_length={member.StoredBytes} records={member.Records}");
                }
            }

            if (duplicateGroups.Count == 0)
            {
                if (files.Length == 0)
                    output.WriteLine("no supported CP/M images found in folder");
                else if (!hasIncompletes)
                    output.WriteLine("no duplicate file groups found");
                else
                    output.WriteLine("no duplicate file groups found on readable images");
            }

            int totalDuplicateFiles = duplicateGroups.Sum(g => g.Members.Count);
            output.WriteLine($"summary duplicate_groups={duplicateGroups.Count} duplicate_files={totalDuplicateFiles} unique_contents={allGroups.Count} images_checked={files.Length} incomplete_images={incompletes.Count}");

            if (hasIncompletes)
                return 2;
            if (duplicateGroups.Count == 0)
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
