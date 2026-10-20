using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hc2000.Disasm;

[Flags]
public enum ExecutionFlags : byte { M1 = 1, Read = 2, Write = 4, Target = 8, Start = 16, Mismatch = 32 }
public sealed record MapWindow(int CpuStart, int ImageOffset, int Length);
public sealed class MapConfiguration
{
    private readonly byte[] flags;
    public string Id { get; }
    public ReadOnlyCollection<MapWindow> Windows { get; }
    internal MapConfiguration(string id, List<MapWindow> windows, byte[] flags)
    { Id = id; Windows = windows.AsReadOnly(); this.flags = flags; }
    public ExecutionFlags FlagsAt(int address) => (ExecutionFlags)flags[address];
}

/// <summary>Strict reader for hc2000-execution-map-v1. No partial map is applied after an error.</summary>
public sealed class ExecutionMap
{
    public int ImageLength { get; private init; }
    public string ImageHash { get; private init; } = "";
    public string ProfileId { get; private init; } = "";
    public string ProfileFingerprint { get; private init; } = "";
    public IReadOnlyList<MapConfiguration> Configurations { get; private init; } = [];
    public IReadOnlyList<DisasmDiagnostic> Diagnostics { get; private init; } = [];
    public bool Success => Diagnostics.Count == 0;

    public static ExecutionMap Parse(byte[] input)
    {
        try
        {
            var binary = input.AsSpan().StartsWith("HCMAPB1\0"u8);
            var payload = 0; var manifestLength = input.Length;
            if (binary)
            {
                if (input.Length < 12) throw new FormatException("Truncated map header");
                var size = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8, 4));
                if (size is 0 or > 1048576 || size > input.Length - 12) throw new FormatException("Invalid manifest size");
                payload = 12 + (int)size; manifestLength = (int)size;
            }
            var text = new UTF8Encoding(false, true).GetString(input, binary ? 12 : 0, manifestLength);
            if (text.StartsWith('\ufeff')) throw new FormatException("Map must not contain a BOM");
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var rows = lines.Select((l, i) => (Tokens: l.Split(';')[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries), Line: i + 1))
                .Where(r => r.Tokens.Length > 0).ToArray();
            var cursor = 0; var line = 0;
            string[] Next(string keyword, int count)
            {
                if (cursor == rows.Length) throw new FormatException($"Missing {keyword}");
                var row = rows[cursor++]; line = row.Line;
                if (row.Tokens.Length != count || row.Tokens[0] != keyword) throw new FormatException($"Expected {keyword} at line {line}");
                return row.Tokens;
            }
            var header = Next("hcmap", 2); if (header[1] != "1") throw new FormatException("Unsupported execution map version");
            var image = Next("image", 3); var length = Number(image[1], 8);
            if (length is < 1 or > 65536 || !Hash(image[2])) throw new FormatException("Invalid map image binding");
            var profile = Next("profile", 3);
            if (!Regex.IsMatch(profile[1], @"^[A-Za-z0-9._-]{1,64}$") || !Hash(profile[2])) throw new FormatException("Invalid map profile binding");
            var configs = new List<MapConfiguration>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            while (cursor < rows.Length)
            {
                var config = Next("config", 2); var id = config[1];
                if (!Regex.IsMatch(id, @"^7e-0[0-9a-f]-cpm-[01]-shadow-[01]$") || !ids.Add(id) || ids.Count > 64) throw new FormatException("Invalid/duplicate configuration ID");
                var windows = new List<MapWindow>(); var flags = new byte[65536]; var covered = new bool[65536];
                while (cursor < rows.Length && rows[cursor].Tokens[0] == "window")
                {
                    var window = Next("window", 4);
                    var start = Number(window[1], 4); var offset = Number(window[2], 8); var size = Number(window[3], 8);
                    if (size <= 0 || (long)start + size > 65536 || (long)offset + size > length) throw new FormatException("Window outside CPU/image bounds");
                    for (var a = start; a < start + size; a++)
                    {
                        if (covered[a]) throw new FormatException("Overlapping CPU windows");
                        covered[a] = true;
                    }
                    windows.Add(new(start, offset, size));
                }
                if (windows.Count == 0) throw new FormatException("Configuration has no windows");
                while (cursor < rows.Length && rows[cursor].Tokens[0] == "mark")
                {
                    if (binary) throw new FormatException("Binary manifest must not contain marks");
                    var mark = Next("mark", 3); var range = mark[1].Split('-');
                    if (range.Length != 2) throw new FormatException("Invalid mark range");
                    var start = Number(range[0], 4); var end = Number(range[1], 4); var mask = Number(mark[2], 2);
                    if (end < start || (mask & 0xc0) != 0) throw new FormatException("Invalid mark range/mask");
                    for (var a = start; a <= end; a++)
                    {
                        if (!covered[a]) throw new FormatException("Mark outside windows");
                        flags[a] |= (byte)mask;
                    }
                }
                Next("end", 1);
                if (binary)
                {
                    if (input.Length - payload < 65536) throw new FormatException("Truncated map bitmap");
                    Array.Copy(input, payload, flags, 0, 65536); payload += 65536;
                }
                for (var a = 0; a < 65536; a++)
                {
                    if ((flags[a] & 0xc0) != 0 || ((flags[a] & 0x10) != 0 && (flags[a] & 1) == 0)) throw new FormatException("Invalid flags (reserved bits or Start without M1)");
                    if (flags[a] != 0 && !covered[a]) throw new FormatException("Nonzero bitmap outside windows");
                }
                configs.Add(new(id, windows, flags));
            }
            if (configs.Count == 0) throw new FormatException("Map has no configurations");
            if (binary && payload != input.Length) throw new FormatException("Trailing map data");
            return new()
            {
                ImageLength = length, ImageHash = image[2].ToLowerInvariant(), ProfileId = profile[1], ProfileFingerprint = profile[2].ToLowerInvariant(),
                Configurations = configs.AsReadOnly()
            };
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        { return new() { Diagnostics = new[] { new DisasmDiagnostic(0, ex.Message) } }; }
    }

    internal MapConfiguration? Select(byte[] image, string? configId, List<DisasmDiagnostic> diagnostics)
    {
        if (!Success) { diagnostics.AddRange(Diagnostics); return null; }
        if (image.Length != ImageLength || Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant() != ImageHash)
        { diagnostics.Add(new(0, "Execution map image length/SHA256 mismatch")); return null; }
        if (configId is null && Configurations.Count == 1) return Configurations[0];
        var config = Configurations.FirstOrDefault(c => c.Id == configId);
        if (config is null) diagnostics.Add(new(0, "Select --map-config from: " + string.Join(", ", Configurations.Select(c => c.Id))));
        return config;
    }

    private static bool Hash(string value) => Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z");
    private static int Number(string value, int width)
    {
        if (value.Length != width || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n) || n > int.MaxValue)
            throw new FormatException("Invalid map hexadecimal field " + value);
        return (int)n;
    }
}
