using System.Diagnostics;
using System.Security.Cryptography;
using Hc2000.Asm;
using Hc2000.Machine;
using Hc2000.Machine.Input;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;
using Xunit.Abstractions;
using Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Tests;

[CollectionDefinition("ram-paged-v2-bios", DisableParallelization = true)]
public sealed class RamPagedV2BiosCollection { }

// M: v2 (hc2000-v2-paged-m-v2) firmware proof: the pinned 8 KiB-block DPB, and AC4 -
// the two page-high checks admit group 0 or 1 only (pages 0-511), still refuse the
// boundary (group 2, Track+1 = 2), and every other v1 guard (7CFD exact presence,
// nested, locked, faithful default) keeps refusing. M: v1 and the faithful default
// stay as shipped; the v1 source is guarded by the fixture's input hashes.
[Collection("ram-paged-v2-bios")]
public sealed class UpgradeRamPagedV2BiosTests(RamPagedV2PrivateOutput artifact, ITestOutputHelper output) : IClassFixture<RamPagedV2PrivateOutput>
{

    [RamPagedV2AssetFact]
    public void RealBuilderV2KeepsV1MediaOwnershipAndStampsPinned8KibGeometry()
    {
        var root = StorageAssetTests.Root();
        var original = File.ReadAllBytes(Path.Combine(root, "disks/cpm/CPM22-HC.DSK"));
        Assert.Equal("ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008", Hash(original));
        var before = Dsk.Load(original);
        var after = Dsk.Load(artifact.Media);
        var permitted = new HashSet<(int, int)> { (0, 1), (1, 7), (1, 9), (1, 2), (1, 4), (1, 6), (1, 8) };
        foreach (var track in before.Tracks)
        foreach (var sector in track.Sectors)
        {
            var next = after.Tracks.Single(t => t.Cylinder == track.Cylinder && t.Side == track.Side)
                .Sectors.Single(s => s.R == sector.R).Data;
            if (track.Cylinder == 0 && permitted.Contains((track.Side, sector.R))) continue;
            Assert.Equal(sector.Data, next);
        }
        var oldBoot = before.Tracks.Single(t => t.Cylinder == 0 && t.Side == 0).Sectors.Single(s => s.R == 1).Data;
        var newBoot = after.Tracks.Single(t => t.Cylinder == 0 && t.Side == 0).Sectors.Single(s => s.R == 1).Data;
        Assert.Equal(15, oldBoot[0x14]); Assert.Equal(17, newBoot[0x14]);
        Assert.Equal(oldBoot.Where((_, i) => i != 0x14), newBoot.Where((_, i) => i != 0x14));
        Assert.Equal(3072, artifact.Bios.Length);
        var oldBios = new byte[2048];
        var addresses = new[] { 7, 9, 2, 4, 6, 8 };
        foreach (var i in Enumerable.Range(0, 6))
        {
            var sector = after.Tracks.Single(t => t.Cylinder == 0 && t.Side == 1).Sectors.Single(s => s.R == addresses[i]);
            Assert.Equal(artifact.Bios.AsSpan(i * 512, 512).ToArray(), sector.Data);
            if (i < 4) before.Tracks.Single(t => t.Cylinder == 0 && t.Side == 1).Sectors.Single(s => s.R == addresses[i]).Data.CopyTo(oldBios, i * 512);
        }
        Assert.Equal("ad2bdb9053de9ec5fb971e7b660c3e07ace1fee8382cfa8f1c4f4610e48bcea4", Hash(oldBios));
        var vectorOperands = new[] { 1, 4, 0x19, 0x1c, 0x1f, 0x22, 0x28, 0x2b };
        foreach (var i in Enumerable.Range(0, 0x815))
        {
            if (i >= oldBios.Length) break;
            if (!vectorOperands.Any(v => i == v || i == v + 1)) Assert.Equal(oldBios[i], artifact.Bios[i]);
        }
        Assert.Equal(0xDCB4, artifact.Symbol("MCodeStart"));
        Assert.InRange(artifact.Symbol("MCodeEnd"), 0xDCB5, 0xDE80);
        // Pinned 8 KiB-block fallback (PO ruling 634840): SPT 128, BSH 6, BLM 63, EXM 1,
        // DSM 1023, DRM 255, ALV 0, AL0 0x80, AL1 0, CKS 0, OFF 0.
        Assert.Equal(new byte[] {128,0,6,63,1,255,3,255,0,128,0,0,0,0,0}, artifact.Bios.AsSpan(0x825,15).ToArray());
        Assert.Contains("PROFILE hc2000-v2-paged-m-v2 hc2000-ram-paged-m-transfer-16k65536-v2", artifact.Manifest);
        Assert.Contains("capacity=460", artifact.Manifest);
        Assert.Contains("stack=DEA0-DEFF SP=DF00", artifact.Manifest);
        Assert.Contains($"SOURCE {Hash(File.ReadAllBytes(Path.Combine(root, "bios/src/hc2000/upgrade/ram.paged.v2.asm")))}", artifact.Manifest);
        output.WriteLine(artifact.Manifest);
    }

