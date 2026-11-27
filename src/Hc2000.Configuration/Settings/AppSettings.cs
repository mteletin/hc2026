using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hc2000.App.Settings;

/// <summary>Preferences only; applying them to the machine is owned by the runtime slices.</summary>
public sealed record AppSettings
{
    public string Theme { get; init; } = "Dark";
    public string Profile { get; init; } = "hc2000-v2";
    // hc2000-accelerated-clock-domains: the user's main-Z80 clock choice (owner decision #317).
    // "auto" (the default since hc2000-speed-140-turbo-280: 140 MHz on the machines that start CP/M, faithful on Classic
    // and the BASIC expansion machines),
    // "faithful", or a 7/14/28/140/280 MHz target, as MainCpuSpeedInfo.Setting strings; validated below. A distinct
    // dimension from the legacy cpu.turbo multiplier: the two are never combined. Stored via the shadow JSON property,
    // omitted while auto, exactly like the machine drafts.
    [JsonIgnore]
    public string MainCpuTarget { get; init; } = Hc2000.Machine.MainCpuSpeedInfo.Auto;

    /// <summary>JSON shadow for <see cref="MainCpuTarget"/>: omitted while auto, so files without the key (old ones
    /// included, which left faithful out) read as auto; an explicit faithful is written and kept
    /// (hc2000-speed-140-turbo-280).</summary>
    [JsonPropertyName("mainCpuTarget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MainCpuTargetJson
    {
        get => Hc2000.Machine.MainCpuSpeedInfo.IsAuto(MainCpuTarget) ? null : MainCpuTarget;
        init { if (value is { } target) MainCpuTarget = target; }
    }
    public string RomSet { get; init; } = "roms/hc2000/eprom-dump-hc2k-2";
    // 0 means unlimited, not the profile's cpu.turbo upgrade flag.
    public int SpeedPercent { get; init; } = 100;
    // 0 = Fit (largest fitting integer); positive values are explicit caps.
    public int Scale { get; init; } = 0;
    // Screen border around the 256x192 picture: Thin (8px default, 272x208), None (256x192) or Full (352x296).
    public string Border { get; init; } = "Thin";
    // Backend-specific values remain opaque until the audio ADR is implemented.
    public string AudioPreference { get; init; } = "default";
    public Dictionary<string, string> KeyMap { get; init; } = new();
    // Media homes: user directories scanned for disk/tape images and snapshots (contract section 1).
    // Absent (null) by default and omitted from settings.json until set. Null or a non-empty
    // string are the only valid values; this layer never touches the filesystem. Missing,
    // relative or non-directory values fail closed at scan time, owned by the scan slice.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiskHome { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TapeHome { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SnapshotHome { get; init; }
    // Sound-dump route (hc2000-app-sound-dump-route): the requested WAV path. Shape-only here
    // (absent or non-empty); the root fence (user home only) is owned by Audio.SoundDumpGate and
    // runs once on the dump path, identically for the CLI flag and this setting.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoundDump { get; init; }
    // hc2000-ssh-terminal: the default SSH connection CP/M's SSH uses with no arguments, user@host[:port][,key:name];
    // never a password. Absent until set or remembered.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SshDefault { get; init; }

    public Configuration.WorkspaceSettings? Workspace { get; init; }

    public void Validate()
    {
        if (Theme is not ("Dark" or "Light" or "System"))
            throw new ArgumentException("Theme must be Dark, Light or System.");
        if (string.IsNullOrWhiteSpace(Profile) || string.IsNullOrWhiteSpace(RomSet)
            || string.IsNullOrWhiteSpace(AudioPreference))
            throw new ArgumentException("Profile, ROM set and audio preference must not be empty.");
        Hc2000.Machine.MainCpuSpeedInfo.ValidateSetting(MainCpuTarget); // refuses an unadmitted main CPU clock choice.
        if (SpeedPercent is not (0 or 100 or 200 or 400))
            throw new ArgumentException("Speed must be 100, 200, 400 or 0 (unlimited).");
        if (Scale < 0)
            throw new ArgumentException("Scale must be zero (Fit) or a positive integer.");
        if (Border is not ("Thin" or "None" or "Full"))
            throw new ArgumentException("Border must be Thin, None or Full.");
        if (KeyMap is null || KeyMap.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new ArgumentException("Key mapping must contain non-empty key/value strings.");
        if (DiskHome is not null && DiskHome.Length == 0)
            throw new ArgumentException("diskHome must be absent or a non-empty string.");
        if (TapeHome is not null && TapeHome.Length == 0)
            throw new ArgumentException("tapeHome must be absent or a non-empty string.");
        if (SnapshotHome is not null && SnapshotHome.Length == 0)
            throw new ArgumentException("snapshotHome must be absent or a non-empty string.");
        if (SoundDump is not null && SoundDump.Length == 0)
            throw new ArgumentException("soundDump must be absent or a non-empty string.");
        if (SshDefault is not null && (SshDefault.Length is 0 or > 200 || SshDefault.Contains('\n') || !SshDefault.Contains('@')))
            throw new ArgumentException("sshDefault must be absent or user@host[:port][,key:name].");
        Workspace?.Validate();
        if (Workspace is not null && System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(Workspace, JsonOptions)) > 1024 * 1024)
            throw new ArgumentException("Workspace exceeds 1 MiB UTF-8.");
    }

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
