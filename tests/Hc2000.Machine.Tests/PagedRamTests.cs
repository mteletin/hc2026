using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Input;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

[CollectionDefinition("paged-ram", DisableParallelization = true)]
public sealed class PagedRamCollection : ICollectionFixture<PagedRamFixture> { }

// Literal spec-12 v2 vectors, not values obtained from the implementation's policy/map.
public sealed class PagedRamFixture
{
    public HcMemory Memory { get; }
    public PortDecoder Ports { get; }
    public PagedRamFixture()
    {
        var profile = MachineProfile.Hc2000V2PagedV1.Memory;
        // Synthetic, deliberately distinct resident halves and shadow ROM; not a boot receipt.
        var basic = Enumerable.Repeat((byte)0x31, 0x4000).ToArray();
        var cpm = Enumerable.Repeat((byte)0xa2, 0x4000).ToArray();
        var shadow = Enumerable.Repeat((byte)0x57, 0x4000).ToArray();
        Memory = new(profile, basic, cpm, shadow);
        Ports = new(HcIoProfile.WithPagedRam(Memory, new PortDevice(), new PortDevice(), new PortDevice(),
            new PortDevice(), new PortDevice(), new PortDevice()));
    }
}

[Collection("paged-ram")]
public sealed class PagedRamTests
{
    private readonly HcMemory m;
    private readonly PortDecoder ports;
    public PagedRamTests(PagedRamFixture fixture)
    {
        m = fixture.Memory; ports = fixture.Ports; m.ResetPaging();
    }
    private void Out(ushort port, byte value) => ports.Write(port, value, 0);
    private byte In(ushort port) => ports.Read(port, 0);
    private void Bank(ushort bank) { Out(0x7dfd, (byte)(bank >> 8)); Out(0x7efd, (byte)bank); }

