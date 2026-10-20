using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Io;
using Hc2000.Machine.Input;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

[CollectionDefinition("m-base-view-snapshot", DisableParallelization = true)]
public sealed class MBaseViewSnapshotCollection { }

[Collection("m-base-view-snapshot")]
public sealed class MBaseViewSnapshotTests
{
    private static MachineProfile Profile => MachineProfile.Hc2000V2PagedMTransferV1;
    // SHA256 of 1GiB of zeros except byte7=23 and byte3FFFC007=65 (hex).
    private const string ExpandedHash = "32910355777056363c670b7ef0e7699aa0aa5eb99caf4344e407b541adcbd33d";

    public static IEnumerable<object[]> Malformed()
    {
        foreach (var key in new[] { "v", "view", "bank", "screen", "rom", "locked", "transfer", "ramBytes", "ramSha256" })
            yield return ["missing:" + key, "InvalidFormat: fields"];
        yield return ["extra", "InvalidFormat: fields"];
        yield return ["v1", "UnsupportedVersion: ram.paged"];
        yield return ["v3", "UnsupportedVersion: ram.paged"];
        yield return ["view", "StateInconsistent: ram.paged view"];
        yield return ["view-number", "InvalidFormat: string"];
        foreach (var key in new[] { "screen", "rom", "locked", "transfer" })
        {
            yield return ["bool-number:" + key, "InvalidFormat: " + key];
            yield return ["bool-null:" + key, "InvalidFormat: " + key];
            yield return ["bool-text:" + key, "InvalidFormat: " + key];
        }
        foreach (var key in new[] { "bank-negative", "bank-big", "bank-string" })
            yield return [key, "InvalidFormat: uint"];
        yield return ["bank-fraction", "InvalidFormat: integer"];
        yield return ["length-short", "StateInconsistent: ram.paged length"];
        yield return ["length-long", "StateInconsistent: ram.paged length"];
        yield return ["length-number", "InvalidFormat: string"];
        yield return ["length-leading-zero", "InvalidFormat: decimal64"];
        yield return ["length-huge", "InvalidFormat: decimal64"];
        foreach (var key in new[] { "hash-short", "hash-upper", "hash-nonhex" })
            yield return [key, "InvalidFormat: ram.paged hash"];
    }

