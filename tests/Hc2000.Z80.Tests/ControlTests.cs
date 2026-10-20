using Xunit;

namespace Hc2000.Z80.Tests;

public class ControlTests
{
    [Fact]
    public void EiThenHaltDefersIntUntilAfterHaltAndStacksFollowingPc()
    {
        var bus = new TestBus(0xfb, 0x76);
        var cpu = new Z80(bus) { IntLine = true };
        cpu.R.IM = 1; cpu.R.SP = 0x8000;
        Assert.Equal(4, cpu.Step());
        Assert.True(cpu.R.IFF1);
        Assert.Equal(4, cpu.Step());
        Assert.True(cpu.Halted);
        Assert.Equal(13, cpu.Step());
        Assert.False(cpu.Halted);
        Assert.False(cpu.R.IFF1);
        Assert.False(cpu.R.IFF2);
        Assert.Equal((ushort)0x38, cpu.R.PC);
        Assert.Equal((ushort)0x7ffe, cpu.R.SP);
        Assert.Equal((byte)2, bus.Memory[0x7ffe]);
        Assert.Equal((byte)0, bus.Memory[0x7fff]);
        Assert.Equal((byte)3, cpu.R.R);
        Assert.Equal(1, bus.Acknowledgements);
    }

    [Fact]
    public void Im2KeepsOddVectorAndStacksBeforeReadingVector()
    {
        var bus = new TestBus() { Vector = 0xff };
        bus.Memory[0x12ff] = 0x78; bus.Memory[0x1300] = 0x56;
        var cpu = new Z80(bus) { IntLine = true };
        cpu.R.IM = 2; cpu.R.I = 0x12; cpu.R.IFF1 = cpu.R.IFF2 = true;
        cpu.R.PC = 0xabcd; cpu.R.SP = 0x8000;
        Assert.Equal(19, cpu.Step());
        Assert.Equal((ushort)0x5678, cpu.R.PC);
        Assert.Equal(["ACK", "W:7FFF:AB", "W:7FFE:CD", "R:12FF", "R:1300"], bus.Events);
    }

    [Theory]
    [InlineData(0xc7, 0x00)] [InlineData(0xef, 0x28)] [InlineData(0xff, 0x38)]
    public void Im0ExecutesRstFromBus(int vector, int destination)
    {
        var bus = new TestBus() { Vector = (byte)vector };
        var cpu = new Z80(bus) { IntLine = true };
        cpu.R.IFF1 = true; cpu.R.PC = 0x1234;
        Assert.Equal(13, cpu.Step());
        Assert.Equal((ushort)destination, cpu.R.PC);
    }

    [Fact]
    public void NmiDuringHaltIsEdgeLatchedAndRetnRestoresIff1()
    {
        var bus = new TestBus(0x76);
        bus.Memory[0x66] = 0xed; bus.Memory[0x67] = 0x45;
        var cpu = new Z80(bus);
        cpu.R.IFF1 = true;
        Assert.Equal(4, cpu.Step());
        cpu.Nmi(); cpu.Nmi();
        Assert.Equal(11, cpu.Step());
        Assert.False(cpu.Halted); Assert.False(cpu.R.IFF1); Assert.True(cpu.R.IFF2);
        Assert.Equal((ushort)0x66, cpu.R.PC);
        Assert.Equal(14, cpu.Step());
        Assert.True(cpu.R.IFF1);
        Assert.Equal((ushort)1, cpu.R.PC);
        Assert.Equal(4, cpu.Step()); // no second latched NMI
    }

    [Theory]
    [InlineData(true, 0x00)] [InlineData(false, 0x04)]
    public void LdAiInterruptParityQuirkCanBeDisabled(bool enabled, int expectedPv)
    {
        var bus = new TestBus(0xed, 0x57);
        var cpu = new Z80(bus) { NmosLdAirInterruptBug = enabled };
        cpu.R.I = 0x12; cpu.R.IFF1 = cpu.R.IFF2 = true; cpu.R.IM = 1;
        Assert.Equal(9, cpu.Step());
        Assert.Equal((byte)4, (byte)(cpu.R.F & 4));
        cpu.IntLine = true;
        Assert.Equal(13, cpu.Step());
        Assert.Equal(expectedPv, cpu.R.F & 4);
    }

    [Fact]
    public void CallReturnUsesHighThenLowWritesAndLowThenHighReads()
    {
        var bus = new TestBus(0xcd, 0x05, 0, 0, 0, 0xc9);
        var cpu = new Z80(bus);
        cpu.R.SP = 0;
        Assert.Equal(17, cpu.Step());
        Assert.Equal((ushort)5, cpu.R.PC);
        Assert.Equal((byte)3, bus.Memory[0xfffe]);
        Assert.Equal((byte)0, bus.Memory[0xffff]);
        Assert.Equal(10, cpu.Step());
        Assert.Equal((ushort)3, cpu.R.PC);
        Assert.Equal((ushort)0, cpu.R.SP);
        Assert.Equal(["R:0000", "R:0001", "R:0002", "W:FFFF:00", "W:FFFE:03", "R:0005", "R:FFFE", "R:FFFF"], bus.Events);
    }

