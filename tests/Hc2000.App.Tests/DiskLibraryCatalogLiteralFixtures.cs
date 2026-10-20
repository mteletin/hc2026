using System.Text;

namespace Hc2000.App.Tests;

// Hand-laid container inputs from the independent-input-outcome-matrix, not Dsk.Create/Save.
// Digests pinned independently with Python hashlib.sha256 over the specified bytes.
internal static class DiskLibraryCatalogLiteralFixtures
{
    internal const string StandardHash = "d92059c1136c09ba45f21ab1d797bb6a564e80315b823576bd7773c5a492cf4b";
    internal const string ExtendedHash = "e7d372b235138e4e660305448a695de36df09fb0236f3f5cdc87e5d0c8f37470";
    internal const string UnformattedHash = "fa04e5a4cccb9822d9534da3b60af6dcc113eee48b82e836677fa08f53004439";
    internal const string EntryId = "11111111-1111-4111-8111-111111111111";
    internal const string EmptyJson = """{"version":1,"entries":[],"copies":[]}""";
    internal const string EntryJson = """
        {"id":"11111111-1111-4111-8111-111111111111","name":"Original CP/M","sourcePath":"/catalog-fixture/CPM22-HC.DSK","length":778496,"sha256":"ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008","fileId":null,"format":"extended-dsk","provenance":{"source":"repository disks/cpm/CPM22-HC.DSK","note":"independent attribution; not ownership permission"},"contents":{"state":"unknown","profile":null,"reason":"not-scanned","files":[]}}
        """;
    internal static string CatalogJson(string entry = EntryJson) =>
        "{\"version\":1,\"entries\":[" + entry + "],\"copies\":[]}";
    internal static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    internal static IEnumerable<(string Name, byte[] Bytes)> InvalidCatalogs()
    {
        foreach (var (name, json) in new (string, string)[]
        {
            ("truncated", "{\"version\":1"),
            ("trailing-data", EmptyJson + " false"),
            ("comment", EmptyJson.Replace("1", "/* no comments */1", StringComparison.Ordinal)),
            ("trailing-comma", "{\"version\":1,\"entries\":[],\"copies\":[],}"),
            ("duplicate-root-key", "{\"version\":1,\"version\":1,\"entries\":[],\"copies\":[]}"),
            ("unknown-root-key", "{\"version\":1,\"entries\":[],\"copies\":[],\"extra\":false}"),
            ("missing-copies", "{\"version\":1,\"entries\":[]}"),
            ("wrong-key-case", "{\"Version\":1,\"entries\":[],\"copies\":[]}"),
            ("null-root", "null"),
            ("null-entries", "{\"version\":1,\"entries\":null,\"copies\":[]}"),
            ("null-entry", CatalogJson("null")),
            ("duplicate-entry-id", CatalogJson(EntryJson + "," + EntryJson))
        }) yield return (name, Utf8(json));
        foreach (var version in new[] { "null", "\"1\"", "0", "2", "1.0", "1e0", "true" })
            yield return ("version-" + version, Utf8(EmptyJson.Replace(":1,", ":" + version + ",", StringComparison.Ordinal)));
        foreach (var (name, oldValue, replacement) in new (string, string, string)[]
        {
            ("blank-name", "\"Original CP/M\"", "\" \""),
            ("empty-name", "\"Original CP/M\"", "\"\""),
            ("null-name", "\"Original CP/M\"", "null"),
            ("long-name", "\"Original CP/M\"", "\"" + new string('n', 257) + "\""),
            ("relative-path", "/catalog-fixture/CPM22-HC.DSK", "relative.dsk"),
            ("control-path", "/catalog-fixture/CPM22-HC.DSK", "/bad\\u0000.dsk"),
            ("zero-length", "778496", "0"),
            ("over-cap-length", "778496", "4194305"),
            ("fractional-length", "778496", "1.5"),
            ("string-length", "778496", "\"778496\""),
            ("raw-format", "extended-dsk", "raw"),
            ("format-case", "extended-dsk", "Extended-DSK"),
            ("bad-profile", "\"profile\":null", "\"profile\":\"cpm3\""),
            ("bad-reason", "not-scanned", "available"),
            ("empty-file-id", "\"fileId\":null", "\"fileId\":\"\""),
            ("empty-provenance", "repository disks/cpm/CPM22-HC.DSK", ""),
            ("duplicate-entry-key", "\"name\":", "\"name\":\"extra\",\"name\":"),
            ("duplicate-provenance-key", "\"note\":", "\"note\":\"extra\",\"note\":"),
            ("duplicate-contents-key", "\"state\":", "\"state\":\"unknown\",\"state\":"),
            ("unknown-entry-key", "\"fileId\":null", "\"fileId\":null,\"availability\":\"available\"")
        }) yield return (name, Utf8(CatalogJson(EntryJson.Replace(oldValue, replacement, StringComparison.Ordinal))));
        yield return ("invalid-utf8", [0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]);
    }

    internal static byte[] Standard() => Container(false);
    internal static byte[] Extended() => Container(true);
    internal static byte[] Unformatted()
    {
        var bytes = new byte[256];
        "EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8.CopyTo(bytes);
        bytes[0x30] = 1;
        bytes[0x31] = 1;
        return bytes;
    }

    private static byte[] Container(bool extended)
    {
        var bytes = new byte[768];
        if (extended) "EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8.CopyTo(bytes);
        else "MV - CPCEMU Disk-File\r\nDisk-Info\r\n"u8.CopyTo(bytes);
        bytes[0x30] = 1;
        bytes[0x31] = 1;
        bytes[extended ? 0x34 : 0x33] = 2;
        "Track-Info\r\n"u8.CopyTo(bytes.AsSpan(0x100));
        bytes[0x115] = 1;
        bytes[0x116] = 0x4e;
        bytes[0x117] = 0xe5;
        bytes[0x11a] = 1;
        if (extended) bytes[0x11e] = 128;
        bytes.AsSpan(0x200, 128).Fill(0xe5);
        return bytes;
    }
}

internal sealed class DiskLibraryCatalogTestDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "hc2000-catalog-" + Guid.NewGuid().ToString("N"));
    internal string CatalogPath => Path.Combine(Root, "catalog.json");
    internal DiskLibraryCatalogTestDirectory() => Directory.CreateDirectory(Root);
    internal string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
