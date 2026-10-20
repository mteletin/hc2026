using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

[CollectionDefinition("m-base-view", DisableParallelization = true)]
public sealed class MBaseViewCollection : ICollectionFixture<MBaseViewFixture> { }

// One real store, not a reference mapper; all expectations below are literal contract vectors.
public sealed class MBaseViewFixture
{
    public HcMemory Memory { get; } = new(MachineProfile.Hc2000V2PagedMTransferV1.Memory,
        Enumerable.Repeat((byte)0x31, 0x4000).ToArray(),
        Enumerable.Repeat((byte)0xa2, 0x4000).ToArray(),
        Enumerable.Repeat((byte)0x57, 0x4000).ToArray());
    public PortDecoder Ports { get; }
    public MBaseViewFixture()
    {
        var idle = new PortDevice();
        Ports = new(HcIoProfile.WithPagedRam(Memory, idle, idle, idle, idle, idle, idle));
    }
}

[Collection("m-base-view")]
public sealed class MBaseViewTests
{
    private readonly HcMemory m;
    private readonly PortDecoder ports;
    public MBaseViewTests(MBaseViewFixture fixture)
    {
        m = fixture.Memory;
        ports = fixture.Ports;
        m.ResetPaging();
    }
    private void Out(ushort port, byte value) => ports.Write(port, value, 0);
    private byte In(ushort port) => ports.Read(port, 0);
    private void Bank(ushort bank) { Out(0x7dfd, (byte)(bank >> 8)); Out(0x7efd, (byte)bank); }

    [Fact]
    public void ResetIsFaithfulBaseNotImplicitExpandedPageZero()
    {
        Assert.Equal(0x40010000, m.Ram.Length);
        Assert.Equal(0x40, In(0x7cfd));
        Assert.Equal(0xc000, m.Resolve(0xc000).Offset);
        Assert.Equal(0x8000, m.Resolve(0x8000).Offset);
        Out(0xc7, 0);
        Assert.Equal(0xe000, m.Resolve(0xc000).Offset);
        Assert.Equal(0x8000, m.Resolve(0x8000).Offset);
        Assert.Equal(0x31, m.Read(0));
    }

    [Fact]
    public void FormerCcpAliasAndBaseDmaRemainDisjointFromTransferData()
    {
        m.Ram.Span[0xdd00] = 0x12;
        m.Ram.Span[0xfd00] = 0x34;
        m.Ram.Span[0x8000] = 0x56;
        m.Ram.Span[0x10000] = 0x78;
        Out(0xc7, 0);
        Assert.Equal(0x34, m.Read(0xdd00)); // CCP/BIOS C7 alias, not physical DD00.
        Assert.Equal(0x56, m.Read(0x8000));
        Out(0x7cfd, 1);
        Assert.Equal(0x78, m.Read(0x8000));
        Assert.Equal(0x34, m.Read(0xdd00));
        m.Write(0x8000, 0x9a);
        Out(0x7cfd, 0);
        Assert.Equal(0x56, m.Read(0x8000));
        Assert.Equal(0x9a, m.Ram.Span[0x10000]);
        Assert.Equal(0x12, m.Ram.Span[0xdd00]);
    }

    [Theory]
    [InlineData(0x00c5, 0x00c7)]
    [InlineData(0x55c5, 0x55c7)]
    [InlineData(0xffc5, 0xffc7)]
    public void C5ConsoleIrqViewIsFaithfulWithoutLosingTransferFlag(int c5, int c7)
    {
        m.Ram.Span[0xc100] = 0x11;
        m.Ram.Span[0xe100] = 0x22;
        m.Ram.Span[0x8100] = 0x33;
        m.Ram.Span[0x10100] = 0x44;
        Out((ushort)c7, 0); Out(0x7cfd, 1);
        Assert.Equal(0x44, m.Read(0x8100));
        Assert.Equal(0x22, m.Read(0xc100));
        Out((ushort)c5, 0);
        Assert.Equal(0x41, In(0x7cfd));
        Assert.Equal(0x33, m.Read(0x8100));
        Assert.Equal(0x11, m.Read(0xc100));
        m.Write(0xc100, 0x55);
        Assert.Equal(0x22, m.Ram.Span[0xe100]);
        Out((ushort)c7, 0);
        Assert.Equal(0x44, m.Read(0x8100));
        Assert.Equal(0x22, m.Read(0xc100));
    }

    [Fact]
    public void EnableRequiresC7AndReadbackAndInvalidWritesAreInert()
    {
        Out(0x7cfd, 1);
        Assert.Equal(0x40, In(0x7cfd));
        Out(0xc7, 0); Out(0x7cfd, 1);
        var pages = m.Pages;
        var latches = m.Latches;
        for (var value = 2; value <= 255; value++)
        {
            Out(0x7cfd, (byte)value);
            Assert.Equal(0x41, In(0x7cfd));
            Assert.Equal(latches, m.Latches);
            Assert.Same(pages, m.Pages);
        }
        Out(0x7cfd, 1);
        Assert.Same(pages, m.Pages);
        Out(0x7cfd, 0);
        pages = m.Pages;
        Out(0x7cfd, 0);
        Assert.Same(pages, m.Pages);
    }

