using Xunit;

namespace Hc2000.Z80.Tests;

public class DecoderCoverageTests
{
    [Fact]
    public void EveryCatalogKeyReachesAnExecutionCase()
    {
        var table = new Opcodes();
        var executed = 0;
        var bus = new CoverageBus();
        var cpu = new Z80(bus);
        foreach (var entry in table.Entries)
        {
            Array.Clear(bus.Memory);
            entry.Encoding.CopyTo(bus.Memory, 0x4000);
            cpu.Reset();
            cpu.R.PC = 0x4000; cpu.R.SP = 0x8000;
            cpu.R.HL = cpu.R.IX = cpu.R.IY = 0x6000; cpu.R.BC = 0x0200; cpu.R.DE = 0x7000;
            Assert.InRange(cpu.Step(), 4, 23);
            executed++;
        }
        Assert.Equal(1780, executed);
    }

    [Theory]
    [InlineData(0x4a, 0x7fff, 0, 1, 0x8000, 0x94)] // ADC overflow/half carry
    [InlineData(0x4a, 0xffff, 0, 1, 0, 0x51)]
    [InlineData(0x42, 0x8000, 1, 0, 0x7fff, 0x3e)] // SBC overflow
    [InlineData(0x42, 0, 0, 1, 0xffff, 0xbb)]
    public void SixteenBitAdcSbcFlags(int opcode, int hl, int bc, int carry, int result, int flags)
    {
        var bus = new CoverageBus(); bus.Memory[0] = 0xed; bus.Memory[1] = (byte)opcode;
        var cpu = new Z80(bus);
        cpu.R.HL = (ushort)hl; cpu.R.BC = (ushort)bc; cpu.R.F = (byte)carry;
        Assert.Equal(15, cpu.Step());
        Assert.Equal((ushort)result, cpu.R.HL);
        Assert.Equal((byte)flags, cpu.R.F);
        Assert.Equal(unchecked((ushort)(hl + 1)), cpu.R.WZ);
    }

    [Theory]
    [InlineData(0x67, 0x12, 0x34, 0x14, 0x23)] // RRD
    [InlineData(0x6f, 0x12, 0x34, 0x13, 0x42)] // RLD
    public void NibbleRotations(int opcode, int a, int memory, int finalA, int finalMemory)
    {
        var bus = new CoverageBus(); bus.Memory[0] = 0xed; bus.Memory[1] = (byte)opcode; bus.Memory[0x2000] = (byte)memory;
        var cpu = new Z80(bus); cpu.R.A = (byte)a; cpu.R.F = 1; cpu.R.HL = 0x2000;
        Assert.Equal(18, cpu.Step());
        Assert.Equal((byte)finalA, cpu.R.A);
        Assert.Equal((byte)finalMemory, bus.Memory[0x2000]);
        Assert.Equal((ushort)0x2001, cpu.R.WZ);
        Assert.Equal(1, cpu.R.F & 1);
    }

    [Fact]
    public void InputUsesFullPortAndOriginalBcForMemptr()
    {
        var bus = new CoverageBus(); bus.Memory[0] = 0xed; bus.Memory[1] = 0x40;
        var cpu = new Z80(bus); cpu.R.BC = 0x1234;
        Assert.Equal(12, cpu.Step());
        Assert.Equal((ushort)0x1234, bus.LastPort);
        Assert.Equal((byte)0xa5, cpu.R.B);
        Assert.Equal((ushort)0x1235, cpu.R.WZ);
    }

    [Fact]
    public void InterruptRaisedInsidePrefixCannotBeAcceptedBetweenPrefixes()
    {
        var bus = new CoverageBus();
        bus.Memory[0] = 0xdd; bus.Memory[1] = 0xfd; bus.Memory[2] = 0x84;
        var cpu = new Z80(bus); cpu.R.IFF1 = true; cpu.R.IM = 1;
        bus.RaiseInterrupt = () => cpu.IntLine = true;
        Assert.Equal(12, cpu.Step());
        Assert.Equal((ushort)3, cpu.R.PC);
        Assert.Equal(0, bus.Acknowledgements);
        Assert.Equal(13, cpu.Step());
        Assert.Equal(1, bus.Acknowledgements);
    }

    [Fact]
    public void LdArObservesBothM1RefreshIncrementsAndLdRAOverwritesRefresh()
    {
        var bus = new CoverageBus(); bus.Memory[0] = 0xed; bus.Memory[1] = 0x5f;
        bus.Memory[2] = 0xed; bus.Memory[3] = 0x4f;
        var cpu = new Z80(bus); cpu.R.R = 0xfe;
        Assert.Equal(9, cpu.Step()); Assert.Equal((byte)0x80, cpu.R.A);
        cpu.R.A = 0xff;
        Assert.Equal(9, cpu.Step()); Assert.Equal((byte)0xff, cpu.R.R);
    }

    private sealed class CoverageBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public ushort LastPort;
        public int Acknowledgements;
        public Action? RaiseInterrupt;
        public byte Read(ushort addr) { RaiseInterrupt?.Invoke(); return Memory[addr]; }
        public void Write(ushort addr, byte value) => Memory[addr] = value;
        public byte In(ushort port) { LastPort = port; return 0xa5; }
        public void Out(ushort port, byte value) => LastPort = port;
        public void Contend(ushort addr, int tstates) { }
        public byte IntVector() { Acknowledgements++; return 0xff; }
    }
}
