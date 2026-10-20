using System.Text.Json;

namespace Hc2000.App.Settings;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning = null);

public sealed class SettingsStore
{
    public string FilePath { get; }

    public SettingsStore(string? configDirectory = null)
    {
        configDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hc2000");
        FilePath = Path.Combine(configDirectory, "settings.json");
    }

    public SettingsLoadResult Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new(new AppSettings());
            var json = File.ReadAllText(FilePath);
            using var document = JsonDocument.Parse(json);
            // Keep the legacy top-level unknown-key policy; nested workspace data is strict.
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var sections = document.RootElement.EnumerateObject().Where(p => p.Name == "workspace").ToArray();
                if (sections.Length > 1) throw new JsonException("Duplicate workspace section.");
                if (sections.Length == 1 && sections[0].Value.ValueKind != JsonValueKind.Null)
                {
                    if (System.Text.Encoding.UTF8.GetByteCount(sections[0].Value.GetRawText()) > 1024 * 1024)
                        throw new JsonException("Workspace exceeds 1 MiB UTF-8.");
                    RejectDuplicates(sections[0].Value);
                }
            }
            var settings = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)
                ?? throw new JsonException("Settings must be a JSON object.");
            settings.Validate();
            return new(settings);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Do not destroy a malformed file on startup. Only an explicit Save replaces it.
            return new(new AppSettings(), $"Could not load settings; using defaults. {exception.Message}");
        }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate workspace key: " + property.Name);
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        var json = JsonSerializer.Serialize(settings, AppSettings.JsonOptions);
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
