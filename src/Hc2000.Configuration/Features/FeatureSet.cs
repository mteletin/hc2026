using System.Text.Json;

namespace Hc2000.Features;

/// <summary>The flags this installed release ships on, read once at startup from hc2000-features.json beside the
/// executable. A feature is available when it is declared, compiled in and listed on. A missing or bad manifest turns
/// every flagged feature off, with <see cref="Diagnostic"/> saying why; nothing else of the program depends on it.</summary>
public sealed class FeatureSet
{
    public const string FileName = "hc2000-features.json";

    private static readonly Lazy<FeatureSet> Installed = new(() => Load(AppContext.BaseDirectory));
    private readonly HashSet<string> on;

    private FeatureSet(IEnumerable<string> flagsOn, string? diagnostic)
    {
        on = new(flagsOn, StringComparer.Ordinal);
        Diagnostic = diagnostic;
    }

    /// <summary>The installed release's flags: the manifest beside this executable, and no other place.</summary>
    public static FeatureSet Current => Installed.Value;

    /// <summary>Why no flag is on although a manifest was expected; null when the manifest was read.</summary>
    public string? Diagnostic { get; }

    /// <summary>The slugs shipped on, in ordinal order.</summary>
    public IReadOnlyList<string> FlagsOn => on.Order(StringComparer.Ordinal).ToArray();

    public bool IsAvailable(FeatureId feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return feature.Compiled && on.Contains(feature.Slug);
    }

    /// <summary>Reads the manifest in <paramref name="baseDirectory"/>.</summary>
    public static FeatureSet Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, FileName);
        if (!File.Exists(path)) return Off($"{FileName} is missing");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number)
                return Off($"{FileName} has no schemaVersion");
            if (!version.TryGetInt32(out var schema) || schema != 1) return Off($"{FileName} has schemaVersion {version.GetRawText()}, and this release reads 1");
            if (!root.TryGetProperty("flagsOn", out var flags) || flags.ValueKind != JsonValueKind.Array) return Off($"{FileName} has no flagsOn list");
            var slugs = new List<string>();
            foreach (var item in flags.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return Off($"{FileName} lists something that is not a flag name");
                var slug = item.GetString()!;
                if (slugs.Contains(slug, StringComparer.Ordinal)) return Off($"{FileName} lists {slug} twice");
                var declared = Features.Declared.FirstOrDefault(feature => feature.Slug == slug);
                if (declared is null) return Off($"{FileName} lists {slug}, which this release does not know");
                if (!declared.Compiled) return Off($"{FileName} lists {slug}, which is not built into this release");
                slugs.Add(slug);
            }
            return new(slugs, null);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return Off($"{FileName} cannot be read ({error.Message})");
        }
    }

    private static FeatureSet Off(string why) => new([], why + ": the flagged features are off");
}
