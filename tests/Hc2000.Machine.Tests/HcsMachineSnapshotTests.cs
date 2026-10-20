using System.Security.Cryptography;
using System.Text;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>.hcs logical machine snapshot round-trip and rejection tests (04g).
/// Wires a synthetic machine (zero ROMs, one mounted disk, idle FDC, fresh tape) the way
/// KB hc2000 doc spec-04-machine composes it: frame clock 1, flash 2, tape 3, FDC base 100.</summary>
public sealed class HcsMachineSnapshotTests
{
    private sealed class FakeCpu : ICpuSnapshotSeam
    {
        private CpuLogicalState state = new(0x0444, 0, 0, 0, 0, 0, 0x5000, 0x0100, 0, 0, 0, 0, 0,
            0, 0, 0, true, false, false, false, false, false, false, 1, 0, 0);
        /// <summary>Keep the seam's T-states locked to the machine's drained CPU counter.</summary>
        public void Sync(long tStates) => state = state with { TStates = tStates, Pc = (ushort)(0x0100 + tStates / 4) };
        public CpuLogicalState Capture() => state;
        public void Restore(CpuLogicalState restored) => state = restored;
    }

    private sealed record Wiring(MachineProfile Profile, Machine Machine, FakeCpu Cpu,
        HcMemory Memory, FloppyController Floppy, DeviceScheduler Scheduler, CpuTimeMapper Mapper);

