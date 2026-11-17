using System.Globalization;
using System.Security.Cryptography;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

internal static class CpmFileLocator
{
    public sealed record LocateMatch(
        string ImagePath,
        string FullPath,
        int User,
        string FileName,
        int StoredBytes,
        int Records,
        DateTime LastWriteUtc,
        long FileLength)
    {
        public override string ToString() => $"{ImagePath} · user {User} · {FileName} · {StoredBytes:N0} bytes";
    }

    public sealed record IncompleteImage(string ImagePath, string Reason)
    {
        public override string ToString() => $"[Skipped] {ImagePath}: {Reason}";
    }

    public sealed record SearchResult(
        IReadOnlyList<LocateMatch> Matches,
        IReadOnlyList<IncompleteImage> Incompletes,
        int ImagesChecked,
        bool Cancelled);

    public static void ValidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Filename pattern cannot be empty.");

        if (pattern.Any(c => c is <= ' ' or > '~' || "<>/\\:\"|,".Contains(c)))
            throw new ArgumentException($"Filename pattern '{pattern}' contains invalid characters; CP/M filenames follow 8.3 naming.");

        if (pattern.Count(c => c == '.') > 1)
            throw new ArgumentException($"Filename pattern '{pattern}' contains multiple dots; CP/M filenames follow 8.3 naming.");

        int dot = pattern.IndexOf('.');
        string stem = dot < 0 ? pattern : pattern[..dot];
        string ext = dot < 0 ? "" : pattern[(dot + 1)..];

        if (stem.Length == 0)
            throw new ArgumentException("Filename pattern stem cannot be empty.");

        if (!stem.Contains('*') && stem.Length > 8)
            throw new ArgumentException($"Filename pattern stem '{stem}' exceeds 8 characters; CP/M stems are at most 8 characters.");

