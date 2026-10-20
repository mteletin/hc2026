using System.Security.Cryptography;
using System.Text;
using Hc2000.Machine;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Tests;

/// <summary>Synthetic machine for snapshot tests, wired exactly like
/// KB hc2000 doc spec-04-machine composes it (frame clock 1, flash 2, tape 3, FDC base 100,
/// zero ROMs, one mounted disk, idle FDC, fresh tape). Copied from
/// tests/Hc2000.Machine.Tests/HcsMachineSnapshotTests.cs because App.Tests does not
/// reference the Machine test project; it is a fixture, not a hardware/boot claim.</summary>
internal static class HcsMachineFixture
{
    public sealed class FakeCpu : ICpuSnapshotSeam
    {
        private CpuLogicalState state = new(0x0444, 0, 0, 0, 0, 0, 0x5000, 0x0100, 0, 0, 0, 0, 0,
            0, 0, 0, true, false, false, false, false, false, false, 1, 0, 0);
        /// <summary>Keep the seam's T-states locked to the machine's drained CPU counter.</summary>
        public void Sync(long tStates) => state = state with { TStates = tStates, Pc = (ushort)(0x0100 + tStates / 4) };
        public CpuLogicalState Capture() => state;
        public void Restore(CpuLogicalState restored) => state = restored;
    }

    public sealed record Wiring(MachineProfile Profile, LiveMachine Machine, FakeCpu Cpu,
        HcMemory Memory, FloppyController Floppy, DeviceScheduler Scheduler, CpuTimeMapper Mapper);

    public static Wiring Compose(MachineProfile? profile = null)
    {
        var p = profile ?? MachineProfile.Default;
        var mapper = CpuTimeMapper.FromProfile(p);
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        var memory = new HcMemory(p.Memory with { RomProvenance = "synthetic hcs snapshot fixture" },
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ula = new Ula(memory, new KeyboardMatrix(), scheduler, clock, 2, p.Video);
        var floppy = new FloppyController(scheduler, 100,
            new FloppyDrive(new(), Dsk.Create(new(80, 2, 9, 512))), new FloppyDrive(new()));
        var cpu = new FakeCpu();
        long states = 0;
        var machine = new LiveMachine(scheduler, clock, adapter, ula,
            () => { states += 4; cpu.Sync(states); }, () => states);
        machine.EnableTape(3);
        machine.AttachSnapshotState(p, clock, memory, mapper, cpu, 2, floppy);
        return new(p, machine, cpu, memory, floppy, scheduler, mapper);
    }

    /// <summary>Patches raw file bytes in place and recomputes the trailing SHA-256, so the
    /// container checksum stays valid and the failure surfaces in schema/model validation
    /// (Load-side rejection, copied from the 04g test).</summary>
    public static byte[] PatchAndRehash(byte[] file, byte[] search, byte[] replace)
    {
        var index = file.IndexOf(search);
        Assert.True(index >= 0, "patch target not found in manifest");
        Assert.Equal(search.Length, replace.Length);
        replace.CopyTo(file, index);
        var hash = SHA256.HashData(file.AsSpan(0, file.Length - 32));
        hash.CopyTo(file.AsSpan(file.Length - 32));
        return file;
    }

    public static string AssetSha(HcsDocument doc, string assetId)
    {
        foreach (var asset in doc.Manifest.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("id").GetString() == assetId) return asset.GetProperty("sha256").GetString()!;
        throw new Xunit.Sdk.XunitException($"asset {assetId} not in manifest");
    }
}
