using System.Security.Cryptography;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Replay of documented ROM port/INI/OUTI boundaries, not a Z80 interpreter or proof of an executed boot.</summary>
public sealed class FdcRomReplayTests
{
    [FdcReplayAssetsFact]
    public void CanonicalRomReadWriteLoopReplayKeepsUpAndTransfersThroughC5C7PageTable() => ReplayCanonicalLoop(false);

    [FdcReplayAssetsFact]
    public void InstantCanonicalRomReadWriteLoopReplayUsesUnmodifiedCommandsAndTc() => ReplayCanonicalLoop(true);

    private static void ReplayCanonicalLoop(bool instant)
    {
        var rom = File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "roms/hc2000/v2/cpm-hc2kzip.bin"));
        Assert.Equal("eede2ed1a608602bd5314a3e877f9f032a38ff1435b4a24317ab307a48686dc4", Hash(rom));
        Assert.Equal(Convert.FromHexString("DB850730FBEDA2E3E320F5"), rom.AsSpan(0xa93, 11).ToArray());
        Assert.Equal(Convert.FromHexString("DB850730FBEDA3E3E320F5"), rom.AsSpan(0xace, 11).ToArray());
        Assert.Equal(Convert.FromHexString("3A9EDFE6033C87F619D307E6FED307"), rom.AsSpan(0xade, 15).ToArray());
        Assert.Equal(Convert.FromHexString("010303"), rom.AsSpan(0x181f, 3).ToArray()); // LD BC,0303; first byte is C
        Assert.Equal(Convert.FromHexString("DFFF"), rom.AsSpan(0x1841, 2).ToArray()); // parameters at runtime 8033
        var diskBytes = File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "disks/cpm/CPM1.DSK"));
        Assert.Equal("91133bbfe28c847239eb5b0bf2754177db67efd5791e0a7ccd364350477e7019", Hash(diskBytes));
        var disk = Dsk.Load(diskBytes);
        var source = disk.Tracks[0].Sectors.Single(x => x.R == 1).Data.ToArray();
        Assert.Equal(512, source.Length);
        var s = new DeviceScheduler(); var b = new FloppyController(s, 10, new(new(), disk), new(new()), instant: instant);
        b.WriteLatch(0); b.WriteLatch(0x10); // ROM boot reset at 800A
        SendWithRomDelay(s, b.Fdc, 3, 0xdf, 0xff);
        b.WriteLatch(0x1a);
        SendWithRomDelay(s, b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        var memory = new HcMemory(new(RomProvenance: "synthetic zero ROMs; port replay only"), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        memory.SetCpm(true); // C5/C7 physical E000 bank aliases C000
        ReplayDataLoop(s, b, memory, 0xc000, source.Length, write: false);
        FdcTests.AdvanceUntilReady(s, b.Fdc);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 2, 2 }, FdcTests.Result(b.Fdc));
        Assert.Equal(source, memory.Ram.Span.Slice(0xe000, source.Length).ToArray());
        Assert.All(memory.Ram.Span.Slice(0xc000, source.Length).ToArray(), x => Assert.Equal(0, x));
        memory.SetCpm(false); Assert.Equal(0, memory.Read(0xc000));
        memory.SetCpm(true); Assert.Equal(source[0], memory.Read(0xc000));
        // OUTI reads the same mapped bank; only the CPU bus touches RAM, not the controller.
        SendWithRomDelay(s, b.Fdc, 0x45, 0, 0, 0, 2, 2, 9, 0x2a, 0xff);
        ReplayDataLoop(s, b, memory, 0xc000, source.Length, write: true);
        FdcTests.AdvanceUntilReady(s, b.Fdc);
        Assert.Equal(0, FdcTests.Result(b.Fdc)[0]);
        Assert.Equal(source, b.DriveA.Track(0)!.Sectors.Single(x => x.R == 2).Data);
        Assert.Single(b.DriveA.DirtySectors);
    }

    [Fact]
    public void SlowedRomPollLoopGetsOverrunRatherThanUnlimitedRqm()
    {
        var (s, b) = FdcTests.Create();
        FdcTests.Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        FdcTests.AdvanceUntilReady(s, b.Fdc);
        Assert.Equal(0xe5, b.Fdc.ReadData());
        s.AdvanceTo(s.Now + 240 * 5); // > two byte slots, deliberately slower than 88T
        Assert.Equal(0x10, FdcTests.Result(b.Fdc)[1]);
    }

    [Theory]
    [InlineData(5, 1)] [InlineData(5, 2)] [InlineData(5, 4)]
    public void ByteDeadlinesUseSameMasterRateWithDifferentCpuTimeDividers(int p, int q)
    {
        var (s, b) = FdcTests.Create();
        FdcTests.Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        FdcTests.AdvanceUntilReady(s, b.Fdc); var origin = s.Now;
        var mapper = new CpuTimeMapper(p, q);
        for (var i = 0; i < 512; i++)
        {
            Assert.Equal(0xe5, b.Fdc.ReadData());
            var due = s.PendingEvents.Single(e => e.DeviceId == 10).DueTick;
            Assert.Equal(origin + ((i + 1L) * 141_875_800 + 249_999) / 250_000, due);
            var cpuT = (due * q + p - 1) / p;
            s.AdvanceTo(mapper.Map(cpuT));
        }
        // No TC: EOT=9, next sector begins later; one source's 512 exact byte deadlines are independent of CPU T.
        Assert.Equal(FdcPhase.Execution, b.Fdc.Phase);
    }

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

public sealed class FdcReplayAssetsFactAttribute : FactAttribute
{
    public FdcReplayAssetsFactAttribute()
    {
        foreach (var file in new[] { "roms/hc2000/v2/cpm-hc2kzip.bin", "disks/cpm/CPM1.DSK" })
        {
            var path = Path.Combine(StorageAssetTests.Root(), file);
            if (!File.Exists(path) || new FileInfo(path).Length < 1024)
                Skip = "Copyrighted FDC replay fixture absent/not LFS-materialized: " + file;
        }
    }
}
