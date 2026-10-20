using Xunit;

namespace Hc2000.Z80.Tests;

// Spec 01-z80.md: IM0 requires RST support, not arbitrary bus instructions.
// All assertions exercise the existing core; no interrupt behavior is extended.
public class InterruptAcceptanceTests
{
    [Theory]
    [InlineData(0xc7, 0x00)] [InlineData(0xcf, 0x08)]
    [InlineData(0xd7, 0x10)] [InlineData(0xdf, 0x18)]
    [InlineData(0xe7, 0x20)] [InlineData(0xef, 0x28)]
    [InlineData(0xf7, 0x30)] [InlineData(0xff, 0x38)]
    public void AllRequiredIm0RstsAcknowledgeThenStackAtExactTimes(int vector, int destination)
    {
        var bus = new TraceBus { Vector = (byte)vector };
        var cpu = Create(bus);
        cpu.R.IM = 0; cpu.R.PC = 0xabcd; cpu.R.SP = 0;
        cpu.R.R = 0xff; cpu.R.IFF1 = cpu.R.IFF2 = true; cpu.IntLine = true;
        Assert.Equal(13, cpu.Step());
        Assert.Equal((ushort)destination, cpu.R.PC);
        Assert.Equal((ushort)destination, cpu.R.WZ);
        Assert.Equal((ushort)0xfffe, cpu.R.SP);
        Assert.Equal((byte)0x80, cpu.R.R);
        Assert.False(cpu.R.IFF1); Assert.False(cpu.R.IFF2);
        Assert.Equal(new[] { "0 C ABCD 7", "7 ACK", "7 C FFFF 3", "10 W FFFF AB", "10 C FFFE 3", "13 W FFFE CD" }, bus.Events);
    }

    [Fact]
    public void Im2OddVectorWrapsItsHighByteReadWithoutForcingTheLowBit()
    {
        var bus = new TraceBus { Vector = 0xff }; var cpu = Create(bus);
        cpu.R.IM = 2; cpu.R.I = 0xff; cpu.R.PC = 0xabcd; cpu.R.SP = 0x9000;
        cpu.R.IFF1 = cpu.R.IFF2 = true; cpu.IntLine = true;
        bus.Memory[0xffff] = 0x78; bus.Memory[0] = 0x56;
        Assert.Equal(19, cpu.Step());
        Assert.Equal((ushort)0x5678, cpu.R.PC); Assert.Equal((ushort)0x5678, cpu.R.WZ);
        Assert.Equal(new[] { "0 C ABCD 7", "7 ACK", "7 C 8FFF 3", "10 W 8FFF AB", "10 C 8FFE 3", "13 W 8FFE CD", "13 C FFFF 3", "16 R FFFF", "16 C 0000 3", "19 R 0000" }, bus.Events);
    }

    [Fact]
    public void NmiWinsOverSimultaneousIntAndPreservesThePriorIff1InIff2()
    {
        var bus = new TraceBus(); var cpu = Create(bus);
        cpu.R.PC = 0xabcd; cpu.R.SP = 0x9000; cpu.R.R = 0xff;
        cpu.R.IFF1 = cpu.R.IFF2 = true; cpu.IntLine = true; cpu.Nmi();
        Assert.Equal(11, cpu.Step());
        Assert.Equal((ushort)0x66, cpu.R.PC); Assert.Equal((byte)0x80, cpu.R.R);
        Assert.False(cpu.R.IFF1); Assert.True(cpu.R.IFF2);
        Assert.Equal(new[] { "0 C ABCD 5", "5 C 8FFF 3", "8 W 8FFF AB", "8 C 8FFE 3", "11 W 8FFE CD" }, bus.Events);
    }