    [Fact]
    public void EveryOther16BitAddressCannotSetOrClearTransfer()
    {
        Out(0xc7, 0);
        for (var address = 0; address <= 0xffff; address++)
        {
            if (address == 0x7cfd) continue;
            Out((ushort)address, 1);
            _ = In((ushort)address);
            Assert.Equal(0, In(0x7cfd) & 1);
        }
        m.ResetPaging(); Out(0xc7, 0); Out(0x7cfd, 1);
        for (var address = 0; address <= 0xffff; address++)
        {
            if (address == 0x7cfd) continue;
            Out((ushort)address, 0);
            _ = In((ushort)address);
            Assert.Equal(1, In(0x7cfd) & 1);
        }
        Out(0x7cfd, 0);
        Assert.Equal(0, In(0x7cfd) & 1);
    }

    [Fact]
    public void BankEndpointsAreExactAndLowThreeBitsDoNotEraseUpperBankBits()
    {
        Bank(0xabcd);
        Out(0x7ffd, 0x1a);
        Assert.Equal(0xabca, m.Latches.PagedBank);
        Assert.Equal(0x1a, In(0x7ffd));
        Assert.Equal(0xca, In(0x7efd)); Assert.Equal(0xab, In(0x7dfd));
        var before = m.Latches;
        var pages = m.Pages;
        foreach (ushort port in new ushort[] { 0x00fd, 0x01fd, 0x7cfc, 0x7cfe, 0x7cff, 0xfffd, 0x7ff5 })
        {
            Out(port, 0xff);
            Assert.Equal(0xff, In(port));
            Assert.Equal(before, m.Latches);
            Assert.Same(pages, m.Pages);
        }
        Out(0x7efd, 0xca); Out(0x7dfd, 0xab); Out(0x7ffd, 0x1a);
        Assert.Same(pages, m.Pages);
    }

