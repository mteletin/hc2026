using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class WavHcsBudgetTests
{
    // Lower only the generic blob bound to exercise the exact admission path cheaply.
    private static readonly HcsLimits Limits = new(BlobBytes: 1024 * 1024);
    private static readonly HcsWavPolicy Policy = new(MaxInputBytes: 4 * 1024 * 1024, TransientBytes: 64L * 1024 * 1024);
    private static MachineSchema Schema(HcsWavPolicy? policy = null) => new(MachineProfile.Default, policy);
    private static byte[] IndependentWave()
    {
        const int junk = 2 * 1024 * 1024;
        var bytes = new byte[44 + junk + 8 + 2];
        "RIFF"u8.CopyTo(bytes); Put32(bytes, 4, (uint)bytes.Length - 8); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        Put32(bytes, 16, 16); Put16(bytes, 20, 1); Put16(bytes, 22, 1); Put32(bytes, 24, 1);
        Put32(bytes, 28, 2); Put16(bytes, 32, 2); Put16(bytes, 34, 16);
        "JUNK"u8.CopyTo(bytes.AsSpan(36)); Put32(bytes, 40, junk);
        "data"u8.CopyTo(bytes.AsSpan(44 + junk)); Put32(bytes, 48 + junk, 2);
        return bytes;
    }
    private static void Put16(byte[] b, int at, ushort n) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), n);
    private static void Put32(byte[] b, int at, uint n) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), n);
    private static (JsonElement Manifest, byte[][] Blobs) Fixture()
    {
        var a = TapeHcs2Tests.Compose();
        var read = new WavTapeCodec().Read(IndependentWave(), new(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60));
        Assert.True(read.Success, read.Failure?.Detail);
        var duration = new TapeTime(1800, 1);
        var blank = new TapeSideImage(duration, default, [new(1, [new(false, duration)])], [], [], [], new(TapeFormat.Blank, null, true, [], null));
        var insertion = a.Tape!.Deck.Insert(new(TapeCapacity.C60, read.Value.Image, blank, read.Value.SourceAssets), false);
        Assert.True(insertion.Success, insertion.Failure?.Detail);
        var bytes = a.Save();
        var doc = HcsCodec.Read(new MemoryStream(bytes), Schema());
        return (doc.Manifest, Enumerable.Range(1, doc.BlobCount).Select(i => doc.CopyBlob((uint)i)).ToArray());
    }
    private static byte[] Encode(JsonElement manifest, byte[][] blobs, HcsWavPolicy? policy)
    {
        using var stream = new MemoryStream();
        HcsCodec.Write(stream, manifest, blobs, Schema(policy), Limits);
        return stream.ToArray();
    }
    private static int SourceBlob(JsonElement manifest) => checked((int)manifest.GetProperty("assets").EnumerateArray()
        .Single(a => a.GetProperty("format").GetString() == "wav1").GetProperty("blobId").GetUInt32() - 1);

    [Fact]
    public void GenuineOriginalUsesExplicitAdmissionAndSurvivesExactSourceRoundtrip()
    {
        var (manifest, blobs) = Fixture(); var file = Encode(manifest, blobs, Policy);
        var doc = HcsCodec.Read(new MemoryStream(file), Schema(Policy), Limits);
        var source = SourceBlob(manifest);
        Assert.Equal(blobs[source], doc.CopyBlob((uint)source + 1));
        Assert.Equal(SHA256.HashData(IndependentWave()), SHA256.HashData(doc.CopyBlob((uint)source + 1)));
        Assert.Equal(64 * 1024 * 1024, new HcsLimits().BlobBytes);
        Assert.Equal(256 * 1024 * 1024, new HcsLimits().TotalBytes);
    }

    [Fact]
    public void NoOptInAndExplicitLowerCapsRefuseBeforeAnyOutput()
    {
        var (manifest, blobs) = Fixture();
        foreach (var policy in new HcsWavPolicy?[] { null, Policy with { MaxInputBytes = 1024 * 1024 }, Policy with { TransientBytes = 1 } })
        {
            using var output = new MemoryStream();
            var error = Assert.Throws<HcsException>(() => HcsCodec.Write(output, manifest, blobs, Schema(policy), Limits));
            Assert.Equal(HcsError.LimitExceeded, error.Error); Assert.Equal(0, output.Length);
        }
    }

    [Theory]
    [InlineData("label")] [InlineData("riff")] [InlineData("rate")] [InlineData("span")]
    [InlineData("hash")] [InlineData("alias")] [InlineData("orphan")]
    public void ForgedQualificationOrBrokenIntegrityNeverWritesDestination(string mutation)
    {
        var (manifest, blobs) = Fixture(); var root = JsonNode.Parse(manifest.GetRawText())!;
        var assets = root["assets"]!.AsArray(); var source = assets.Single(a => a!["format"]!.GetValue<string>() == "wav1")!;
        var id = source["blobId"]!.GetValue<int>() - 1;
        switch (mutation)
        {
            case "label": source["kind"] = "rom"; break;
            case "riff": blobs[id][0] = 0; break;
            case "rate": Put32(blobs[id], 24, 0); break;
            case "span": Put32(blobs[id], 40, uint.MaxValue); break;
            case "hash": source["sha256"] = new string('0', 64); break;
            case "alias":
                var rom = assets.Single(a => a!["id"]!.GetValue<string>() == "rom")!;
                rom["blobId"] = id + 1; rom["byteLength"] = source["byteLength"]!.DeepClone(); rom["sha256"] = source["sha256"]!.DeepClone(); break;
            case "orphan": root["components"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "tape")!["state"]!["cassetteAssetId"] = null; break;
        }
        using var output = new MemoryStream();
        Assert.Throws<HcsException>(() => HcsCodec.Write(output, JsonSerializer.SerializeToElement(root), blobs, Schema(Policy), Limits));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("json")] [InlineData("null-side")] [InlineData("null-route")]
    public void MalformedNativeCassetteReturnsStructuredRefusalBeforeOutputOrOriginalRead(string mutation)
    {
        var (manifest, blobs) = Fixture(); var root = JsonNode.Parse(manifest.GetRawText())!;
        var native = root["assets"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "tape-cassette")!;
        var id = native["blobId"]!.GetValue<int>() - 1;
        var dto = JsonNode.Parse(blobs[id])!;
        if (mutation == "null-side") dto["sides"]![0] = null;
        if (mutation == "null-route") dto["sides"]![0]!["sourceRoute"] = null;
        blobs[id] = mutation == "json" ? [(byte)'{'] : JsonSerializer.SerializeToUtf8Bytes(dto);
        native["byteLength"] = blobs[id].Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        native["sha256"] = Convert.ToHexStringLower(SHA256.HashData(blobs[id]));
        manifest = JsonSerializer.SerializeToElement(root);
        using var output = new MemoryStream();
        var ordinary = Limits with { BlobBytes = 4 * 1024 * 1024 };
        Assert.Equal(HcsError.InvalidFormat, Assert.Throws<HcsException>(() => HcsCodec.Write(output, manifest, blobs, Schema(Policy), ordinary)).Error);
        Assert.Equal(0, output.Length);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Write(output, manifest, blobs, Schema(Policy), Limits)).Error);
        Assert.Equal(0, output.Length);
        // Existing generic readers treat the native cassette as opaque; construct a hash-valid hostile file.
        HcsCodec.Write(output, manifest, blobs, Schema(), ordinary);
        var file = output.ToArray();
        using var guarded = new GuardedRead(file);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Read(guarded, Schema(Policy), Limits)).Error);
        Assert.True(guarded.MaximumRead <= 1024 * 1024);
        Assert.Equal(HcsError.InvalidFormat, Assert.Throws<HcsException>(() => HcsCodec.Read(new MemoryStream(file), Schema(Policy), ordinary)).Error);
    }

    [Fact]
    public void ReadRequiresSeekableInputAndRechecksLowerAndTotalLimits()
    {
        var (manifest, blobs) = Fixture(); var file = Encode(manifest, blobs, Policy);
        using var nonseekable = new NonSeekable(file);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Read(nonseekable, Schema(Policy), Limits)).Error);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Read(new MemoryStream(file), Schema(), Limits)).Error);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Read(new MemoryStream(file), Schema(Policy with { MaxInputBytes = 1024 * 1024 }), Limits)).Error);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Read(new MemoryStream(file), Schema(Policy), Limits with { TotalBytes = file.Length - 1 })).Error);
    }

    [Fact]
    public void InvalidOversizedHeaderRefusesBeforeAnyLargePayloadRead()
    {
        var (manifest, blobs) = Fixture(); var file = Encode(manifest, blobs, Policy);
        var offset = 32 + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24));
        var source = SourceBlob(manifest);
        for (var i = 0; i < source; i++) offset += 12 + checked((int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(offset + 4)));
        file[offset + 12] = 0; // Invalid RIFF; stale footer must not be the first refusal.
        using var input = new GuardedRead(file);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Read(input, Schema(Policy), Limits)).Error);
        Assert.True(input.MaximumRead <= 1024 * 1024);
    }

    [Fact]
    public void OwnedOriginalIsRevalidatedWhenInputChangesAfterMetadataProbe()
    {
        var (manifest, blobs) = Fixture(); var file = Encode(manifest, blobs, Policy);
        using var input = new MutatingRead(file);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Read(input, Schema(Policy), Limits)).Error);
        Assert.True(input.Mutated);
    }

    [Fact]
    public void SpoofedWavSourceNeverChangesPriorDirtyDeckOnInsertOrRestore()
    {
        var machine = TapeHcs2Tests.Compose(); var tape = machine.Tape!;
        Assert.True(tape.Deck.Blank(TapeCapacity.C60, false).Success);
        var state = tape.Capture().Value;
        Assert.True(tape.Restore(state with { Component = state.Component with { Dirty = [true, false], EditRevision = ["1", "0"] } }).Success);
        var prior = tape.Deck.Observe(); state = tape.Capture().Value;
        var bytes = IndependentWave();
        var read = new WavTapeCodec().Read(bytes, new(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60));
        Assert.True(read.Success); bytes[0] = 0;
        var candidate = new TapeCassetteImage(TapeCapacity.C60, read.Value.Image, state.Cassette!.B,
            [new("tape-source-0", TapeFormat.Wav, bytes)]);
        Assert.False(tape.Deck.Insert(candidate, true).Success);
        Assert.Equal(prior, tape.Deck.Observe()); Assert.True(tape.Deck.Observe().A.Dirty);
        Assert.False(tape.Restore(state with { Cassette = candidate }).Success);
        Assert.Equal(prior, tape.Deck.Observe()); Assert.True(tape.Deck.Observe().A.Dirty);
    }

    private sealed class GuardedRead(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int MaximumRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            if (buffer.Length > 1024 * 1024) throw new InvalidOperationException("Unexpected large payload read.");
            return base.Read(buffer);
        }
    }
    private sealed class MutatingRead(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool Mutated { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            if (count > 1024 * 1024) { buffer[0] = 0; Mutated = true; }
            return count;
        }
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
