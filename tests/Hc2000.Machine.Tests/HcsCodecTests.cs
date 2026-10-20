using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Snapshots;
using Xunit;

namespace Hc2000.Machine.Tests;

public class HcsCodecTests
{
    // All assets and profiles here are synthetic, not HC2000 ROM/boot/continuation evidence.
    private static readonly byte[] Blob = [1, 2, 3];
    private static readonly JsonElement Definition = JsonSerializer.SerializeToElement(new { fixture = "synthetic-hcs1-v1" });
    private sealed class SyntheticSchema : HcsSchema
    {
        public SyntheticSchema() : this(Definition) { }
        public SyntheticSchema(JsonElement definition) : base("synthetic", 1, definition) { }
        protected override void ValidateModels(JsonElement m)
        {
            Fields(m.GetProperty("profile").GetProperty("definition"), "fixture");
            if (m.GetProperty("components").GetArrayLength() != 0 || m.GetProperty("events").GetArrayLength() != 0
                || m.GetProperty("pendingOutput").GetArrayLength() != 0 || m.GetProperty("clock").GetProperty("deviceGenerations").GetArrayLength() != 0)
                Error(HcsError.StateUnsupported, "synthetic model roster");
            var assets = m.GetProperty("assets"); var stores = m.GetProperty("stores");
            if (assets.GetArrayLength() != 1 || stores.GetArrayLength() != 1 || Text(assets[0].GetProperty("id")) != "ram"
                || Text(assets[0].GetProperty("kind")) != "ram" || Text(assets[0].GetProperty("format")) != "bytes"
                || Counter(assets[0].GetProperty("byteLength")) != 3 || Text(stores[0].GetProperty("id")) != "ram"
                || Text(stores[0].GetProperty("assetId")) != "ram") Error(HcsError.StateInconsistent, "synthetic stores");
        }
    }
    private static JsonObject Fixture()
    {
        var cpu = new JsonObject();
        foreach (var key in new[] { "af", "bc", "de", "hl", "ix", "iy", "sp", "pc", "afShadow", "bcShadow", "deShadow", "hlShadow", "wz", "i", "r", "q", "im", "eiDelay" }) cpu[key] = 0;
        foreach (var key in new[] { "iff1", "iff2", "halted", "intLine", "nmiPending", "lastLdAiOrR", "nmosLdAirInterruptBug" }) cpu[key] = false;
        cpu["modelId"] = "nmos-z80"; cpu["modelVersion"] = 1; cpu["tStates"] = "9007199254740993";
        return new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = "synthetic", ["version"] = 1, ["fingerprint"] = HcsCodec.ProfileFingerprint(Definition), ["definition"] = JsonNode.Parse(Definition.GetRawText()) },
            ["assets"] = new JsonArray(new JsonObject { ["id"] = "ram", ["kind"] = "ram", ["format"] = "bytes", ["blobId"] = 1, ["byteLength"] = "3", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Blob)), ["rawGeometry"] = null, ["provenance"] = "synthetic 01 02 03" }),
            ["cpu"] = cpu,
            ["clock"] = new JsonObject { ["epoch"] = "0", ["now"] = "9007199254740993", ["masterHz"] = "1", ["dividerNumerator"] = "1", ["dividerDenominator"] = "1", ["mapperWhole"] = "9007199254740993", ["mapperRemainder"] = "0", ["lastCpuTStates"] = "9007199254740993", ["nextSequence"] = "0", ["deviceGenerations"] = new JsonArray() },
            ["stores"] = new JsonArray(new JsonObject { ["id"] = "ram", ["assetId"] = "ram" }),
            ["components"] = new JsonArray(), ["events"] = new JsonArray(), ["pendingOutput"] = new JsonArray()
        };
    }
    private static JsonElement Element(JsonNode n) => JsonSerializer.SerializeToElement(n);
    private static byte[] Encode(JsonObject? fixture = null)
    {
        using var stream = new MemoryStream(); HcsCodec.Write(stream, Element(fixture ?? Fixture()), [Blob], new SyntheticSchema()); return stream.ToArray();
    }
    private static HcsDocument Decode(byte[] bytes, HcsLimits? limits = null) => HcsCodec.Read(new MemoryStream(bytes), new SyntheticSchema(), limits);
    private static void Rehash(byte[] bytes) => SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)).CopyTo(bytes.AsSpan(bytes.Length - 32));
    [Fact]
    public void RoundTripIsOwnedExactAndByteStable()
    {
        byte[] bytes = Encode(); var doc = Decode(bytes);
        // Independent Python json(sort_keys=True,separators=(',',':')) + struct('<HHIQII') + hashlib.
        Assert.Equal(1141, bytes.Length);
        Assert.Equal("4843530d0a1a0a00010000000000000075040000000000002604000001000000", Convert.ToHexStringLower(bytes.AsSpan(0, 32)));
        Assert.Equal("f63e4e94b672492eb344034fad75aa03ffa79579ec2e1159eae28f0412c09f8f", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal("559f95f4922c64cba2733e6c26ede4f54cdf4ff2619d11deb83781480e5f443f", Convert.ToHexStringLower(bytes.AsSpan(bytes.Length - 32)));
        Assert.Equal("9007199254740993", doc.Manifest.GetProperty("cpu").GetProperty("tStates").GetString());
        Assert.Equal(Blob, doc.CopyBlob(1)); var copy = doc.CopyBlob(1); copy[0] = 99; Assert.Equal(Blob, doc.CopyBlob(1));
        Assert.Equal(bytes, Encode());
        using var output = new MemoryStream(); HcsCodec.Write(output, doc.Manifest, [doc.CopyBlob(1)], new SyntheticSchema()); Assert.Equal(bytes, output.ToArray());
    }
    [Theory]
    [InlineData(0, 0, HcsError.InvalidFormat)]
    [InlineData(8, 0, HcsError.UnsupportedVersion)]
    [InlineData(8, 2, HcsError.UnsupportedVersion)]
    [InlineData(10, 1, HcsError.UnsupportedVersion)]
    [InlineData(12, 1, HcsError.InvalidFormat)]
    [InlineData(16, 0, HcsError.InvalidFormat)]
    [InlineData(28, 0, HcsError.InvalidFormat)]
    public void HeaderFailuresAreExplicit(int offset, byte value, HcsError error)
    {
        byte[] bytes = Encode(); bytes[offset] = value; Rehash(bytes);
        Assert.Equal(error, Assert.Throws<HcsException>(() => Decode(bytes)).Error);
    }
    [Fact]
    public void EveryTruncationAndTrailingByteIsRejected()
    {
        byte[] bytes = Encode();
        for (int length = 0; length < bytes.Length; length++) Assert.Throws<HcsException>(() => Decode(bytes[..length]));
        Assert.Throws<HcsException>(() => Decode([.. bytes, 0]));
    }
    [Fact]
    public void ChecksumAndIndividualHashAreIndependent()
    {
        byte[] bytes = Encode(); bytes[^33] ^= 1;
        Assert.Equal(HcsError.Checksum, Assert.Throws<HcsException>(() => Decode(bytes)).Error);
        Rehash(bytes); Assert.Equal(HcsError.AssetMismatch, Assert.Throws<HcsException>(() => Decode(bytes)).Error);
    }
    [Fact]
    public void OrphanBlobAndMissingBlobReferencesAreRejected()
    {
        var fixture = Fixture(); fixture["assets"] = new JsonArray(); fixture["stores"] = new JsonArray();
        Assert.Equal(HcsError.AssetMismatch, Assert.Throws<HcsException>(() => Decode(ReplaceJson(Encode(), HcsCodec.CanonicalJson(Element(fixture))))).Error);
        fixture = Fixture(); fixture["assets"]![0]!["blobId"] = 2;
        Assert.Equal(HcsError.AssetMismatch, Assert.Throws<HcsException>(() => Decode(ReplaceJson(Encode(), HcsCodec.CanonicalJson(Element(fixture))))).Error);
    }
    [Fact]
    public void WriterValidatesBeforeEmittingAnything()
    {
        var fixture = Fixture(); fixture["cpu"]!["eiDelay"] = 2;
        using var output = new MemoryStream();
        Assert.Throws<HcsException>(() => HcsCodec.Write(output, Element(fixture), [Blob], new SyntheticSchema()));
        Assert.Equal(0, output.Length);
    }
    [Fact]
    public void BlobIdAndOverflowLengthAreRejectedBeforeAllocation()
    {
        byte[] bytes = Encode(); int record = 32 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
        bytes[record] = 0; Rehash(bytes); Assert.Throws<HcsException>(() => Decode(bytes));
        bytes = Encode(); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(record + 4), ulong.MaxValue); Rehash(bytes);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => Decode(bytes)).Error);
    }
    [Theory]
    [InlineData("missing")][InlineData("unknown")][InlineData("version")][InlineData("profile")]
    [InlineData("counter")][InlineData("word")][InlineData("boolean")][InlineData("clock")][InlineData("store")][InlineData("model")]
    [InlineData("asset-kind")][InlineData("nested-field")][InlineData("fingerprint")][InlineData("definition")]
    [InlineData("state-field")][InlineData("event-kind")]
    public void StrictManifestFailures(string mutation)
    {
        var f = Fixture();
        switch (mutation)
        {
            case "missing": f.Remove("events"); break;
            case "unknown": f["unknown"] = 1; break;
            case "version": f["cpu"]!["modelVersion"] = 2; break;
            case "profile": f["profile"]!["id"] = "other"; break;
            case "counter": f["cpu"]!["tStates"] = "01"; break;
            case "word": f["cpu"]!["af"] = 65536; break;
            case "boolean": f["cpu"]!["iff1"] = 1; break;
            case "clock": f["clock"]!["mapperRemainder"] = "1"; break;
            case "store": f["stores"]![0]!["assetId"] = "absent"; break;
            case "model": f["components"] = new JsonArray(new JsonObject { ["id"] = "unknown", ["modelId"] = "unknown", ["modelVersion"] = 1, ["state"] = new JsonObject() }); break;
            case "asset-kind": f["assets"]![0]!["kind"] = "unknown"; break;
            case "nested-field": f["assets"]![0]!["unknown"] = 0; break;
            case "fingerprint": f["profile"]!["fingerprint"] = new string('0', 64); break;
            case "definition":
                f["profile"]!["definition"]!["fixture"] = "other";
                f["profile"]!["fingerprint"] = HcsCodec.ProfileFingerprint(Element(f["profile"]!["definition"]!)); break;
            case "state-field": f["components"] = new JsonArray(new JsonObject { ["id"] = "unknown", ["modelId"] = "unknown", ["modelVersion"] = 1, ["state"] = new JsonObject { ["hostPath"] = "forbidden" } }); break;
            case "event-kind":
                f["clock"]!["deviceGenerations"] = new JsonArray(new JsonObject { ["id"] = "unknown", ["generation"] = "0" });
                f["clock"]!["nextSequence"] = "1";
                f["events"] = new JsonArray(new JsonObject { ["dueTick"] = "9007199254740994", ["phase"] = 0, ["deviceId"] = "unknown", ["kind"] = "unknown", ["generation"] = "0", ["sequence"] = "0", ["payload"] = new JsonObject() }); break;
        }
        Assert.Throws<HcsException>(() => Encode(f));
    }
    private static byte[] ReplaceJson(byte[] bytes, byte[] json)
    {
        int old = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
        byte[] result = [.. bytes.AsSpan(0, 32), .. json, .. bytes.AsSpan(32 + old)];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), (uint)json.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)result.Length); Rehash(result); return result;
    }
    [Theory]
    [InlineData("duplicate")][InlineData("nested")][InlineData("float")][InlineData("exponent")]
    [InlineData("bom")][InlineData("utf8")][InlineData("surrogate")][InlineData("comment")][InlineData("comma")]
    public void HostileJsonIsRejected(string mutation)
    {
        byte[] bytes = Encode(); int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
        string json = Encoding.UTF8.GetString(bytes, 32, length);
        json = mutation switch
        {
            "duplicate" => json.Insert(1, "\"assets\":[],"),
            "nested" => json.Replace("\"af\":0", "\"af\":0,\"af\":0"),
            "float" => json.Replace("\"af\":0", "\"af\":0.0"),
            "exponent" => json.Replace("\"af\":0", "\"af\":0e0"),
            "surrogate" => json.Replace("synthetic 01 02 03", "\\ud800"),
            "comment" => json.Insert(1, "/*comment*/"),
            "comma" => json.Insert(json.Length - 1, ","), _ => json
        };
        byte[] data = Encoding.UTF8.GetBytes(json);
        if (mutation == "bom") data = [0xef, 0xbb, 0xbf, .. data];
        if (mutation == "utf8") data[1] = 0xff;
        Assert.Throws<HcsException>(() => Decode(ReplaceJson(bytes, data)));
    }
    [Fact]
    public void UnsignedScalarRejectsNegativeZero()
    {
        byte[] bytes = Encode(); int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
        string json = Encoding.UTF8.GetString(bytes, 32, length).Replace("\"af\":0", "\"af\":-0");
        Assert.Throws<HcsException>(() => Decode(ReplaceJson(bytes, Encoding.UTF8.GetBytes(json))));
    }
    [Fact]
    public void SchemaCannotBeSelectedFromFileOrOmitted()
    {
        using var def = JsonDocument.Parse(Definition.GetRawText());
        var schema = new SyntheticSchema(def.RootElement); def.Dispose();
        var doc = HcsCodec.Read(new MemoryStream(Encode()), schema);
        Assert.Equal("synthetic", doc.Manifest.GetProperty("profile").GetProperty("id").GetString());
        Assert.Throws<ArgumentNullException>(() => HcsCodec.Read(new MemoryStream(Encode()), null!));
        Assert.Throws<ArgumentNullException>(() => HcsCodec.Write(new MemoryStream(), Element(Fixture()), [Blob], null!));
    }
    [Fact]
    public void AllGenericResourceLimitsAreEnforced()
    {
        byte[] bytes = Encode();
        foreach (var l in new[] { new HcsLimits(TotalBytes: 32), new HcsLimits(ManifestBytes: 2), new HcsLimits(BlobBytes: 2), new HcsLimits(BlobCount: 0), new HcsLimits(StringBytes: 1), new HcsLimits(Depth: 2), new HcsLimits(Records: 0) })
            Assert.Throws<HcsException>(() => Decode(bytes, l));
    }
    [Fact]
    public void CanonicalJsonAndProfileFingerprintMatchIndependentGolden()
    {
        using var doc = JsonDocument.Parse("{\"z\":\"é\\n\",\"a\":1}");
        Assert.Equal("{\"a\":1,\"z\":\"é\\u000a\"}", Encoding.UTF8.GetString(HcsCodec.CanonicalJson(doc.RootElement)));
        Assert.Equal("bbae7e9cbdda6a94747464f37c4c79ec60d627a6444145c07157848327dfd373", HcsCodec.ProfileFingerprint(Definition));
    }
    private sealed class NonSeekingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => inner.Read(b, o, Math.Min(n, 1));
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long l) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }
    [Fact]
    public void NonSeekableShortReadsAreSupported() => Assert.Equal(Blob, HcsCodec.Read(new NonSeekingStream(Encode()), new SyntheticSchema()).CopyBlob(1));
}