        if (!ext.Contains('*') && ext.Length > 3)
            throw new ArgumentException($"Filename pattern extension '{ext}' exceeds 3 characters; CP/M extensions are at most 3 characters.");
    }

    public static bool MatchesPattern(string pattern, string cpmFileName)
    {
        pattern = pattern.ToUpperInvariant();
        cpmFileName = cpmFileName.ToUpperInvariant();

        if (pattern is "*" or "*.*")
            return true;

        int fileDot = cpmFileName.IndexOf('.');
        string fileStem = fileDot < 0 ? cpmFileName : cpmFileName[..fileDot];
        string fileExt = fileDot < 0 ? "" : cpmFileName[(fileDot + 1)..];

        if (pattern.Contains('.'))
        {
            int pDot = pattern.IndexOf('.');
            string pStem = pattern[..pDot];
            string pExt = pattern[(pDot + 1)..];

            return MatchField(pStem, fileStem) && MatchField(pExt, fileExt);
        }
        else
        {
            if (pattern.EndsWith('*'))
            {
                return MatchField(pattern, fileStem);
            }
            return MatchField(pattern, fileStem) && fileExt.Length == 0;
        }
    }

    public static bool MatchField(string pattern, string text)
    {
        if (pattern == "*") return true;
        int p = 0, t = 0, starIdx = -1, match = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                match = t;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                match++;
                t = match;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    public static (CpmFs? Fs, string? IssueReason) InspectCandidateImage(
        string path,
        string format = "auto",
        DiskGeometry? geometry = null,
        NativeImageAdmission? admission = null)
    {
        try
        {
            if (!File.Exists(path))
                return (null, "image not found: " + path);

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
                    return (null, "image is a multi-volume container; multi-volume containers requiring volume selection are unsupported.");
            }

            if (length > 16 * 1024 * 1024)
                return (null, "image exceeds supported floppy size.");

            var bytes = new byte[checked((int)length)];
            source.Position = 0;
            source.ReadExactly(bytes);
            if (source.Length != bytes.Length || source.ReadByte() != -1)
                return (null, "image changed while reading.");
            if (facts is not null) admission!.ValidateSourceStillCurrent(source, facts);

            var disk = Dsk.Load(bytes, geometry);
            var uniform = disk.UniformGeometry();
            var candidates = Dpb.Presets().Where(p => p.Geometry == uniform &&
                (format == "auto" ? !p.HcHeader : (format == p.Name || format == (p.HcHeader ? "hcfs" : "cpm-hc"))))
                .Select(p => CpmFs.InspectStructure(disk, p)).ToArray();

            if (candidates.Length == 0)
            {
                if (Dpb.Presets().Any(p => p.Geometry == uniform && p.HcHeader))
                    return (null, "image is an HCFS native disk, not a CP/M filesystem.");
                return (null, "No supported format matches the image geometry and selection.");
            }

            var healthy = candidates.Where(c => c.Issues.Count == 0).ToArray();
            if (healthy.Length > 1 || (healthy.Length == 0 && candidates.Length > 1))
                return (null, "Ambiguous filesystem formats: " + string.Join(", ", candidates.Select(c => c.Parameters.Name)) + "; select --format.");

            var chosenReport = healthy.Length == 1 ? healthy[0] : candidates[0];

            if (chosenReport.Issues.Count > 0)
                return (null, $"image is structurally damaged: {chosenReport.Issues[0].Detail}");

            return (new CpmFs(disk, chosenReport.Parameters), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or InvalidOperationException or AdmissionException)
        {
            return (null, ex.Message);
        }
    }

    public static bool PathExistsOrLink(string path)
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
        return false;
    }

    public static SearchResult SearchFolder(
        string folder,
        string pattern,
        int? userFilter = null,
        bool recursive = false,
        string format = "auto",
        DiskGeometry? geometry = null,
        NativeImageAdmission? admission = null,
        CancellationToken cancellationToken = default,
        Action<string>? onCheckingImage = null)
    {
        ValidatePattern(pattern);

        var fullFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(fullFolder))
        {
            if (File.Exists(fullFolder) || PathExistsOrLink(fullFolder))
                throw new ArgumentException($"Input folder '{folder}' is not a directory.");
            throw new DirectoryNotFoundException($"Input folder does not exist: '{folder}'.");
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
            throw new IOException($"unreadable folder '{folder}': {ex.Message}", ex);
        }

        var matches = new List<LocateMatch>();
        var incompletes = new List<IncompleteImage>();
        int checkedCount = 0;

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SearchResult(matches, incompletes, checkedCount, Cancelled: true);
            }

            var relPath = Path.GetRelativePath(fullFolder, file).Replace('\\', '/');
            onCheckingImage?.Invoke(relPath);
            checkedCount++;

            var (cpmFs, issueReason) = InspectCandidateImage(file, format, geometry, admission);
            if (issueReason is not null)
            {
                incompletes.Add(new IncompleteImage(relPath, issueReason));
            }
            else if (cpmFs is not null)
            {
                DateTime lastWrite = DateTime.MinValue;
                long fileLength = 0;
                try
                {
                    var fi = new FileInfo(file);
                    lastWrite = fi.LastWriteTimeUtc;
                    fileLength = fi.Length;
                }
                catch { }

                foreach (var cpmFile in cpmFs.List())
                {
                    if (userFilter.HasValue && cpmFile.User != userFilter.Value)
                        continue;

                    if (MatchesPattern(pattern, cpmFile.Name))
                    {
                        matches.Add(new LocateMatch(
                            ImagePath: relPath,
                            FullPath: file,
                            User: cpmFile.User,
                            FileName: cpmFile.Name,
                            StoredBytes: cpmFile.Records * 128,
                            Records: cpmFile.Records,
                            LastWriteUtc: lastWrite,
                            FileLength: fileLength));
                    }
                }
            }
        }

        matches.Sort((a, b) =>
        {
            int c = StringComparer.Ordinal.Compare(a.ImagePath, b.ImagePath);
            if (c != 0) return c;
            c = a.User.CompareTo(b.User);
            if (c != 0) return c;
            return StringComparer.Ordinal.Compare(a.FileName, b.FileName);
        });

        incompletes.Sort((a, b) => StringComparer.Ordinal.Compare(a.ImagePath, b.ImagePath));

        return new SearchResult(matches, incompletes, checkedCount, Cancelled: false);
    }
}