    [RamPagedV2AssetTheory]
    [InlineData(0x00FF, 1, 255)] // Track selects group 0 despite the caller's group 1.
    [InlineData(0x01FF, 1, 511)] // Highest M: v2 page.
    [InlineData(0x0100, 0, 256)] // Track selects group 1 despite the caller's group 0.
    public void V2UpperGroupPageHighAdmittedAndCopies(int track, int latch, int expectedPage)
    {
        var r = new RamPagedV2Cpu(artifact);
        r.Out(0x7efd, 43); r.Out(0x7ffd, 0x18 | 3);
        if (latch != 0) r.Out(0x7dfd, (byte)latch);
        var original = r.Memory.Latches;
        var otherGroupOffset = 0x10000 + (expectedPage ^ 256) * 16384;
        var otherGroupBefore = r.Memory.Ram.Span.Slice(otherGroupOffset, 16384).ToArray();
        r.Call(0xD324, 0x80);
        r.Call(0xD31B, 12);
        Assert.Equal(0xDB15, r.Cpu.R.HL);
        r.Call(0xD31E, (ushort)track); r.Call(0xD321, 127);
        var payload = Enumerable.Range(0,128).Select(i => (byte)(i ^ 0xA7)).ToArray();
        r.Write(0x80, payload);
        r.Cpu.R.IFF1 = r.Cpu.R.IFF2 = true;
        r.Call(0xD32A);
        Assert.Equal(0, r.Cpu.R.A);
        Assert.Equal(original, r.Memory.Latches);
        Assert.True(r.Cpu.R.IFF1); Assert.True(r.Cpu.R.IFF2);
        Assert.True(r.SawTransfer);
        Assert.Equal(payload, r.Memory.Ram.Span.Slice(0x10000 + expectedPage * 16384 + 127 * 128,128).ToArray());
        // Round trip: restore the window back to DMA.
        r.Write(0x80, new byte[128]);
        r.Call(0xD31E, (ushort)track); r.Call(0xD321, 127);
        r.Call(0xD327);
        Assert.Equal(0, r.Cpu.R.A);
        Assert.Equal(payload, r.Read(0x80,128));
        Assert.Equal(original, r.Memory.Latches);
        Assert.True(r.Cpu.R.IFF1);
        Assert.Equal(otherGroupBefore, r.Memory.Ram.Span.Slice(otherGroupOffset, 16384).ToArray());
        output.WriteLine($"track={track:X4} latch={latch}: page {expectedPage} copied, other group untouched, caller latches restored");
    }

