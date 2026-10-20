using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hc2000.App.Library;

public enum DiskLibraryFormat { StandardDsk, ExtendedDsk }
public enum DiskLibraryContentState { Unknown, DirectoryListed }
public sealed record DiskLibraryProvenance(string Source, string Note);
public sealed record DiskLibraryFileRow(int User, string Name, bool ReadOnly, bool System, int Records, int AllocatedBytes, int DirectoryIndex);

/// <summary>Closed contents block: unknown carries an honest reason and never guessed rows; listed rows are bounded copies of the CP/M directory.</summary>
public sealed record DiskLibraryContents(DiskLibraryContentState State, string? Profile, ImmutableArray<DiskLibraryFileRow> Files, string Reason);
public sealed record DiskLibraryEntry(string Id, string Name, string SourcePath, long Length, string Sha256, string? FileId,
    DiskLibraryFormat Format, DiskLibraryProvenance Provenance, DiskLibraryContents Contents);
public sealed record DiskLibraryCopy(string Id, string EntryId, string RelativePath, string OriginalSha256, string InitialSha256, long Length);
public sealed record DiskLibraryDocument(int Version, ImmutableArray<DiskLibraryEntry> Entries, ImmutableArray<DiskLibraryCopy> Copies);
public sealed record DiskLibrarySearchResult(ImmutableArray<DiskLibraryEntry> Matches, int Remaining);
public sealed class DiskLibrarySchemaException(string message) : Exception(message);

/// <summary>Strict v1 catalog schema: fixed key sets, pinned budgets, no host I/O, no guest reader. Load and Encode round-trip.</summary>
public static class DiskLibrarySchema
{
    public const int Version = 1;
    public const int MaxCatalogBytes = 8 * 1024 * 1024;
    public const int MaxJsonDepth = 8;
    public const int MaxEntries = 1024;
    public const int MaxCopies = 1024;
    public const int MaxCopiesPerEntry = 16;
    public const int MaxDirectoryRows = 128;
    public const int MaxQueryLength = 256;
    public const int MaxSearchResults = 100;
    public const int MaxContainerBytes = 4194304;

    private static readonly string[] PresetProfiles = ["cpm-hc80", "cpm-hc40", "hcfs80", "hcfs40"];
    private static readonly string[] UnknownReasons = ["not-scanned", "no-preset", "ambiguous", "unsupported-directory", "reader-error", "budget"];
    private const string ListedReason = "listing-only";

