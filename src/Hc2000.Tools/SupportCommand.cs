using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using Hc2000.App;
using Hc2000.App.Settings;

namespace Hc2000.Tools;

/// <summary>
/// Local, privacy-safe support report for an installed build: the release version, the source
/// identity when available, OS/runtime facts and configuration validation results. It reads the
/// configuration files and its own package metadata, never media, guest memory, credentials,
/// environment values or home/media paths, never boots or changes the machine, and makes no
/// network requests. The report is a local file the user shares manually; nothing is uploaded.
/// Exits 0 (complete), 2 (partial: a configuration file is missing or invalid) or 1 (failed
/// generation or usage).
/// </summary>
public static class SupportCommand
{
    private const string Unknown = "unknown";

    private const string Usage = """
        usage: hc support report --output FILE [--config-dir DIRECTORY]
          Writes a local, privacy-safe support report to FILE (never overwritten).

        What the report includes
          installed release version, source commit when available, operating system,
          CPU architecture, .NET runtime, and configuration validation results
          (settings and session). Unavailable fields are marked "unknown".

        What it excludes
          media contents, guest memory, credentials, environment values, and
          home/media paths. Generation does not boot or change the machine,
          configuration or media, and makes no network requests.

        Sharing
          The report is a plain local file; attach or paste it to the maintainer
          manually. Nothing is uploaded automatically.

        Exit status: 0 complete, 2 partial (configuration missing or invalid),
        1 failed generation or usage.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error,
        Func<IReadOnlyList<string>>? configRoots = null, string? identityBase = null)
    {
        if (args is ["--help"] or ["report", "--help"])
        {
            output.WriteLine(Usage);
            return 0;
        }
        string? outputPath = null, configDir = null;
        if (args.Length == 0 || args[0] != "report")
        {
            error.WriteLine(Usage);
            return 1;
        }
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output":
                    if (i + 1 >= args.Length) { error.WriteLine("hc support: --output needs a file path"); return 1; }
                    outputPath = args[++i];
                    break;
                case "--config-dir":
                    if (i + 1 >= args.Length) { error.WriteLine("hc support: --config-dir needs a directory path"); return 1; }
                    configDir = args[++i];
                    break;
                default:
                    error.WriteLine($"hc support: unknown option '{args[i]}'");
                    error.WriteLine(Usage);
                    return 1;
            }
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error.WriteLine("hc support: --output FILE is required");
            error.WriteLine(Usage);
            return 1;
        }

        try
        {
            var roots = configRoots ?? InstalledLayout.ConfigDirectories;
            var directory = ResolveConfigDirectory(roots, configDir);
            var settings = InspectSettings(directory);
            var session = InspectSession(directory);
            var (report, status) = BuildReport(directory, settings, session, identityBase ?? AppContext.BaseDirectory);
            WriteReport(outputPath, report);
            output.WriteLine($"hc support: report written (status: {status})");
            return status == Status.Partial ? 2 : 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or DirectoryNotFoundException)
        {
            error.WriteLine("hc support: generation failed; destination must be a new writable file and configuration paths must be valid.");
            return 1;
        }
    }

    private enum Status { Complete, Partial }

    private static string ResolveConfigDirectory(Func<IReadOnlyList<string>> roots, string? explicitDir)
    {
        if (explicitDir is not null)
        {
            if (string.IsNullOrWhiteSpace(explicitDir) || explicitDir.Contains('\0'))
                throw new ArgumentException("config directory path is invalid.");
            return Path.GetFullPath(explicitDir);
        }
        var candidates = roots().ToArray();
        if (candidates.Length == 0) throw new ArgumentException("no configuration directory is available.");
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    private record SettingsResult(string Status, string? Detail = null, int? Machines = null);

    private static SettingsResult InspectSettings(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return new("missing");
        try
        {
            var loaded = new SettingsStore(directory).Load();
            return loaded.Warning is null
                ? new("ok", Machines: loaded.Settings.Workspace?.Machines.Length ?? 1)
                : new("invalid or unreadable", Detail: "settings.json could not be loaded or failed configuration validation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return new("unreadable");
        }
    }

    private record SessionResult(string Status, string? Detail = null);

    private static SessionResult InspectSession(string directory)
    {
        // The installed session loader resolves session.json inside each config root; point it
        // at the single directory the report inspected. The read-only loader is the same
        // diagnostics the App uses at launch.
        var loader = new SessionLaunchConfigLoader(
            configRoots: () => new[] { directory });
        var loaded = loader.Load();
        return loaded.Kind switch
        {
            SessionLoadKind.Absent => new("missing"),
            SessionLoadKind.Loaded => new("ok"),
            SessionLoadKind.Refused when loaded.Refusal?.Reason.StartsWith("Session read refused:") == true
                => new("unreadable"),
            _ => new("invalid", Detail: "session.json failed schema validation."),
        };
    }

    private static (string Report, Status Status) BuildReport(string directory, SettingsResult settings,
        SessionResult session, string identityBase)
    {
        var partial = settings.Status != "ok"
            || session.Status is "invalid" or "unreadable";
        var lines = new List<string>
        {
            "HC2000 support report",
            $"generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}",
            $"status: {(partial ? "partial" : "complete")}",
            $"release-version: {VersionText()}",
            $"source-identity: {SourceIdentity(identityBase)}",
            $"operating-system: {OperatingSystemText()}",
            $"architecture: {RuntimeInformation.ProcessArchitecture}",
            $"dotnet-runtime: {Environment.Version}",
            $"settings: {settings.Status}"
        };
        if (settings.Detail is not null) lines.Add($"settings-detail: {settings.Detail}");
        if (settings.Machines is int machines) lines.Add($"machines: {machines}");
        lines.Add($"session: {session.Status}");
        if (session.Detail is not null) lines.Add($"session-detail: {session.Detail}");
        return (string.Join('\n', lines) + '\n', partial ? Status.Partial : Status.Complete);
    }

    private static string VersionText()
    {
        var version = Program.VersionString();
        return version == "0.0.0" ? Unknown : version;
    }

    private static string OperatingSystemText()
    {
        try
        {
            return OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux"
                : OperatingSystem.IsMacOS() ? "macOS" : Unknown;
        }
        catch (PlatformNotSupportedException)
        {
            return Unknown;
        }
    }

    /// <summary>The 40-hex source commit from the installed package's source-commit file,
    /// else the commit stamped into the assembly's informational version, else unknown.</summary>
    private static string SourceIdentity(string identityBase)
    {
        try
        {
            var path = Path.Combine(identityBase, "source-commit");
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                if (IsSha(value)) return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // Fall through to the stamped version.
        }
        var informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            var suffix = informational.Split('+').Last();
            if (IsSha(suffix)) return suffix;
        }
        return Unknown;
    }

    private static bool IsSha(string value)
    {
        if (value.Length != 40) return false;
        return value.All(Uri.IsHexDigit);
    }

    private static void WriteReport(string path, string report)
    {
        if (path.Contains('\0'))
            throw new ArgumentException("report path is invalid.");
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) throw new ArgumentException("report path is a directory; choose a file name.");
        if (File.Exists(full))
            throw new ArgumentException("report file already exists; refusing to overwrite it. Choose a new name.");
        try
        {
            using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            writer.Write(report);
        }
        catch (IOException)
        {
            throw new IOException("report destination is not writable; nothing was written.");
        }
    }
}