    [Fact]
    public void TransferReplacesOnly8000ThroughBfffAndKeepsUpperRomWriteProtection()
    {
        Out(0x7e, 3); // RAM low, upper CP/M ROM.
        Out(0xc7, 0); Bank(1); Out(0x7cfd, 1);
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0x7fff, true), m.Resolve(0x7fff));
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0x14000, true), m.Resolve(0x8000));
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0x17fff, true), m.Resolve(0xbfff));
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0xe000, true), m.Resolve(0xc000));
        Assert.Equal(new MemoryLocation(MemoryStore.Rom, 0x6000, false), m.Resolve(0xe000));
        m.Write(0xe000, 0x19);
        Assert.Equal(0xa2, m.Read(0xe000));
        Out(0xc5, 0);
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0x8000, true), m.Resolve(0x8000));
        Assert.Equal(new MemoryLocation(MemoryStore.Ram, 0xc000, true), m.Resolve(0xc000));
        Assert.Equal(new MemoryLocation(MemoryStore.Rom, 0x6000, false), m.Resolve(0xe000));
        Assert.Equal(0x41, In(0x7cfd));
    }

    [Fact]
    public void LockRetainsActiveViewButAlwaysAllowsBaseEscape()
    {
        Out(0xc7, 0); Bank(0x1234); Out(0x7cfd, 1); Out(0x7ffd, 0x3c);
        Assert.Equal(0x61, In(0x7cfd));
        Assert.Equal(0x1234, m.Latches.PagedBank);
        Out(0x7efd, 0xff); Out(0x7dfd, 0xff); Out(0x7ffd, 0);
        Assert.Equal(0x1234, m.Latches.PagedBank);
        Out(0x7cfd, 0);
        Assert.Equal(0x60, In(0x7cfd));
        Assert.Equal(0x8000, m.Resolve(0x8000).Offset);
        Out(0x7cfd, 1);
        Assert.Equal(0x60, In(0x7cfd));
        Assert.Equal(0x1234, m.Latches.PagedBank);
        Assert.Equal(0x3c, In(0x7ffd));
    }

    [Theory]
    [InlineData(0, 0x10000)]
    [InlineData(255, 0x40c000)]
    [InlineData(256, 0x410000)]
    [InlineData(65535, 0x4000c000)]
    public void FullWidthBanksIncludingFfHaveLiteralOffsetsAndRetainBytes(int bank, int offset)
    {
        Out(0xc7, 0); Bank((ushort)bank); Out(0x7cfd, 1);
        Assert.Equal(offset, m.Resolve(0x8000).Offset);
        Assert.Equal(offset + 0x3fff, m.Resolve(0xbfff).Offset);
        m.Write(0xbfff, 0xa5);
        Bank((ushort)(bank == 0 ? 1 : 0));
        m.Write(0xbfff, 0x5a);
        Bank((ushort)bank);
        Assert.Equal(0xa5, m.Read(0xbfff));
        Assert.Equal((byte)bank, In(0x7efd));
        Assert.Equal((byte)(bank >> 8), In(0x7dfd));
    }

    [Fact]
    public void BankControlBitsAreReadableButCannotSelectMPolicyRomOrVideo()
    {
        Out(0x7ffd, 0x18);
        Assert.Equal(0x18, In(0x7ffd));
        Assert.Equal(0x31, m.Read(0));
        Assert.Equal(0x4000, m.VideoBase);
        Out(0x7e, 9); // Faithful high video and CP/M lower ROM.
        Assert.Equal(0xa2, m.Read(0));
        Assert.Equal(0xc000, m.VideoBase);
        Out(0xc7, 0); Out(0x7cfd, 1);
        Assert.Equal(0xa2, m.Read(0));
        Assert.Equal(0xc000, m.VideoBase);
        Out(0xc5, 0); Out(0x7ffd, 0);
        Assert.Equal(0xa2, m.Read(0));
        Assert.Equal(0xc000, m.VideoBase);
        m.SetShadow(true);
        Assert.Equal(0x57, m.Read(0));
        m.Write(0, 0xff);
        Assert.Equal(0x57, m.Read(0));
    }

    [Fact]
    public void C5UlaReadsPhysicalC000NotOldExpandedScreenPage7()
    {
        m.Ram.Span[0xc000] = 0x80; m.Ram.Span[0xd800] = 2;
        m.Ram.Span[0x2c000] = 0; m.Ram.Span[0x2d800] = 7;
        m.Ram.Span[0x4000] = 0; m.Ram.Span[0x5800] = 7;
        Out(0x7e, 8); Out(0x7ffd, 8);
        Out(0xc7, 0); Out(0x7cfd, 1); Out(0xc5, 0);
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var ula = new Ula(m, new KeyboardMatrix(), scheduler, clock, 2);
        scheduler.AdvanceTo(FrameTiming.FrameTicks);
        Assert.Equal(0xffcd0000u, ula.LastFrame!.Pixels.Span[40 * 352 + 48]);
    }

    [Theory]
    [InlineData("faithful")]
    [InlineData("banks")]
    [InlineData("paged-v1")]
    public void OldProfilesHaveNoTransferEndpointAndKeepTheirDifferentMaps(string kind)
    {
        var profile = kind switch
        {
            "banks" => MachineProfile.Hc2000V2RamBanksV1.Memory,
            "paged-v1" => MachineProfile.Hc2000V2PagedV1.Memory,
            _ => MachineProfile.Hc2000V2.Memory
        };
        var old = new HcMemory(profile, Enumerable.Repeat((byte)0x31, 0x4000).ToArray(),
            Enumerable.Repeat((byte)0xa2, 0x4000).ToArray(), new byte[0x4000]);
        var idle = new PortDevice();
        var decoder = new PortDecoder(kind switch
        {
            "banks" => HcIoProfile.WithRamBanks(old, idle, idle, idle, idle, idle, idle),
            "paged-v1" => HcIoProfile.WithPagedRam(old, idle, idle, idle, idle, idle, idle),
            _ => HcIoProfile.Faithful(old, idle, idle, idle, idle, idle, idle)
        });
        decoder.Write(0xc7, 0, 0);
        var before = old.Latches;
        var pages = old.Pages;
        for (var value = 0; value < 256; value++)
        {
            decoder.Write(0x7cfd, (byte)value, 0);
            Assert.Equal(0xff, decoder.Read(0x7cfd, 0));
            Assert.Equal(before, old.Latches);
            Assert.Same(pages, old.Pages);
        }
        Assert.Equal(kind == "paged-v1" ? 0x10000 : 0x8000, old.Resolve(0x8000).Offset);
        decoder.Write(0xc5, 0, 0);
        decoder.Write(0x7ffd, 0x18, 0);
        Assert.Equal(kind == "paged-v1" ? 0xa2 : 0x31, old.Read(0));
        Assert.Equal(kind == "paged-v1" ? 0x2c000 : 0x4000, old.VideoBase);
        if (kind == "banks")
        {
            decoder.Write(0xff8b, 1, 0);
            Assert.Equal(1, decoder.Read(0x558b, 0));
            Assert.Equal(0x12000, old.Resolve(0xa000).Offset);
            decoder.Write(0x008b, 0xff, 0);
            Assert.Equal(0xa000, old.Resolve(0xa000).Offset);
        }
    }

    [Fact]
    public void ResetClearsControlLockAndTransferButDoesNotEraseCopiedData()
    {
        Out(0xc7, 0); Bank(256); Out(0x7cfd, 1);
        m.Write(0x8123, 0x9d); Out(0x7ffd, 0x38);
        m.ResetPaging();
        Assert.Equal(0x40, In(0x7cfd));
        Assert.Equal(0, In(0x7ffd)); Assert.Equal(0, In(0x7efd)); Assert.Equal(0, In(0x7dfd));
        Assert.Equal(0x8000, m.Resolve(0x8000).Offset);
        Assert.Equal(0x9d, m.Ram.Span[0x410123]);
        Assert.Equal(0xc000, m.Resolve(0xc000).Offset);
    }
}
