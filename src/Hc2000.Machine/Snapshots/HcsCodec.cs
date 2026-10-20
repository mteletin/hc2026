using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hc2000.Machine.Snapshots;

public enum HcsError { InvalidFormat, Checksum, UnsupportedVersion, LimitExceeded, ProfileMismatch, AssetMismatch, StateInconsistent, StateUnsupported }
public sealed class HcsException(HcsError error, string field) : Exception($"{error}: {field}")
{
    public HcsError Error { get; } = error;
}
public sealed record HcsLimits(int TotalBytes = 256 * 1024 * 1024, int ManifestBytes = 16 * 1024 * 1024,
    int BlobBytes = 64 * 1024 * 1024, int BlobCount = 1024, int Records = 65536, int Depth = 64,
    int StringBytes = 4096, int PayloadBytes = 4096)
{
    /// <summary>Trusted composition policy, never selected from an imported manifest or UI draft.
    /// The schema still admits only the exact unique paged RAM blob above BlobBytes.</summary>
    public static HcsLimits ForProfile(MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return profile.Memory.RamPaged is null ? new() : new(TotalBytes: 0x44000000);
    }

    public bool AllowsFileLength(long length) => length >= 0 && length <= TotalBytes;
}

/// <summary>Owned detached data. No runtime object creation or Machine adoption is performed.</summary>
public sealed class HcsDocument
{
    public JsonElement Manifest { get; }
    private readonly byte[][] blobs;
    internal HcsDocument(JsonElement manifest, byte[][] blobs) { Manifest = manifest.Clone(); this.blobs = blobs; }
    public int BlobCount => blobs.Length;
    internal ReadOnlyMemory<byte> BlobView(uint id) => blobs[checked((int)id - 1)];
    public byte[] CopyBlob(uint id) => (byte[])blobs[checked((int)id - 1)].Clone();
}

