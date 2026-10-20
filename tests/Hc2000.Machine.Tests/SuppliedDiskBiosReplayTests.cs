using System.Security.Cryptography;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>
/// Pinned-fixture replay of the supplied-disk CPM22-HC.DSK BIOS load documented in the KB pin
/// (hc2000-block-disk-port-v1 / supplied-disk-bios-reference, 2026-10-05): the ROM 3010 bootstrap
/// loop's 15 read sectors into CPU BD00..DAFF, replayed at the documented ROM port/INI/OUTI
/// boundaries on the baseline FDC, and through the 07c opt-in block port. Transfer mechanics
/// verified only: this repository has no Z80 interpreter (the CPU is a caller seam, per spec 04
/// and FdcRomReplayTests), so no A&gt; boot outcome is claimed. The M: RAM-disk driver is out of
/// scope (PO 575877; SELDISK D492 rejects drives &gt;= 2).
/// </summary>
public sealed class SuppliedDiskBiosReplayTests
{
    // Pinned identity (KB pin 2026-10-05).
    private const string DiskSha = "ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008";
    private const string RomSha = "eede2ed1a608602bd5314a3e877f9f032a38ff1435b4a24317ab307a48686dc4";
    private const string BootSha = "04353a3e8e82de88a11eab003d62be49e0c3541902a583b12c20b3240b45f1d5";
    private const string ImageSha = "d5dfd47e72dac1fb155d35627803f5099f9585943dc74bd774c37fae33731694";
    private const string SuffixSha = "ad2bdb9053de9ec5fb971e7b660c3e07ace1fee8382cfa8f1c4f4610e48bcea4";
    private const string VectorsSha = "5c2b6aed4b8aab3cd6bb19766b5159b6b06182ad34c2b2df456a77b441d5f6c2";
    private const string DpbSha = "53a9c19afbc44e3c1a6cc2fb3caa500bd192c8fac333a454c63fbd3d8b84478b";
    private const string DiskRelative = "disks/cpm/CPM22-HC.DSK";
    private const string RomRelative = "roms/hc2000/v2/cpm-hc2kzip.bin";

    // Pinned 3010-loop rows (KB pin 2026-10-05): CPU destination <- FDC C/H/R.
    private static readonly (int C, int H, int R, ushort Dst)[] Rows =
    [
        (0, 0, 3, 0xbd00), (0, 0, 5, 0xbf00), (0, 0, 7, 0xc100), (0, 0, 9, 0xc300),
        (0, 0, 2, 0xc500), (0, 0, 4, 0xc700), (0, 0, 6, 0xc900), (0, 0, 8, 0xcb00),
        (0, 1, 1, 0xcd00), (0, 1, 3, 0xcf00), (0, 1, 5, 0xd100), (0, 1, 7, 0xd300),
        (0, 1, 9, 0xd500), (0, 1, 2, 0xd700), (0, 1, 4, 0xd900),
    ];