    [RamPagedV2AssetTheory]
    [InlineData("latch-2")]
    [InlineData("track-high-2")]
    [InlineData("transfer")]
    [InlineData("locked")]
    [InlineData("nested")]
    [InlineData("absent")]
    public void V2RefusedBoundariesAndGuardsNeverTouchMOrChangeCallerState(string condition)
    {
        var r = new RamPagedV2Cpu(artifact, absent: condition == "absent");
        r.Call(0xD31B,12); r.Call(0xD324,0x80);
        var track = condition == "track-high-2" ? (ushort)0x0200 : (ushort)255;
        r.Call(0xD31E, track); r.Call(0xD321,127);
        if (condition == "latch-2") r.Out(0x7dfd,2);
        if (condition == "transfer") r.Out(0x7cfd,1);
        if (condition == "locked") r.Out(0x7ffd,0x20);
        if (condition == "nested") r.Memory.Write(0xDE8E,1);
        var before = r.Memory.Ram.Span.Slice(0x10000, Math.Min(8*1024*1024, r.Memory.Ram.Length-0x10000)).ToArray();
        var latches = r.Memory.Latches;
        r.Cpu.R.IFF1 = r.Cpu.R.IFF2 = true;
        r.Call(0xD32A);
        Assert.Equal(1, r.Cpu.R.A);
        Assert.Equal(latches, r.Memory.Latches);
        Assert.True(r.Cpu.R.IFF1); Assert.True(r.Cpu.R.IFF2);
        Assert.Equal(condition == "nested" ? 1 : 0, r.Memory.Read(0xDE8E));
        Assert.Equal(before, r.Memory.Ram.Span.Slice(0x10000, Math.Min(8*1024*1024, r.Memory.Ram.Length-0x10000)).ToArray());
        output.WriteLine($"v2 {condition}: refused, A=1, no M data touched");
    }

    [RamPagedV2AssetFact]
    public void V2LowerGroupRoundTripsThroughPinnedDpbGeometry()
    {
        var r = new RamPagedV2Cpu(artifact);
        r.Out(0x7efd, 43); r.Out(0x7ffd, 0x18 | 3);
        var original = r.Memory.Latches;
        r.Call(0xD324, 0xC480);
        r.Call(0xD31B, 12);
        r.Call(0xD31E, 255); r.Call(0xD321, 127);
        var payload = Enumerable.Range(0,128).Select(i => (byte)(i ^ 0x55)).ToArray();
        r.Write(0xC480, payload);
        r.Cpu.R.IFF1 = r.Cpu.R.IFF2 = true;
        r.Call(0xD32A);
        Assert.Equal(0, r.Cpu.R.A);
        Assert.Equal(original, r.Memory.Latches);
        Assert.True(r.Cpu.R.IFF1); Assert.True(r.Cpu.R.IFF2);
        Assert.Equal(payload, r.Memory.Ram.Span.Slice(0x10000 + 255 * 16384 + 127 * 128,128).ToArray());
        r.Write(0xC480, new byte[128]);
        r.Call(0xD324, 0xC480);
        r.Call(0xD327);
        Assert.Equal(0, r.Cpu.R.A);
        Assert.Equal(payload, r.Read(0xC480,128));
        Assert.Equal(original, r.Memory.Latches);
        Assert.True(r.Cpu.R.IFF1);
        output.WriteLine("v2 lower group (track 255, latch 0): copied to page 255 and restored through the pinned DPB image");
    }

