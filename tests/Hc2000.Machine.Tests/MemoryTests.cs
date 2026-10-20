using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class MemoryTests
{
    public static IEnumerable<object[]> Configurations()
    {
        foreach (var layout in new[] { HcRomLayout.V1, HcRomLayout.V2 })
        foreach (var shadowBytes in new[] { 0x400, 0x800 })
        for (var port = 0; port < 16; port++)
        foreach (var cpm in new[] { false, true })
        foreach (var shadow in new[] { false, true })
            yield return new object[] { layout, shadowBytes, (byte)port, cpm, shadow };
    }

    internal static HcMemory Create(MemoryProfile profile)
    {
        var basic = Fixture(0x4000, 0x20);
        var cpm = Fixture(0x4000, 0x60); // Full synthetic half, neither layout claims real ROM provenance.
        var if1 = Fixture(0x4000, 0xa0);
        var memory = new HcMemory(profile, basic, cpm, if1);
        Fixture(profile.RamBytes, 0x10).CopyTo(memory.Ram);
        Fixture(profile.ShadowRamBytes, 0xe0).CopyTo(memory.ShadowRam);
        return memory;
    }

    private static byte[] Fixture(int size, int seed) => Enumerable.Range(0, size)
        .Select(n => (byte)(seed + n / 0x400 + n % 251)).ToArray();

    // Independent CPU-range truth table from spec 04 and expansion-docs §1/§4.
    // Store numbers: RAM=0, ROM=1, shadow ROM=2, shadow RAM=3.
    private static (int Store, int Offset, bool Writable) Expected(int address, int port, bool cpm, bool shadow, int shadowBytes)
    {
        if ((port & 2) == 0 && address < 0x4000)
        {
            if (!shadow) return (1, (port & 1) * 0x4000 + address, false);
            if (address is >= 0x2800 and < 0x3000 or >= 0x3800 and < 0x4000)
                return (3, (address - 0x2800) % shadowBytes, true);
            return (2, address, false);
        }
        if ((port & 2) != 0 && address >= 0xe000)
            return (1, (port & 1) * 0x4000 + address - 0xc000, false);
        if (cpm && address is >= 0xc000 and < 0xe000) return (0, address + 0x2000, true);
        return (0, address, true);
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void EveryLatchCombinationReadsAndWritesExpectedStores(HcRomLayout layout, int shadowBytes, byte port, bool cpm, bool shadow)
    {
        var memory = Create(new(layout, ShadowRamBytes: shadowBytes));
        memory.WritePort7E(port);
        memory.SetCpm(cpm); // C7 sets; C5 clears. Port decode is intentionally not part of this slice.
        memory.SetShadow(shadow);
        var stores = new[] { memory.Ram.ToArray(), memory.Rom.ToArray(), memory.ShadowRom.ToArray(), memory.ShadowRam.ToArray() };
        Assert.Equal(8, memory.Pages.Count);
        Assert.Equal((port & 8) == 0 ? 0x4000 : 0xc000, memory.VideoBase);
        // All addresses, including every chip-select edge and both 1-KiB/2-KiB mirrors.
        for (var address = 0; address < 0x10000; address++)
        {
            var expected = Expected(address, port, cpm, shadow, shadowBytes);
            var actual = memory.Resolve((ushort)address);
            Assert.Equal(expected.Store, (int)actual.Store);
            Assert.Equal(expected.Offset, actual.Offset);
            Assert.Equal(expected.Writable, actual.Writable);
            Assert.Equal(stores[expected.Store][expected.Offset], memory.Read((ushort)address));
        }
        for (var address = 0; address < 0x10000; address++)
        {
            var expected = Expected(address, port, cpm, shadow, shadowBytes);
            var value = (byte)(address / 0x100 + 7);
            memory.Write((ushort)address, value);
            if (expected.Writable) stores[expected.Store][expected.Offset] = value;
        }
        Assert.Equal(stores[0], memory.Ram.ToArray());
        Assert.Equal(stores[1], memory.Rom.ToArray());
        Assert.Equal(stores[2], memory.ShadowRom.ToArray());
        Assert.Equal(stores[3], memory.ShadowRam.ToArray());
    }

    [Fact]
    public void LockResetAndNoOpWritesPreserveStoresAndTableIdentity()
    {
        var memory = Create(new());
        var initial = memory.Pages;
        memory.WritePort7E(0xf0); // D4..D7 do not exist in the four-bit latch.
        memory.SetCpm(false); memory.SetShadow(false); memory.ResetPaging();
        Assert.Same(initial, memory.Pages);
        memory.WritePort7E(0x0f);
        var locked = memory.Pages;
        memory.WritePort7E(0); memory.WritePort7E(0xff);
        Assert.Same(locked, memory.Pages);
        Assert.Equal(0x0f, memory.Latches.Port7E);
        memory.SetCpm(true); Assert.Equal(0xe000, memory.Resolve(0xc000).Offset);
        memory.SetShadow(true); // Shadow latch can change while 7E is locked, but b1 suppresses its mapping.
        Assert.Equal(MemoryStore.Ram, memory.Resolve(0x2800).Store);
        var ram = memory.Ram.ToArray(); var shadowRam = memory.ShadowRam.ToArray();
        memory.ResetPaging();
        Assert.Equal(new PagingLatches { BankSelector = 0xFF }, memory.Latches);
        Assert.Equal(MemoryStore.Rom, memory.Resolve(0).Store);
        Assert.Equal(ram, memory.Ram.ToArray()); Assert.Equal(shadowRam, memory.ShadowRam.ToArray());
        memory.WritePort7E(2); Assert.Equal(MemoryStore.Ram, memory.Resolve(0).Store);
        var table = memory.Pages; memory.Read(0); memory.Write(0, 42);
        Assert.Same(table, memory.Pages);
    }

    [Theory]
    [InlineData(0x400)] [InlineData(0x800)]
    public void ShadowRamMirrorsAndRomWritesNeverReachMainRam(int size)
    {
        var memory = Create(new(ShadowRamBytes: size)); memory.SetShadow(true);
        var ram = memory.Ram.ToArray(); var rom = memory.ShadowRom.ToArray();
        memory.Write(0x2800, 42); Assert.Equal(42, memory.Read(0x3800));
        memory.Write(0x2c00, 17); Assert.Equal(17, memory.Read(0x3c00));
        Assert.Equal(size == 0x400 ? 17 : 42, memory.Read(0x2800));
        memory.Write(0x27ff, 42); memory.Write(0x3000, 42); memory.Write(0x37ff, 42);
        Assert.Equal(ram, memory.Ram.ToArray()); Assert.Equal(rom, memory.ShadowRom.ToArray());
        memory.SetShadow(false); Assert.Equal(MemoryStore.Rom, memory.Resolve(0x2800).Store);
        memory.SetShadow(true); Assert.Equal(17, memory.Read(0x2c00));
    }

    [Theory]
    [InlineData(HcRomLayout.V1)] [InlineData(HcRomLayout.V2)]
    public void LayoutCopiesInputsAndPreservesBothSuppliedCpmPages(HcRomLayout layout)
    {
        var basic = Fixture(0x4000, 1); var cpm = Fixture(0x4000, 33); var if1 = Fixture(0x4000, 65);
        var memory = new HcMemory(new(layout, RomProvenance: "synthetic complete paging fixture"), basic, cpm, if1);
        Assert.Equal(basic, memory.Rom.Span[..0x4000].ToArray());
        Assert.Equal(cpm, memory.Rom.Span[0x4000..].ToArray());
        Assert.Equal(if1, memory.ShadowRom.ToArray());
        var before = memory.Rom.ToArray(); var shadowBefore = memory.ShadowRom.ToArray();
        basic[0] ^= 0xff; cpm[0] ^= 0xff; if1[0] ^= 0xff;
        Assert.Equal(before, memory.Rom.ToArray()); Assert.Equal(shadowBefore, memory.ShadowRom.ToArray());
    }

    [Fact]
    public void LargerPhysicalStoresAndWideOffsetsAreNotTruncatedOrShared()
    {
        var profile = new MemoryProfile(RamBytes: 0x200000, RomBytes: 0x10000, ShadowRomBytes: 0x8000);
        var first = Create(profile); var second = Create(profile);
        Assert.Equal(0x200000, first.Ram.Length); Assert.Equal(0x10000, first.Rom.Length); Assert.Equal(0x8000, first.ShadowRom.Length);
        var page = new MemoryPage(new(MemoryStore.Ram, 0x120000, true));
        var location = page.Resolve(0x1fff); Assert.Equal(0x121fff, location.Offset);
        first.Ram.Span[location.Offset] = 42;
        Assert.NotEqual(first.Ram.Span[location.Offset], second.Ram.Span[location.Offset]);
        var tail = first.Ram.Span[0x10000..].ToArray();
        first.WritePort7E(15); first.SetCpm(true); first.SetShadow(true); first.Write(0xc000, 42); first.ResetPaging();
        Assert.Equal(tail, first.Ram.Span[0x10000..].ToArray());
        Assert.Equal(new PagingLatches { BankSelector = 0xFF }, second.Latches); Assert.NotEqual(42, second.Read(0xc000));
    }

    [Fact]
    public void ShadowLatchIsGatedNotClearedAndPureMappingDoesNotReadStores()
    {
        var memory = Create(new()); memory.SetShadow(true);
        var shadowTable = memory.Pages;
        memory.WritePort7E(2);
        Assert.True(memory.Latches.Shadow);
        Assert.Equal(MemoryStore.Ram, memory.Resolve(0x2800).Store);
        memory.WritePort7E(0);
        Assert.True(memory.Latches.Shadow);
        Assert.Equal(MemoryStore.ShadowRam, memory.Resolve(0x2800).Store);
        Assert.Equal(MemoryStore.ShadowRam, shadowTable[1].Resolve(0x800).Store); // Old tables stay immutable.
        var rebuilt = MemoryPaging.Build(memory.Profile, memory.Latches);
        for (var address = 0; address < 0x10000; address++)
            Assert.Equal(memory.Resolve((ushort)address), rebuilt[address >> 13].Resolve(address & 0x1fff));
        var windows = new[] { new MemorySubwindow(0x800, 0x800, new(MemoryStore.ShadowRam, 0, true, 0x3ff)) };
        var page = new MemoryPage(new(MemoryStore.ShadowRom, 0x2000, false), windows);
        windows[0] = default;
        Assert.Equal(MemoryStore.ShadowRam, page.Resolve(0x800).Store);
    }

    [Fact]
    public void CpuMemoryAccessDoesNotAllocateOrRebuildTheTable()
    {
        var memory = Create(new()); memory.SetShadow(true);
        var table = memory.Pages;
        // Warm every mapping (including RAM and shadow RAM), using the measured code path.
        AccessWholeAddressSpace(memory);
        AccessWholeAddressSpace(memory);
        Assert.Same(table, memory.Pages);

        // Runtime bookkeeping can land on this thread during a sample in a loaded full run.
        // Require a genuinely allocation-free pass, not a positive allowance or an average.
        var minimumAllocated = long.MaxValue;
        for (var sample = 0; sample < 5; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var sum = AccessWholeAddressSpace(memory);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            minimumAllocated = Math.Min(minimumAllocated, allocated);
            Assert.True(sum > 0);
            Assert.Same(table, memory.Pages);
        }
        Assert.Equal(0, minimumAllocated);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int AccessWholeAddressSpace(HcMemory memory)
    {
        var sum = 0;
        for (var i = 0; i < 0x10000; i++) { memory.Write((ushort)i, 42); sum += memory.Read((ushort)i); }
        return sum;
    }

    [Fact]
    public void InvalidProfilesAndRomSizesAreRejected()
    {
        var basic = new byte[0x4000]; var cpm = new byte[0x4000]; var if1 = new byte[0x4000];
        foreach (var profile in new[] { new MemoryProfile(RamBytes: 0xffff), new(RomBytes: 0x6000), new(ShadowRomBytes: 0x2000), new(ShadowRamBytes: 512), new(RomLayout: (HcRomLayout)99) })
            Assert.Throws<ArgumentOutOfRangeException>(() => new HcMemory(profile, basic, cpm, if1));
        Assert.Throws<ArgumentException>(() => new HcMemory(new(), basic[..^1], cpm, if1));
        Assert.Throws<ArgumentException>(() => new HcMemory(new(), basic, new byte[0x2000], if1));
        Assert.Throws<ArgumentException>(() => new HcMemory(new(HcRomLayout.V1), basic, new byte[0x2000], if1));
        Assert.Throws<ArgumentException>(() => new HcMemory(new(RomProvenance: ""), basic, cpm, if1));
        Assert.Throws<ArgumentException>(() => new HcMemory(new(), basic, cpm, if1[..^1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryPage(new()).Resolve(0x2000));
    }
}
