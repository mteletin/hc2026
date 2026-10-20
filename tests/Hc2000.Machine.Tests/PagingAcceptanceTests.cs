using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>
/// Spec 04 CPU-view acceptance through the real faithful port table and memory read/write seam.
/// Complements MemoryTests' isolated latch truth table with persistent writes across port changes.
/// Synthetic complete ROM halves only: no CPU execution, IF1 trap, BIOS, boot or snapshot claim.
/// </summary>
public sealed class PagingAcceptanceTests
{
    [Fact]
    public void DistinctPagingIdentitiesRejectSwappedPoliciesAndKeepLegacySerializedDeviceShape()
    {
        var legacy = MachineProfile.Hc2000V2PagedV1;
        var transfer = MachineProfile.Hc2000V2PagedMTransferV1;
        legacy.Validate();
        transfer.Validate();
        Assert.Equal("hc2000-v2-paged-m-v1", transfer.Name);
        Assert.Equal("hc2000-ram-paged-m-transfer-16k65536-v1", transfer.Memory.RamPaged!.PolicyId);
        Assert.Throws<ArgumentException>(() => (legacy with { Memory = transfer.Memory }).Validate());
        Assert.Throws<ArgumentException>(() => (transfer with { Memory = legacy.Memory }).Validate());
        Assert.Equal("{\"PolicyId\":\"hc2000-ram-paged-16k65536-v1\"}",
            System.Text.Json.JsonSerializer.Serialize(new PagedRamDevice()));
        Assert.Equal("{\"PolicyId\":\"hc2000-ram-paged-m-transfer-16k65536-v1\"}",
            System.Text.Json.JsonSerializer.Serialize(transfer.Memory.RamPaged));
        Assert.Null(MachineProfile.Default.Memory.RamPaged);
    }

    public static IEnumerable<object[]> ProfilesAndPortAliases()
    {
        foreach (var layout in new[] { HcRomLayout.V1, HcRomLayout.V2 })
        foreach (var high in new[] { 0x0000, 0x5500, 0xff00 })
            yield return new object[] { layout, high };
    }

    [Theory]
    [MemberData(nameof(ProfilesAndPortAliases))]
    public void C5C7SelectPersistentIndependentRamWhileLockedUpperRomRejectsWrites(HcRomLayout layout, int high)
    {
        var (memory, ports) = Create(layout);
        var expectedRam = memory.Ram.ToArray();
        var expectedRom = memory.Rom.ToArray();
        var expectedShadowRom = memory.ShadowRom.ToArray();
        var expectedShadowRam = memory.ShadowRam.ToArray();

        // The documented CP/M paging state: upper CP/M ROM, video at C000, 7E locked.
        ports.Write((ushort)(high | 0x7e), 0x0f, 0);
        ports.Write((ushort)(high | 0xc5), 0xff, 0); // Address A1, not data, clears CPM.
        Assert.Equal(0xc000, memory.VideoBase);
        AssertWindow(memory, 0xe000, expectedRom, 0x6000, 0x2000);
        AssertWindow(memory, 0xc000, expectedRam, 0xc000, 0x2000);
        WritePattern(memory, 0xc000, 0x2000, 0x31);
        SetPattern(expectedRam, 0xc000, 0x2000, 0x31);
        Assert.Equal(expectedRam, memory.Ram.ToArray());

        ports.Write((ushort)(high | 0xc7), 0, 0); // Set CPM while 7E is still locked.
        Assert.Equal(0x0f, memory.Latches.Port7E);
        Assert.Equal(0xc000, memory.VideoBase); // Video is physical, not the CPU alias.
        AssertWindow(memory, 0xc000, expectedRam, 0xe000, 0x2000);
        WritePattern(memory, 0xc000, 0x2000, 0x92);
        SetPattern(expectedRam, 0xe000, 0x2000, 0x92);
        Assert.Equal(expectedRam, memory.Ram.ToArray());

        // Writes at E000 must hit neither ROM nor the RAM visible through C7's C000 alias.
        WritePattern(memory, 0xe000, 0x2000, 0xe3);
        AssertWindow(memory, 0xe000, expectedRom, 0x6000, 0x2000);
        AssertWindow(memory, 0xc000, expectedRam, 0xe000, 0x2000);
        Assert.Equal(expectedRam, memory.Ram.ToArray());
        Assert.Equal(expectedRom, memory.Rom.ToArray());
        Assert.Equal(expectedShadowRom, memory.ShadowRom.ToArray());
        Assert.Equal(expectedShadowRam, memory.ShadowRam.ToArray());

        ports.Write((ushort)(high | 0x7e), 0, 0); // Locked ROM mapping cannot change.
        Assert.Equal(0x0f, memory.Latches.Port7E);
        ports.Write((ushort)(high | 0xc5), 0xff, 0);
        AssertWindow(memory, 0xc000, expectedRam, 0xc000, 0x2000);
        ports.Write((ushort)(high | 0xc7), 0, 0);
        AssertWindow(memory, 0xc000, expectedRam, 0xe000, 0x2000);

        memory.ResetPaging();
        AssertWindow(memory, 0, expectedRom, 0, 0x4000);
        AssertWindow(memory, 0xc000, expectedRam, 0xc000, 0x2000);
        AssertWindow(memory, 0xe000, expectedRam, 0xe000, 0x2000);
        Assert.Equal(expectedRam, memory.Ram.ToArray());
    }