    [RamPagedV2AssetTheory]
    [InlineData(0xDC34, true)] // Exact DPH-owned directory buffer used by BDOS.
    [InlineData(0xDC33, false)]
    [InlineData(0xDC35, false)]
    [InlineData(0xDBB4, false)] // Bounce is not a caller buffer.
    [InlineData(0xDCB4, false)] // Resident code is not a caller buffer.
    [InlineData(0xC500, false)]
    [InlineData(0xC481, false)] // Last byte would reach the OS boundary.
    [InlineData(0xC480, true)]
    public void V2DirectoryDmaIsExactAndDoesNotRelaxTheOsFence(int dma, bool admitted)
    {
        var r = new RamPagedV2Cpu(artifact);
        r.Call(0xD31B, 12);
        r.Call(0xD324, (ushort)dma);
        r.Call(0xD31E, 0); r.Call(0xD321, 0);
        var payload = Enumerable.Range(0, 128).Select(i => (byte)(i ^ 0xA7)).ToArray();
        payload.CopyTo(r.Memory.Ram.Span[0x10000..]);
        var before = r.Memory.Ram.Span.Slice(0x10000, 8 * 1024 * 1024).ToArray();
        var beforeDma = r.Read(dma, 128);
        var latches = r.Memory.Latches;
        r.Cpu.R.IFF1 = r.Cpu.R.IFF2 = true;
        r.Call(0xD327);
        Assert.Equal(admitted ? 0 : 1, r.Cpu.R.A);
        Assert.Equal(latches, r.Memory.Latches);
        Assert.True(r.Cpu.R.IFF1); Assert.True(r.Cpu.R.IFF2);
        Assert.Equal(before, r.Memory.Ram.Span.Slice(0x10000, before.Length).ToArray());
        if (admitted)
        {
            Assert.Equal(payload, r.Read(dma, 128));
            Array.Reverse(payload);
            r.Write(dma, payload);
            r.Call(0xD32A);
            Assert.Equal(0, r.Cpu.R.A);
            Assert.Equal(payload, r.Memory.Ram.Span.Slice(0x10000, 128).ToArray());
            Assert.Equal(latches, r.Memory.Latches);
            Assert.True(r.Cpu.R.IFF1); Assert.True(r.Cpu.R.IFF2);
        }
        else
        {
            Assert.False(r.SawTransfer);
            Assert.Equal(beforeDma, r.Read(dma, 128));
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class RamPagedV2PrivateOutput : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-ram-paged-v2-" + Guid.NewGuid().ToString("N"));
    internal HcMemory? WorkingMemory { get; set; }
    public byte[] Bios { get; }
    public byte[] Media { get; }
    public string Manifest { get; }
    private readonly Dictionary<string, int> symbols;
    private readonly Dictionary<string,byte[]> inputs = new();
    public int Symbol(string name) => symbols[name];
    public RamPagedV2PrivateOutput()
    {
        var root = StorageAssetTests.Root();
        foreach (var relative in new[] {"disks/cpm/CPM22-HC.DSK","roms/hc2000/eprom-dump-hc2k-2/M27C512.bin",
            "bios/src/hc2000/upgrade/ram.paged.asm","bios/src/hc2000/upgrade/ram.paged.v2.asm",
            "tools/UpgradeBiosBuilder/Program.cs"})
            inputs[relative]=SHA256.HashData(File.ReadAllBytes(Path.Combine(root,relative)));
        try
        {
        using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
        { WorkingDirectory=root, UseShellExecute=false } };
        foreach (var arg in new[] {"run","--project","tools/UpgradeBiosBuilder","--","m-paged-v2",directory}) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        if (!process.WaitForExit(120_000)) { process.Kill(true); throw new TimeoutException("Real m-paged-v2 producer timed out."); }
        Assert.True(process.ExitCode == 0, $"Real m-paged-v2 producer exit={process.ExitCode}");
        Bios = File.ReadAllBytes(Path.Combine(directory,"ram.paged.v2.bin"));
        Media = File.ReadAllBytes(Path.Combine(directory,"CPM22-HC-ram-paged-v2-upgrade.DSK"));
        Manifest = File.ReadAllText(Path.Combine(directory,"source-fit.txt"));
        var source = Path.Combine(root,"bios/src/hc2000/upgrade/ram.paged.v2.asm");
        var assembly = Assembler.AssembleText(File.ReadAllText(source),file:source);
        Assert.True(assembly.Success);
        symbols = assembly.Symbols.ToDictionary(p => p.Key,p => (int)p.Value);
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        try
        {
            foreach (var input in inputs)
                Assert.Equal(input.Value,SHA256.HashData(File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(),input.Key))));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
}

file sealed class RamPagedV2Cpu
{
    public HcMemory Memory { get; }
    public Cpu Cpu { get; }
    private readonly PortDecoder ports;
    private readonly int privateStart, privateEnd, copyStart, copyEnd;
    public int LowestPrivateSp { get; private set; } = 0xDF00;
    public bool SawTransfer { get; private set; }
    public RamPagedV2Cpu(RamPagedV2PrivateOutput artifact, bool absent=false)
    {
        privateStart=artifact.Symbol("MPrivateStart"); privateEnd=artifact.Symbol("MPrivateEnd");
        copyStart=artifact.Symbol("MCopy"); copyEnd=artifact.Symbol("MCodeEnd");
        var root = StorageAssetTests.Root();
        var chip = File.ReadAllBytes(Path.Combine(root,"roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"));
        var profile = absent ? MachineProfile.Hc2000V2 : MachineProfile.Hc2000V2PagedMTransferV2;
        Memory = absent ? NewMemory() : artifact.WorkingMemory ??= NewMemory();
        HcMemory NewMemory() => new(profile.Memory,chip.AsSpan(0x8000,0x4000),chip.AsSpan(0xC000,0x4000),chip.AsSpan(0x4000,0x4000));
        Memory.ResetPaging();
        Memory.Ram.Span[..Math.Min(Memory.Ram.Length,0x810000)].Clear();
        var scheduler = new DeviceScheduler(); var clock = new FrameClock(scheduler,1);
        var adapter = new CpuDeviceTimeAdapter(scheduler,CpuTimeMapper.FromProfile(profile));
        var ula = new Ula(Memory,new KeyboardMatrix(),scheduler,clock,2,profile.Video);
        var b = new FloppyDrive(new());
        var floppy = new FloppyController(scheduler,100,new FloppyDrive(new DriveProfile(80,2),Dsk.Load(artifact.Media)),b,instant:false);
        var idle = new PortDevice();
        var latch = new PortDevice(read: (_,_)=>floppy.ReadLatch(),write:(_,v,_)=>floppy.WriteLatch(v));
        var status = new PortDevice(read:(_,_)=>floppy.Fdc.ReadStatus());
        var data = new PortDevice(read:(_,_)=>floppy.Fdc.ReadData(),write:(_,v,_)=>floppy.Fdc.WriteData(v));
        ports = new(profile.Memory.RamPaged is not null ? HcIoProfile.WithPagedRam(Memory,ula,latch,status,data,idle,idle)
            : HcIoProfile.Faithful(Memory,ula,latch,status,data,idle,idle));
        Cpu = new(new HcMachineBus(Memory,ports,adapter,scheduler));
        Memory.WritePort7E(0x0F); Memory.SetCpm(true);
        Write(0xD300,artifact.Bios);
        Write(0xDEA0,Enumerable.Repeat((byte)0x6D,96).ToArray());
    }
    public void Out(ushort port,byte value) => ports.Write(port,value,0);
    public void Write(int address,byte[] bytes) { for (var i=0;i<bytes.Length;i++) Memory.Write((ushort)(address+i),bytes[i]); }
    public byte[] Read(int address,int count) => Enumerable.Range(address,count).Select(a=>Memory.Read((ushort)a)).ToArray();
    public void Call(ushort entry,ushort bc=0)
    {
        Cpu.R.PC=entry; Cpu.R.BC=bc; Cpu.R.SP=0x7000;
        Memory.Write(0x7000,0x00); Memory.Write(0x7001,0x60);
        RunTo(0x6000,5_000_000);
        Assert.Equal(0x7002,Cpu.R.SP);
    }
    public void RunTo(ushort pc,int budget)
    {
        for (var i=0;i<budget;i++)
        {
            if (Cpu.R.PC==pc) return;
            if ((Cpu.R.PC>=privateStart && Cpu.R.PC<privateEnd) || (Cpu.R.PC>=copyStart && Cpu.R.PC<copyEnd))
            {
                Assert.False(Cpu.R.IFF1);
                Assert.InRange((int)Cpu.R.SP,0xDEFE,0xDF00);
                LowestPrivateSp=Math.Min(LowestPrivateSp,Cpu.R.SP);
            }
            SawTransfer |= Memory.Latches.PagedTransfer;
            Cpu.Step();
        }
        Assert.Fail($"CPU failed to reach {pc:X4}; PC={Cpu.R.PC:X4} SP={Cpu.R.SP:X4} AF={Cpu.R.AF:X4}");
    }
}

internal sealed class RamPagedV2AssetFactAttribute : FactAttribute
{
    public RamPagedV2AssetFactAttribute() => Skip=RamPagedV2AssetAvailability.Missing();
}
file sealed class RamPagedV2AssetTheoryAttribute : TheoryAttribute
{
    public RamPagedV2AssetTheoryAttribute() => Skip=RamPagedV2AssetAvailability.Missing();
}
file static class RamPagedV2AssetAvailability
{
    public static string? Missing()
    {
        foreach (var (path,length) in new[] {("disks/cpm/CPM22-HC.DSK",778496),
            ("roms/hc2000/eprom-dump-hc2k-2/M27C512.bin",65536)})
        {
            var full=Path.Combine(StorageAssetTests.Root(),path);
            if (!File.Exists(full) || new FileInfo(full).Length!=length)
                return "Copyrighted asset missing or LFS pointer (run git lfs pull): "+path;
        }
        return null;
    }
}
