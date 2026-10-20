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

public sealed class TapeHcs2Tests
{
    private sealed class Cpu : ICpuSnapshotSeam
    {
        private CpuLogicalState state = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, false, false, false, false, false, false, false, 1, 0, 0);
        public CpuLogicalState Capture() => state;
        public void Restore(CpuLogicalState value) => state = value;
    }
    internal static Machine Compose()
    {
        var p = MachineProfile.Default; var s = new DeviceScheduler(); var clock = new FrameClock(s, 1);
        var memory = new HcMemory(p.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var mapper = CpuTimeMapper.FromProfile(p); var u = new Ula(memory, new KeyboardMatrix(), s, clock, 2, p.Video); var cpu = new Cpu();
        var machine = new Machine(s, clock, new(s, mapper), u, () => { }, () => 0);
        machine.EnableTape(3); machine.AttachSnapshotState(p, clock, memory, mapper, cpu, 2); return machine;
    }
    private static HcsDocument Read(byte[] bytes) => HcsCodec.Read(new MemoryStream(bytes), new MachineSchema(MachineProfile.Default));
    private static void AssertV2(HcsDocument doc) => Assert.Equal(2U, doc.Manifest.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "tape").GetProperty("modelVersion").GetUInt32());

    [Fact]
    public void EmptyComposedDeckEmitsTape2WithoutCassetteAssetAndRoundTrips()
    {
        var a = Compose(); var bytes = a.Save(); var doc = Read(bytes); AssertV2(doc);
        Assert.DoesNotContain(doc.Manifest.GetProperty("assets").EnumerateArray(), a => a.GetProperty("id").GetString() == "tape-cassette");
        var b = Compose(); b.Load(bytes); Assert.Equal(bytes, b.Save());
    }
    [Fact]
    public void NativePlayingCassetteRoundTripsExactSignalAndOnePendingEvent()
    {
        var a = Compose(); a.Tape!.Deck.Blank(TapeCapacity.C90, false); a.Tape.Deck.Play();
        var bytes = a.Save(); var doc = Read(bytes); AssertV2(doc);
        var b = Compose(); b.Load(bytes);
        Assert.Equal(a.Tape.Deck.Observe(), b.Tape!.Deck.Observe()); Assert.Equal(bytes, b.Save());
        Assert.Single(doc.Manifest.GetProperty("events").EnumerateArray(), e => e.GetProperty("deviceId").GetString() == "device-3");
    }
    [Fact]
    public void InvalidCrossStateRestoreLeavesFreshMachineAndCanLoadValidFileAfterwards()
    {
        var a = Compose(); a.Tape!.Deck.Blank(TapeCapacity.C60, false); var bytes = a.Save(); var doc = Read(bytes); AssertV2(doc);
        var root = JsonNode.Parse(doc.Manifest.GetRawText())!.AsObject();
        var state = root["components"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "tape")!["state"]!;
        state["position"]!["num"] = "1801"; state["anchorPosition"]!["num"] = "1801";
        using var modified = new MemoryStream();
        HcsCodec.Write(modified, JsonSerializer.SerializeToElement(root), Enumerable.Range(1, doc.BlobCount).Select(i => doc.CopyBlob((uint)i)).ToArray(), new MachineSchema(MachineProfile.Default));
        var target = Compose(); var before = target.Save();
        Assert.Throws<HcsException>(() => target.Load(modified.ToArray()));
        Assert.Equal(before, target.Save()); target.Load(bytes); Assert.Equal(bytes, target.Save());
    }
}
