using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class IoTests
{
    private static HcMemory Memory() => new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);

    [Theory]
    [InlineData(0x05, 0x15)] [InlineData(0x07, 0x15)]
    [InlineData(0x85, 0x25)] [InlineData(0x87, 0x27)]
    [InlineData(0xf7, 0x37)] [InlineData(0xef, 0x4f)]
    [InlineData(0xfb, 0xff)] [InlineData(0x7e, 0xff)] [InlineData(0xc5, 0xff)]
    [InlineData(0xc7, 0xff)] [InlineData(0x7f, 0xff)]
    public void PortReadsIgnoreHighByteAndRouteToDistinctHardwareDevices(int lowPort, int expected)
    {
        var memory = Memory();
        var nul = new PortDevice();
        var decoder = new PortDecoder(HcIoProfile.Faithful(memory, nul,
            new PortDevice((_, _) => 0x15), new PortDevice((_, _) => 0x25),
            new PortDevice((_, _) => 0x27), new PortDevice((_, _) => 0x37), new PortDevice((_, _) => 0x4f)));
        foreach (var high in new[] { 0, 0x0100, 0x7f00, 0xff00 })
            Assert.Equal((byte)expected, decoder.Read((ushort)(high | lowPort), 123));
    }

    [Fact]
    public void EveryLowByteMatchesTheDocumentedDirectionalTable()
    {
        var memory = Memory();
        var read = "";
        var write = "";
        IPortDevice Spy(string name) => new PortDevice((_, _) => { read = name; return 0xfe; },
            (_, _, _) => write = name);
        var decoder = new PortDecoder(HcIoProfile.Faithful(memory, Spy("FE"), Spy("drive"),
            Spy("MSR"), Spy("data"), Spy("F7"), Spy("EF")));
        for (var port = 0; port <= 255; port++)
        {
            read = write = "";
            var address = (ushort)(0x5500 | port);
            decoder.Read(address, 100);
            decoder.Write(address, 0, 100);
            var expectedRead = (port & 0x81) == 0x80 ? "FE" : port switch
            { 5 or 7 => "drive", 0x85 => "MSR", 0x87 => "data", 0xf7 => "F7", 0xef => "EF", _ => "" };
            var expectedWrite = (port & 0x81) == 0x80 ? "FE" : port switch
            { 5 or 7 => "drive", 0x87 => "data", 0xf7 => "F7", 0xef => "EF", _ => "" };
            Assert.Equal(expectedRead, read);
            Assert.Equal(expectedWrite, write);
        }
    }

    [Fact]
    public void PagingHasEvenPartialDecodeLockAndIndependentCpmAddressLatch()
    {
        var memory = Memory();
        var nul = new PortDevice();
        var decoder = new PortDecoder(HcIoProfile.Faithful(memory, nul, nul, nul, nul, nul, nul));
        decoder.Write(0x1234, 9, 0);
        Assert.Equal(9, memory.Latches.Port7E);
        decoder.Write(0xff7e, 6, 0);
        decoder.Write(0x0000, 0, 0);
        Assert.Equal(6, memory.Latches.Port7E);
        decoder.Write(0x12c7, 0, 0);
        Assert.True(memory.Latches.Cpm);
        decoder.Write(0xffc5, 255, 0);
        Assert.False(memory.Latches.Cpm);
        memory.ResetPaging();
        decoder.Write(0x127f, 9, 0);
        Assert.Equal(0, memory.Latches.Port7E);
    }

    [Fact]
    public void M1AndIntaNeverInvokeThePortDevice()
    {
        var calls = 0;
        var decoder = new PortDecoder([new(0, 0, new PortDevice((_, _) => { calls++; return 0; },
            (_, _, _) => calls++), PortAccess.ReadWrite)]);
        Assert.Equal(0xff, decoder.Read(0xfe, 0, m1: true));
        decoder.Write(0xfe, 0, 0, m1: true);
        Assert.Equal(0xff, PortDecoder.IntVector());
        Assert.Equal(0, calls);
    }

    [Fact]
    [Trait("hardware", "unverified")]
    public void SelectedOddOnlyFdcDecodeDoesNotGuessTheUnconfirmed04Alias()
    {
        var memory = Memory();
        var calls = 0;
        var device = new PortDevice(write: (_, _, _) => calls++);
        var decoder = new PortDecoder(HcIoProfile.Faithful(memory, device, device, device, device, device, device));
        decoder.Write(0x04, 0, 0); // Selected model routes this even alias to 7E, not FDC.
        Assert.Equal(0, calls);
        decoder.Write(0x05, 0, 0);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void StartupRejectsOverlapsInvalidMasksAndAccessButAllowsDirectionalSplit()
    {
        var nul = new PortDevice();
        Assert.Throws<ArgumentException>(() => new PortDecoder([
            new(0x80, 0x81, nul, PortAccess.ReadWrite), new(0xfe, 0xff, nul, PortAccess.Read)]));
        Assert.Throws<ArgumentException>(() => new PortDecoder([new(0xfe, 0x81, nul, PortAccess.Read)]));
        Assert.Throws<ArgumentException>(() => new PortDecoder([new(0, 0, nul, (PortAccess)0)]));
        var profile = new List<PortMapping> { new(0xfe, 0xff, new PortDevice((_, _) => 42), PortAccess.Read) };
        var decoder = new PortDecoder(profile);
        profile.Clear(); // Frozen mapping table.
        Assert.Equal(42, decoder.Read(0xfe, 0));
        _ = new PortDecoder([new(0xfe, 0xff, nul, PortAccess.Read), new(0xfe, 0xff, nul, PortAccess.Write)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Read(0, -1));
    }

    [Fact]
    public void FeReadHasActiveLowKeyboardEarAndFixedHighBits()
    {
        var scheduler = new DeviceScheduler();
        var keyboard = new KeyboardMatrix();
        var ula = new Ula(Memory(), keyboard, scheduler, new FrameClock(scheduler, 1), 2);
        Assert.Equal(0xbf, ula.Read(0xfffe, 0));
        ula.TapeIn = true;
        Assert.Equal(0xff, ula.Read(0xfffe, 0));
        keyboard.SetKey(HcKey.CapsShift, true);
        Assert.Equal(0xfe, ula.Read(0xfefe, 0));
        Assert.Equal(0xff, ula.Read(0xfffe, 0));
        Assert.Throws<ArgumentException>(() => ula.Write(0xfe, 0, 10));
    }
}