    [Fact]
    public void EiDefersIntAcrossTheWholeRepeatedPrefixInstruction()
    {
        var bus = new TraceBus(); var cpu = Create(bus);
        bus.Memory[0] = 0xfb; bus.Memory[1] = 0xdd; bus.Memory[2] = 0xfd;
        cpu.R.IM = 1; cpu.R.SP = 0x9000; cpu.IntLine = true;
        Assert.Equal(4, cpu.Step()); Assert.Equal((ushort)1, cpu.R.PC);
        Assert.Equal(12, cpu.Step()); Assert.Equal((ushort)4, cpu.R.PC);
        Assert.DoesNotContain("ACK", bus.Events.Select(e => e.Split(' ')[1]));
        bus.Events.Clear();
        Assert.Equal(13, cpu.Step()); Assert.Equal((ushort)0x38, cpu.R.PC);
        Assert.Equal((byte)5, cpu.R.R);
        Assert.Equal(new[] { "16 C 0004 7", "23 ACK", "23 C 8FFF 3", "26 W 8FFF 00", "26 C 8FFE 3", "29 W 8FFE 04" }, bus.Events);
    }

    [Fact]
    public void NmiRaisedInsidePrefixFetchWaitsForTheNextInstructionBoundary()
    {
        var bus = new TraceBus(); var cpu = Create(bus);
        bus.Memory[0] = 0xdd; bus.Memory[1] = 0xfd;
        bus.OnRead = address => { if (address == 0) cpu.Nmi(); };
        Assert.Equal(12, cpu.Step()); Assert.Equal((ushort)3, cpu.R.PC);
        Assert.Equal((byte)3, cpu.R.R);
        bus.Events.Clear();
        Assert.Equal(11, cpu.Step()); Assert.Equal((ushort)0x66, cpu.R.PC);
        Assert.Equal((byte)4, cpu.R.R);
        Assert.Equal("12 C 0003 5", bus.Events[0]);
        Assert.DoesNotContain(bus.Events, e => e.EndsWith("ACK", StringComparison.Ordinal));
    }

    [Fact]
    public void MaskedIntDoesNotAcknowledgeOrClearIff2()
    {
        var bus = new TraceBus(); var cpu = Create(bus);
        cpu.R.IFF2 = true; cpu.IntLine = true;
        Assert.Equal(4, cpu.Step()); Assert.True(cpu.R.IFF2);
        Assert.Equal(new[] { "0 C 0000 4", "4 R 0000" }, bus.Events);
    }

    [Fact]
    public void ResetClearsPendingNmiHaltIntAndEpochWithoutAnyBusEffect()
    {
        var bus = new TraceBus(); var cpu = Create(bus);
        cpu.Nmi(); cpu.Halted = true; cpu.IntLine = true; cpu.TStates = 123;
        cpu.R.IFF1 = cpu.R.IFF2 = true;
        cpu.Reset();
        Assert.Empty(bus.Events); Assert.Equal(0L, cpu.TStates);
        Assert.False(cpu.Halted); Assert.False(cpu.IntLine);
        Assert.Equal(new Registers { AF = 0xffff, SP = 0xffff }, cpu.R);
        Assert.Equal(4, cpu.Step()); Assert.Equal((ushort)1, cpu.R.PC);
        Assert.Equal(new[] { "0 C 0000 4", "4 R 0000" }, bus.Events);
    }

    private static Z80 Create(TraceBus bus)
    {
        var cpu = new Z80(bus); bus.Cpu = cpu; return cpu;
    }

    private sealed class TraceBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public readonly List<string> Events = [];
        public Z80 Cpu = null!;
        public byte Vector = 0xff;
        public Action<ushort>? OnRead;
        public byte Read(ushort address) { Events.Add($"{Cpu.TStates} R {address:X4}"); OnRead?.Invoke(address); return Memory[address]; }
        public void Write(ushort address, byte value) { Events.Add($"{Cpu.TStates} W {address:X4} {value:X2}"); Memory[address] = value; }
        public byte In(ushort port) => 0;
        public void Out(ushort port, byte value) { }
        public void Contend(ushort address, int count) => Events.Add($"{Cpu.TStates} C {address:X4} {count}");
        public byte IntVector() { Events.Add($"{Cpu.TStates} ACK"); return Vector; }
    }
}
