using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Hc2000.Machine.Snapshots;

public delegate void HcsReadWindow(long offset, Span<byte> destination);

/// <summary>Caller-selected, compiled model schema. Subclasses must validate exact profile/store/component
/// rosters, asset formats/geometry, event kinds/phases/payloads and cross-state invariants.
/// The codec does not claim complete device continuation support.</summary>
public abstract class HcsSchema(string profileId, uint profileVersion, JsonElement definition)
{
    private readonly string fingerprint = HcsCodec.ProfileFingerprint(definition);
    protected abstract void ValidateModels(JsonElement manifest);
    protected virtual bool UsesCpuFrontiers => false;
    protected internal virtual bool HasLargeBlobPolicy => false;
    protected virtual void ValidateBlobModels(JsonElement manifest, byte[][] blobs, HcsLimits limits) { }
    protected internal virtual bool AdmitLargeBlob(JsonElement manifest, uint blobId, int length,
        HcsReadWindow read, IReadOnlyList<byte[]> blobs, HcsLimits limits) => false;
    protected internal virtual void ReserveCopies(long wireBytes) { }
    protected static void Error(HcsError error, string field) => HcsCodec.Fail(error, field);
    protected static void Fields(JsonElement e, params string[] fields)
    {
        if (e.ValueKind != JsonValueKind.Object) Error(HcsError.InvalidFormat, "object");
        var actual = e.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != fields.Length || actual.Except(fields, StringComparer.Ordinal).Any()) Error(HcsError.InvalidFormat, "fields");
    }
    protected static string Text(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) Error(HcsError.InvalidFormat, "string");
        return e.GetString()!;
    }
    protected static string Id(JsonElement e)
    {
        string s = Text(e);
        if (!Regex.IsMatch(s, "\\A[a-z][a-z0-9_.-]{0,63}\\z", RegexOptions.CultureInvariant)) Error(HcsError.InvalidFormat, "id");
        return s;
    }
    protected static long Counter(JsonElement e)
    {
        string s = Text(e);
        if (!Regex.IsMatch(s, "\\A(?:0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant) || !long.TryParse(s, out _))
            Error(HcsError.InvalidFormat, "decimal64");
        return long.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }
    protected static uint UInt(JsonElement e, uint max = uint.MaxValue)
    {
        if (e.ValueKind != JsonValueKind.Number || e.GetRawText().StartsWith('-') || !e.TryGetUInt32(out var n) || n > max) Error(HcsError.InvalidFormat, "uint");
        return e.GetUInt32();
    }
    private static string Hash(JsonElement e)
    {
        string s = Text(e);
        if (!Regex.IsMatch(s, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)) Error(HcsError.InvalidFormat, "sha256");
        return s;
    }
    private static JsonElement[] Array(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) Error(HcsError.InvalidFormat, "array");
        return e.EnumerateArray().ToArray();
    }
    private static void Sorted(JsonElement[] items)
    {
        string? prior = null;
        foreach (var item in items)
        {
            string id = Id(item.GetProperty("id"));
            if (prior != null && string.CompareOrdinal(prior, id) >= 0) Error(HcsError.StateInconsistent, "id order");
            prior = id;
        }
    }
    internal void Validate(JsonElement root, byte[][] blobs, HcsLimits limits)
    {
        try { ValidateCore(root, blobs, blobs.Length, limits); ValidateBlobModels(root, blobs, limits); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new HcsException(HcsError.InvalidFormat, "manifest"); }
    }
    internal void Prevalidate(JsonElement root, int blobCount, HcsLimits limits)
    {
        try { ValidateCore(root, null, blobCount, limits); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new HcsException(HcsError.InvalidFormat, "manifest"); }
    }
    private void ValidateCore(JsonElement root, byte[][]? blobs, int blobCount, HcsLimits limits)
    {
        Fields(root, "profile", "assets", "cpu", "clock", "stores", "components", "events", "pendingOutput");
        var profile = root.GetProperty("profile");
        Fields(profile, "id", "version", "fingerprint", "definition");
        if (Id(profile.GetProperty("id")) != profileId || UInt(profile.GetProperty("version")) != profileVersion || profileVersion == 0
            || Hash(profile.GetProperty("fingerprint")) != fingerprint || HcsCodec.ProfileFingerprint(profile.GetProperty("definition")) != fingerprint)
            Error(HcsError.ProfileMismatch, "profile");
        var assets = Array(root.GetProperty("assets")); Sorted(assets);
        var assetIds = new HashSet<string>(); var refs = new HashSet<uint>(); uint nextBlob = 1;
        foreach (var a in assets)
        {
            Fields(a, "id", "kind", "format", "blobId", "byteLength", "sha256", "rawGeometry", "provenance");
            assetIds.Add(Id(a.GetProperty("id")));
            string kind = Text(a.GetProperty("kind"));
            if (kind is not ("rom" or "ram" or "disk-base" or "disk-current" or "tape" or "buffer")) Error(HcsError.StateUnsupported, "asset kind");
            Id(a.GetProperty("format")); Text(a.GetProperty("provenance"));
            uint id = UInt(a.GetProperty("blobId"));
            if (id == 0 || id > blobCount) Error(HcsError.AssetMismatch, "blobId");
            if (refs.Add(id) && id != nextBlob++) Error(HcsError.StateInconsistent, "blob first-use order");
            var declaredLength = Counter(a.GetProperty("byteLength"));
            var declaredHash = Hash(a.GetProperty("sha256"));
            if (blobs is not null && (declaredLength != blobs[id - 1].Length
                || declaredHash != Convert.ToHexStringLower(SHA256.HashData(blobs[id - 1]))))
                Error(HcsError.AssetMismatch, "asset length/hash");
            if (Text(a.GetProperty("format")) != "raw" && a.GetProperty("rawGeometry").ValueKind != JsonValueKind.Null)
                Error(HcsError.InvalidFormat, "rawGeometry");
        }
        if (refs.Count != blobCount) Error(HcsError.AssetMismatch, "orphan blob");
        var stores = Array(root.GetProperty("stores")); Sorted(stores);
        foreach (var s in stores)
        { Fields(s, "id", "assetId"); if (!assetIds.Contains(Id(s.GetProperty("assetId")))) Error(HcsError.AssetMismatch, "store"); }
        var components = Array(root.GetProperty("components")); Sorted(components);
        foreach (var c in components)
        {
            Fields(c, "id", "modelId", "modelVersion", "state"); Id(c.GetProperty("modelId"));
            if (UInt(c.GetProperty("modelVersion")) == 0) Error(HcsError.UnsupportedVersion, "component");
            if (c.GetProperty("state").ValueKind != JsonValueKind.Object) Error(HcsError.InvalidFormat, "state");
        }
        ValidateCpu(root.GetProperty("cpu"));
        var clock = root.GetProperty("clock");
        Fields(clock, "epoch", "now", "masterHz", "dividerNumerator", "dividerDenominator", "mapperWhole", "mapperRemainder", "lastCpuTStates", "nextSequence", "deviceGenerations");
        foreach (var p in clock.EnumerateObject()) if (p.Name != "deviceGenerations") Counter(p.Value);
        long now = Counter(clock.GetProperty("now")), pnum = Counter(clock.GetProperty("dividerNumerator")), den = Counter(clock.GetProperty("dividerDenominator"));
        long remainder = Counter(clock.GetProperty("mapperRemainder")), whole = Counter(clock.GetProperty("mapperWhole"));
        static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }
        if (pnum == 0 || den == 0 || Gcd(pnum, den) != 1 || Counter(clock.GetProperty("masterHz")) == 0 || remainder >= den
            || (UsesCpuFrontiers
                ? (System.Numerics.BigInteger)whole + (remainder == 0 ? 0 : 1) > now
                : (System.Numerics.BigInteger)whole + (remainder == 0 ? 0 : 1) != now)
            || Counter(clock.GetProperty("lastCpuTStates")) != Counter(root.GetProperty("cpu").GetProperty("tStates")))
            Error(HcsError.StateInconsistent, "clock");
        var gens = Array(clock.GetProperty("deviceGenerations")); Sorted(gens);
        var generations = new Dictionary<string, long>();
        foreach (var g in gens) { Fields(g, "id", "generation"); generations.Add(Id(g.GetProperty("id")), Counter(g.GetProperty("generation"))); }
        var sequences = new HashSet<long>(); (long, uint, string, long)? previous = null;
        foreach (var e in Array(root.GetProperty("events")))
        {
            Fields(e, "dueTick", "phase", "deviceId", "kind", "generation", "sequence", "payload");
            long due = Counter(e.GetProperty("dueTick")), sequence = Counter(e.GetProperty("sequence"));
            string device = Id(e.GetProperty("deviceId")); Id(e.GetProperty("kind"));
            uint phase = UInt(e.GetProperty("phase"), UsesCpuFrontiers ? 5u : 4u);
            var tuple = (due, phase, device, sequence);
            if (due <= now || !generations.TryGetValue(device, out var gen) || gen != Counter(e.GetProperty("generation"))
                || !sequences.Add(sequence) || sequence >= Counter(clock.GetProperty("nextSequence"))
                || (previous.HasValue && Compare(previous.Value, tuple) >= 0)) Error(HcsError.StateInconsistent, "event");
            previous = tuple;
            if (HcsCodec.CanonicalJson(e.GetProperty("payload"), limits).Length > limits.PayloadBytes) Error(HcsError.LimitExceeded, "payload");
        }
        (long, string, long)? last = null;
        foreach (var o in Array(root.GetProperty("pendingOutput")))
        {
            Fields(o, "sourceId", "kind", "masterTick", "sequence", "consumedCursor", "payload");
            string id = Id(o.GetProperty("sourceId")); Id(o.GetProperty("kind"));
            long tick = Counter(o.GetProperty("masterTick")), sequence = Counter(o.GetProperty("sequence")); Counter(o.GetProperty("consumedCursor"));
            var tuple = (tick, id, sequence);
            if (tick > now || (last.HasValue && CompareOutput(last.Value, tuple) >= 0)) Error(HcsError.StateInconsistent, "output order");
            last = tuple;
        }
        ValidateModels(root);
    }
    private static int Compare((long Tick, uint Phase, string Device, long Sequence) a, (long Tick, uint Phase, string Device, long Sequence) b)
    {
        int c = a.Tick.CompareTo(b.Tick); if (c != 0) return c;
        c = a.Phase.CompareTo(b.Phase); if (c != 0) return c;
        c = string.CompareOrdinal(a.Device, b.Device); return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    }
    private static int CompareOutput((long Tick, string Source, long Sequence) a, (long Tick, string Source, long Sequence) b)
    {
        int c = a.Tick.CompareTo(b.Tick); if (c != 0) return c;
        c = string.CompareOrdinal(a.Source, b.Source); return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    }
    private void ValidateCpu(JsonElement cpu)
    {
        string[] words = ["af", "bc", "de", "hl", "ix", "iy", "sp", "pc", "afShadow", "bcShadow", "deShadow", "hlShadow", "wz"];
        string[] bytes = ["i", "r", "q"];
        string[] booleans = ["iff1", "iff2", "halted", "intLine", "nmiPending", "lastLdAiOrR", "nmosLdAirInterruptBug"];
        Fields(cpu, [.. words, .. bytes, .. booleans, "im", "eiDelay", "tStates", "modelId", "modelVersion"]);
        if (Text(cpu.GetProperty("modelId")) != (UsesCpuFrontiers ? "nmos-z80-frontier" : "nmos-z80") || UInt(cpu.GetProperty("modelVersion")) != 1) Error(HcsError.UnsupportedVersion, "cpu");
        foreach (string k in words) UInt(cpu.GetProperty(k), 65535);
        foreach (string k in bytes) UInt(cpu.GetProperty(k), 255);
        foreach (string k in booleans) if (cpu.GetProperty(k).ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Error(HcsError.InvalidFormat, k);
        UInt(cpu.GetProperty("im"), 2); UInt(cpu.GetProperty("eiDelay"), UsesCpuFrontiers ? 2u : 1u); Counter(cpu.GetProperty("tStates"));
    }
}