    public static DiskLibraryDocument Load(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > MaxCatalogBytes) throw Bad($"catalog must be at most {MaxCatalogBytes} encoded bytes");
        RequireValidUtf8(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = MaxJsonDepth }); }
        catch (JsonException ex) { throw new DiskLibrarySchemaException("catalog is not valid JSON depth " + MaxJsonDepth + " or below: " + ex.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("catalog root must be an object");
            RequireExactKeys(root, 1, "version", "entries", "copies");
            if (GetProp(root, "version").GetRawText() != "1") throw Bad("version must be the integer 1");
            var entries = ParseEntries(GetProp(root, "entries"), 2);
            var copies = ParseCopies(GetProp(root, "copies"), 2, entries);
            return new DiskLibraryDocument(Version, entries, copies);
        }
    }

    public static byte[] Encode(DiskLibraryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateBudgets(document);
        var sb = new StringBuilder(2048);
        sb.Append("{\"version\":").Append(Version).Append(",\"entries\":[");
        for (var i = 0; i < document.Entries.Length; i++) { if (i > 0) sb.Append(','); AppendEntry(sb, document.Entries[i]); }
        sb.Append("],\"copies\":[");
        for (var i = 0; i < document.Copies.Length; i++) { if (i > 0) sb.Append(','); AppendCopy(sb, document.Copies[i]); }
        sb.Append("]}");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        if (bytes.Length > MaxCatalogBytes) throw new DiskLibrarySchemaException("encoded catalog exceeds the " + MaxCatalogBytes + " byte budget");
        return bytes;
    }

    /// <summary>Ordinal case-insensitive substring over name, provenance, preset, directory user/name and full sha256; order name then id; at most 100 plus remaining; no payload reads.</summary>
    public static DiskLibrarySearchResult Search(DiskLibraryDocument document, string query)
    {
        if (query is null) throw new ArgumentException("query is required", nameof(query));
        if (query.Length > MaxQueryLength) throw new DiskLibrarySchemaException("search query exceeds " + MaxQueryLength + " characters");
        if (HasUnpairedSurrogate(query)) throw new ArgumentException("search query must be a well-formed UTF-8 string", nameof(query));
        ArgumentNullException.ThrowIfNull(document);
        var matches = document.Entries
            .Where(e => MatchesQuery(e, query))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        var shown = matches.Take(MaxSearchResults).ToImmutableArray();
        return new DiskLibrarySearchResult(shown, Math.Max(0, matches.Count - MaxSearchResults));
    }

    private static bool MatchesQuery(DiskLibraryEntry entry, string query)
    {
        if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (entry.Sha256.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (entry.Provenance.Source.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (entry.Provenance.Note.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (entry.Contents.Profile is not null && entry.Contents.Profile.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var row in entry.Contents.Files)
            if (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.User.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void ValidateBudgets(DiskLibraryDocument document)
    {
        if (document.Entries.Length > MaxEntries) throw new DiskLibrarySchemaException("entries exceed " + MaxEntries);
        if (document.Copies.Length > MaxCopies) throw new DiskLibrarySchemaException("copies exceed " + MaxCopies);
        if (document.Entries.Any(e => e.Contents.Files.Length > MaxDirectoryRows))
            throw new DiskLibrarySchemaException("contents files exceed " + MaxDirectoryRows);
        if (document.Copies.GroupBy(c => c.EntryId).Any(g => g.Count() > MaxCopiesPerEntry))
            throw new DiskLibrarySchemaException("copies per entry exceed " + MaxCopiesPerEntry);
        if (document.Entries.Any(e => e.Contents.Files.Any(r => r.User is < 0 or > 15
                || r.Name.Length is < 1 or > 16
                || r.Records is < 0 or > MaxContainerBytes
                || r.AllocatedBytes is < 0 or > MaxContainerBytes
                || r.DirectoryIndex is < 0 or > 127)))
            throw new DiskLibrarySchemaException("contents file rows exceed the schema bounds");
    }

    private static ImmutableArray<DiskLibraryEntry> ParseEntries(JsonElement el, int depth)
    {
        if (el.ValueKind != JsonValueKind.Array) throw Bad("entries must be an array");
        var list = new List<DiskLibraryEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in el.EnumerateArray())
        {
            if (list.Count == MaxEntries) throw Bad("entries exceed " + MaxEntries);
            var entry = ParseEntry(item, depth + 1);
            if (!ids.Add(entry.Id)) throw Bad("duplicate entry id " + entry.Id);
            list.Add(entry);
        }
        return list.ToImmutableArray();
    }

    private static DiskLibraryEntry ParseEntry(JsonElement el, int depth)
    {
        if (el.ValueKind != JsonValueKind.Object) throw Bad("entry must be an object");
        RequireExactKeys(el, depth, "id", "name", "sourcePath", "length", "sha256", "fileId", "format", "provenance", "contents");
        var id = RequireGuidD(RequireString(GetProp(el, "id"), "entry id", 36, 36, true));
        var name = RequireString(GetProp(el, "name"), "entry name", 1, 256);
        var sourcePath = RequireString(GetProp(el, "sourcePath"), "entry sourcePath", 1, 4096);
        if (!sourcePath.StartsWith('/')) throw Bad("entry sourcePath must be an absolute path");
        var length = RequireInt64(GetProp(el, "length"), "entry length", 1, MaxContainerBytes);
        var sha256 = RequireSha256(GetProp(el, "sha256"), "entry sha256");
        var fileIdEl = GetProp(el, "fileId");
        string? fileId = fileIdEl.ValueKind == JsonValueKind.Null ? null
            : RequireString(fileIdEl, "entry fileId", 1, 256, true);
        var formatEl = RequireString(GetProp(el, "format"), "entry format", 1, 32, true);
        var format = formatEl switch
        {
            "standard-dsk" => DiskLibraryFormat.StandardDsk,
            "extended-dsk" => DiskLibraryFormat.ExtendedDsk,
            _ => throw Bad("entry format must be standard-dsk or extended-dsk")
        };
        return new DiskLibraryEntry(id, name, sourcePath, length, sha256, fileId, format,
            ParseProvenance(GetProp(el, "provenance"), depth + 1), ParseContents(GetProp(el, "contents"), depth + 1));
    }

    private static DiskLibraryProvenance ParseProvenance(JsonElement el, int depth)
    {
        if (el.ValueKind != JsonValueKind.Object) throw Bad("provenance must be an object");
        RequireExactKeys(el, depth, "source", "note");
        return new(RequireString(GetProp(el, "source"), "provenance source", 1, 2048),
            RequireString(GetProp(el, "note"), "provenance note", 0, 2048, true));
    }

    private static DiskLibraryContents ParseContents(JsonElement el, int depth)
    {
        if (el.ValueKind != JsonValueKind.Object) throw Bad("contents must be an object");
        RequireExactKeys(el, depth, "state", "profile", "files", "reason");
        var stateText = RequireString(GetProp(el, "state"), "contents state", 1, 32, true);
        var profileEl = GetProp(el, "profile");
        string? profile = profileEl.ValueKind == JsonValueKind.Null ? null
            : RequireString(profileEl, "contents profile", 1, 32, true);
        if (profile is not null && PresetProfiles.All(p => p != profile))
            throw Bad("contents profile must be null or one of " + string.Join(", ", PresetProfiles));
        var filesEl = GetProp(el, "files");
        if (filesEl.ValueKind != JsonValueKind.Array) throw Bad("contents files must be an array");
        var files = new List<DiskLibraryFileRow>();
        foreach (var item in filesEl.EnumerateArray())
        {
            if (files.Count == MaxDirectoryRows) throw Bad("contents files exceed " + MaxDirectoryRows);
            files.Add(ParseFileRow(item, depth + 1));
        }
        var reason = RequireString(GetProp(el, "reason"), "contents reason", 1, 32, true);
        if (stateText == "unknown")
        {
            if (files.Count != 0) throw Bad("unknown contents must carry no files");
            if (!UnknownReasons.Contains(reason))
                throw Bad("unknown contents reason must be one of " + string.Join(", ", UnknownReasons));
            return new DiskLibraryContents(DiskLibraryContentState.Unknown, profile, files.ToImmutableArray(), reason);
        }
        if (stateText == "directory-listed")
        {
            if (profile is null) throw Bad("directory-listed contents must name a preset");
            if (reason != ListedReason) throw Bad("directory-listed contents reason must be listing-only");
            return new DiskLibraryContents(DiskLibraryContentState.DirectoryListed, profile, files.ToImmutableArray(), reason);
        }
        throw Bad("contents state must be unknown or directory-listed");
    }

    private static DiskLibraryFileRow ParseFileRow(JsonElement el, int depth)
    {
        if (el.ValueKind != JsonValueKind.Object) throw Bad("file row must be an object");
        RequireExactKeys(el, depth, "user", "name", "readOnly", "system", "records", "allocatedBytes", "directoryIndex");
        return new(
            RequireInt32(GetProp(el, "user"), "row user", 0, 15),
            RequireString(GetProp(el, "name"), "row name", 1, 16),
            RequireBool(GetProp(el, "readOnly"), "row readOnly"),
            RequireBool(GetProp(el, "system"), "row system"),
            RequireInt32(GetProp(el, "records"), "row records", 0, MaxContainerBytes),
            RequireInt32(GetProp(el, "allocatedBytes"), "row allocatedBytes", 0, MaxContainerBytes),
            RequireInt32(GetProp(el, "directoryIndex"), "row directoryIndex", 0, 127));
    }

    private static ImmutableArray<DiskLibraryCopy> ParseCopies(JsonElement el, int depth, ImmutableArray<DiskLibraryEntry> entries)
    {
        if (el.ValueKind != JsonValueKind.Array) throw Bad("copies must be an array");
        var byId = entries.ToDictionary(e => e.Id, e => e, StringComparer.Ordinal);
        var list = new List<DiskLibraryCopy>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var perEntry = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in el.EnumerateArray())
        {
            if (list.Count == MaxCopies) throw Bad("copies exceed " + MaxCopies);
            if (item.ValueKind != JsonValueKind.Object) throw Bad("copy must be an object");
            RequireExactKeys(item, depth + 1, "id", "entryId", "relativePath", "originalSha256", "initialSha256", "length");
            var id = RequireGuidD(RequireString(GetProp(item, "id"), "copy id", 36, 36, true));
            var entryId = RequireString(GetProp(item, "entryId"), "copy entryId", 36, 36, true);
            if (!byId.TryGetValue(entryId, out var entry)) throw Bad("copy " + id + " references unknown entry " + entryId);
            var relativePath = RequireString(GetProp(item, "relativePath"), "copy relativePath", 1, 4096, true);
            if (relativePath != "copies/" + id + ".dsk") throw Bad("copy relativePath must be copies/<copy-id>.dsk");
            if (RequireSha256(GetProp(item, "originalSha256"), "copy originalSha256") != entry.Sha256)
                throw Bad("copy originalSha256 must equal the entry sha256");
            if (RequireSha256(GetProp(item, "initialSha256"), "copy initialSha256") != entry.Sha256)
                throw Bad("copy initialSha256 must equal the entry sha256");
            if (RequireInt64(GetProp(item, "length"), "copy length", 1, MaxContainerBytes) != entry.Length)
                throw Bad("copy length must equal the entry length");
            if (!ids.Add(id)) throw Bad("duplicate copy id " + id);
            perEntry.TryGetValue(entryId, out var count);
            if (++count > MaxCopiesPerEntry) throw Bad("entry " + entryId + " exceeds " + MaxCopiesPerEntry + " copies");
            perEntry[entryId] = count;
            list.Add(new DiskLibraryCopy(id, entryId, relativePath, entry.Sha256, entry.Sha256, entry.Length));
        }
        return list.ToImmutableArray();
    }

    private static void RequireExactKeys(JsonElement el, int depth, params string[] expected)
    {
        if (depth > MaxJsonDepth) throw Bad("JSON depth exceeds " + MaxJsonDepth);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in el.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Bad("duplicate key " + property.Name);
            if (expected.AsSpan().Contains(property.Name)) continue;
            throw Bad("unexpected key " + property.Name);
        }
        foreach (var key in expected)
            if (!seen.Contains(key)) throw Bad("missing key " + key);
    }

    private static JsonElement GetProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value)) throw Bad("missing key " + name);
        return value;
    }

    private static string RequireString(JsonElement el, string what, int min, int max, bool allowBlank = false)
    {
        if (el.ValueKind != JsonValueKind.String) throw Bad(what + " must be a string");
        var value = el.GetString() ?? string.Empty;
        if (value.Length < min || value.Length > max) throw Bad(what + " length must be " + min + ".." + max);
        if (!allowBlank && value.Trim().Length == 0) throw Bad(what + " must not be blank");
        if (HasControl(value)) throw Bad(what + " contains a control character");
        if (HasUnpairedSurrogate(value)) throw Bad(what + " contains an unpaired surrogate");
        return value;
    }

    private static bool RequireBool(JsonElement el, string what)
    {
        if (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False) throw Bad(what + " must be a boolean");
        return el.GetBoolean();
    }

    private static int RequireInt32(JsonElement el, string what, int min, int max)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var value) || value < min || value > max)
            throw Bad(what + " must be an integer " + min + ".." + max);
        return value;
    }

    private static long RequireInt64(JsonElement el, string what, long min, long max)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var value) || value < min || value > max)
            throw Bad(what + " must be an integer " + min + ".." + max);
        return value;
    }

    private static string RequireSha256(JsonElement el, string what)
    {
        var value = RequireString(el, what, 64, 64, true);
        if (!IsLowerHex64(value))
            throw Bad(what + " must be 64 lowercase hex characters");
        return value;
    }

    private static string RequireGuidD(string value)
    {
        if (value.Length != 36) throw Bad("id must be a 36 character UUID D-format string");
        for (var i = 0; i < 36; i++)
        {
            var c = value[i];
            if (i is 8 or 13 or 18 or 23) { if (c != '-') throw Bad("id must be a UUID D-format string"); continue; }
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) continue;
            throw Bad("id must be a lowercase UUID D-format string");
        }
        return value;
    }

    private static bool HasControl(string value)
    {
        foreach (var c in value) if (c < 0x20 || c == 0x7F) return true;
        return false;
    }

    /// <summary>Strict UTF-8 byte scan: the reader would parse invalid UTF-8 into unpaired surrogates instead of throwing, so the whole load is rejected before parsing.</summary>
    private static void RequireValidUtf8(ReadOnlySpan<byte> s)
    {
        for (var i = 0; i < s.Length; )
        {
            var b = s[i];
            if (b < 0x80) { i++; continue; }
            int len;
            if ((b & 0xE0) == 0xC0) len = 2;
            else if ((b & 0xF0) == 0xE0) len = 3;
            else
            {
                if ((b & 0xF8) != 0xF0 || b > 0xF4) throw Bad("catalog is not valid UTF-8");
                len = 4;
            }
            if (i + len > s.Length) throw Bad("catalog is not valid UTF-8");
            if (len == 2 && (b < 0xC2 || s[i + 1] is < 0x80 or > 0xBF)) throw Bad("catalog is not valid UTF-8");
            if (len == 3)
            {
                if (b == 0xE0 && s[i + 1] < 0xA0) throw Bad("catalog is not valid UTF-8");
                if (b == 0xED && s[i + 1] is >= 0xA0 and <= 0xBF) throw Bad("catalog is not valid UTF-8");
                if ((s[i + 1] & 0xC0) != 0x80 || (s[i + 2] & 0xC0) != 0x80) throw Bad("catalog is not valid UTF-8");
            }
            if (len == 4)
            {
                if (b == 0xF0 && s[i + 1] < 0x90) throw Bad("catalog is not valid UTF-8");
                if (b == 0xF4 && s[i + 1] > 0x8F) throw Bad("catalog is not valid UTF-8");
                if ((s[i + 1] & 0xC0) != 0x80 || (s[i + 2] & 0xC0) != 0x80 || (s[i + 3] & 0xC0) != 0x80) throw Bad("catalog is not valid UTF-8");
            }
            i += len;
        }
    }

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c >= 0xD800 && c <= 0xDBFF)
            {
                if (i + 1 >= value.Length || value[i + 1] < 0xDC00 || value[i + 1] > 0xDFFF) return true;
                i++;
            }
            else if (c >= 0xDC00 && c <= 0xDFFF) return true;
        }
        return false;
    }

    private static bool IsLowerHex64(string value)
    {
        foreach (var c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    private static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void AppendEntry(StringBuilder sb, DiskLibraryEntry e)
    {
        sb.Append("{\"id\":"); AppendString(sb, e.Id);
        sb.Append(",\"name\":"); AppendString(sb, e.Name);
        sb.Append(",\"sourcePath\":"); AppendString(sb, e.SourcePath);
        sb.Append(",\"length\":").Append(e.Length);
        sb.Append(",\"sha256\":"); AppendString(sb, e.Sha256);
        sb.Append(",\"fileId\":");
        if (e.FileId is null) sb.Append("null"); else AppendString(sb, e.FileId);
        sb.Append(",\"format\":\"").Append(e.Format == DiskLibraryFormat.ExtendedDsk ? "extended-dsk" : "standard-dsk").Append('"');
        sb.Append(",\"provenance\":{\"source\":"); AppendString(sb, e.Provenance.Source);
        sb.Append(",\"note\":"); AppendString(sb, e.Provenance.Note);
        sb.Append('}');
        sb.Append(",\"contents\":{\"state\":\"").Append(e.Contents.State == DiskLibraryContentState.DirectoryListed ? "directory-listed" : "unknown").Append('"');
        sb.Append(",\"profile\":");
        if (e.Contents.Profile is null) sb.Append("null"); else AppendString(sb, e.Contents.Profile);
        sb.Append(",\"files\":[");
        for (var i = 0; i < e.Contents.Files.Length; i++) { if (i > 0) sb.Append(','); AppendFileRow(sb, e.Contents.Files[i]); }
        sb.Append("],\"reason\":"); AppendString(sb, e.Contents.Reason);
        sb.Append("}}");
    }

    private static void AppendFileRow(StringBuilder sb, DiskLibraryFileRow r)
    {
        sb.Append("{\"user\":").Append(r.User).Append(",\"name\":");
        AppendString(sb, r.Name);
        sb.Append(",\"readOnly\":").Append(r.ReadOnly ? "true" : "false");
        sb.Append(",\"system\":").Append(r.System ? "true" : "false");
        sb.Append(",\"records\":").Append(r.Records);
        sb.Append(",\"allocatedBytes\":").Append(r.AllocatedBytes);
        sb.Append(",\"directoryIndex\":").Append(r.DirectoryIndex).Append('}');
    }

    private static void AppendCopy(StringBuilder sb, DiskLibraryCopy c)
    {
        sb.Append("{\"id\":"); AppendString(sb, c.Id);
        sb.Append(",\"entryId\":"); AppendString(sb, c.EntryId);
        sb.Append(",\"relativePath\":"); AppendString(sb, c.RelativePath);
        sb.Append(",\"originalSha256\":"); AppendString(sb, c.OriginalSha256);
        sb.Append(",\"initialSha256\":"); AppendString(sb, c.InitialSha256);
        sb.Append(",\"length\":").Append(c.Length).Append('}');
    }

    private static DiskLibrarySchemaException Bad(string message) => new("catalog schema violation: " + message);
}
