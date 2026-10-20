using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeLegacyHcsTests
{
    private sealed class Cpu : ICpuSnapshotSeam
    {
        private CpuLogicalState state = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, false, false, false, false, false, false, false, 1, 0, 0);
        public void Sync(long tStates) => state = state with { TStates = tStates };
        public CpuLogicalState Capture() => state;
        public void Restore(CpuLogicalState value) => state = value;
    }
    private sealed record Fixture(Machine Machine, DeviceScheduler Clock, CpuTimeMapper Mapper, Cpu Cpu, Ula Ula);
    private static Fixture Compose()
    {
        var p = MachineProfile.Default; var s = new DeviceScheduler(); var clock = new FrameClock(s, 1);
        var memory = new HcMemory(p.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var mapper = CpuTimeMapper.FromProfile(p); var u = new Ula(memory, new KeyboardMatrix(), s, clock, 2, p.Video); var cpu = new Cpu();
        var machine = new Machine(s, clock, new(s, mapper), u, () => { }, () => 0);
        machine.EnableTape(3); machine.AttachSnapshotState(p, clock, memory, mapper, cpu, 2); return new(machine, s, mapper, cpu, u);
    }

    // Authored old-wire fixture, not another emulator: preserves real current profile/clock/
    // stores but encodes exactly the tape1 framing/fields inspected at .81. Its PLAY remainder
    // is deliberately dormant, as old Snapshot() stored it rather than Cursor's observation.
    private static byte[] Tape1File(Fixture f, TapeCursor cursor, bool stopped)
    {
        var current = HcsCodec.Read(new MemoryStream(f.Machine.Save()), new MachineSchema(MachineProfile.Default));
        var root = JsonNode.Parse(current.Manifest.GetRawText())!.AsObject();
        var tape = root["components"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "tape")!;
        tape["modelVersion"] = 1;
        tape["state"] = new JsonObject
        {
            ["state"] = stopped ? "stopped" : "playing", ["blockIndex"] = cursor.BlockIndex, ["stage"] = (uint)cursor.Stage,
            ["pilotConsumed"] = cursor.PilotConsumed, ["nextBit"] = cursor.NextBit, ["half"] = (uint)cursor.Half,
            ["waveformLevel"] = cursor.WaveformLevel, ["idealTarget"] = cursor.IdealTargetNumerator.ToString(CultureInfo.InvariantCulture),
            ["remaining"] = (stopped ? cursor.RemainingNumerator : (Int128)2168 * FrameTiming.MasterHz).ToString(CultureInfo.InvariantCulture),
            ["mediaIdentity"] = cursor.MediaIdentity.ToString(CultureInfo.InvariantCulture),
            ["pendingToken"] = stopped ? "0" : f.Clock.PendingEvents.Single(e => e.DeviceId == 3).InsertionSequence.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var e in root["events"]!.AsArray())
            if (e!["deviceId"]!.GetValue<string>() == "device-3") e["kind"] = "kind-" + (int)cursor.Stage;
        var assets = new JsonArray(); var blobs = new List<byte[]>();
        foreach (var original in current.Manifest.GetProperty("assets").EnumerateArray())
        {
            var id = original.GetProperty("id").GetString()!;
            if (id.StartsWith("tape", StringComparison.Ordinal)) continue;
            var a = JsonNode.Parse(original.GetRawText())!.AsObject();
            blobs.Add(current.CopyBlob(original.GetProperty("blobId").GetUInt32())); a["blobId"] = blobs.Count; assets.Add(a);
        }
        byte[] framed = [1, 0, 0, 0, 2, 0, 0, 0, 0, 0]; // int32 block count, int32 length, flag/checksum
        blobs.Add(framed);
        assets.Add(new JsonObject { ["id"] = "tape", ["kind"] = "tape", ["format"] = "tap1", ["blobId"] = blobs.Count,
            ["byteLength"] = "10", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(framed)), ["rawGeometry"] = null, ["provenance"] = "authored tape1 wire fixture" });
        root["assets"] = assets;
        using var output = new MemoryStream(); HcsCodec.Write(output, JsonSerializer.SerializeToElement(root), blobs, new MachineSchema(MachineProfile.Default));
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldPlayingDormantRemainderAndStoppedHighPhaseRestoreTransactionally(bool stopped)
    {
        var f = Compose(); f.Machine.Tape!.LoadTap([2, 0, 0, 0]); f.Machine.Tape.Play();
        f.Mapper.Map(2217); f.Cpu.Sync(2217); f.Clock.AdvanceTo(11085);
        if (stopped) f.Machine.Tape.Stop();
        var cursor = f.Machine.Tape.Cursor; Assert.True(cursor.WaveformLevel);
        var file = Tape1File(f, cursor, stopped); var restored = Compose(); restored.Machine.Load(file);
        Assert.Equal(cursor.State, restored.Machine.Tape!.Cursor.State);
        Assert.Equal(cursor.Stage, restored.Machine.Tape.Cursor.Stage);
        Assert.Equal(cursor.PilotConsumed, restored.Machine.Tape.Cursor.PilotConsumed);
        Assert.Equal(cursor.RemainingNumerator, restored.Machine.Tape.Cursor.RemainingNumerator);
        Assert.Equal(f.Ula.TapeIn, restored.Ula.TapeIn);
        var newFile = HcsCodec.Read(new MemoryStream(restored.Machine.Save()), new MachineSchema(MachineProfile.Default));
        Assert.Equal(2U, newFile.Manifest.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "tape").GetProperty("modelVersion").GetUInt32());
    }
}
