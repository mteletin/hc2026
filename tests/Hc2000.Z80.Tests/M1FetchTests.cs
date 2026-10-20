using Xunit;

namespace Hc2000.Z80.Tests;

public class M1FetchTests
{
    [Theory]
    [InlineData(new byte[] { 0 }, 1)]
    [InlineData(new byte[] { 0xcb, 0 }, 2)]
    [InlineData(new byte[] { 0xed, 0x44 }, 2)]
    [InlineData(new byte[] { 0xdd, 0xfd, 0xdd, 0 }, 4)]
    [InlineData(new byte[] { 0xdd, 0xed, 0x44 }, 3)]
    [InlineData(new byte[] { 0xdd, 0xcb, 1, 0x46 }, 2)]
    [InlineData(new byte[] { 0xfd, 0xcb, 1, 0x06 }, 2)]
    [InlineData(new byte[] { 0x3e, 0x42 }, 1)]
    public void FetchesReplaceReadsAndPreserveOrderedBusAndState(byte[] program, int count)
    {
        var plain = new TraceBus(); var observed = new M1Bus();
        program.CopyTo(plain.Memory, 0); program.CopyTo(observed.Memory, 0);
        var a = new Z80(plain); var b = new Z80(observed);
        plain.Cpu = a; observed.Cpu = b;
        a.R.IX = b.R.IX = a.R.IY = b.R.IY = 0x8000;
        Assert.Equal(a.Step(), b.Step());
        Assert.Equal(a.R, b.R); Assert.Equal(a.TStates, b.TStates);
        Assert.Equal(plain.Events, observed.Events);
        Assert.Equal(plain.Memory, observed.Memory);
        Assert.Equal(count, observed.Fetches.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(((ushort)i, i == 0, (long)(4 * (i + 1))), observed.Fetches[i]);
        Assert.DoesNotContain(observed.Reads, address => address < count);
        if (program.Length > count)
            Assert.Contains((ushort)count, observed.Reads);
    }

    [Fact]
    public void ReturnedByteIsExecutedAndTimestampUsesAbsoluteEpoch()
    {
        var bus = new M1Bus { Replacement = 0x3e }; var cpu = new Z80(bus); bus.Cpu = cpu;
        cpu.TStates = 123; cpu.R.PC = 0xffff;
        bus.Memory[0] = 0x42;
        Assert.Equal(7, cpu.Step()); Assert.Equal((byte)0x42, cpu.R.A);
        Assert.Equal(new[] { ((ushort)0xffff, true, 127L) }, bus.Fetches);
        Assert.Equal(new ushort[] { 0 }, bus.Reads);
    }

    [Fact]
    public void HaltRefreshRemainsOrdinaryReadAndNmiResumesStart()
    {
        var bus = new M1Bus(); var cpu = new Z80(bus); bus.Cpu = cpu;
        bus.Memory[0] = 0x76;
        cpu.Step(); cpu.Step(); cpu.Step();
        Assert.Equal(new[] { ((ushort)0, true, 4L) }, bus.Fetches);
        Assert.Equal(new ushort[] { 1, 1 }, bus.Reads);
        Assert.Equal((byte)3, cpu.R.R);
        cpu.Nmi(); Assert.Equal(11, cpu.Step());
        Assert.Single(bus.Fetches);
        cpu.Step(); Assert.Equal(((ushort)0x66, true, 27L), bus.Fetches[1]);
    }

    [Theory]
    [InlineData(0, 0xff, 13)]
    [InlineData(1, 0xff, 13)]
    [InlineData(2, 0xff, 19)]
    public void InterruptAcknowledgeIsExcludedAndNextInstructionStarts(int mode, int vector, int cycles)
    {
        var bus = new M1Bus { Vector = (byte)vector }; var cpu = new Z80(bus); bus.Cpu = cpu;
        cpu.R.IM = (byte)mode; cpu.R.IFF1 = true; cpu.IntLine = true; cpu.R.SP = 0x9000;
        Assert.Equal(cycles, cpu.Step());
        Assert.Empty(bus.Fetches);
        cpu.IntLine = false;
        ushort pc = cpu.R.PC; long timestamp = cpu.TStates + 4;
        cpu.Step(); Assert.Equal((pc, true, timestamp), bus.Fetches[^1]);
    }

    [Theory]
    [InlineData(0x00)] [InlineData(0xcb)] [InlineData(0xed)] [InlineData(0xdd)] [InlineData(0xfd)]
    public void UnsupportedIm0PrefixesPreserveExistingExceptionWithoutFetch(int vector)
    {
        var bus = new M1Bus { Vector = (byte)vector }; var cpu = new Z80(bus); bus.Cpu = cpu;
        cpu.R.IM = 0; cpu.R.IFF1 = true; cpu.IntLine = true;
        Assert.Throws<NotSupportedException>(() => cpu.Step());
        Assert.Empty(bus.Fetches); Assert.Empty(bus.Reads);
        Assert.Equal(7L, cpu.TStates);
    }

    private class TraceBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public readonly List<string> Events = [];
        public readonly List<ushort> Reads = [];
        public Z80 Cpu = null!;
        public byte Vector;
        protected byte Access(ushort address, byte value) { Events.Add($"{Cpu.TStates} R {address} {value}"); return value; }
        public byte Read(ushort address) { Reads.Add(address); return Access(address, Memory[address]); }
        public void Write(ushort address, byte value) { Events.Add($"{Cpu.TStates} W {address} {value}"); Memory[address] = value; }
        public byte In(ushort port) { Events.Add($"{Cpu.TStates} IN {port}"); return 0; }
        public void Out(ushort port, byte value) => Events.Add($"{Cpu.TStates} OUT {port} {value}");
        public void Contend(ushort address, int count) => Events.Add($"{Cpu.TStates} C {address} {count}");
        public byte IntVector() { Events.Add($"{Cpu.TStates} ACK"); return Vector; }
    }
    private sealed class M1Bus : TraceBus, IM1FetchBus
    {
        public readonly List<(ushort Address, bool Start, long Time)> Fetches = [];
        public byte? Replacement;
        public byte FetchM1(ushort address, bool instructionStart, long cpuTStates)
        {
            Assert.Equal(Cpu.TStates, cpuTStates);
            Fetches.Add((address, instructionStart, cpuTStates));
            return Access(address, Replacement ?? Memory[address]);
        }
    }
}
