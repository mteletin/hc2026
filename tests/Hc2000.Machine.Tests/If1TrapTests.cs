using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class If1TrapTests
{
    public static IEnumerable<object[]> Configurations()
    {
        foreach (var layout in new[] { HcRomLayout.V1, HcRomLayout.V2 })
        foreach (var bytes in new[] { 0x400, 0x800 })
        for (var port = 0; port < 16; port++)
        foreach (var cpm in new[] { false, true })
            yield return new object[] { layout, bytes, (byte)port, cpm };
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void FetchSelectsNewMappingBeforeReturningByteAndPreservesOtherLatches(
        HcRomLayout layout, int bytes, byte port, bool cpm)
    {
        var memory = MemoryTests.Create(new(layout, ShadowRamBytes: bytes));
        memory.WritePort7E(port);
        memory.SetCpm(cpm);
        foreach (var trap in new ushort[] { 0x0008, 0x1708 })
        {
            memory.SetShadow(false);
            var originalRam = memory.Ram.ToArray();
            var originalRom = memory.Rom.ToArray();
            Assert.Equal((port & 2) == 0 ? memory.ShadowRom.Span[trap] : memory.Ram.Span[trap], memory.ReadM1(trap));
            Assert.Equal(new PagingLatches(port, cpm, true), memory.Latches);
            var table = memory.Pages;
            Assert.Equal(memory.Read(trap), memory.ReadM1(trap));
            Assert.Same(table, memory.Pages); // Repeated page-in does not rebuild.
            if ((port & 2) == 0)
            {
                memory.Write(0x2800, 0x5a);
                if (bytes == 0x400) Assert.Equal(0x5a, memory.Read(0x2c00));
                else Assert.NotEqual(0x5a, memory.Read(0x2c00)); // Mod's second KiB is distinct.
                Assert.Equal(0x5a, memory.Read(0x3800));
                memory.Write(0x0008, 0x77); // Shadow ROM is read-only.
            }
            var pageOutByte = memory.Read(0x0700); // Fetch the opcode from the mapping active before page-out.
            Assert.Equal(pageOutByte, memory.ReadM1(0x0700));
            Assert.Equal(new PagingLatches(port, cpm, false), memory.Latches);
            table = memory.Pages;
            memory.ReadM1(0x0700);
            Assert.Same(table, memory.Pages);
            Assert.Equal(originalRam, memory.Ram.ToArray());
            Assert.Equal(originalRom, memory.Rom.ToArray());
        }
    }

    [Fact]
    public void PageOutFetchReturnsShadowRetThenClearsShadowForLaterAccesses()
    {
        var memory = MemoryWithPageOutByte(0xc9);
        memory.SetShadow(true);

        Assert.Equal(0xc9, memory.ReadM1(0x0700));
        Assert.False(memory.Latches.Shadow);
        Assert.Equal(0x71, memory.ReadM1(0x0700)); // Inactive-shadow page-out uses BASIC ROM.
        Assert.False(memory.Latches.Shadow);

        memory.SetShadow(true);
        Assert.Equal(0xc9, memory.Read(0x0700)); // Ordinary reads observe the map but never page out.
        Assert.True(memory.Latches.Shadow);
    }

    private static HcMemory MemoryWithPageOutByte(byte pageOutByte)
    {
        var seeded = MemoryTests.Create(new());
        var rom = seeded.Rom.ToArray();
        var if1 = seeded.ShadowRom.ToArray();
        rom[0x0700] = 0x71;
        if1[0x0700] = pageOutByte;
        return new HcMemory(new(), rom[..0x4000], rom[0x4000..0x8000], if1);
    }

    [Fact]
    public void OrdinaryReadsAndWritesAtTrapAddressesNeverChangeShadow()
    {
        var memory = MemoryTests.Create(new());
        foreach (var shadow in new[] { false, true })
        {
            memory.SetShadow(shadow);
            var table = memory.Pages;
            foreach (var address in new ushort[] { 8, 0x1708, 0x700 })
            {
                var expected = memory.Read(address);
                memory.Write(address, 0x55);
                Assert.Equal(expected, memory.Read(address));
                Assert.Equal(shadow, memory.Latches.Shadow);
                Assert.Same(table, memory.Pages);
            }
        }
    }

    [Theory]
    [InlineData(0x0007)]
    [InlineData(0x0009)]
    [InlineData(0x1707)]
    [InlineData(0x1709)]
    [InlineData(0x06ff)]
    [InlineData(0x0701)]
    [InlineData(0x8008)]
    [InlineData(0x9708)]
    [InlineData(0x8700)]
    public void TrapDecodeIsExactNotAnAddressRangeOrLowByteAlias(int address)
    {
        var memory = MemoryTests.Create(new());
        foreach (var shadow in new[] { false, true })
        {
            memory.SetShadow(shadow);
            var table = memory.Pages;
            Assert.Equal(memory.Read((ushort)address), memory.ReadM1((ushort)address));
            Assert.Equal(shadow, memory.Latches.Shadow);
            Assert.Same(table, memory.Pages);
        }
    }

    [Fact]
    public void CustomProfileReplacesUnconfirmedFallbackAddresses()
    {
        var memory = MemoryTests.Create(new() { If1Traps = new(0x1234, 0x2345, 0x3456) });
        foreach (var address in new ushort[] { 8, 0x1708 })
        {
            memory.ReadM1(address);
            Assert.False(memory.Latches.Shadow);
        }
        foreach (var address in new ushort[] { 0x1234, 0x2345 })
        {
            memory.ReadM1(address);
            Assert.True(memory.Latches.Shadow);
            memory.ReadM1(0x0700);
            Assert.True(memory.Latches.Shadow);
            memory.ReadM1(0x3456);
            Assert.False(memory.Latches.Shadow);
        }
    }

    [Fact]
    public void ResetDropsTrapLatchButPreservesWorkspaceAndOtherMachines()
    {
        var first = MemoryTests.Create(new());
        var second = MemoryTests.Create(new());
        first.ReadM1(8);
        first.Write(0x2800, 0x5a);
        var workspace = first.ShadowRam.ToArray();
        first.ResetPaging();
        Assert.False(first.Latches.Shadow);
        Assert.Equal(workspace, first.ShadowRam.ToArray());
        Assert.False(second.Latches.Shadow);
        Assert.NotEqual(0x5a, second.ShadowRam.Span[0]);
        first.ReadM1(0x1708);
        Assert.Equal(0x5a, first.Read(0x2800));
    }

    [Fact]
    public void AmbiguousOrMissingProfileIsRejected()
    {
        Assert.Throws<ArgumentException>(() => MemoryTests.Create(new() { If1Traps = new(8, 0x1708, 8) }));
        Assert.Throws<ArgumentException>(() => MemoryTests.Create(new() { If1Traps = new(8, 0x1708, 0x1708) }));
        Assert.Throws<ArgumentNullException>(() => MemoryTests.Create(new() { If1Traps = null! }));
    }
}