    [Theory]
    [MemberData(nameof(ProfilesAndPortAliases))]
    public void BothRomHalvesRejectWritesAndRevealUnchangedHiddenRamAfterRemapping(HcRomLayout layout, int high)
    {
        var (memory, ports) = Create(layout);
        var ram = memory.Ram.ToArray();
        var rom = memory.Rom.ToArray();
        var shadowRom = memory.ShadowRom.ToArray();
        var shadowRam = memory.ShadowRam.ToArray();

        for (byte half = 0; half < 2; half++)
        {
            ports.Write((ushort)(high | 0x7e), half, 0);
            AssertWindow(memory, 0, rom, half * 0x4000, 0x4000);
            WritePattern(memory, 0, 0x4000, 0xd1);
            AssertWindow(memory, 0, rom, half * 0x4000, 0x4000);
            AssertStores();

            ports.Write((ushort)(high | 0x7e), (byte)(half | 2), 0);
            AssertWindow(memory, 0, ram, 0, 0x4000); // Hidden RAM did not absorb ROM writes.
            AssertWindow(memory, 0xe000, rom, half * 0x4000 + 0x2000, 0x2000);
            WritePattern(memory, 0xe000, 0x2000, 0xf2);
            AssertWindow(memory, 0xe000, rom, half * 0x4000 + 0x2000, 0x2000);
            AssertStores();

            ports.Write((ushort)(high | 0x7e), half, 0);
            AssertWindow(memory, 0xe000, ram, 0xe000, 0x2000);
        }

        void AssertStores()
        {
            Assert.Equal(ram, memory.Ram.ToArray());
            Assert.Equal(rom, memory.Rom.ToArray());
            Assert.Equal(shadowRom, memory.ShadowRom.ToArray());
            Assert.Equal(shadowRam, memory.ShadowRam.ToArray());
        }
    }

    private static (HcMemory Memory, PortDecoder Ports) Create(HcRomLayout layout)
    {
        var basic = new byte[0x4000];
        var cpm = new byte[0x4000];
        var if1 = new byte[0x4000];
        SetPattern(basic, 0, basic.Length, 0x20);
        SetPattern(cpm, 0, cpm.Length, 0x60);
        SetPattern(if1, 0, if1.Length, 0xa0);
        var memory = new HcMemory(new(layout, RomProvenance: "synthetic complete paging acceptance fixture"), basic, cpm, if1);
        for (var address = 0; address < memory.Ram.Length; address++)
            memory.Ram.Span[address] = Pattern(address, 0x10);
        var idle = new PortDevice();
        return (memory, new PortDecoder(HcIoProfile.Faithful(memory, idle, idle, idle, idle, idle, idle)));
    }

    private static byte Pattern(int offset, int seed) => (byte)(seed + offset / 0x100 + offset % 251);

    private static void SetPattern(byte[] store, int start, int length, int seed)
    {
        for (var i = 0; i < length; i++) store[start + i] = Pattern(i, seed);
    }

    private static void WritePattern(HcMemory memory, int start, int length, int seed)
    {
        for (var i = 0; i < length; i++) memory.Write((ushort)(start + i), Pattern(i, seed));
    }

    private static void AssertWindow(HcMemory memory, int start, byte[] expected, int offset, int length)
    {
        for (var i = 0; i < length; i++) Assert.Equal(expected[offset + i], memory.Read((ushort)(start + i)));
    }
}