    [Theory, MemberData(nameof(Malformed))]
    public void StrictLiteralV2RejectsMalformedFieldsBeforeBlobValidation(string change, string expected)
    {
        var paged = Paged();
        if (change.StartsWith("missing:", StringComparison.Ordinal)) paged.Remove(change[8..]);
        else if (change.StartsWith("bool-number:", StringComparison.Ordinal)) paged[change[12..]] = 1;
        else if (change.StartsWith("bool-null:", StringComparison.Ordinal)) paged[change[10..]] = null;
        else if (change.StartsWith("bool-text:", StringComparison.Ordinal)) paged[change[10..]] = "true";
        else switch (change)
        {
            case "extra": paged["unexpected"] = false; break;
            case "v1": paged["v"] = 1; break;
            case "v3": paged["v"] = 3; break;
            case "view": paged["view"] = "base"; break;
            case "view-number": paged["view"] = 1; break;
            case "bank-negative": paged["bank"] = -1; break;
            case "bank-big": paged["bank"] = 65536; break;
            case "bank-string": paged["bank"] = "65535"; break;
            case "bank-fraction": paged["bank"] = 1.5; break;
            case "length-short": paged["ramBytes"] = "1073741823"; break;
            case "length-long": paged["ramBytes"] = "1073741825"; break;
            case "length-number": paged["ramBytes"] = 1073741824; break;
            case "length-leading-zero": paged["ramBytes"] = "01073741824"; break;
            case "length-huge": paged["ramBytes"] = "18446744073709551615"; break;
            case "hash-short": paged["ramSha256"] = "00"; break;
            case "hash-upper": paged["ramSha256"] = new string('A', 64); break;
            case "hash-nonhex": paged["ramSha256"] = new string('g', 64); break;
            default: throw new ArgumentException(change);
        }
        var error = Assert.Throws<HcsException>(() => HcsCodec.Write(Stream.Null,
            JsonSerializer.SerializeToElement(Detached(Profile, paged)), [], new MachineSchema(Profile)));
        // Pin the actual rejection site: an unrelated incomplete-fixture failure cannot pass.
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void V1ObjectWithoutV2FieldsIsNotConvertedOnM()
    {
        var paged = Paged(); paged["v"] = 1; paged.Remove("view"); paged.Remove("transfer");
        var error = Assert.Throws<HcsException>(() => HcsCodec.Write(Stream.Null,
            JsonSerializer.SerializeToElement(Detached(Profile, paged)), [], new MachineSchema(Profile)));
        Assert.Equal("InvalidFormat: fields", error.Message);
    }

    [Theory]
    [InlineData("hc2000-v2")]
    [InlineData("hc2000-v2-paged-v1")]
    [InlineData("hc2000-v2-ram-banks-v1")]
    public void FileProfileCannotPromoteTrustedMIdentity(string foreign)
    {
        var manifest = Detached(Profile, Paged());
        manifest["profile"]!["id"] = foreign;
        var error = Assert.Throws<HcsException>(() => HcsCodec.Write(Stream.Null,
            JsonSerializer.SerializeToElement(manifest), [], new MachineSchema(Profile)));
        Assert.Equal(HcsError.ProfileMismatch, error.Error);
        Assert.Equal(0x40010000, Profile.Memory.RamBytes);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("format")]
    [InlineData("store")]
    public void RawBlobExceptionIsOnlyForExactTrustedLogicalStore(string change)
    {
        // A sparse file supplies extent only. Every case must refuse BEFORE reading/allocating
        // the payload; no file-supplied size becomes the logical RAM allocation.
        var manifest = Detached(Profile, Paged());
        var length = change == "short" ? 0x4000ffff : change == "long" ? 0x40010001 : 0x40010000;
        var id = change == "id" ? "foreign" : "ram";
        manifest["assets"] = new JsonArray(new JsonObject {
            ["id"] = id, ["kind"] = change == "kind" ? "rom" : "ram",
            ["format"] = change == "format" ? "raw2" : "raw1", ["blobId"] = 1,
            ["byteLength"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sha256"] = new string('0', 64), ["rawGeometry"] = null, ["provenance"] = "literal extent-only fixture" });
        manifest["stores"] = new JsonArray(new JsonObject { ["id"] = change == "store" ? "foreign" : "ram", ["assetId"] = id });
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var header = Header("truncated");
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)(76L + json.Length + length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 1);
        var record = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(record, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), (ulong)length);
        var path = Path.Combine(Path.GetTempPath(), "m-sparse-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.DeleteOnClose);
        file.Write(header); file.Write(json); file.Write(record); file.SetLength(76L + json.Length + length); file.Position = 0;
        var error = Assert.Throws<HcsException>(() => HcsCodec.Read(file, new MachineSchema(Profile),
            new HcsLimits(TotalBytes: 0x44000000)));
        Assert.Equal(HcsError.LimitExceeded, error.Error);
        Assert.Equal("LimitExceeded: blob length", error.Message);
        Assert.Equal(44L + json.Length, file.Position);
        Assert.Equal(0x40010000, Profile.Memory.RamBytes);
    }

    private sealed class SnapshotCpu : ICpuSnapshotSeam
    {
        public CpuLogicalState State = new(0x1234, 0x2345, 0x3456, 0x4567, 0, 0,
            0xdeb0, 0x1357, 0, 0, 0, 0, 0, 0, 0, 0,
            false, false, false, false, false, false, false, 1, 0, 0);
        public CpuLogicalState Capture() => State;
        public void Restore(CpuLogicalState value) => State = value;
    }

    [Fact]
    public void LiteralActiveLockedC5AndOffStatesRestoreRamCpuAndEscapeAtomically()
    {
        var memory = new HcMemory(Profile.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ports = new PortDecoder(HcIoProfile.WithPagedRam(memory, new PortDevice(), new PortDevice(),
            new PortDevice(), new PortDevice(), new PortDevice(), new PortDevice()));
        var scheduler = new DeviceScheduler(); var clock = new FrameClock(scheduler, 1);
        var keyboard = new KeyboardMatrix(); var ula = new Ula(memory, keyboard, scheduler, clock, 2);
        var cpu = new SnapshotCpu(); var expectedCpu = cpu.State;
        var snapshot = new MachineSnapshot(Profile, scheduler, clock, CpuTimeMapper.FromProfile(Profile),
            memory, ula, keyboard, cpu, 2, null, null);
        void Out(ushort port, byte value) => ports.Write(port, value, 0);
        byte In(ushort port) => ports.Read(port, 0);
        void Disturb()
        {
            memory.ResetPaging(); memory.Ram.Span[0x8007] = 0x19;
            memory.Ram.Span[0x4000c007] = 0x19; memory.Ram.Span[0xdb15] = 0x19;
            memory.Ram.Span[0xdd01] = 0x19; memory.Ram.Span[0xdeb0] = 0x19;
            cpu.State = cpu.State with { Pc = 0x9999, Sp = 0x8888 };
            GC.Collect();
        }
        void CheckRamCpu()
        {
            Assert.Equal(0x81, memory.Ram.Span[0x8007]);
            Assert.Equal(0x23, memory.Ram.Span[0x10007]);
            Assert.Equal(0x65, memory.Ram.Span[0x4000c007]);
            Assert.Equal(0x42, memory.Ram.Span[0xdb15]);
            Assert.Equal(0x53, memory.Ram.Span[0xdd01]);
            Assert.Equal(0x64, memory.Ram.Span[0xdeb0]);
            Assert.Equal(expectedCpu, cpu.State);
        }
        memory.Ram.Span[0x8007] = 0x81; memory.Ram.Span[0x10007] = 0x23;
        memory.Ram.Span[0x4000c007] = 0x65;
        memory.Ram.Span[0xdb15] = 0x42; memory.Ram.Span[0xdd01] = 0x53; memory.Ram.Span[0xdeb0] = 0x64;
        memory.SetCpm(true); Out(0x7dfd, 0xff); Out(0x7efd, 0xff);
        Out(0x7cfd, 1); Out(0x7ffd, 0x3f);
        Assert.Equal(0x61, In(0x7cfd));
        var path = Path.Combine(Path.GetTempPath(), "m-snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = File.Create(path)) snapshot.Save(file);
            // Release capture's detached GiB before the next detached-load copy plan. This is
            // deliberately test-only resource discipline, not a machine lifecycle operation.
            GC.Collect();
            var captured = ReadManifest(path);
            var actual = MemoryState(captured)["paged"]!.AsObject();
            var literal = Paged(); literal["ramSha256"] = ExpandedHash;
            Assert.True(JsonNode.DeepEquals(literal, actual), actual.ToJsonString());
            var ram = captured["assets"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "ram")!;
            Assert.Equal("1073807360", ram["byteLength"]!.GetValue<string>());
            foreach (var cpm in new[] { true, false })
            {
                Disturb();
                // Replace the ENTIRE paged object by the independent literal; only outer framing
                // and unrelated component tables are reused from capture.
                using (var file = Rewrite(path, root => {
                    MemoryState(root)["paged"] = literal.DeepClone(); MemoryState(root)["cpm"] = cpm;
                })) snapshot.Load(file);
                CheckRamCpu();
                Assert.Equal(cpm, memory.Latches.Cpm);
                Assert.True(memory.Latches.PagedTransfer); Assert.True(memory.Latches.PagedEnabled);
                Assert.Equal(0x61, In(0x7cfd)); Assert.Equal(0xff, In(0x7efd)); Assert.Equal(0xff, In(0x7dfd));
                Assert.Equal(0x3f, In(0x7ffd));
                Assert.Equal(cpm ? 0x65 : 0x81, memory.Read(0x8007));
                Out(0x7efd, 0); Assert.Equal(0xff, In(0x7efd));
                Out(0x7cfd, 0); Assert.Equal(0x60, In(0x7cfd));
                Assert.False(memory.Latches.PagedTransfer); Assert.Equal(0x81, memory.Read(0x8007));
            }
            // A valid outer checksum cannot disguise a wrong expanded-RAM identity.
            using (var bad = Rewrite(path, root => MemoryState(root)["paged"]!["ramSha256"] = new string('0', 64)))
            {
                Disturb(); var before = memory.Latches; var beforeCpu = cpu.State;
                var error = Assert.Throws<HcsException>(() => snapshot.Load(bad));
                Assert.Equal(HcsError.AssetMismatch, error.Error);
                Assert.Equal(before, memory.Latches); Assert.Equal(beforeCpu, cpu.State);
                Assert.Equal(0x19, memory.Ram.Span[0x4000c007]); Assert.Equal(0x19, memory.Ram.Span[0xdb15]);
            }
            foreach (var missing in new[] { false, true })
            {
                Disturb();
                using (var file = Rewrite(path, root => {
                    if (missing) MemoryState(root).Remove("paged"); else MemoryState(root)["paged"] = null;
                })) snapshot.Load(file);
                CheckRamCpu();
                Assert.False(memory.Latches.PagedEnabled); Assert.False(memory.Latches.PagedTransfer);
                Assert.Equal(0, memory.Latches.PagedBank);
                foreach (ushort port in new ushort[] { 0x7cfd, 0x7dfd, 0x7efd, 0x7ffd })
                { Assert.Equal(0xff, In(port)); Out(port, 1); Assert.Equal(0xff, In(port)); }
                Assert.Equal(0x81, memory.Read(0x8007));
                memory.ResetPaging(); Assert.Equal(0xff, In(0x7cfd));
            }
            // Header/resource/truncation refusals must not adopt even CPU or latch state.
            foreach (var mode in new[] { "total", "manifest", "count", "truncated" })
            {
                Disturb(); var before = memory.Latches; var beforeCpu = cpu.State;
                var header = Header(mode);
                using var file = new MemoryStream(header);
                var error = Assert.Throws<HcsException>(() => snapshot.Load(file));
                Assert.Equal(mode == "truncated" ? HcsError.InvalidFormat : HcsError.LimitExceeded, error.Error);
                Assert.Equal(before, memory.Latches); Assert.Equal(beforeCpu, cpu.State);
                Assert.Equal(0x19, memory.Ram.Span[0x8007]);
            }
        }
        finally { File.Delete(path); }
    }

    private static byte[] Header(string mode)
    {
        byte[] bytes = new byte[32];
        new byte[] { 0x48, 0x43, 0x53, 0x0d, 0x0a, 0x1a, 0x0a, 0 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), mode == "total" ? 0x44000001UL : 0x44000000UL);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), mode == "manifest" ? 0x1000001U : 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), mode == "count" ? 1025U : 0);
        return bytes;
    }

