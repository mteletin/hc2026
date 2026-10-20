using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hc2000.App.Settings;

/// <summary>Preferences only; applying them to the machine is owned by the runtime slices.</summary>
public sealed record AppSettings
{
    public string Profile { get; init; } = "hc2000-v2";
    public string RomSet { get; init; } = "roms/hc2000/eprom-dump-hc2k-2";
    // 0 means unlimited, not the profile's cpu.turbo upgrade flag.
    public int SpeedPercent { get; init; } = 100;
    public int Scale { get; init; } = 2;
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

    public Configuration.WorkspaceSettings? Workspace { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Profile) || string.IsNullOrWhiteSpace(RomSet)
            || string.IsNullOrWhiteSpace(AudioPreference))
            throw new ArgumentException("Profile, ROM set and audio preference must not be empty.");
        if (SpeedPercent is not (0 or 100 or 200 or 400))
            throw new ArgumentException("Speed must be 100, 200, 400 or 0 (unlimited).");
        if (Scale < 1)
            throw new ArgumentException("Scale must be a positive integer.");
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
