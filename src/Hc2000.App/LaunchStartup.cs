using System.Collections.Immutable;
using Hc2000.App.Media;
using Hc2000.Machine.Capabilities;
using Hc2000.Machine.Tape;

namespace Hc2000.App;

/// <summary>Consumes a parsed <see cref="LaunchOptions"/> at startup. Disks go through the MediaBridge
/// after the worker is composed — never an auto-mount on the FDC. ROM loading is not this type's job.</summary>
internal static class LaunchStartup
{
    public static readonly string Usage = "Usage: hc2000 [--disk A:<path>] [--disk B:<path>] [--start] [--workstation [path]] [hardware options] [--writeback|--no-writeback] [--session <path>] [--tape <path>] [--tape-mount <name>]... [--tape-commit|--tape-discard] [--network-gateway] [--ssh-default <user@host[:port][,key:name]>] [--help]\nHardware (cold): "
        + string.Join(' ', CapabilityCatalog.SurfaceRows.Select(row => "[--" + row.Name
            + (row.TakesValue ? " <" + string.Join('|', row.AdmittedValues) + ">" : "") + "]"))
        + "\n--hdd: opt-in cold NEW-machine image preparation; original BIOS, no live HDD switching.\n--workstation [path]: opt-in combined-workstation A: preparation; from the named base disk, or the shipped unbound-template base media when no path is given, the bound system disk is composed on the machine's media lane against its mounted live tape volume and staged into A:. It needs a mounted tape volume; it writes no file and reports refusals with the lane's wording.\n--start boots the adopted saved draft (a recorded named-HDD C:–F: profile included) through the same configured-start path the Start button takes, without a Start press; on such a draft the launch --disk A:/B: are inserted through the host lane after the acknowledged start.\n--writeback writes a command-line or session disk back to its own file. Default off. --help reads no session file.\n--tape loads a cassette into the deck before the tape actions; --tape-mount (repeatable, in order) mounts a volume as a CP/M drive; --tape-commit unmounts committing changed data; --tape-discard unmounts discarding it. Refusals carry the same wording as the App tape actions and stop the sequence.\n--network-gateway switches the X6 network gateway on as the machine starts; --ssh-default sets, for this run, the connection CP/M's SSH makes with no arguments (a key name logs in with the App's key, otherwise SSH asks for the password).";

