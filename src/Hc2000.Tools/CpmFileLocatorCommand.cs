using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

namespace Hc2000.Tools;

public static class CpmFileLocatorCommand
{
    public sealed record LocateMatch(string ImagePath, int User, string FileName, int StoredBytes, int Records);

    public static void PrintHelp(TextWriter output)
    {
        output.WriteLine("usage: hc fs locate <folder> <pattern> [--recursive] [--user <0..31>] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
        output.WriteLine("       hc fs find <folder> <pattern> [--recursive] [--user <0..31>] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
        output.WriteLine("Searches a folder of supported HC40/HC80 CP/M data DSK images for files matching a pattern without mounting them.");
        output.WriteLine();
        output.WriteLine("Arguments:");
        output.WriteLine("  <folder>          Host folder containing CP/M disk images (.dsk, or .raw with --geometry).");
        output.WriteLine("                    Subfolders are searched only when --recursive is specified.");
        output.WriteLine("  <pattern>         Case-insensitive CP/M filename pattern (8.3 format). Supports standard");
        output.WriteLine("                    CP/M wildcards: '*' matches zero or more characters, '?' matches one character.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --recursive, -r   Search subfolders recursively (default: top-level folder only).");
        output.WriteLine("  --user, -u <0..31> Filter matches by CP/M user area (0..31).");
        output.WriteLine("  --format <name>   Disk format preset (default: auto; supported: cpm-hc, cpm-hc40, cpm-hc80).");
        output.WriteLine("  --geometry <geom> Geometry (cylinders,sides,sectors,sector-size) for raw sector images.");
        output.WriteLine("  --help, -h        Show this help message and exit.");
        output.WriteLine();
        output.WriteLine("Supported Media:");
        output.WriteLine("  - HC40 and HC80 CP/M data DSK images (standard or extended DSK).");
        output.WriteLine("  - Raw sector images require explicit --geometry.");
        output.WriteLine("  - Multi-volume HDD containers and non-CP/M filesystems (e.g. HCFS) are reported as uncheckable.");
        output.WriteLine("  - Non-image files in the folder are skipped.");
        output.WriteLine();
        output.WriteLine("Stored-Length Boundary:");
        output.WriteLine("  - Stored byte length is the CP/M stored record length (record-aligned: records * 128 bytes).");
        output.WriteLine("  - It is not an inferred application end-of-file length; padding is preserved and not stripped.");
        output.WriteLine("  - File contents are not compared or extracted.");
        output.WriteLine();
        output.WriteLine("Read-Only Safety:");
        output.WriteLine("  - Uses supported offline read admission; no media is mounted.");
        output.WriteLine("  - Never modifies source images, creates no index or sidecar files, and executes no guest code.");
        output.WriteLine();
        output.WriteLine("Exit status:");
        output.WriteLine("  0: Complete search, matching file(s) found.");
        output.WriteLine("  1: Complete search, no matching files found.");
        output.WriteLine("  2: Invalid invocation, missing/unreadable folder, refused cold read, or incomplete search");
        output.WriteLine("     (one or more candidate images were unreadable, damaged, unsupported, or refused).");
    }

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--help"] or ["locate", "--help"] or ["find", "--help"] or ["locate", "-h"] or ["find", "-h"] ||
            args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp(output);
            return 0;
        }

        NativeImageAdmission? admission = null;
        try
        {
            var options = new StorageCommand.Options(args);
            if (options.Positional.Count != 3)
            {
                throw new ArgumentException("usage: hc fs locate <folder> <pattern> [--recursive] [--user <0..31>] [--format auto|cpm-hc|hcfs|cpm-hc40|cpm-hc80|hcfs40|hcfs80] [--geometry cylinders,sides,sectors,sector-size]");
            }
            options.Allow("--recursive", "-r", "--user", "-u", "--format", "--geometry");

            var folder = options.Positional[1];
            var pattern = options.Positional[2];

            ValidatePattern(pattern);

            bool recursive = options.Values.ContainsKey("--recursive") || options.Values.ContainsKey("-r");

            int? userFilter = null;
            if (options.Values.TryGetValue("--user", out var userStr) || options.Values.TryGetValue("-u", out userStr))
            {
                if (!int.TryParse(userStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUser) ||
                    parsedUser < 0 || parsedUser > 31)
                {
                    throw new ArgumentException($"User area filter must be an integer between 0 and 31; got '{userStr}'.");
                }
                userFilter = parsedUser;
            }

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

            var searchResult = CpmFileLocator.SearchFolder(folder, pattern, userFilter, recursive, format, geometry, admission);
            var matches = searchResult.Matches;
            var incompletes = searchResult.Incompletes;

            bool hasIncompletes = incompletes.Count > 0;
            output.WriteLine("status " + (hasIncompletes ? "incomplete" : "complete"));
            output.WriteLine("scope record-aligned stored bytes (128-byte CP/M records); original application EOF is not inferred");

            foreach (var inc in incompletes)
            {
                output.WriteLine($"incomplete image={JsonSerializer.Serialize(inc.ImagePath)} reason={JsonSerializer.Serialize(inc.Reason)}");
            }

            foreach (var match in matches)
            {
                output.WriteLine($"match image={JsonSerializer.Serialize(match.ImagePath)} user={match.User} file={JsonSerializer.Serialize(match.FileName)} stored_bytes={match.StoredBytes} stored_length={match.StoredBytes} records={match.Records}");
            }

            if (matches.Count == 0)
            {
                if (searchResult.ImagesChecked == 0)
                    output.WriteLine("no supported CP/M images found in folder");
                else if (!hasIncompletes)
                    output.WriteLine("no matching files found");
                else
                    output.WriteLine("no matching files found on readable images");
            }

            output.WriteLine($"summary matches={matches.Count} images_checked={searchResult.ImagesChecked} incomplete_images={incompletes.Count}");

            if (hasIncompletes)
                return 2;
            if (matches.Count == 0)
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

    public static void ValidatePattern(string pattern) => CpmFileLocator.ValidatePattern(pattern);

    public static bool MatchesPattern(string pattern, string cpmFileName) => CpmFileLocator.MatchesPattern(pattern, cpmFileName);

    public static bool MatchField(string pattern, string text) => CpmFileLocator.MatchField(pattern, text);

    internal static (CpmFs? Fs, string? IssueReason) InspectCandidateImage(string path, string format, DiskGeometry? geometry, NativeImageAdmission? admission) =>
        CpmFileLocator.InspectCandidateImage(path, format, geometry, admission);
}