    [SuppliedDiskBiosFact]
    public void SuppliedDiskAndPinnedRomMatchReferenceIdentity()
    {
        var rom = File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), RomRelative));
        Assert.Equal(RomSha, Hash(rom));
        var diskBytes = File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), DiskRelative));
        Assert.Equal(DiskSha, Hash(diskBytes));
        var disk = Dsk.Load(diskBytes);
        Assert.Equal(80, disk.Cylinders);
        Assert.Equal(2, disk.Sides);
        Assert.Equal(9, disk.Tracks[0].Sectors.Count);
        // Boot sector C0/H0/R1: the ROM's first load, before the 15-sector 3010 loop.
        var boot = disk.Tracks[0].Sectors.Single(x => x.R == 1).Data;
        Assert.Equal(512, boot.Length);
        Assert.Equal(BootSha, Hash(boot));
    }

    [SuppliedDiskBiosFact]
    public void BaselineFdcPortReplayLoadsPinnedBiosImageTransferMechanicsOnly()
    {
        var disk = Dsk.Load(File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), DiskRelative)));
        var s = new DeviceScheduler();
        var b = new FloppyController(s, 10, new(new(), disk), new(new()));
        var memory = new HcMemory(new(RomProvenance: "synthetic zero ROMs; port replay only"),
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        memory.SetCpm(true); // the 3010 loop runs with the C5/C7 physical E000 bank aliased at C000
        b.WriteLatch(0); b.WriteLatch(0x10); // ROM boot reset at 800A
        SendWithRomDelay(s, b.Fdc, 3, 0xdf, 0xff);
        b.WriteLatch(0x1a);
        foreach (var (c, h, r, dst) in Rows)
        {
            SendWithRomDelay(s, b.Fdc, 0x46, (byte)(h << 2), (byte)c, (byte)h, (byte)r, 2, 9, 0x2a, 0xff); // 9-byte HC layout [op,unit|side<<2,C,H,R,N,EOT,ST0,ST1]
            ReplayDataLoop(s, b, memory, dst, 512, write: false);
            FdcTests.AdvanceUntilReady(s, b.Fdc);
            // 7-byte result [st0,st1,st2,c,h,r,n]: st0 carries unit|side<<2 (error bit 0x40 absent),
            // st1/st2 clean, and the result ID advances to the next sector (R9 wraps to C1/R1 at eot 9).
            var res = FdcTests.Result(b.Fdc);
            Assert.Equal(new byte[] { (byte)(h << 2), 0, 0, (byte)(r == 9 ? 1 : 0), (byte)h, (byte)(r == 9 ? 1 : r + 1), 2 }, res);
        }
        AssertPinnedImage(CpuView(memory, 0xbd00));
        AssertPhysicalSplit(memory);
    }

    [SuppliedDiskBiosFact]
    public void UpgradedBlockPortLoadsPinnedBiosImageByteIdenticalTransferMechanicsOnly()
    {
        var disk = Dsk.Load(File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), DiskRelative)));
        var s = new DeviceScheduler();
        var b = new FloppyController(s, 100, new(new(), disk), new(new()));
        var memory = new HcMemory(new(RomProvenance: "synthetic zero ROMs; port replay only"),
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        memory.SetCpm(true);
        var device = new BlockDiskPort(memory, b);
        var idle = new PortDevice();
        var faithful = HcIoProfile.Faithful(memory, idle, idle, idle, idle, idle, idle);
        // Isolated synthetic test composition, NOT a registered or executable product profile.
        var ports = new PortDecoder(faithful.Concat(device.Mappings()));
        foreach (var (c, h, r, dst) in Rows)
        {
            // Descriptor {version 1, drive 0, logical cylinder, side, sector R, DMA lo, DMA hi, 0}.
            var descriptor = new byte[] { 1, 0, (byte)c, (byte)h, (byte)r, (byte)dst, (byte)(dst >> 8), 0 };
            for (var i = 0; i < descriptor.Length; i++) memory.Write((ushort)(0x8000 + i), descriptor[i]);
            ports.Write(0x81, 0x00, s.Now); // pointer 0x8000 (low, high)
            ports.Write(0x83, 0x80, s.Now);
            ports.Write(0x89, 1, s.Now); // op 1 = READ; transfer is synchronous at the drained bus tick
            Assert.Equal(0x00, ports.Read(0x89, s.Now));
        }
        AssertPinnedImage(CpuView(memory, 0xbd00));
        AssertPhysicalSplit(memory);
    }

    private static void AssertPhysicalSplit(HcMemory memory)
    {
        // CPU BD00..BFFF is identity; CPU C000..DAFF is the physical E000 bank under SetCpm(true).
        var image = CpuView(memory, 0xbd00);
        Assert.Equal(image.AsSpan(0, 0x200).ToArray(), memory.Ram.Span.Slice(0xbd00, 0x200).ToArray());
        Assert.Equal(image.AsSpan(0x400, 0x1a00).ToArray(), memory.Ram.Span.Slice(0xe100, 0x1a00).ToArray());
        Assert.All(memory.Ram.Span.Slice(0xc100, 0x200).ToArray(), x => Assert.Equal(0, x)); // identity C000..DFFF RAM untouched
    }

    private static void AssertPinnedImage(byte[] image)
    {
        Assert.Equal(7680, image.Length);
        Assert.Equal(ImageSha, Hash(image)); // the 15 pinned rows concatenated in ROM order BD00..DAFF
        Assert.Equal(SuffixSha, Hash(image.AsSpan(0xd300 - 0xbd00, 2048).ToArray())); // BIOS-bearing suffix D300..DAFF
        Assert.Equal(VectorsSha, Hash(image.AsSpan(0xd300 - 0xbd00, 51).ToArray())); // CP/M vector block at D300
        Assert.Equal(Convert.FromHexString("6960C9"), image.AsSpan(0xd330 - 0xbd00, 3).ToArray()); // SECTRAN
        Assert.Equal(DpbSha, Hash(image.AsSpan(0xd366 - 0xbd00, 64).ToArray())); // four 16-byte DPB slots
    }

    private static byte[] CpuView(HcMemory memory, ushort start)
    {
        var view = new byte[7680];
        for (var i = 0; i < view.Length; i++) view[i] = memory.Read((ushort)(start + i));
        return view;
    }

    // Private duplicates of the FdcRomReplayTests helpers: documented ROM instruction timings.
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void SendWithRomDelay(DeviceScheduler s, Fdc8272 fdc, params byte[] bytes)
    {
        foreach (var value in bytes)
        {
            Assert.Equal(0x80, fdc.ReadStatus() & 0xc0);
            fdc.WriteData(value); s.AdvanceTo(s.Now + 76 * 5); // EA11: 4 x EX (SP),HL
        }
    }

    private static void ReplayDataLoop(DeviceScheduler s, FloppyController b, HcMemory memory, ushort address, int count, bool write)
    {
        // Times at instruction completion are the scoped replay seam, not inferred internal Z80 bus microcycles.
        for (var i = 0; i < count; i++)
        {
            byte msr;
            do
            {
                s.AdvanceTo(s.Now + 11 * 5); msr = b.Fdc.ReadStatus(); // IN A,(85)
                s.AdvanceTo(s.Now + 4 * 5); // RLCA
                s.AdvanceTo(s.Now + ((msr & 0x80) == 0 ? 12 : 7) * 5); // JR NC
            } while ((msr & 0x80) == 0);
            Assert.Equal(FdcPhase.Execution, b.Fdc.Phase);
            s.AdvanceTo(s.Now + 16 * 5); // INI/OUTI through CPU memory bus
            if (write) b.Fdc.WriteData(memory.Read(address)); else memory.Write(address, b.Fdc.ReadData());
            address++;
            s.AdvanceTo(s.Now + 38 * 5); // 2 x EX (SP),HL
            s.AdvanceTo(s.Now + ((i + 1) % 256 == 0 ? 7 : 12) * 5); // JR NZ
            if ((i + 1) % 256 == 0)
            {
                s.AdvanceTo(s.Now + 23 * 5); // DEC (IY+13)
                s.AdvanceTo(s.Now + (i + 1 == count ? 7 : 12) * 5); // JR NZ next page
            }
        }
        // Reconstructed EADE-EAEB port trace: read path also takes JR EADE at EAA3 (12T).
        s.AdvanceTo(s.Now + (46 + (write ? 0 : 12)) * 5); b.WriteLatch(0x1b);
        s.AdvanceTo(s.Now + 18 * 5); b.WriteLatch(0x1a);
    }
}

public sealed class SuppliedDiskBiosFactAttribute : FactAttribute
{
    public SuppliedDiskBiosFactAttribute()
    {
        foreach (var file in new[] { "disks/cpm/CPM22-HC.DSK", "roms/hc2000/v2/cpm-hc2kzip.bin" })
        {
            var path = Path.Combine(StorageAssetTests.Root(), file);
            if (!File.Exists(path) || new FileInfo(path).Length < 1024)
                Skip = "Pinned supplied-disk BIOS fixture absent/not LFS-materialized: " + file;
        }
    }
}