using Hc2000.Machine.Floppy;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>
/// RAM-banks upgrade fixture tests (hc2000-ram-banks-abi-v2; failures doc):
/// exact physical store, selector port, window page, FF restore, invalid selectors,
/// 128 banks, no ushort cast on physical offsets, reset retention, faithful sweep,
/// polled FDC transfer, and upgrade profile validation.
/// </summary>
public sealed class RamBanksTests
{
    // ---- fixtures -------------------------------------------------------

    private static byte[] Fixture(int size, int seed) => Enumerable.Range(0, size)
        .Select(n => (byte)(seed + n / 0x400 + n % 251)).ToArray();

    private static readonly ProfileRomRegion[] V1Regions =
    [
        new(ProfileRomRole.Basic, "roms/hc2000/v1/basic.rom", 0x4000, 0, ProfileRomStore.MainRom, 0, ProfileRomAvailability.SuppliedUnverified),
        new(ProfileRomRole.Cpm, "roms/hc2000/v1/cpm.rom", 0x4000, 0, ProfileRomStore.MainRom, 0x4000, ProfileRomAvailability.SuppliedUnverified),
        new(ProfileRomRole.If1, "roms/hc2000/v1/if1.rom", 0x4000, 0, ProfileRomStore.ShadowRom, 0, ProfileRomAvailability.SuppliedUnverified)
    ];
    private static readonly ProfileRomRegion[] V2Regions =
    [
        new(ProfileRomRole.Basic, "roms/hc2000/v2/basic.rom", 0x4000, 0, ProfileRomStore.MainRom, 0, ProfileRomAvailability.SuppliedUnverified),
        new(ProfileRomRole.CpmUpper, "roms/hc2000/v2/cpm-hc2kzip.bin", 0x2000, 0, ProfileRomStore.MainRom, 0x6000, ProfileRomAvailability.SuppliedUnverified),
        new(ProfileRomRole.CpmLower, null, 0x2000, 0, ProfileRomStore.MainRom, 0x4000, ProfileRomAvailability.MissingLowerHalf),
        new(ProfileRomRole.If1, "roms/hc2000/v2/if1.rom", 0x4000, 0, ProfileRomStore.ShadowRom, 0, ProfileRomAvailability.SuppliedUnverified)
    ];

    private static MachineProfile Upgrade(string name)
    {
        var isV1 = name == "hc2000-v1-ram-banks-v1";
        var layout = isV1 ? HcRomLayout.V1 : HcRomLayout.V2;
        return new MachineProfile
        {
            Name = name,
            Version = 1,
            CpuTurboMultiplier = 1,
            Memory = new MemoryProfile(RomLayout: layout, RamBytes: BankPolicy.RamBytes,
                RomBytes: 0x8000, ShadowRomBytes: 0x4000, ShadowRamBytes: 0x400,
                RomProvenance: "test synthetic upgrade",
                RamBanks: new RamBankDevice(BankPolicy.PolicyId, BankPolicy.BankCount, BankPolicy.BankBytes)),
            Video = new VideoProfile(),
            Io = MachineIoPolicy.FaithfulOddFdcV1,
            RomRegions = isV1 ? V1Regions : V2Regions
        };
    }

    private static HcMemory CreateProfile()
    {
        var profile = Upgrade("hc2000-v2-ram-banks-v1");
        profile.Validate();
        var basic = Fixture(0x4000, 0x20);
        var cpm = Fixture(0x4000, 0x60);
        var if1 = Fixture(0x4000, 0xa0);
        var memory = new HcMemory(profile.Memory, basic, cpm, if1);
        // Distinct sentinel per 8-KiB bank region so base and banks never collide.
        var ram = memory.Ram.Span;
        for (var bank = 0; bank < BankPolicy.BankCount; bank++)
            for (var i = 0; i < BankPolicy.BankBytes; i++)
                ram[BankPolicy.BaseRamBytes + bank * BankPolicy.BankBytes + i] = (byte)(0x20 + bank * 3 + (i % 251));
        return memory;
    }