    [Fact] public void Case01_FaithfulPortsAreAbsent()
    {
        var faithful = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var p = new PortDecoder(HcIoProfile.Faithful(faithful, new PortDevice(), new PortDevice(),
            new PortDevice(), new PortDevice(), new PortDevice(), new PortDevice()));
        foreach (ushort port in new ushort[] { 0x7ffd, 0x7efd, 0x7dfd })
        { p.Write(port, 0xff, 0); Assert.Equal(0xff, p.Read(port, 0)); }
        Assert.Equal(0x10000, faithful.Ram.Length);
        Assert.Equal(0xc000, faithful.Resolve(0xc000).Offset);
    }
    [Fact] public void Case02_ResetPageZeroIsDisjointFromBase()
    {
        Assert.Equal(0x40010000, m.Ram.Length);
        m.Ram.Span[0xc000] = 0x44; m.Ram.Span[0x10000] = 0x11;
        Assert.Equal(0x11, m.Read(0xc000)); Assert.Equal(0x10000, m.Resolve(0xc000).Offset);
    }
    [Fact] public void Case03_PagesZeroAndOneRetainIndependentPatterns()
    {
        Bank(0); m.Write(0xc013, 0x23); Bank(1); m.Write(0xc013, 0x91);
        Bank(0); Assert.Equal(0x23, m.Read(0xc013)); Bank(1); Assert.Equal(0x91, m.Read(0xc013));
    }
    [Fact] public void Case04_HighLatchSelectsDisjointPage256()
    {
        Bank(0); m.Write(0xc019, 0x22); Out(0x7efd, 0); Assert.Equal(0, In(0x7dfd));
        Out(0x7dfd, 1); Assert.Equal(0x410000, m.Resolve(0xc000).Offset);
        m.Write(0xc019, 0x93); Out(0x7dfd, 0); Assert.Equal(0x22, m.Read(0xc019));
        Out(0x7dfd, 1); Assert.Equal(0x93, m.Read(0xc019));
    }
    [Fact] public void Case05_FfIsPageNotDisableAndLastPageHasFullWidth()
    {
        Bank(255); m.Write(0xffff, 0x65); Bank(65535); m.Write(0xffff, 0x87);
        Assert.Equal(0x4000ffff, m.Resolve(0xffff).Offset); Assert.Equal(0x87, m.Read(0xffff));
        Bank(255); Assert.Equal(0x65, m.Read(0xffff));
    }
    [Fact] public void Case06_CompatReplacesOnlyThreeBankBits()
    {
        Bank(0x81fc); Out(0x7ffd, 3); Assert.Equal(0xfb, In(0x7efd)); Assert.Equal(0x81, In(0x7dfd));
        m.ResetPaging(); Out(0x7ffd, 3); Assert.Equal(3, In(0x7efd));
        Assert.Equal(0x1c000, m.Resolve(0xc000).Offset);
    }
    [Fact] public void Case07_LockBlocksAllThreePortsAndResetRetainsRam()
    {
        Bank(255); m.Write(0xc067, 0x89); Out(0x7ffd, 0xff);
        Out(0x7ffd, 0); Out(0x7efd, 0); Out(0x7dfd, 1);
        Assert.Equal(0x3f, In(0x7ffd)); Assert.Equal(255, In(0x7efd)); Assert.Equal(0, In(0x7dfd));
        m.ResetPaging(); Assert.Equal(0, In(0x7ffd)); Assert.Equal(0, In(0x7efd));
        Bank(255); Assert.Equal(0x89, m.Read(0xc067));
    }
    [Fact] public void Case08_ExactDecodeAndNoRamBanksDevice()
    {
        Bank(0x1234);
        foreach (ushort port in new ushort[] { 0x3ffd, 0x00fd, 0x008b, 0x00ef /* faithful serial, not paging */ })
            Out(port, 0xff);
        for (var high = 0; high <= 255; high++)
        {
            ushort port = (ushort)((high << 8) | 0xfd);
            if (port is 0x7ffd or 0x7efd or 0x7dfd) continue;
            Out(port, 0xff); Assert.Equal(0xff, In(port));
        }
        Assert.Equal(0x34, In(0x7efd)); Assert.Equal(0x12, In(0x7dfd)); Assert.Equal(0xff, In(0x008b));
    }
    [Fact] public void Case09_CpmWindowPreservesC7AliasAndIm2Rom()
    {
        Out(0xc7, 0); Out(0x7e, 2); Bank(0x1234);
        Assert.Equal(0x48e0000, m.Resolve(0x8000).Offset);
        Assert.Equal(0xe000, m.Resolve(0xc000).Offset); Assert.Equal(MemoryStore.Ram, m.Resolve(0xc000).Store);
        Assert.Equal(0x31, m.Read(0xfeff)); Assert.Equal(0x31, m.Read(0xff00));
        m.Write(0x8000, 0x77); Assert.Equal(0x77, m.Read(0x8000));
    }
    [Fact] public void Case10_BasicUpperRomWinsOverHalfOfPage()
    {
        Bank(1); Out(0x7e, 3); Assert.Equal(0x14000, m.Resolve(0xc000).Offset);
        Assert.Equal(MemoryStore.Rom, m.Resolve(0xe000).Store); Assert.Equal(0xa2, m.Read(0xe000));
        m.Write(0xe000, 0x10); Assert.Equal(0xa2, m.Read(0xe000));
    }
    [Fact] public void Case11_ReadbackReconstructsControlAndPreservesIndependentBits()
    {
        Bank(0xabcd); Out(0x7ffd, 0xda); Assert.Equal(0x1a, In(0x7ffd));
        Assert.Equal(0xca, In(0x7efd)); Assert.Equal(0xab, In(0x7dfd));
        Out(0x7efd, 0x11); Assert.Equal(0x19, In(0x7ffd)); Assert.Equal(0xab, In(0x7dfd));
    }
    private sealed class SnapshotCpu : ICpuSnapshotSeam
    {
        private CpuLogicalState state = new(0, 0, 0, 0, 0, 0, 0x5000, 0x0100, 0, 0, 0, 0, 0,
            0, 0, 0, false, false, false, false, false, false, false, 1, 0, 0);
        public CpuLogicalState Capture() => state;
        public void Restore(CpuLogicalState value) => state = value;
    }
    private MachineSnapshot Snapshot()
    {
        var profile = MachineProfile.Hc2000V2PagedV1;
        var scheduler = new DeviceScheduler(); var clock = new FrameClock(scheduler, 1);
        var keyboard = new KeyboardMatrix(); var ula = new Ula(m, keyboard, scheduler, clock, 2);
        return new(profile, scheduler, clock, CpuTimeMapper.FromProfile(profile), m, ula, keyboard,
            new SnapshotCpu(), 2, null, null);
    }
    [Fact] public void Case12_SnapshotRestoresLastBankLockAndRam()
    {
        Bank(65535); m.Write(0xc097, 0x75); Out(0x7ffd, 0x3f);
        var path = Path.Combine(Path.GetTempPath(), "paging-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = File.Create(path)) Snapshot().Save(file);
            m.ResetPaging(); Bank(65535); m.Write(0xc097, 0x19); m.ResetPaging();
            using (var file = File.OpenRead(path)) Snapshot().Load(file);
            Assert.Equal(0x3f, In(0x7ffd)); Assert.Equal(255, In(0x7efd)); Assert.Equal(255, In(0x7dfd));
            Assert.Equal(0x75, m.Read(0xc097)); Out(0x7efd, 0); Assert.Equal(255, In(0x7efd));
            foreach (var mutation in new[] { "version", "length", "numeric", "bank", "hash", "absent", "null" })
            {
                m.ResetPaging(); Bank(1); m.Write(0xc097, 0x29);
                using var changed = Rewrite(path, mutation);
                if (mutation is "absent" or "null")
                {
                    Snapshot().Load(changed);
                    Assert.Equal(0xff, In(0x7ffd)); Assert.Equal(0xc000, m.Resolve(0xc000).Offset);
                    Assert.Equal(0, m.Latches.PagedBank);
                    m.ResetPaging(); Assert.Equal(0xff, In(0x7ffd));
                }
                else
                {
                    var error = Assert.Throws<HcsException>(() => Snapshot().Load(changed));
                    Assert.Equal(mutation == "version" ? HcsError.UnsupportedVersion
                        : mutation == "hash" ? HcsError.AssetMismatch
                        : mutation is "bank" or "numeric" ? HcsError.InvalidFormat : HcsError.StateInconsistent, error.Error);
                    Assert.Equal(1, In(0x7efd)); Assert.Equal(0x29, m.Read(0xc097));
                }
            }
            using (var original = File.OpenRead(path)) Snapshot().Load(original);
        }
        finally { File.Delete(path); }
    }

