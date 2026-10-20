using System.Text.Json;

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