    private static PortDecoder Decoder(HcMemory memory) => new(HcIoProfile.WithRamBanks(
        memory, new PortDevice(), new PortDevice(), new PortDevice(), new PortDevice(),
        new PortDevice(), new PortDevice()));

    private static int Offset(HcMemory memory, ushort address) => memory.Resolve(address).Offset;

    // ---- profile validation (fail-not-repair) ---------------------------

    [Theory]
    [InlineData("hc2000-v2-ram-banks-v1")]
    [InlineData("hc2000-v1-ram-banks-v1")]
    public void UpgradeProfilesValidateWithExactStore(string name)
    {
        var profile = Upgrade(name);
        profile.Validate();
        Assert.Equal(BankPolicy.RamBytes, profile.Memory.RamBytes);
        Assert.Equal(BankPolicy.PolicyId, profile.Memory.RamBanks!.PolicyId);
    }

    [Fact]
    public void FaithfulProfilesRemainDeviceless()
    {
        MachineProfile.Hc2000V1.Validate();
        MachineProfile.Hc2000V2.Validate();
        Assert.Null(MachineProfile.Hc2000V1.Memory.RamBanks);
        Assert.Null(MachineProfile.Hc2000V2.Memory.RamBanks);
    }

    [Fact]
    public void FaithfulIdentityRefusesCarryingTheDevice()
    {
        var profile = new MachineProfile
        {
            Name = "hc2000-v2",
            Memory = new MemoryProfile(RamBytes: BankPolicy.RamBytes,
                RamBanks: new RamBankDevice(BankPolicy.PolicyId, 128, 0x2000))
        };
        var error = Assert.Throws<ArgumentException>(() => profile.Validate());
        Assert.Contains("faithful", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hc2000-v2-ram-banks-v2")]
    [InlineData("hc2000-v2-RAM-banks-v1")]
    public void UpgradeIdentityIsExactAndVersionPinned(string name)
    {
        var profile = Upgrade("hc2000-v2-ram-banks-v1");
        var wrong = new MachineProfile
        {
            Name = name,
            Version = 1,
            CpuTurboMultiplier = 1,
            Memory = profile.Memory,
            Video = new VideoProfile(),
            Io = MachineIoPolicy.FaithfulOddFdcV1
        };
        Assert.ThrowsAny<ArgumentException>(() => wrong.Validate());
    }

    [Fact]
    public void UpgradeRequiresTheDevice()
    {
        var profile = new MachineProfile
        {
            Name = "hc2000-v2-ram-banks-v1",
            Memory = new MemoryProfile(RamBytes: BankPolicy.RamBytes)
        };
        Assert.Throws<ArgumentException>(() => profile.Validate());
    }

    [Fact]
    public void MalformedPolicyFailsConstructionNotRepair()
    {
        var wrongCount = new MachineProfile
        {
            Name = "hc2000-v2-ram-banks-v1",
            Memory = new MemoryProfile(RamBytes: BankPolicy.RamBytes,
                RamBanks: new RamBankDevice(BankPolicy.PolicyId, 64, 0x2000))
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => wrongCount.Validate());

        var wrongStore = new MachineProfile
        {
            Name = "hc2000-v2-ram-banks-v1",
            Memory = new MemoryProfile(RamBytes: 0x100000, // 1 MiB total, not 110000h
                RamBanks: new RamBankDevice(BankPolicy.PolicyId, 128, 0x2000))
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => wrongStore.Validate());

        var wrongId = new MachineProfile
        {
            Name = "hc2000-v2-ram-banks-v1",
            Memory = new MemoryProfile(RamBytes: BankPolicy.RamBytes,
                RamBanks: new RamBankDevice("hc2000-ram-banks-8k128-v2", 128, 0x2000))
        };
        Assert.Throws<ArgumentException>(() => wrongId.Validate());
    }

    [Fact]
    public void WithRamBanksRefusesADevicelessProfile()
    {
        var memory = new HcMemory(MachineProfile.Hc2000V2.Memory,
            Fixture(0x4000, 1), Fixture(0x4000, 2), Fixture(0x4000, 3));
        Assert.Throws<ArgumentException>(() => HcIoProfile.WithRamBanks(
            memory, new PortDevice(), new PortDevice(), new PortDevice(),
            new PortDevice(), new PortDevice(), new PortDevice()));
    }

    // ---- initial state and sentinels ------------------------------------

    [Fact]
    public void DeviceStartsDisabledAndBaseRamIsFaithful()
    {
        var memory = CreateProfile();
        Assert.Equal(0xff, memory.Latches.BankSelector);
        // A000 maps to base RAM page 5 (0xA000h physical = 5th 8-KiB page).
        Assert.Equal(0xA000, Offset(memory, 0xa000));
        Assert.Equal(0xBFFF, Offset(memory, 0xbfff));
    }

    [Fact]
    public void MappingSentinelsBaseAndBanks()
    {
        var memory = CreateProfile();
        var decoder = Decoder(memory);
        // Base (FF): page 5 is faithful RAM at 0xA000h physical (5th 8-KiB page).
        Assert.Equal(0xA000, Offset(memory, 0xa000));
        Assert.Equal(0xBFFF, Offset(memory, 0xbfff));
        // Select bank 0: page 5 moves to 0x10000h physical.
        decoder.Write(0x8b, 0x00, 0);
        Assert.Equal(0x10000, Offset(memory, 0xa000));
        Assert.Equal(0x11FFF, Offset(memory, 0xbfff));
        // Select bank 1: A000 -> 0x12000h.
        decoder.Write(0x8b, 0x01, 0);
        Assert.Equal(0x12000, Offset(memory, 0xa000));
        // Select bank 127: A000 -> 0x10E000h, BFFF -> 0x10FFFFh.
        decoder.Write(0x8b, 0x7f, 0);
        Assert.Equal(0x10E000, Offset(memory, 0xa000));
        Assert.Equal(0x10FFFF, Offset(memory, 0xbfff));
        // Readback confirms the selection (no hidden latch).
        Assert.Equal(0x7f, decoder.Read(0x8b, 0));
        // FF disables: page 5 returns to faithful 0xA000h.
        decoder.Write(0x8b, 0xff, 0);
        Assert.Equal(0xff, decoder.Read(0x8b, 0));
        Assert.Equal(0xA000, Offset(memory, 0xa000));
    }

    [Fact]
    public void All129SelectorValuesAndInvalidRange()
    {
        var memory = CreateProfile();
        var decoder = Decoder(memory);
        var baselinePages = memory.Pages.Select(p => p.Mapping).ToArray();
        var faithfulOffset = Offset(memory, 0xa000); // 0xA000 at FF
        Assert.Equal(0xA000, faithfulOffset);
        for (var value = 0; value < 0x80; value++)
        {
            decoder.Write(0x8b, (byte)value, 0);
            Assert.Equal((byte)value, decoder.Read(0x8b, 0));
            Assert.Equal(BankPolicy.BaseRamBytes + value * BankPolicy.BankBytes, Offset(memory, 0xa000));
        }
        // FF disables.
        decoder.Write(0x8b, 0xff, 0);
        Assert.Equal(0xff, decoder.Read(0x8b, 0));
        Assert.Equal(faithfulOffset, Offset(memory, 0xa000));
        Assert.Equal(baselinePages[5], memory.Pages[5].Mapping);
        // 80..FE are ignored: readback stays FF, page table unchanged.
        for (var value = 0x80; value < 0xff; value++)
        {
            decoder.Write(0x8b, (byte)value, 0);
            Assert.Equal(0xff, decoder.Read(0x8b, 0));
            Assert.Equal(baselinePages[5], memory.Pages[5].Mapping);
        }
        // Bytes are untouched by invalid writes.
        memory.Ram.Span[faithfulOffset] = 0xab;
        for (var value = 0x80; value < 0xff; value++)
            decoder.Write(0x8b, (byte)value, 0);
        Assert.Equal(0xab, memory.Ram.Span[faithfulOffset]);
    }

    [Fact]
    public void SameValueWriteDoesNotRebuildOrResetState()
    {
        var memory = CreateProfile();
        var decoder = Decoder(memory);
        decoder.Write(0x8b, 0x21, 0);
        var before = memory.Pages[5].Mapping;
        memory.Ram.Span[Offset(memory, 0xa000) + 1] = 0x55;
        decoder.Write(0x8b, 0x21, 0);
        Assert.Equal(before, memory.Pages[5].Mapping);
        Assert.Equal(0x55, memory.Ram.Span[Offset(memory, 0xa000) + 1]);
        // FF when already FF.
        decoder.Write(0x8b, 0xff, 0);
        var beforeFf = memory.Pages[5].Mapping;
        memory.Ram.Span[Offset(memory, 0xa000)] = 0x66;
        decoder.Write(0x8b, 0xff, 0);
        Assert.Equal(beforeFf, memory.Pages[5].Mapping);
        Assert.Equal(0x66, memory.Ram.Span[Offset(memory, 0xa000)]);
    }

    // ---- port decode: low-byte aliases, high-byte inert ------------------

    [Fact]
    public void LowByteAliasesOverAll256HighBytes()
    {
        var memory = CreateProfile();
        var decoder = Decoder(memory);
        for (var high = 0; high < 256; high++)
        {
            var selectorPort = (ushort)(high << 8 | 0x8b);
            var countPort = (ushort)(high << 8 | 0x8d);
            // Reset to FF so the initial readback is deterministic.
            decoder.Write(0x8b, 0xff, 0);
            // 8B readback over every high byte (FF).
            Assert.Equal(0xff, decoder.Read(selectorPort, 0));
            // 8B write selects.
            decoder.Write(selectorPort, 0x05, 0);
            Assert.Equal(0x05, decoder.Read(selectorPort, 0));
            // 8D is constant 0x80 over every high byte.
            Assert.Equal(0x80, decoder.Read(countPort, 0));
        }
        // 8F stays unmapped (0xff) over every high byte; 8C unmapped.
        for (var high = 0; high < 256; high++)
        {
            Assert.Equal(0xff, decoder.Read((ushort)(high << 8 | 0x8f), 0));
            Assert.Equal(0xff, decoder.Read((ushort)(high << 8 | 0x8c), 0));
        }
    }

    [Fact]
    public void CountPortReads0x80AndOutIsInert()
    {
        var memory = CreateProfile();
        var decoder = Decoder(memory);
        Assert.Equal(0x80, decoder.Read(0x8d, 0));
        Assert.Equal(0xff, decoder.Read(0x8b, 0));
        for (var value = 0; value < 256; value++)
        {
            decoder.Write(0x8d, (byte)value, 0);
            Assert.Equal(0x80, decoder.Read(0x8d, 0));
            Assert.Equal(0xff, decoder.Read(0x8b, 0));
        }
        // Physical RAM untouched by all 256 count writes.
        var offset = Offset(memory, 0xa000);
        memory.Ram.Span[offset] = 0x77;
        for (var value = 0; value < 256; value++)
            decoder.Write(0x8d, (byte)value, 0);
        Assert.Equal(0x77, memory.Ram.Span[offset]);
    }

    [Fact]
    public void AbsentDeviceKeeps8b8dUnmapped()
    {
        var memory = new HcMemory(MachineProfile.Hc2000V2.Memory,
            Fixture(0x4000, 1), Fixture(0x4000, 2), Fixture(0x4000, 3));
        var decoder = new PortDecoder(HcIoProfile.Faithful(
            memory, new PortDevice(), new PortDevice(), new PortDevice(),
            new PortDevice(), new PortDevice(), new PortDevice()));
        for (var high = 0; high < 256; high++)
        {
            Assert.Equal(0xff, decoder.Read((ushort)(high << 8 | 0x8b), 0));
            Assert.Equal(0xff, decoder.Read((ushort)(high << 8 | 0x8d), 0));
        }
        // Writing unmapped ports has no effect on latches.
        decoder.Write(0x8b, 0x10, 0);
        decoder.Write(0x8d, 0x10, 0);
        Assert.Equal(0xff, memory.Latches.BankSelector);
        Assert.Equal(0xA000, Offset(memory, 0xa000));
    }

    // ---- faithful latch sweep: only page 5 moves ------------------------

    private static void SetCombination(HcMemory memory, byte port, bool cpm, bool shadow)
    {
        // Reset first: clears the 7E D2 lock so the port write is honoured,
        // also resets CPM/shadow to false so both states are tested cleanly.
        memory.ResetPaging();
        memory.SetCpm(cpm);
        memory.SetShadow(shadow);
        memory.WritePort7E(port);
    }

    private static List<(byte Port, bool Cpm, bool Shadow)> Combinations()
    {
        var list = new List<(byte Port, bool Cpm, bool Shadow)>();
        for (var port = 0; port < 16; port++)
            foreach (var cpm in new[] { false, true })
                foreach (var shadow in new[] { false, true })
                    list.Add(((byte)port, cpm, shadow));
        return list;
    }

    [Fact]
    public void FaithfulLatchSweepChangesOnlyPageFive()
    {
        var memory = CreateProfile();
        var combinations = Combinations();
        // Baseline: every faithful combination at selector FF.
        var baselines = new Dictionary<(byte, bool, bool), (int Page, int Offset)[]>();
        foreach (var (port, cpm, shadow) in combinations)
        {
            SetCombination(memory, port, cpm, shadow);
            baselines[(port, cpm, shadow)] =
                memory.Pages.Select((p, i) => (i, p.Mapping.Offset)).ToArray();
        }
        Assert.Equal(64, baselines.Count);
        // The same sweep with each of the 129 selectors: page 5 is the bank, every
        // other page is identical to that combination's FF baseline.
        foreach (var selector in Enumerable.Range(0, 0x80).Concat(new[] { 0xff }))
        {
            foreach (var (port, cpm, shadow) in combinations)
            {
                SetCombination(memory, port, cpm, shadow);
                memory.SetBank((byte)selector);
                var expectedFive = selector == 0xff
                    ? 0xA000
                    : BankPolicy.BaseRamBytes + selector * BankPolicy.BankBytes;
                Assert.Equal(expectedFive, memory.Pages[5].Mapping.Offset);
                var baseline = baselines[(port, cpm, shadow)];
                for (var page = 0; page < 8; page++)
                {
                    if (page == 5) continue;
                    Assert.Equal(baseline[page].Item2, memory.Pages[page].Mapping.Offset);
                }
            }
        }
    }

    // ---- reset -----------------------------------------------------------

    [Fact]
    public void ResetRestoresSelectorAndKeepsAllPhysicalBytes()
    {
        var memory = CreateProfile();
        var decoder = Decoder(memory);
        decoder.Write(0x8b, 0x40, 0);
        var bank000Offset = Offset(memory, 0xa000);
        var ram = memory.Ram.Span;
        ram[bank000Offset] = 0x01;
        ram[bank000Offset + 0x2000 - 1] = 0x02;
        ram[0x10e000] = 0x03; // bank 127 physical
        ram[0] = 0x04;        // base RAM
        memory.WritePort7E(0x0f);
        memory.SetCpm(true);
        memory.SetShadow(true);
        memory.ResetPaging();
        Assert.Equal(0, memory.Latches.Port7E);
        Assert.False(memory.Latches.Cpm);
        Assert.False(memory.Latches.Shadow);
        Assert.Equal(0xff, memory.Latches.BankSelector);
        Assert.Equal(0xA000, Offset(memory, 0xa000));
        Assert.Equal(0x01, ram[bank000Offset]);
        Assert.Equal(0x02, ram[bank000Offset + 0x2000 - 1]);
        Assert.Equal(0x03, ram[0x10e000]);
        Assert.Equal(0x04, ram[0]);
    }

    [Fact]
    public void IndependentInstancesDoNotLeakState()
    {
        var first = CreateProfile();
        var second = CreateProfile();
        var decoderFirst = Decoder(first);
        var decoderSecond = Decoder(second);
        decoderFirst.Write(0x8b, 0x10, 0);
        Assert.Equal(0x30000, Offset(first, 0xa000)); // bank 0x10: 0x10000 + 0x10*0x2000 = 0x30000
        Assert.Equal(0xA000, Offset(second, 0xa000)); // second still FF
        Assert.Equal(0x10, decoderFirst.Read(0x8b, 0));
        Assert.Equal(0xff, decoderSecond.Read(0x8b, 0));
        first.Ram.Span[0x10000] = 0x99;
        Assert.Equal(0x99, first.Ram.Span[0x10000]);
        Assert.Equal(0x20, second.Ram.Span[0x10000]); // unchanged: seed pattern 0x20+0*3+0
    }

    // ---- polled FDC through the selected window ---------------------------

    [Fact]
    public void PolledFdcTransfersUnaffectedByBankSelection()
    {
        var memory = CreateProfile();
        var (scheduler, board) = FdcTests.Create();
        var fdc = board.Fdc;
        var decoder = Decoder(memory);

        // Baseline: FDC read with no bank selected (FF).
        FdcTests.Send(fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        FdcTests.AdvanceUntilReady(scheduler, fdc);
        var baseline = FdcTests.Transfer(scheduler, board, 256);
        FdcTests.Result(fdc);

        // Select bank 3: FDC read produces the same data (bank does not affect FDC).
        decoder.Write(0x8b, 0x03, 0);
        var windowOffset = Offset(memory, 0xa000);
        Assert.Equal(BankPolicy.BaseRamBytes + 3 * BankPolicy.BankBytes, windowOffset);
        var ram = memory.Ram.Span;
        for (var i = 0; i < 256; i++)
            ram[windowOffset + i] = (byte)(0x40 + (i % 199));

        FdcTests.Send(fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        FdcTests.AdvanceUntilReady(scheduler, fdc);
        var output = FdcTests.Transfer(scheduler, board, 256);
        FdcTests.Result(fdc);
        // FDC data comes from the disk image, not the banked RAM.
        for (var i = 0; i < 256; i++)
            Assert.Equal(baseline[i], output[i]);
        // Banked RAM is untouched by the FDC transfer.
        for (var i = 0; i < 256; i++)
            Assert.Equal((byte)(0x40 + (i % 199)), ram[windowOffset + i]);

        // Switch to bank 5: FDC still works, bank 3 pattern preserved.
        decoder.Write(0x8b, 0x05, 0);
        var nextOffset = Offset(memory, 0xa000);
        Assert.Equal(BankPolicy.BaseRamBytes + 5 * BankPolicy.BankBytes, nextOffset);
        Assert.NotEqual(windowOffset, nextOffset);
        FdcTests.Send(fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        FdcTests.AdvanceUntilReady(scheduler, fdc);
        var output2 = FdcTests.Transfer(scheduler, board, 256);
        FdcTests.Result(fdc);
        for (var i = 0; i < 256; i++)
            Assert.Equal(baseline[i], output2[i]);
        // Bank 3 pattern still intact.
        for (var i = 0; i < 256; i++)
            Assert.Equal((byte)(0x40 + (i % 199)), ram[windowOffset + i]);
    }
}