    [Theory]
    [InlineData(0x20, 0, 12, 4)] [InlineData(0x20, 0x40, 7, 2)]
    [InlineData(0xc0, 0x40, 5, 1)] [InlineData(0xc0, 0, 11, 0x1234)]
    public void ConditionalPathsHaveDifferentTimings(int opcode, int flags, int cycles, int pc)
    {
        var bus = new TestBus((byte)opcode, 2);
        bus.Memory[0x8000] = 0x34; bus.Memory[0x8001] = 0x12;
        var cpu = new Z80(bus);
        cpu.R.F = (byte)flags; cpu.R.SP = 0x8000;
        Assert.Equal(cycles, cpu.Step());
        Assert.Equal((ushort)pc, cpu.R.PC);
    }

    [Fact]
    public void IndexedExchangeStackDoesNotChangeFlagsAndHasNineteenPlusPrefixCycles()
    {
        var bus = new TestBus(0xfd, 0xe3);
        bus.Memory[0x8000] = 0x34; bus.Memory[0x8001] = 0x12;
        var cpu = new Z80(bus);
        cpu.R.SP = 0x8000; cpu.R.IY = 0xabcd; cpu.R.F = 0x55;
        Assert.Equal(23, cpu.Step());
        Assert.Equal((ushort)0x1234, cpu.R.IY);
        Assert.Equal((byte)0xcd, bus.Memory[0x8000]);
        Assert.Equal((byte)0xab, bus.Memory[0x8001]);
        Assert.Equal((byte)0x55, cpu.R.F);
        Assert.Equal((byte)0, cpu.R.Q);
    }

    [Theory]
    [InlineData("im1", 13)]
    [InlineData("im2", 19)]
    [InlineData("nmi", 11)]
    public void InterruptBusCallbacksOccurAtExactTStates(string mode, int elapsed)
    {
        var bus = new TestBus { Vector = 0xff };
        var cpu = new Z80(bus);
        bus.Cpu = cpu;
        cpu.R.PC = 0x1234; cpu.R.SP = 0x8000;
        cpu.R.IFF1 = cpu.R.IFF2 = true;
        cpu.R.IM = mode == "im2" ? (byte)2 : (byte)1;
        cpu.R.I = 0x12;
        bus.Memory[0x12ff] = 0x78; bus.Memory[0x1300] = 0x56;
        if (mode == "nmi") cpu.Nmi(); else cpu.IntLine = true;
        Assert.Equal(elapsed, cpu.Step());
        var expected = mode == "nmi"
            ? new[] { "0 C:1234:5", "5 C:7FFF:3", "8 W:7FFF:12", "8 C:7FFE:3", "11 W:7FFE:34" }
            : new[] { "0 C:1234:7", "7 ACK", "7 C:7FFF:3", "10 W:7FFF:12", "10 C:7FFE:3", "13 W:7FFE:34" };
        if (mode == "im2") expected = [.. expected, "13 C:12FF:3", "16 R:12FF", "16 C:1300:3", "19 R:1300"];
        Assert.Equal(expected, bus.TimingEvents);
        Assert.Equal((byte)1, cpu.R.R);
    }

    private sealed class TestBus(params byte[] program) : IBus
    {
        public readonly byte[] Memory = MakeMemory(program);
        public readonly List<string> Events = [];
        public byte Vector = 0xff;
        public int Acknowledgements;
        public Z80? Cpu;
        public readonly List<string> TimingEvents = [];
        private void At(string value) { if (Cpu != null) TimingEvents.Add($"{Cpu.TStates} {value}"); }
        private static byte[] MakeMemory(byte[] program)
        {
            var memory = new byte[65536]; program.CopyTo(memory, 0); return memory;
        }
        public byte Read(ushort address) { Events.Add($"R:{address:X4}"); At($"R:{address:X4}"); return Memory[address]; }
        public void Write(ushort address, byte value) { Events.Add($"W:{address:X4}:{value:X2}"); At($"W:{address:X4}:{value:X2}"); Memory[address] = value; }
        public byte IntVector() { Events.Add("ACK"); At("ACK"); Acknowledgements++; return Vector; }
        public byte In(ushort port) => 0;
        public void Out(ushort port, byte value) { }
        public void Contend(ushort address, int tstates) { At($"C:{address:X4}:{tstates}"); }
    }
}