    public static int? EarlyExit(LaunchOptionsResult parsed, TextWriter stdout, TextWriter stderr, Hc2000.Features.FeatureSet? features = null)
    {
        if (parsed.Options?.Help == true)
        {
            stdout.WriteLine(Usage);
            // A flagged feature is listed only in a release that ships it on.
            if (features is not null && Hc2000.FeatureSample.FeatureSampleGate.Available(features))
                stdout.WriteLine(Hc2000.FeatureSample.FeatureSampleGate.AppOption + " prints the feature-flag sample's line and exits.");
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

    public static bool HasTape(LaunchOptions? options) =>
        options is not null && (options.Tape is not null || !options.TapeMounts.IsDefaultOrEmpty || options.TapeCommit || options.TapeDiscard);

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

    /// <summary>Tape volume launch actions (hc2000-tape-mount-cli). A <c>--tape</c> cassette is loaded
    /// into the deck first, then each <c>--tape-mount</c> name in order, then <c>--tape-commit</c>
    /// (unmount committing changed data) or <c>--tape-discard</c>. Every action goes through
    /// <paramref name="send"/> (the panel's Send, which enqueues the SAME command on the MediaBridge the
    /// App buttons use and throws the lane's exact refusal) — one shared validation path, so a mount of a
    /// known volume reaches the same bound session state as the App action and an over-4 MiB or
    /// already-mounted volume is refused with the same wording. The first refusal is returned as the
    /// status line and stops the sequence; the machine still boots either way (the disk pattern). A
    /// missing <c>--tape</c> path is a status line and is not enqueued.</summary>
    public static async Task<string?> ApplyTapeLaunchAsync(LaunchOptions? options, Func<MediaCommand, Task> send)
    {
        if (options is null) return null;
        if (options.Tape is not null)
        {
            var raw = options.Tape;
            if (!TryResolveDisk(raw, InstalledLayout.ConfigDirectories(), AppContext.BaseDirectory, out var resolved, out var checkedPaths))
                return checkedPaths.Count == 0
                    ? $"Tape: not found: {raw}"
                    : $"Tape: not found: {raw}. Checked: {string.Join("; ", checkedPaths)}";
            try
            {
                var image = await TapeFiles.ReadAsync(resolved, TapeCodecRegistry.Installed(),
                    new TapeImportOptions(TapeFiles.FormatOf(resolved), TapeSide.A, TapeCapacity.C60)
                    { Channel = TapeChannel.Left }).ConfigureAwait(false);
                await send(new MediaCommand(MediaAction.InsertCassette, Name: Path.GetFileName(resolved), Cassette: image)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return $"Tape: {ex.GetBaseException().Message}";
            }
        }
        foreach (var name in options.TapeMounts.IsDefault ? ImmutableArray<string>.Empty : options.TapeMounts)
        {
            try
            {
                await send(new MediaCommand(MediaAction.MountTapeVolume, Name: name)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ex.GetBaseException().Message;
            }
        }
        if (options.TapeCommit || options.TapeDiscard)
        {
            var action = options.TapeCommit ? MediaAction.UnmountTapeVolume : MediaAction.DiscardTapeVolume;
            try
            {
                await send(new MediaCommand(action)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ex.GetBaseException().Message;
            }
        }
        return null;
    }

    /// <summary>Workstation launch preparation (hc2000-combined-cpm-workstation): the opt-in
    /// --workstation stages the composed A: system disk. The base media is the named path, or the
    /// shipped unbound-template base media when the flag is bare; the bind runs on the
    /// machine-owner media lane (PrepareWorkstationDisk) against that machine's mounted live tape
    /// session, and the session-specific image is inserted into A: through the ordinary
    /// disk-insertion path. A not-found file, A: already staged by --disk A:, the tape volume being
    /// unmounted by --tape-commit/--tape-discard, a missing live session, or a base that is not the
    /// unbound template come back as the lane's own status line and stop the sequence; no file is
    /// written and the machine boots either way (the disk pattern).</summary>
    public static async Task<string?> PrepareWorkstationAsync(LaunchOptions? options, Func<MediaCommand, Task<MediaReply>> send)
    {
        if (options?.Workstation is null) return null;
        if (options.DiskA is not null)
            return "Workstation: A: is already staged by --disk A:; drop one of them.";
        if (options.TapeCommit || options.TapeDiscard)
            return "Workstation: the tape volume is unmounted by --tape-commit/--tape-discard before the preparation; drop one of them.";
        var name = string.IsNullOrEmpty(options.Workstation) ? Hc2000.Machine.Tape.WorkstationMedia.BaseDiskName : options.Workstation!;
        if (!TryResolveDisk(name, InstalledLayout.ConfigDirectories(), AppContext.BaseDirectory, out var resolved, out var checkedPaths))
            return checkedPaths.Count == 0
                ? $"Workstation: not found: {name}"
                : $"Workstation: not found: {name}. Checked: {string.Join("; ", checkedPaths)}";
        byte[] baseBytes;
        try
        {
            baseBytes = await File.ReadAllBytesAsync(resolved).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return "Workstation: " + ex.GetBaseException().Message;
        }
        try
        {
            var reply = await send(new MediaCommand(MediaAction.PrepareWorkstationDisk, Bytes: ImmutableArray.CreateRange(baseBytes))).ConfigureAwait(false);
            if (reply.Error is not null) return "Workstation: " + reply.Error;
        }
        catch (Exception ex)
        {
            return "Workstation: " + ex.GetBaseException().Message;
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
