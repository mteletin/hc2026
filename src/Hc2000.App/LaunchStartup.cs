using Hc2000.App.Media;

namespace Hc2000.App;

/// <summary>Consumes a parsed <see cref="LaunchOptions"/> at startup. Disks go through the MediaBridge
/// after the worker is composed — never an auto-mount on the FDC. ROM loading is not this type's job.</summary>
internal static class LaunchStartup
{
    public const string Usage = "Usage: hc2000 [--disk A:<path>] [--disk B:<path>] [--start] [--hdd] [--writeback|--no-writeback] [--session <path>] [--help]\n--hdd: opt-in cold NEW-machine image preparation; original BIOS, no live HDD switching.\n--writeback writes a command-line or session disk back to its own file. Default off. --help reads no session file.";

    public static int? EarlyExit(LaunchOptionsResult parsed, TextWriter stdout, TextWriter stderr)
    {
        if (parsed.Options?.Help == true)
        {
            stdout.WriteLine(Usage);
            return 0;
        }
        if (!parsed.Ok)
        {
            stderr.WriteLine(parsed.StatusLine);
            stderr.WriteLine(Usage);
            return 1;
        }
        return null;
    }

    /// <summary>Help must already have exited. A missing default session file is absent, not an error.
    /// A refused session file is a status line and is not mounted.</summary>
    public static LaunchOptionsResult ApplySession(LaunchOptions options, SessionLaunchConfigLoader? loader = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        loader ??= new SessionLaunchConfigLoader();
        var loaded = loader.Load(options.Session, options.Help);
        var resolved = SessionLaunchConfig.Resolve(loaded, options.DiskA, options.DiskB, options.Writeback, options.Help);
        if (resolved.Refusal is { } refusal) return LaunchOptionsResult.Fail(refusal.Reason);
        var launch = resolved.Launch ?? new(null, null, false);
        return new(true, options with { DiskA = launch.DiskA, DiskB = launch.DiskB, Writeback = launch.Writeback }, null);
    }

    public static bool HasDisk(LaunchOptions? options) =>
        options?.DiskA is not null || options?.DiskB is not null;

    /// <summary><c>--start</c> means the session starts without a Start-button press.</summary>
    public static bool OmitsManualStart(LaunchOptions? options) => options?.Start == true;

    /// <summary>Hand each launch disk to <paramref name="insert"/> (the panel's InsertDisk, which enqueues
    /// InsertDisk on the MediaBridge and waits for the reply) before returning. A missing path is a status
    /// line and is not enqueued. No auto-mount. A bare name (<c>A:CPM22-HC.DSK</c>) is resolved per spec v4.2;
    /// a path (separator or rooted) is used as given.</summary>
    public static Task<string?> ApplyDisksAsync(LaunchOptions? options, Func<DiskSlot, string, Task> insert) =>
        ApplyDisksAsync(options, insert, InstalledLayout.ConfigDirectories(), AppContext.BaseDirectory);

    internal static async Task<string?> ApplyDisksAsync(
        LaunchOptions? options,
        Func<DiskSlot, string, Task> insert,
        IReadOnlyList<string> configDirectories,
        string baseDirectory)
    {
        if (!HasDisk(options)) return null;
        string? status = null;
        if (options!.DiskA is not null)
            status = await InsertAsync(insert, options.DiskA, configDirectories, baseDirectory) ?? status;
        if (options.DiskB is not null)
            status = await InsertAsync(insert, options.DiskB, configDirectories, baseDirectory) ?? status;
        return status;
    }

    private static async Task<string?> InsertAsync(
        Func<DiskSlot, string, Task> insert,
        string raw,
        IReadOnlyList<string> configDirectories,
        string baseDirectory)
    {
        var letter = char.ToUpperInvariant(raw[0]);
        var slot = letter == 'B' ? DiskSlot.B : DiskSlot.A;
        var path = raw.Length > 2 ? raw[2..] : "";
        if (!TryResolveDisk(path, configDirectories, baseDirectory, out var resolved, out var checkedPaths))
            return checkedPaths.Count == 0
                ? $"Disk {letter}: not found: {path}"
                : $"Disk {letter}: not found: {path}. Checked: {string.Join("; ", checkedPaths)}";
        try
        {
            await using (var probe = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read))
            { }
            await insert(slot, resolved).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Disk {letter}: {ex.GetBaseException().Message}";
        }
        return null;
    }

    /// <summary>Spec v4.2 disk resolution. An explicit path is used as-is (no search). A bare name
    /// matches, in order: the exact filename, <c>&lt;config&gt;/disks/&lt;name&gt;</c>, then
    /// <c>baseDirectory/disks/&lt;name&gt;</c>.</summary>
    internal static bool TryResolveDisk(
        string path,
        IReadOnlyList<string> configDirectories,
        string baseDirectory,
        out string resolved,
        out IReadOnlyList<string> checkedPaths)
    {
        resolved = "";
        var seen = new List<string>();
        checkedPaths = seen;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsExplicitDiskPath(path))
        {
            if (!File.Exists(path)) return false;
            resolved = path;
            return true;
        }

        seen.Add(path);
        if (File.Exists(path))
        {
            resolved = path;
            return true;
        }

        var name = Path.GetFileName(path);
        foreach (var config in configDirectories)
        {
            if (string.IsNullOrWhiteSpace(config)) continue;
            var candidate = Path.Combine(config, "disks", name);
            seen.Add(candidate);
            if (File.Exists(candidate))
            {
                resolved = candidate;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            var candidate = Path.Combine(baseDirectory, "disks", name);
            seen.Add(candidate);
            if (File.Exists(candidate))
            {
                resolved = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitDiskPath(string path) =>
        Path.IsPathRooted(path) || path.Contains('/') || path.Contains('\\');
}