    private static Wiring Compose(MachineProfile? profile = null)
    {
        var p = profile ?? MachineProfile.Default;
        var mapper = CpuTimeMapper.FromProfile(p);
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        // Synthetic zero ROMs, explicitly NOT a hardware/boot fixture.
        var memory = new HcMemory(p.Memory with { RomProvenance = "synthetic hcs snapshot fixture" },
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ula = new Ula(memory, new KeyboardMatrix(), scheduler, clock, 2, p.Video);
        var floppy = new FloppyController(scheduler, 100,
            new FloppyDrive(new(), Dsk.Create(new(80, 2, 9, 512))), new FloppyDrive(new()));
        var cpu = new FakeCpu();
        long states = 0;
        var machine = new Machine(scheduler, clock, adapter, ula,
            () => { states += 4; cpu.Sync(states); }, () => states);
        machine.EnableTape(3);
        machine.AttachSnapshotState(p, clock, memory, mapper, cpu, 2, floppy);
        return new(p, machine, cpu, memory, floppy, scheduler, mapper);
    }

    /// <summary>Patches raw file bytes in place and recomputes the trailing SHA-256, so the
    /// container checksum stays valid and the failure surfaces in schema/model validation
    /// (HcsCodec.Write re-validates and would throw first; this proves Load-side rejection).</summary>
    private static byte[] PatchAndRehash(byte[] file, byte[] search, byte[] replace)
    {
        var index = file.IndexOf(search);
        Assert.True(index >= 0, "patch target not found in manifest");
        Assert.Equal(search.Length, replace.Length);
        replace.CopyTo(file, index);
        var hash = SHA256.HashData(file.AsSpan(0, file.Length - 32));
        hash.CopyTo(file.AsSpan(file.Length - 32));
        return file;
    }

    private static string AssetSha(HcsDocument doc, string assetId)
    {
        foreach (var asset in doc.Manifest.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("id").GetString() == assetId) return asset.GetProperty("sha256").GetString()!;
        throw new Xunit.Sdk.XunitException($"asset {assetId} not in manifest");
    }

    [Fact]
    public void RoundTripRestoresDeepEqualityAndIsDeterministic()
    {
        var a = Compose();
        a.Machine.RunFrame();
        a.Machine.RunFrame();
        var file = a.Machine.Save();

        var b = Compose();
        b.Machine.Load(file);

        Assert.Equal(2L * FrameTiming.FrameTicks, b.Machine.MasterTick);
        Assert.Equal(a.Machine.MasterTick, b.Machine.MasterTick);
        Assert.Equal(a.Cpu.Capture(), b.Cpu.Capture()); // record deep equality
        Assert.Equal(a.Memory.Ram.ToArray(), b.Memory.Ram.ToArray());
        Assert.Equal(a.Memory.ShadowRam.ToArray(), b.Memory.ShadowRam.ToArray());
        // A re-save of the restored machine reproduces the same document: everything Load
        // could restore (clock, mapper, stores, FDC, tape, pending events) came back equal.
        Assert.Equal(file, b.Machine.Save());
    }

    [Fact]
    public void LoadRejectsAFileFromADifferentProfileChoice()
    {
        var a = Compose();
        a.Machine.RunFrame();
        var file = a.Machine.Save();
        var b = Compose(MachineProfile.Default with { CpuTurboMultiplier = 2 }); // different definition fingerprint
        var ex = Assert.Throws<HcsException>(() => b.Machine.Load(file));
        Assert.Equal(HcsError.ProfileMismatch, ex.Error);
    }

    [Fact]
    public void LoadRejectsATamperedAssetHash()
    {
        var a = Compose();
        a.Machine.RunFrame();
        var file = a.Machine.Save();
        using (var input = new MemoryStream(file))
        {
            var doc = HcsCodec.Read(input, new MachineSchema(a.Profile));
            var sha = Encoding.UTF8.GetBytes(AssetSha(doc, "ram"));
            var flipped = (byte[])sha.Clone();
            flipped[0] = (byte)(sha[0] == 'a' ? 'b' : 'a'); // valid hex, different value, same length
            file = PatchAndRehash(file, sha, flipped);
        }
        var b = Compose();
        var ex = Assert.Throws<HcsException>(() => b.Machine.Load(file));
        Assert.Equal(HcsError.AssetMismatch, ex.Error);
    }

    [Fact]
    public void SaveFailsWhenTheFdcIsNotIdle()
    {
        var a = Compose();
        a.Machine.RunFrame();
        // De-assert reset and issue a read command: the FDC is mid-operation (not idle).
        a.Floppy.WriteLatch(0x1a);
        a.Floppy.Fdc.WriteData(0x06); // READ DATA CMD (9 bytes)
        Assert.False(a.Floppy.Fdc.TryIdleSnapshot(out _, out _, out _, out _));
        var ex = Assert.Throws<HcsException>(() => a.Machine.Save());
        Assert.Equal(HcsError.StateUnsupported, ex.Error);
    }

    [Fact]
    public void LoadFailsWhenTheTargetMachineIsNotFresh()
    {
        var a = Compose();
        a.Machine.RunFrame();
        var file = a.Machine.Save();
        var b = Compose();
        b.Machine.RunFrame(); // Now > 0, mapper advanced: no longer a fresh epoch
        var ex = Assert.Throws<HcsException>(() => b.Machine.Load(file));
        Assert.Equal(HcsError.StateInconsistent, ex.Error);
    }

    [Fact]
    public void LoadRejectsATamperedClockDivider()
    {
        var a = Compose();
        a.Machine.RunFrame();
        var file = PatchAndRehash(a.Machine.Save(),
            Encoding.UTF8.GetBytes("\"dividerNumerator\":\"5\""),
            Encoding.UTF8.GetBytes("\"dividerNumerator\":\"4\"")); // passes base clock identity, diverges from the live mapper
        var b = Compose();
        var ex = Assert.Throws<HcsException>(() => b.Machine.Load(file));
        Assert.Equal(HcsError.StateInconsistent, ex.Error);
    }

    [Fact]
    public void SaveFailsWhenTheClockAndCpuMapperAreDesynchronized()
    {
        var a = Compose();
        a.Scheduler.AdvanceTo(100); // master clock moved without a drained CPU boundary
        var ex = Assert.Throws<HcsException>(() => a.Machine.Save());
        Assert.Equal(HcsError.StateUnsupported, ex.Error);
    }

    [Fact]
    public void LoadRejectsFdcModelVersion1WithUnsupportedVersion()
    {
        var a = Compose();
        a.Machine.RunFrame();
        var file = a.Machine.Save();
        // The FDC component now writes modelVersion 2. Patch it back to 1:
        // the schema must reject with UnsupportedVersion before touching any new fields.
        var file2 = PatchAndRehash(file,
            Encoding.UTF8.GetBytes("\"fdc\",\"modelId\":\"fdc\",\"modelVersion\":2"),
            Encoding.UTF8.GetBytes("\"fdc\",\"modelId\":\"fdc\",\"modelVersion\":1"));
        var b = Compose();
        var ex = Assert.Throws<HcsException>(() => b.Machine.Load(file2));
        Assert.Equal(HcsError.UnsupportedVersion, ex.Error);
    }

    [Fact]
    public void LoadRejectsFdcMissingPendingInterrupts()
    {
        var a = Compose();
        a.Machine.RunFrame();
        // Replace the required property with an unknown same-length name. Do not UTF-8
        // decode binary blobs or change manifest length: that tests framing corruption,
        // not the missing FDC field (tape2 makes that old accident surface as LimitExceeded).
        var patchedBytes = PatchAndRehash(a.Machine.Save(),
            Encoding.UTF8.GetBytes("\"pendingInterrupts\":"),
            Encoding.UTF8.GetBytes("\"unknownInterrupts\":"));
        var b = Compose();
        var ex = Assert.Throws<HcsException>(() => b.Machine.Load(patchedBytes));
        Assert.Equal(HcsError.InvalidFormat, ex.Error);
    }
}
