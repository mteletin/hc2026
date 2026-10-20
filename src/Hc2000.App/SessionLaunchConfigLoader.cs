using System.Text;
using System.Text.Json;

namespace Hc2000.App;

/// <summary>Read-only, strict session-file loader. It never mounts, writes a file or changes preferences.
/// A caller must process --help before loading; help=true also guarantees no filesystem access here.</summary>
public sealed class SessionLaunchConfigLoader(Func<string, byte[]>? read = null,
    Func<IReadOnlyList<string>>? configRoots = null)
{
    private readonly Func<string, byte[]> readFile = read ?? File.ReadAllBytes;
    private readonly Func<IReadOnlyList<string>> roots = configRoots ?? InstalledLayout.ConfigDirectories;

    public SessionLoad Load(string? explicitPath = null, bool help = false)
    {
        if (help) return new(SessionLoadKind.Absent);
        string? path = null;
        try
        {
            if (explicitPath is not null)
            {
                if (string.IsNullOrWhiteSpace(explicitPath)) return Refuse("Empty explicit session path.");
                path = Path.GetFullPath(explicitPath);
            }
            else
            {
                var root = roots().FirstOrDefault();
                if (root is null) return new(SessionLoadKind.Absent);
                path = Path.GetFullPath(Path.Combine(root, "session.json"));
            }
            return Parse(readFile(path), path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return explicitPath is null ? new(SessionLoadKind.Absent)
                : Refuse("Explicit session file not found: " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return Refuse("Session read refused: " + ex.Message);
        }
    }

    /// <summary>Exact nested version-1 schema. No disk stat, source mutation or partial result.</summary>
    public static SessionLoad Parse(ReadOnlyMemory<byte> utf8Json, string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object, "Session must be an object.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            string? diskA = null, diskB = null;
            var writeback = false;
            foreach (var property in root.EnumerateObject())
            {
                Require(keys.Add(property.Name), "Duplicate session key: " + property.Name);
                switch (property.Name)
                {
                    case "version":
                        Require(property.Value.ValueKind == JsonValueKind.Number
                            && property.Value.GetRawText() == "1", "Session version must be integer 1.");
                        break;
                    case "writeback":
                        Require(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                            "Writeback must be a boolean.");
                        writeback = property.Value.GetBoolean();
                        break;
                    case "disks":
                        Require(property.Value.ValueKind == JsonValueKind.Object, "Disks must be an object.");
                        var drives = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var drive in property.Value.EnumerateObject())
                        {
                            Require(drives.Add(drive.Name), "Duplicate drive: " + drive.Name);
                            Require(drive.Name is "A" or "B", "Unknown drive: " + drive.Name);
                            Require(drive.Value.ValueKind == JsonValueKind.String, "Disk path must be a string.");
                            var value = drive.Value.GetString()!;
                            Require(!string.IsNullOrWhiteSpace(value) && value.EnumerateRunes().Count() <= 4096
                                && !value.Any(c => c < 0x20), "Invalid disk path.");
                            if (drive.Name == "A") diskA = value; else diskB = value;
                        }
                        break;
                    default:
                        throw new JsonException("Unknown session key: " + property.Name);
                }
            }
            Require(keys.SetEquals(["version", "disks", "writeback"]), "Missing required session key.");
            return new(SessionLoadKind.Loaded, new(1, diskA, diskB, writeback), Path.GetFullPath(configPath));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException
            or NotSupportedException or IOException)
        {
            return Refuse("Session application refused: " + ex.Message);
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new JsonException(reason);
    }
    private static SessionLoad Refuse(string reason) => new(SessionLoadKind.Refused, Refusal: new(reason));
}
