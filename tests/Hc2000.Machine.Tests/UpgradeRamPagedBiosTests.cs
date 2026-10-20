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

[CollectionDefinition("ram-paged-bios", DisableParallelization = true)]
public sealed class RamPagedBiosCollection { }

// Independent expectations and private outputs only: never rewrite the firmware or repository media.
[Collection("ram-paged-bios")]
public sealed class UpgradeRamPagedBiosTests(RamPagedPrivateOutput artifact, ITestOutputHelper output) : IClassFixture<RamPagedPrivateOutput>
{

    [RamPagedAssetFact]
    public void RealBuilderHasOnlyOwnedSegmentsAndSeventeenSectorBootPatch()
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
        Assert.Equal(new byte[] {128,0,5,31,1,255,3,255,0,192,0,0,0,0,0}, artifact.Bios.AsSpan(0x825,15).ToArray());
        Assert.Contains("capacity=460", artifact.Manifest);
        Assert.Contains("stack=DEA0-DEFF SP=DF00", artifact.Manifest);
        Assert.Contains($"SOURCE {Hash(File.ReadAllBytes(Path.Combine(root, "bios/src/hc2000/upgrade/ram.paged.asm")))}", artifact.Manifest);
        output.WriteLine(artifact.Manifest);
    }

    [RamPagedAssetTheory]
    [InlineData(0x0080, false)]
    [InlineData(0xC480, true)]
    [InlineData(0x8000, true)]
    [InlineData(0xBF80, false)]
    [InlineData(0xDC34, false)]
    public void GlobalDmaRoundTripsActualBytesAndRestoresBankGateIffAndStack(int dma, bool iff)
    {
        var r = new RamPagedCpu(artifact);
        r.Out(0x7efd, 43); r.Out(0x7ffd, 0x18 | 3);
        var original = r.Memory.Latches;
        // SETDMA precedes SELDSK, then repeat with SETDMA after SELDSK.
        r.Call(0xD324, (ushort)dma);
        r.Call(0xD31B, 12);
        Assert.Equal(0xDB15, r.Cpu.R.HL);
        r.Call(0xD31E, 255); r.Call(0xD321, 127);
        var payload = Enumerable.Range(0,128).Select(i => (byte)(i ^ 0xA7)).ToArray();
        r.Write(dma, payload);
        r.Cpu.R.IFF1 = r.Cpu.R.IFF2 = iff;
        r.Call(0xD32A);
        Assert.Equal(0, r.Cpu.R.A);
        Assert.Equal(original, r.Memory.Latches);
        Assert.Equal(iff, r.Cpu.R.IFF1); Assert.Equal(iff, r.Cpu.R.IFF2);
        Assert.Equal(payload, r.Memory.Ram.Span.Slice(0x10000 + 255 * 16384 + 127 * 128,128).ToArray());
        r.Write(dma, new byte[128]);
        r.Call(0xD324, (ushort)dma);
        r.Call(0xD327);
        Assert.Equal(0, r.Cpu.R.A);
        Assert.Equal(payload, r.Read(dma,128));
        Assert.Equal(original, r.Memory.Latches);
        Assert.Equal(iff, r.Cpu.R.IFF1);
        Assert.Equal(0, r.Memory.Read(0xDE8E));
        Assert.All(r.Read(0xDEA0,94), b => Assert.Equal(0x6D,b));
        Assert.Equal(0xDEFE,r.LowestPrivateSp);
        Assert.True(r.SawTransfer);
        output.WriteLine($"DMA={dma:X4} IFF={iff}: native CPU private stack high-water=2/96, gate observed then restored");
    }

    [RamPagedAssetTheory]
    [InlineData(0x100, 0, 0x80)]
    [InlineData(0, 0x100, 0x80)]
    [InlineData(0, 128, 0x80)]
    [InlineData(0, 0, 0x7F)]
    [InlineData(0, 0, 0xC481)]
    [InlineData(0, 0, 0xFF81)]
    [InlineData(0, 0, 0x7000)]
    [InlineData(0, 0, 0xDC33)]
    [InlineData(0, 0, 0xDC35)]
    [InlineData(0, 0, 0xDCB4)]
    public void InvalidFullWidthCoordinatesAndDmaNeverTouchMOrChangeCallerState(int track, int sector, int dma)
    {
        var r = new RamPagedCpu(artifact);
        r.Call(0xD31B,12); r.Call(0xD31E,(ushort)track); r.Call(0xD321,(ushort)sector); r.Call(0xD324,(ushort)dma);
        var before = r.Memory.Ram.Span.Slice(0x10000, Math.Min(4*1024*1024, r.Memory.Ram.Length-0x10000)).ToArray();
        r.Cpu.R.IFF1 = r.Cpu.R.IFF2 = true;
        var latches = r.Memory.Latches;
        r.Call(0xD32A);
        Assert.Equal(1, r.Cpu.R.A);
        Assert.Equal(latches, r.Memory.Latches);
        Assert.True(r.Cpu.R.IFF1); Assert.True(r.Cpu.R.IFF2);
        Assert.Equal(before, r.Memory.Ram.Span.Slice(0x10000, Math.Min(4*1024*1024, r.Memory.Ram.Length-0x10000)).ToArray());
    }

    [RamPagedAssetTheory]
    [InlineData("nested")]
    [InlineData("high")]
    [InlineData("locked")]
    [InlineData("transfer")]
    [InlineData("absent")]
    [InlineData("old-paged")]
    [InlineData("old-banks")]
    public void RefusedCapabilitiesAndReentryPreserveDataAndPendingOwner(string condition)
    {
        var r = new RamPagedCpu(artifact, absent: condition == "absent",
            legacy:condition.StartsWith("old-",StringComparison.Ordinal) ? condition : null);
        r.Call(0xD31B,12); r.Call(0xD324,0x80);
        if (condition == "nested") r.Memory.Write(0xDE8E,1);
        if (condition == "high") r.Out(0x7dfd,1);
        if (condition == "locked") r.Out(0x7ffd,0x20);
        if (condition == "transfer") r.Out(0x7cfd,1);
        var before = r.Memory.Ram.Span.Slice(0x10000, Math.Min(4*1024*1024, r.Memory.Ram.Length-0x10000)).ToArray();
        var latches = r.Memory.Latches;
        r.Call(0xD327);
        Assert.Equal(1,r.Cpu.R.A);
        Assert.Equal(latches,r.Memory.Latches);
        Assert.Equal(condition == "nested" ? 1 : 0,r.Memory.Read(0xDE8E));
        Assert.Equal(before,r.Memory.Ram.Span.Slice(0x10000, Math.Min(4*1024*1024, r.Memory.Ram.Length-0x10000)).ToArray());
    }

    [RamPagedAssetFact]
    public void OriginalABSelectAndSetterAbiRemainGlobalAndUnsupportedDrivesAreRefused()
    {
        var r = new RamPagedCpu(artifact, boot:true, secondDrive:true);
        r.RunTo(0xBD00,20_000_000);
        foreach (var drive in new ushort[] {0,1})
        {
            r.Call(0xD31B,drive); Assert.NotEqual(0,r.Cpu.R.HL);
            Assert.Equal(0,r.Memory.Read(0xDE80));
            r.Call(0xD31E,2); r.Call(0xD321,0); r.Call(0xD324,0x100);
            r.Call(0xD327); Assert.Equal(0,r.Cpu.R.A);
            var before=r.Read(0x100,128);
            Assert.Contains(before,b=>b!=0);
            r.Call(0xD31B,12); r.Call(0xD31E,13); r.Call(0xD321,7);
            r.Write(0x100,Enumerable.Repeat((byte)0xA5,128).ToArray());
            r.Call(0xD32A); Assert.Equal(0,r.Cpu.R.A);
            r.Call(0xD31B,drive); r.Call(0xD31E,2); r.Call(0xD321,0);
            r.Write(0x100,new byte[128]);
            r.Call(0xD327); Assert.Equal(0,r.Cpu.R.A);
            Assert.Equal(before,r.Read(0x100,128)); // native floppy/cache, not M bounce data
        }
        foreach (var drive in new ushort[] {2,11,13,255})
        { r.Call(0xD31B,drive); Assert.Equal(0,r.Cpu.R.HL); }
        r.Call(0xD31E,0x1234); r.Call(0xD321,0x5678); r.Call(0xD324,0x4321);
        Assert.Equal(new byte[] {0x34,0x12,0x78,0x56}, r.Read(0xDE82,4));
        Assert.Equal(new byte[] {0x21,0x43},r.Read(0xD875,2));
        r.Call(0xD31B,12); Assert.Equal(new byte[] {0x21,0x43},r.Read(0xD875,2));
    }

    [RamPagedAssetFact]
    public void CompilingWriteAsReadMutantIsCaughtAndExactProducerRestoresIt()
    {
        var path=Path.Combine(StorageAssetTests.Root(),"bios/src/hc2000/upgrade/ram.paged.asm");
        var source=File.ReadAllText(path);
        Assert.Equal(2,source.Split("LD E,1",StringSplitOptions.None).Length);
        var mutant=Assembler.AssembleText(source.Replace("LD E,1","LD E,0",StringComparison.Ordinal),file:"private-write-as-read-negative.asm");
        Assert.True(mutant.Success);
        var r=new RamPagedCpu(artifact);
        foreach (var segment in mutant.Segments) r.Write(segment.Address,segment.Bytes);
        var bytes=Enumerable.Range(0,128).Select(i=>(byte)(i^0x53)).ToArray();
        void WriteAndCheck()
        {
            r.Call(0xD31B,12); r.Call(0xD31E,3); r.Call(0xD321,2); r.Call(0xD324,0x80);
            r.Write(0x80,bytes); r.Call(0xD32A); Assert.Equal(0,r.Cpu.R.A);
            Assert.Equal(bytes,r.Memory.Ram.Span.Slice(0x10000+3*16384+2*128,128).ToArray());
        }
        var red=Assert.Throws<Xunit.Sdk.EqualException>(WriteAndCheck);
        Assert.Contains("Collections differ",red.Message);
        output.WriteLine($"Compiling LD E,1 -> LD E,0 private mutant failed stored-byte assertion: {red.Message}");
        r.Write(0xD300,artifact.Bios); // exact same actual producer, no source mutation or host-copy data fix
        Assert.Equal(artifact.Bios,r.Read(0xD300,artifact.Bios.Length));
        WriteAndCheck();
    }

    [RamPagedAssetFact]
    public void EmptyBRetainsOriginalBiosSelectErrorPath()
    {
        var r=new RamPagedCpu(artifact,boot:true);
        r.RunTo(0xBD00,20_000_000);
        r.Call(0xD31B,12);
        r.Cpu.R.PC=0xD31B; r.Cpu.R.BC=1;
        r.RunTo(0xD4FE,5_000_000); // retained CALL D7C6 followed by original BIOS Error text
        Assert.Equal(new byte[] {0xCD,0xC6,0xD7,0x0D,0x0A,0x42,0x49,0x4F,0x53},r.Read(0xD4FE,9));
        Assert.Equal(0,r.Memory.Read(0xDE80)); Assert.False(r.Memory.Latches.PagedTransfer);
    }

    [RamPagedAssetFact]
    public void RealSeventeenSectorRomBootAndWarmBootRetainUnformattedMData()
    {
        var r = new RamPagedCpu(artifact, boot:true);
        r.RunTo(0xBD00, 20_000_000);
        // The original cold path self-modifies D3A7 and initializes retained BIOS work areas.
        // Compare immutable produced vectors/code, not mutable original BIOS and zeroed scratch.
        foreach (var operand in new[] {1,4,0x19,0x1c,0x1f,0x22,0x28,0x2b})
            Assert.Equal(artifact.Bios.AsSpan(operand,2).ToArray(),r.Read(0xD300+operand,2));
        Assert.Equal(artifact.Bios.AsSpan(0x9B4,artifact.Symbol("MCodeEnd")-0xDCB4).ToArray(),
            r.Read(0xDCB4,artifact.Symbol("MCodeEnd")-0xDCB4));
        Assert.All(r.Memory.Ram.Span.Slice(0x10000,4*1024*1024).ToArray(), b => Assert.Equal(0,b));
        r.Call(0xD31B,12); r.Call(0xD31E,23); r.Call(0xD321,19); r.Call(0xD324,0x100);
        var bytes = Enumerable.Range(0,128).Select(i => (byte)(255-i)).ToArray();
        r.Write(0x100,bytes); r.Call(0xD32A); Assert.Equal(0,r.Cpu.R.A);
        r.Cpu.R.PC = 0xD303;
        r.RunTo(0xBD00,20_000_000);
        Assert.Equal(0,r.Memory.Read(0xDE80)); Assert.Equal(0,r.Memory.Read(0xDE8E));
        r.Call(0xD31B,12); r.Call(0xD31E,23); r.Call(0xD321,19); r.Call(0xD324,0x200);
        r.Call(0xD327); Assert.Equal(0,r.Cpu.R.A); Assert.Equal(bytes,r.Read(0x200,128));
        output.WriteLine($"actual ROM boot/WBOOT PC={r.Cpu.R.PC:X4}; M bytes retained; media {Hash(artifact.Media)}");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

// Class fixture invokes the actual producer once; no parallel global mutation or test assembler substitute.
public sealed class RamPagedPrivateOutput : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-ram-paged-" + Guid.NewGuid().ToString("N"));
    internal HcMemory? WorkingMemory { get; set; }
    public byte[] Bios { get; }
    public byte[] Media { get; }
    public string Manifest { get; }
    private readonly Dictionary<string, int> symbols;
    private readonly Dictionary<string,byte[]> inputs = new();
    public int Symbol(string name) => symbols[name];
    public RamPagedPrivateOutput()
    {
        var root = StorageAssetTests.Root();
        foreach (var relative in new[] {"disks/cpm/CPM22-HC.DSK","roms/hc2000/eprom-dump-hc2k-2/M27C512.bin",
            "bios/src/hc2000/upgrade/ram.paged.asm","bios/src/hc2000/upgrade/ram.banks.asm",
            "bios/src/hc2000/upgrade/disk.blockport.asm","tools/UpgradeBiosBuilder/Program.cs"})
            inputs[relative]=SHA256.HashData(File.ReadAllBytes(Path.Combine(root,relative)));
        try
        {
        using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
        { WorkingDirectory=root, UseShellExecute=false } };
        foreach (var arg in new[] {"run","--project","tools/UpgradeBiosBuilder","--","m-paged",directory}) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        if (!process.WaitForExit(120_000)) { process.Kill(true); throw new TimeoutException("Real m-paged producer timed out."); }
        Assert.True(process.ExitCode == 0, $"Real m-paged producer exit={process.ExitCode}");
        Bios = File.ReadAllBytes(Path.Combine(directory,"ram.paged.bin"));
        Media = File.ReadAllBytes(Path.Combine(directory,"CPM22-HC-ram-paged-upgrade.DSK"));
        Manifest = File.ReadAllText(Path.Combine(directory,"source-fit.txt"));
        var source = Path.Combine(root,"bios/src/hc2000/upgrade/ram.paged.asm");
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

file sealed class RamPagedCpu
{
    public HcMemory Memory { get; }
    public Cpu Cpu { get; }
    private readonly PortDecoder ports;
    private readonly int privateStart, privateEnd, copyStart, copyEnd;
    public int LowestPrivateSp { get; private set; } = 0xDF00;
    public bool SawTransfer { get; private set; }
    public RamPagedCpu(RamPagedPrivateOutput artifact, bool absent=false, bool boot=false, bool secondDrive=false, string? legacy=null)
    {
        privateStart=artifact.Symbol("MPrivateStart"); privateEnd=artifact.Symbol("MPrivateEnd");
        copyStart=artifact.Symbol("MCopy"); copyEnd=artifact.Symbol("MCodeEnd");
        var root = StorageAssetTests.Root();
        var chip = File.ReadAllBytes(Path.Combine(root,"roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"));
        var profile = legacy switch
        {
            "old-paged" => MachineProfile.Hc2000V2PagedV1,
            "old-banks" => MachineProfile.Hc2000V2RamBanksV1,
            _ => absent ? MachineProfile.Hc2000V2 : MachineProfile.Hc2000V2PagedMTransferV1
        };
        Memory = absent || legacy is not null ? NewMemory() : artifact.WorkingMemory ??= NewMemory();
        HcMemory NewMemory() => new(profile.Memory,chip.AsSpan(0x8000,0x4000),chip.AsSpan(0xC000,0x4000),chip.AsSpan(0x4000,0x4000));
        Memory.ResetPaging();
        Memory.Ram.Span[..Math.Min(Memory.Ram.Length,0x410000)].Clear();
        var scheduler = new DeviceScheduler(); var clock = new FrameClock(scheduler,1);
        var adapter = new CpuDeviceTimeAdapter(scheduler,CpuTimeMapper.FromProfile(profile));
        var ula = new Ula(Memory,new KeyboardMatrix(),scheduler,clock,2,profile.Video);
        var b = secondDrive ? new FloppyDrive(new DriveProfile(80,2),Dsk.Load(artifact.Media)) : new FloppyDrive(new());
        var floppy = new FloppyController(scheduler,100,new FloppyDrive(new DriveProfile(80,2),Dsk.Load(artifact.Media)),b,instant:false);
        var idle = new PortDevice();
        var latch = new PortDevice(read: (_,_)=>floppy.ReadLatch(),write:(_,v,_)=>floppy.WriteLatch(v));
        var status = new PortDevice(read:(_,_)=>floppy.Fdc.ReadStatus());
        var data = new PortDevice(read:(_,_)=>floppy.Fdc.ReadData(),write:(_,v,_)=>floppy.Fdc.WriteData(v));
        ports = new(profile.Memory.RamPaged is not null ? HcIoProfile.WithPagedRam(Memory,ula,latch,status,data,idle,idle)
            : profile.Memory.RamBanks is not null ? HcIoProfile.WithRamBanks(Memory,ula,latch,status,data,idle,idle)
            : HcIoProfile.Faithful(Memory,ula,latch,status,data,idle,idle));
        Cpu = new(new HcMachineBus(Memory,ports,adapter,scheduler));
        Memory.WritePort7E(0x0F); Memory.SetCpm(true);
        if (boot)
        {
            // Same cold ROM entry contract as the existing native boot harness; no BIOS preload.
            Memory.Ram.Span[0xDF1C]=0; Memory.Ram.Span[0xDF1D]=0x50;
            Memory.Ram.Span[0xFEFF]=0x58; Memory.Ram.Span[0xFF00]=0xEE;
            Cpu.R.PC=0xF800; Cpu.R.SP=0x7FFE; Cpu.R.I=0xFE; Cpu.R.IM=2;
        }
        else
        {
            Write(0xD300,artifact.Bios);
            Write(0xDEA0,Enumerable.Repeat((byte)0x6D,96).ToArray());
        }
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

file sealed class RamPagedAssetFactAttribute : FactAttribute
{
    public RamPagedAssetFactAttribute() => Skip=RamPagedAssetAvailability.Missing();
}
file sealed class RamPagedAssetTheoryAttribute : TheoryAttribute
{
    public RamPagedAssetTheoryAttribute() => Skip=RamPagedAssetAvailability.Missing();
}
file static class RamPagedAssetAvailability
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
