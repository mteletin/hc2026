using System.Text.Json;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>.hcs profile-name compatibility (07a, KB hc2000 doc spec-07-profiles): the file stores the
/// exact profile record it was saved from, and loading it under a different profile is refused
/// with HcsError.ProfileMismatch. Uses the accepted HCS1 contract as implemented by 04g: no codec
/// or Machine.Save/Load changes here. Synthetic zero-ROM machines the way 04g's
/// HcsMachineSnapshotTests composes them (frame clock 1, flash 2, tape 3, FDC base 100).</summary>
public sealed class HcsSnapshotProfileBindingTests
{
    private sealed class FakeCpu : ICpuSnapshotSeam
    {
        private CpuLogicalState state = new(0x0444, 0, 0, 0, 0, 0, 0x5000, 0x0100, 0, 0, 0, 0, 0,
            0, 0, 0, true, false, false, false, false, false, false, 1, 0, 0);
        public void Sync(long tStates) => state = state with { TStates = tStates, Pc = (ushort)(0x0100 + tStates / 4) };
        public CpuLogicalState Capture() => state;
        public void Restore(CpuLogicalState restored) => state = restored;
    }

    private sealed record Wiring(MachineProfile Profile, Machine Machine, FakeCpu Cpu, HcMemory Memory,
        FloppyController Floppy, DeviceScheduler Scheduler, CpuTimeMapper Mapper);

    private static Wiring Compose(MachineProfile profile)
    {
        // Synthetic zero ROMs, explicitly NOT a hardware/boot fixture; the real profile record
        // (Name, Version, Io, turbo, Memory and Video choices) is used unmodified.
        var mapper = CpuTimeMapper.FromProfile(profile);
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        var memory = new HcMemory(profile.Memory with { RomProvenance = "synthetic hcs snapshot fixture" },
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ula = new Ula(memory, new KeyboardMatrix(), scheduler, clock, 2, profile.Video);
        var floppy = new FloppyController(scheduler, 100,
            new FloppyDrive(new(), Dsk.Create(new(80, 2, 9, 512))), new FloppyDrive(new()));
        var cpu = new FakeCpu();
        long states = 0;
        var machine = new Machine(scheduler, clock, adapter, ula,
            () => { states += 4; cpu.Sync(states); }, () => states);
        machine.EnableTape(3);
        machine.AttachSnapshotState(profile, clock, memory, mapper, cpu, 2, floppy);
        return new(profile, machine, cpu, memory, floppy, scheduler, mapper);
    }

    // Read with the schema of the profile the file was saved under (a wrong-profile read throws
    // ProfileMismatch, which is exactly the refusal under test).
    private static JsonElement ProfileNode(byte[] file, MachineProfile savedAs)
        => HcsCodec.Read(new MemoryStream(file), new MachineSchema(savedAs)).Manifest.GetProperty("profile");

    private static void DeepEqual(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) throw new Xunit.Sdk.XunitException($"Json kind differs: {a.ValueKind} vs {b.ValueKind}");
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var ak = a.EnumerateObject().ToDictionary(o => o.Name, o => o.Value);
                var bk = b.EnumerateObject().ToDictionary(o => o.Name, o => o.Value);
                Assert.Equal(ak.Keys.OrderBy(k => k), bk.Keys.OrderBy(k => k));
                foreach (var (k, av) in ak) DeepEqual(av, bk[k]);
                break;
            case JsonValueKind.Array:
                var aa = a.EnumerateArray().ToList();
                var ba = b.EnumerateArray().ToList();
                Assert.Equal(aa.Count, ba.Count);
                for (int i = 0; i < aa.Count; i++) DeepEqual(aa[i], ba[i]);
                break;
            default:
                Assert.Equal(a.ToString(), b.ToString());
                break;
        }
    }

    [Fact]
    public void SaveStoresTheExactProfileRecord()
    {
        var a = Compose(MachineProfile.Hc2000V2);
        a.Machine.RunFrame();
        var file = a.Machine.Save();
        var p = ProfileNode(file, a.Profile);
        Assert.Equal("hc2000-v2", p.GetProperty("id").GetString());
        Assert.Equal(1, p.GetProperty("version").GetInt32());
        var definition = MachineSchema.Definition(a.Profile);
        var fingerprint = HcsCodec.ProfileFingerprint(definition);
        Assert.Equal(fingerprint, p.GetProperty("fingerprint").GetString());
        // The stored definition is deep-equal (value for value, every key) to the exact profile
        // record: same identity, turbo, IO policy, memory and video choices as the saving machine.
        DeepEqual(definition, p.GetProperty("definition"));
        Assert.Equal("hc2000-v2", p.GetProperty("definition").GetProperty("name").GetString());
        Assert.Equal(1, p.GetProperty("definition").GetProperty("cpuTurboMultiplier").GetInt32());
        Assert.Equal("faithful-odd-fdc-v1", p.GetProperty("definition").GetProperty("io").GetString());
        Assert.Equal("v2", p.GetProperty("definition").GetProperty("memory").GetProperty("romLayout").GetString());
        // Same-profile load still round-trips after the binding is verified.
        var b = Compose(MachineProfile.Hc2000V2);
        b.Machine.Load(file);
        Assert.Equal(a.Machine.MasterTick, b.Machine.MasterTick);
        Assert.Equal(a.Cpu.Capture(), b.Cpu.Capture());
    }

    [Fact]
    public void LoadRefusesTheRealV1ProfileOnTheV2Machine()
    {
        var a = Compose(MachineProfile.Hc2000V1);
        a.Machine.RunFrame();
        var file = a.Machine.Save();
        Assert.Equal("hc2000-v1", ProfileNode(file, a.Profile).GetProperty("id").GetString());
        var b = Compose(MachineProfile.Hc2000V2);
        var ex = Assert.Throws<HcsException>(() => b.Machine.Load(file));
        Assert.Equal(HcsError.ProfileMismatch, ex.Error);
    }

    [Fact]
    public void LoadRefusesTheRealV2ProfileOnTheV1Machine()
    {
        var a = Compose(MachineProfile.Hc2000V2);
        a.Machine.RunFrame();
        var file = a.Machine.Save();
        var b = Compose(MachineProfile.Hc2000V1);
        var ex = Assert.Throws<HcsException>(() => b.Machine.Load(file));
        Assert.Equal(HcsError.ProfileMismatch, ex.Error);
    }
}