    // Change only the logical manifest, preserving blobs and a valid outer SHA. This bypasses
    // the writer's validation deliberately, to exercise loader rejection and atomicity.
    private static FileStream Rewrite(string path, string mutation)
    {
        using var input = File.OpenRead(path);
        var header = new byte[32]; input.ReadExactly(header);
        var oldLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        var oldJson = new byte[oldLength]; input.ReadExactly(oldJson);
        var root = JsonNode.Parse(oldJson)!;
        var state = root["components"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "memory")!["state"]!.AsObject();
        switch (mutation)
        {
            case "version": state["paged"]!["v"] = 2; break;
            case "length": state["paged"]!["ramBytes"] = "1073741823"; break;
            case "numeric": state["paged"]!["ramBytes"] = 1073741824; break;
            case "bank": state["paged"]!["bank"] = 65536; break;
            case "hash": state["paged"]!["ramSha256"] = new string('0', 64); break;
            case "absent": state.Remove("paged"); break;
            case "null": state["paged"] = null; break;
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(root);
        var total = (ulong)(input.Length - oldLength + json.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), total);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)json.Length);
        var output = new FileStream(path + "-" + mutation, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            1024 * 1024, FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header); output.Write(header); hash.AppendData(json); output.Write(json);
        var buffer = new byte[1024 * 1024]; var remaining = input.Length - input.Position - 32;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, buffer.Length);
            input.ReadExactly(buffer.AsSpan(0, count));
            output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); remaining -= count;
        }
        output.Write(hash.GetHashAndReset()); output.Flush(); output.Position = 0;
        return output;
    }

    private uint FirstPixel()
    {
        var scheduler = new DeviceScheduler(); var clock = new FrameClock(scheduler, 1);
        var ula = new Ula(m, new KeyboardMatrix(), scheduler, clock, 2);
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        return ula.LastFrame!.Pixels.Span[40 * 352 + 48];
    }
    [Fact] public void Case13_BasicScreenIsRealPage7NotCpu4000OrBaseC000()
    {
        m.Ram.Span[0x4000] = 0; m.Ram.Span[0x5800] = 7;
        m.Ram.Span[0xc000] = 0; m.Ram.Span[0xd800] = 7;
        m.Ram.Span[0x2c000] = 0x80; m.Ram.Span[0x2d800] = 7;
        Assert.Equal(0xff000000u, FirstPixel());
        Out(0x7ffd, 8); Assert.Equal(0xffcdcdcdu, FirstPixel()); Assert.Equal(0, m.Read(0x4000));
        Out(0x7e, 8); Assert.Equal(0xffcdcdcdu, FirstPixel()); Assert.Equal(0, m.Read(0x4000));
        Bank(7); m.Write(0xc000, 0); Assert.Equal(0xff000000u, FirstPixel());
    }
    [Fact] public void Case14_CpmVideoIsFaithfulAnd7ffdLockDoesNotLock7e()
    {
        m.Ram.Span[0x4000] = 0; m.Ram.Span[0x5800] = 7;
        m.Ram.Span[0xc000] = 0x80; m.Ram.Span[0xd800] = 7;
        Out(0xc7, 0); Out(0x7ffd, 0x28); Assert.Equal(0xff000000u, FirstPixel());
        Out(0x7e, 8); Assert.Equal(0xffcdcdcdu, FirstPixel());
        Out(0x7ffd, 0); Assert.Equal(0xffcdcdcdu, FirstPixel());
        m.ResetPaging(); Out(0xc7, 0); Out(0x7e, 8); Out(0x7ffd, 0);
        Assert.Equal(0xffcdcdcdu, FirstPixel());
    }
    [Fact] public void Case15_RomHalfIsRealAndIgnores7eHalfInBasic()
    {
        Assert.Equal(0x31, m.Read(0)); Out(0x7e, 1); Assert.Equal(0x31, m.Read(0));
        Out(0x7ffd, 0x10); Assert.Equal(0xa2, m.Read(0)); Out(0x7e, 0); Assert.Equal(0xa2, m.Read(0));
    }
    [Fact] public void Case16_HcRamAndIf1ShadowPrecedeCompatRom()
    {
        Out(0x7e, 2); m.Write(0, 0x99); Out(0x7ffd, 0x10);
        Assert.Equal(0x99, m.Read(0)); Assert.Equal(0x31, m.Read(0xe000));
        Out(0x7e, 3); Out(0x7ffd, 0); Assert.Equal(0xa2, m.Read(0xe000));
        Assert.Equal(0x31, m.Rom.Span[0]); Assert.Equal(0xa2, m.Rom.Span[0x4000]);
        Out(0x7e, 0); m.ReadM1(0x0008); Assert.Equal(0x57, m.Read(0));
        Assert.Equal(MemoryStore.ShadowRam, m.Resolve(0x2800).Store);
        m.SetShadow(false); Assert.Equal(0x31, m.Read(0));
        Out(0x7ffd, 0x10); Assert.Equal(0xa2, m.Read(0));
    }
}
