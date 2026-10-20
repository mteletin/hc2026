using System.Globalization;
using System.Text.Json;

namespace Hc2000.App.Settings;

/// <summary>Detached draft: invalid edits or Cancel never change the saved preferences.</summary>
public sealed class SettingsEditor
{
    public string Profile { get; set; }
    public string RomSet { get; set; }
    public string SpeedPercent { get; set; }
    public string Scale { get; set; }
    public string AudioPreference { get; set; }
    public string KeyMapJson { get; set; }
    private readonly Configuration.WorkspaceSettings? workspace;

    public SettingsEditor(AppSettings settings)
    {
        workspace = settings.Workspace;
        Profile = settings.Profile;
        RomSet = settings.RomSet;
        SpeedPercent = settings.SpeedPercent.ToString(CultureInfo.InvariantCulture);
        Scale = settings.Scale.ToString(CultureInfo.InvariantCulture);
        AudioPreference = settings.AudioPreference;
        KeyMapJson = JsonSerializer.Serialize(settings.KeyMap, AppSettings.JsonOptions);
    }

    public AppSettings Build()
    {
        if (!int.TryParse(SpeedPercent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var speed)
            || !int.TryParse(Scale, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale))
            throw new ArgumentException("Speed and scale must be integers.");
        var settings = new AppSettings
        {
            Workspace = workspace,
            Profile = Profile.Trim(),
            RomSet = RomSet.Trim(),
            SpeedPercent = speed,
            Scale = scale,
            AudioPreference = AudioPreference.Trim(),
            KeyMap = JsonSerializer.Deserialize<Dictionary<string, string>>(KeyMapJson)
                ?? throw new ArgumentException("Key mapping must be a JSON object.")
        };
        settings.Validate();
        return settings;
    }
}