/// <summary>Pure HCS1 framing. A mandatory trusted schema validates selected profile/device models.
/// There are deliberately no built-in live device model adapters in this detached slice.</summary>
public static class HcsCodec
{
    private static ReadOnlySpan<byte> Magic => [0x48, 0x43, 0x53, 0x0d, 0x0a, 0x1a, 0x0a, 0x00];
    internal static void Fail(HcsError error, string field) => throw new HcsException(error, field);
    public static HcsDocument Read(Stream source, HcsSchema schema, HcsLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var l = limits ?? new();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long consumed = 0;
        byte[] Take(int length, bool hashed = true)
        {
            if (length < 0 || consumed > l.TotalBytes - (long)length) Fail(HcsError.LimitExceeded, "file");
            byte[] bytes = new byte[length];
            try { source.ReadExactly(bytes); } catch (EndOfStreamException) { Fail(HcsError.InvalidFormat, "truncated"); }
            consumed += length;
            if (hashed) hash.AppendData(bytes);
            return bytes;
        }
        var header = Take(32);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic)) Fail(HcsError.InvalidFormat, "magic");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10)) != 0)
            Fail(HcsError.UnsupportedVersion, "container");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) != 0) Fail(HcsError.InvalidFormat, "flags");
        ulong total = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
        uint manifestLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));
        if (total > (ulong)l.TotalBytes || manifestLength > l.ManifestBytes || count > l.BlobCount) Fail(HcsError.LimitExceeded, "header");
        if (total < 64UL + manifestLength + 12UL * count) Fail(HcsError.InvalidFormat, "totalFileLength");
        schema.ReserveCopies((long)total);
        var json = Take((int)manifestLength);
        using var manifest = Parse(json, l);
        var blobs = new byte[(int)count][];
        for (int i = 0; i < blobs.Length; i++)
        {
            if ((ulong)consumed + 44 > total) Fail(HcsError.InvalidFormat, "blob header");
            var record = Take(12);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != i + 1) Fail(HcsError.InvalidFormat, "blobId");
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(4));
            if (length > int.MaxValue || length > (ulong)l.BlobBytes && (!schema.HasLargeBlobPolicy || !source.CanSeek))
                Fail(HcsError.LimitExceeded, "blob length");
            if (length > total - (ulong)consumed - 32) Fail(HcsError.InvalidFormat, "blob length");
            if (length > (ulong)l.BlobBytes)
            {
                if (length > int.MaxValue || !source.CanSeek) Fail(HcsError.LimitExceeded, "seekable blob admission");
                schema.Prevalidate(manifest.RootElement, blobs.Length, l);
                var payloadStart = source.Position;
                if ((ulong)Math.Max(0, source.Length - payloadStart) < length + 32) Fail(HcsError.InvalidFormat, "blob extent");
                void Window(long offset, Span<byte> destination)
                {
                    if (offset < 0 || destination.Length > 18 || (ulong)offset + (ulong)destination.Length > length)
                        Fail(HcsError.LimitExceeded, "blob probe window");
                    source.Position = checked(payloadStart + offset);
                    try { source.ReadExactly(destination); } catch (EndOfStreamException) { Fail(HcsError.InvalidFormat, "blob probe"); }
                }
                try
                {
                    if (!schema.AdmitLargeBlob(manifest.RootElement, (uint)i + 1, (int)length, Window, blobs, l))
                        Fail(HcsError.LimitExceeded, "blob length");
                }
                finally { source.Position = payloadStart; }
            }
            blobs[i] = Take((int)length);
            CheckOwnedBlob(schema, manifest.RootElement, blobs, (uint)i + 1, l);
        }
        if ((ulong)consumed + 32 != total) Fail(HcsError.InvalidFormat, "totalFileLength");
        var checksum = Take(32, false);
        if (!CryptographicOperations.FixedTimeEquals(checksum, hash.GetHashAndReset())) Fail(HcsError.Checksum, "file");
        if (source.ReadByte() != -1) Fail(HcsError.InvalidFormat, "trailing bytes");
        schema.Validate(manifest.RootElement, blobs, l);
        return new(manifest.RootElement, blobs);
    }

    /// <summary>Validates and freezes input before writing. Blob IDs must already follow stable asset order.</summary>
    public static void Write(Stream destination, JsonElement manifest, IReadOnlyList<byte[]> blobs, HcsSchema schema, HcsLimits? limits = null) =>
        WriteCore(destination, manifest, blobs, schema, limits, false);

    // Only MachineSnapshot's private capture arrays may take this path. They are already
    // detached from live stores and never exposed to a caller; public Write still freezes inputs.
    internal static void WriteOwned(Stream destination, JsonElement manifest, IReadOnlyList<byte[]> blobs, HcsSchema schema, HcsLimits limits) =>
        WriteCore(destination, manifest, blobs, schema, limits, true);

    private static void WriteCore(Stream destination, JsonElement manifest, IReadOnlyList<byte[]> blobs, HcsSchema schema, HcsLimits? limits, bool ownsBlobs)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var l = limits ?? new();
        if (blobs.Count > l.BlobCount) Fail(HcsError.LimitExceeded, "blobs");
        var json = CanonicalJson(manifest, l);
        if (json.Length > l.ManifestBytes) Fail(HcsError.LimitExceeded, "manifest");
        using var parsed = Parse(json, l);
        long total = 64L + json.Length;
        foreach (var b in blobs) total = checked(total + 12L + b.Length);
        if (total > l.TotalBytes) Fail(HcsError.LimitExceeded, "file");
        schema.ReserveCopies(total);
        schema.Prevalidate(parsed.RootElement, blobs.Count, l);
        for (int i = 0; i < blobs.Count; i++) CheckOwnedBlob(schema, parsed.RootElement, blobs, (uint)i + 1, l);
        schema.Validate(parsed.RootElement, blobs.ToArray(), l);
        var owned = ownsBlobs ? blobs.ToArray() : blobs.Select(b => (byte[])b.Clone()).ToArray();
        for (int i = 0; i < owned.Length; i++) CheckOwnedBlob(schema, parsed.RootElement, owned, (uint)i + 1, l);
        schema.Validate(parsed.RootElement, owned, l);
        byte[] header = new byte[32]; Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)total);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)owned.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Put(byte[] bytes) { hash.AppendData(bytes); destination.Write(bytes); }
        Put(header); Put(json);
        for (int i = 0; i < owned.Length; i++)
        {
            byte[] record = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)i + 1);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), (ulong)owned[i].Length);
            Put(record); Put(owned[i]);
        }
        destination.Write(hash.GetHashAndReset());
    }

    private static void CheckOwnedBlob(HcsSchema schema, JsonElement manifest, IReadOnlyList<byte[]> blobs, uint id, HcsLimits limits)
    {
        var bytes = blobs[(int)id - 1];
        if (bytes.Length <= limits.BlobBytes) return;
        void Window(long offset, Span<byte> destination)
        {
            if (offset < 0 || destination.Length > 18 || offset > bytes.Length - destination.Length)
                Fail(HcsError.LimitExceeded, "owned blob probe window");
            bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
        }
        if (!schema.AdmitLargeBlob(manifest, id, bytes.Length, Window, blobs, limits)) Fail(HcsError.LimitExceeded, "blob");
    }

    private static JsonDocument Parse(byte[] json, HcsLimits l)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(json);
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = int.MaxValue });
            while (reader.Read())
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= l.Depth)
                    Fail(HcsError.LimitExceeded, "depth");
            var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = l.Depth });
            try { CheckJson(doc.RootElement, l); return doc; } catch { doc.Dispose(); throw; }
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or InvalidOperationException)
        { throw new HcsException(HcsError.InvalidFormat, "JSON"); }
    }
    private static void CheckJson(JsonElement node, HcsLimits l, int depth = 0)
    {
        if (depth > l.Depth) Fail(HcsError.LimitExceeded, "depth");
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
            {
                CheckString(p.Name, l);
                if (!keys.Add(p.Name)) Fail(HcsError.InvalidFormat, "duplicate JSON key");
                CheckJson(p.Value, l, depth + 1);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            if (node.GetArrayLength() > l.Records) Fail(HcsError.LimitExceeded, "array");
            foreach (var item in node.EnumerateArray()) CheckJson(item, l, depth + 1);
        }
        else if (node.ValueKind == JsonValueKind.String) CheckString(node.GetString()!, l);
        else if (node.ValueKind == JsonValueKind.Number)
        {
            string raw = node.GetRawText();
            if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || !node.TryGetInt64(out _)) Fail(HcsError.InvalidFormat, "integer");
        }
    }
    private static void CheckString(string s, HcsLimits l)
    {
        try { if (new UTF8Encoding(false, true).GetByteCount(s) > l.StringBytes) Fail(HcsError.LimitExceeded, "string"); }
        catch (EncoderFallbackException) { Fail(HcsError.InvalidFormat, "surrogate"); }
    }
    public static byte[] CanonicalJson(JsonElement node, HcsLimits? limits = null)
    {
        var text = new StringBuilder();
        void String(string s)
        {
            text.Append('"');
            foreach (char c in s)
                if (c == '"' || c == '\\') text.Append('\\').Append(c);
                else if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4"));
                else text.Append(c);
            text.Append('"');
        }
        void Emit(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    text.Append('{'); bool first = true;
                    foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    { if (!first) text.Append(','); first = false; String(p.Name); text.Append(':'); Emit(p.Value); }
                    text.Append('}'); break;
                case JsonValueKind.Array:
                    text.Append('['); bool start = true;
                    foreach (var v in e.EnumerateArray()) { if (!start) text.Append(','); start = false; Emit(v); }
                    text.Append(']'); break;
                case JsonValueKind.String: String(e.GetString()!); break;
                case JsonValueKind.Number:
                    if (!e.TryGetInt64(out var n) || e.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0) Fail(HcsError.InvalidFormat, "integer");
                    text.Append(n.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case JsonValueKind.True: text.Append("true"); break;
                case JsonValueKind.False: text.Append("false"); break;
                case JsonValueKind.Null: text.Append("null"); break;
                default: Fail(HcsError.InvalidFormat, "JSON token"); break;
            }
        }
        CheckJson(node, limits ?? new HcsLimits()); Emit(node);
        return new UTF8Encoding(false, true).GetBytes(text.ToString());
    }
    public static string ProfileFingerprint(JsonElement definition)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData("HCPROFILE1\0"u8); h.AppendData(CanonicalJson(definition));
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}
