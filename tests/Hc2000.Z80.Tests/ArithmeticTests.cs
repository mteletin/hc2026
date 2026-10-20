using System.Numerics;
using Xunit;

namespace Hc2000.Z80.Tests;

public class ArithmeticTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(1, 1)]
    [InlineData(2, 0)] [InlineData(3, 0)] [InlineData(3, 1)]
    [InlineData(4, 0)] [InlineData(5, 0)] [InlineData(6, 0)] [InlineData(7, 0)]
    public void EightBitAluExhaustive(int operation, int carry)
    {
        var bus = new MemoryBus();
        var cpu = new Z80(bus);
        bus.Memory[0] = (byte)(0x80 + operation * 8); // operand B
        for (var a = 0; a < 256; a++)
        for (var b = 0; b < 256; b++)
        {
            cpu.R.PC = 0; cpu.R.A = (byte)a; cpu.R.B = (byte)b; cpu.R.F = (byte)carry;
            Assert.Equal(4, cpu.Step());
            var subtraction = operation is 2 or 3 or 7;
            int raw;
            int expected;
            if (operation is <= 3 or 7)
            {
                raw = subtraction ? a - b - carry : a + b + carry;
                var signed = subtraction ? (sbyte)a - (sbyte)b - carry : (sbyte)a + (sbyte)b + carry;
                expected = ResultFlags((byte)raw, false) | (raw < 0 || raw > 255 ? 1 : 0)
                    | (signed < -128 || signed > 127 ? 4 : 0)
                    | ((subtraction ? (a & 15) - (b & 15) - carry < 0 : (a & 15) + (b & 15) + carry > 15) ? 16 : 0)
                    | (subtraction ? 2 : 0);
                if (operation == 7) expected = (expected & ~0x28) | (b & 0x28);
            }
            else
            {
                raw = operation switch { 4 => a & b, 5 => a ^ b, _ => a | b };
                expected = ResultFlags((byte)raw, true) | (operation == 4 ? 16 : 0);
            }
            Assert.Equal((byte)expected, cpu.R.F);
            Assert.Equal((byte)(operation == 7 ? a : raw), cpu.R.A);
            Assert.Equal(cpu.R.F, cpu.R.Q);
        }
    }

    [Theory]
    [InlineData(0x04, 0x7f, 0x80, 0x95)] // INC B, carry retained
    [InlineData(0x05, 0x80, 0x7f, 0x3f)] // DEC B
    [InlineData(0x04, 0xff, 0x00, 0x51)]
    [InlineData(0x05, 0x00, 0xff, 0xbb)]
    public void IncDecOverflowHalfCarryAndCarryPreservation(int opcode, int before, int after, int flags)
    {
        var bus = new MemoryBus((byte)opcode);
        var cpu = new Z80(bus);
        cpu.R.B = (byte)before; cpu.R.F = 1;
        Assert.Equal(4, cpu.Step());
        Assert.Equal((byte)after, cpu.R.B);
        Assert.Equal((byte)flags, cpu.R.F);
    }

    [Theory]
    [InlineData(0x00, 0x81, 0x03, 0x05)] // RLC
    [InlineData(0x08, 0x81, 0xc0, 0x85)] // RRC
    [InlineData(0x10, 0x80, 0x01, 0x01)] // RL
    [InlineData(0x18, 0x01, 0x80, 0x81)] // RR
    [InlineData(0x20, 0x80, 0x00, 0x45)] // SLA
    [InlineData(0x28, 0x81, 0xc0, 0x85)] // SRA
    [InlineData(0x30, 0x00, 0x01, 0x00)] // SLL
    [InlineData(0x38, 0x01, 0x00, 0x45)] // SRL
    public void CbRotatesAndUndocumentedSll(int opcode, int before, int after, int flags)
    {
        var bus = new MemoryBus(0xcb, (byte)opcode);
        var cpu = new Z80(bus);
        cpu.R.B = (byte)before; cpu.R.F = 1;
        Assert.Equal(8, cpu.Step());
        Assert.Equal((byte)after, cpu.R.B);
        Assert.Equal((byte)flags, cpu.R.F);
        Assert.Equal((byte)2, cpu.R.R);
    }

    [Fact]
    public void IndexedCbCopiesResultAndDoesNotCountFinalOpcodeAsM1()
    {
        var bus = new MemoryBus(0xdd, 0xcb, 0xfe, 0x00); // RLC (IX-2),B
        bus.Memory[0xffff] = 0x81;
        var cpu = new Z80(bus);
        cpu.R.IX = 1; cpu.R.R = 0xfe;
        Assert.Equal(23, cpu.Step());
        Assert.Equal((byte)3, bus.Memory[0xffff]);
        Assert.Equal((byte)3, cpu.R.B);
        Assert.Equal((byte)0x80, cpu.R.R);
        Assert.Equal((ushort)0xffff, cpu.R.WZ);
    }

    [Fact]
    public void BitHlUsesMemptrAndIndexedBitDoesNotCopyRegister()
    {
        var bus = new MemoryBus(0xcb, 0x46);
        var cpu = new Z80(bus);
        cpu.R.HL = 0x1000; cpu.R.WZ = 0x2800; cpu.R.F = 1;
        Assert.Equal(12, cpu.Step());
        Assert.Equal((byte)0x7d, cpu.R.F);
        bus.Memory[2] = 0xdd; bus.Memory[3] = 0xcb; bus.Memory[4] = 0; bus.Memory[5] = 0x40;
        cpu.R.IX = 0x2000; cpu.R.B = 99;
        Assert.Equal(20, cpu.Step());
        Assert.Equal((byte)99, cpu.R.B);
        Assert.Equal((byte)0x75, cpu.R.F);
    }

    [Fact]
    public void RepeatedPrefixesLastIndexWinsAndEachCountsM1()
    {
        var bus = new MemoryBus(0xdd, 0xfd, 0xdd, 0x84); // ADD A,IXH
        var cpu = new Z80(bus);
        cpu.R.A = 1; cpu.R.IX = 0x0200; cpu.R.IY = 0x0400;
        Assert.Equal(16, cpu.Step());
        Assert.Equal((byte)3, cpu.R.A);
        Assert.Equal((byte)4, cpu.R.R);
    }

    [Fact]
    public void IndexedMemoryLoadUsesPlainHNotIxh()
    {
        var bus = new MemoryBus(0xdd, 0x66, 0xff, 0xdd, 0x74, 0x01);
        bus.Memory[0x1fff] = 0x55;
        var cpu = new Z80(bus);
        cpu.R.IX = 0x2000;
        Assert.Equal(19, cpu.Step());
        Assert.Equal((byte)0x55, cpu.R.H);
        Assert.Equal((ushort)0x2000, cpu.R.IX);
        Assert.Equal(19, cpu.Step());
        Assert.Equal((byte)0x55, bus.Memory[0x2001]);
    }

    [Fact]
    public void QControlsScfAndCcfUndocumentedBits()
    {
        var bus = new MemoryBus(0x37, 0x3f, 0x37);
        var cpu = new Z80(bus);
        cpu.R.A = 0; cpu.R.F = 0x28; cpu.R.Q = 0;
        cpu.Step(); Assert.Equal((byte)0x29, cpu.R.F);
        cpu.Step(); Assert.Equal((byte)0x10, cpu.R.F); // Q nonzero => XY from A, old carry becomes H
        cpu.R.F = 0x28; cpu.R.Q = 0xff;
        cpu.Step(); Assert.Equal((byte)1, cpu.R.F);
    }

    [Theory]
    [InlineData(0x4c)] [InlineData(0x00)]
    public void EdHolesOrAliasesDoNotLosePrefixCycles(int opcode)
    {
        var bus = new MemoryBus(0xdd, 0xed, (byte)opcode);
        var cpu = new Z80(bus);
        cpu.R.A = 1;
        Assert.Equal(12, cpu.Step());
        Assert.Equal((byte)3, cpu.R.R);
        if (opcode == 0x4c) Assert.Equal((byte)0xff, cpu.R.A);
    }

    private static int ResultFlags(byte value, bool parity) => (value & 0xa8) | (value == 0 ? 0x40 : 0)
        | (parity && BitOperations.PopCount((uint)value) % 2 == 0 ? 4 : 0);

    private sealed class MemoryBus(params byte[] program) : IBus
    {
        public readonly byte[] Memory = MakeMemory(program);
        private static byte[] MakeMemory(byte[] program)
        {
            var memory = new byte[65536];
            program.CopyTo(memory, 0);
            return memory;
        }
        public byte Read(ushort addr) => Memory[addr];
        public void Write(ushort addr, byte value) => Memory[addr] = value;
        public byte In(ushort port) => 0xff;
        public void Out(ushort port, byte value) { }
        public void Contend(ushort addr, int tstates) { }
        public byte IntVector() => 0xff;
    }
}