    private static JsonObject MemoryState(JsonObject root) => root["components"]!.AsArray()
        .Single(c => c!["id"]!.GetValue<string>() == "memory")!["state"]!.AsObject();

    private static JsonObject ReadManifest(string path)
    {
        using var file = File.OpenRead(path); var header = new byte[32]; file.ReadExactly(header);
        var json = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24))]; file.ReadExactly(json);
        return JsonNode.Parse(json)!.AsObject();
    }

    // Bounded streaming rewrite: no in-memory HCS/GiB array or CopyBlob. The footer is recomputed
    // so refusals exercise the logical schema/hash, not merely a broken outer checksum.
    private static FileStream Rewrite(string path, Action<JsonObject> change)
    {
        using var input = File.OpenRead(path); var header = new byte[32]; input.ReadExactly(header);
        var oldLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        var oldJson = new byte[oldLength]; input.ReadExactly(oldJson);
        var root = JsonNode.Parse(oldJson)!.AsObject(); change(root);
        var json = JsonSerializer.SerializeToUtf8Bytes(root);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)(input.Length - oldLength + json.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)json.Length);
        var output = new FileStream(path + "-rewrite", FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            1024 * 1024, FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Write(ReadOnlySpan<byte> bytes) { output.Write(bytes); hash.AppendData(bytes); }
        Write(header); Write(json);
        var buffer = new byte[1024 * 1024]; var remaining = input.Length - input.Position - 32;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, buffer.Length); input.ReadExactly(buffer.AsSpan(0, count));
            Write(buffer.AsSpan(0, count)); remaining -= count;
        }
        output.Write(hash.GetHashAndReset()); output.Flush(); output.Position = 0;
        return output;
    }
    // This is a literal wire contract, not output from the production paging encoder.
    private static JsonObject Paged() => JsonNode.Parse("""
        {"v":2,"view":"m-transfer","bank":65535,"screen":true,"rom":true,
         "locked":true,"transfer":true,"ramBytes":"1073741824",
         "ramSha256":"0000000000000000000000000000000000000000000000000000000000000000"}
        """)!.AsObject();

    [Theory]
    [InlineData("faithful")]
    [InlineData("banks")]
    [InlineData("paged-v1")]
    public void ForeignProfilesRejectLiteralV2(string identity)
    {
        var profile = identity switch
        {
            "banks" => MachineProfile.Hc2000V2RamBanksV1,
            "paged-v1" => MachineProfile.Hc2000V2PagedV1,
            _ => MachineProfile.Default
        };
        var manifest = Detached(profile, Paged());
        Assert.Throws<HcsException>(() => HcsCodec.Write(Stream.Null,
            JsonSerializer.SerializeToElement(manifest), [], new MachineSchema(profile)));
    }

    private static JsonObject Detached(MachineProfile profile, JsonNode? paged)
    {
        var definition = MachineSchema.Definition(profile);
        return new JsonObject
        {
            ["profile"] = new JsonObject { ["id"] = profile.Name, ["version"] = 1,
                ["definition"] = JsonNode.Parse(definition.GetRawText()),
                ["fingerprint"] = HcsCodec.ProfileFingerprint(definition) },
            ["assets"] = new JsonArray(), ["stores"] = new JsonArray(),
            ["components"] = new JsonArray(new JsonObject { ["id"] = "memory", ["modelId"] = "memory",
                ["modelVersion"] = 1, ["state"] = new JsonObject {
                    ["port7e"] = 0, ["cpm"] = false, ["shadow"] = false, ["paged"] = paged } }),
            ["cpu"] = new CpuLogicalState(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, false, false, false, false, false, false, false, 1, 0, 0).ToJson(),
            ["clock"] = new JsonObject { ["epoch"] = "0", ["now"] = "0", ["masterHz"] = "17500000",
                ["dividerNumerator"] = "5", ["dividerDenominator"] = "1", ["lastCpuTStates"] = "0",
                ["mapperWhole"] = "0", ["mapperRemainder"] = "0", ["nextSequence"] = "0", ["deviceGenerations"] = new JsonArray() },
            ["events"] = new JsonArray(), ["pendingOutput"] = new JsonArray()
        };
    }
}
