using System.Numerics;
using Xunit;

namespace Hc2000.Z80.Tests;

public class BlockTests
{
    [Theory]
    [InlineData(0xb0)] [InlineData(0xb8)] [InlineData(0xb1)] [InlineData(0xb9)]
    public void TransferCompareRepeatAndTerminalIterationsUseDifferentFlagSources(int opcode)
    {
        var bus = new BlockBus();
        var cpu = new Z80(bus);
        foreach (var address in new ushort[] { 0x00ff, 0x0800, 0x2000, 0x2800 })
        foreach (var count in new ushort[] { 1, 2 })
        {
            cpu.Reset();
            bus.Memory[address] = 0xed; bus.Memory[unchecked((ushort)(address + 1))] = (byte)opcode;
            cpu.R.PC = address; cpu.R.HL = 0x6000; cpu.R.DE = 0x7000; cpu.R.BC = count;
            cpu.R.A = 0x10; cpu.R.F = 1; bus.Memory[0x6000] = 2;
            var compare = (opcode & 1) != 0;
            var direction = (opcode & 8) == 0 ? 1 : -1;
            var repeat = count == 2;
            Assert.Equal(repeat ? 21 : 16, cpu.Step());
            Assert.Equal((ushort)(count - 1), cpu.R.BC);
            Assert.Equal(unchecked((ushort)(0x6000 + direction)), cpu.R.HL);
            Assert.Equal(repeat ? address : unchecked((ushort)(address + 2)), cpu.R.PC);
            var ordinary = compare ? 0x1b : 0x21; // A-data=0E, adjusted=0D; A+data=12
            if (repeat) ordinary |= 4;
            var expected = repeat ? (ordinary & ~0x28) | ((address >> 8) & 0x28) : ordinary;
            Assert.Equal((byte)expected, cpu.R.F);
            if (!compare) Assert.Equal((byte)2, bus.Memory[0x7000]);
        }
    }

    [Fact]
    public void CpirStopsOnMatchEvenWithRemainingCount()
    {
        var bus = new BlockBus(); bus.Memory[0x2800] = 0xed; bus.Memory[0x2801] = 0xb1;
        var cpu = new Z80(bus); cpu.R.PC = 0x2800; cpu.R.HL = 0x6000; cpu.R.BC = 2; cpu.R.AF = 0;
        Assert.Equal(16, cpu.Step());
        Assert.Equal((ushort)0x2802, cpu.R.PC);
        Assert.Equal((byte)0x46, cpu.R.F);
    }

    [Theory]
    [InlineData(0xb2)] [InlineData(0xba)] [InlineData(0xb3)] [InlineData(0xbb)]
    public void IoRepeatFlagsMatchPinnedBanksTransformAcrossAllBValues(int opcode)
    {
        var bus = new BlockBus(); var cpu = new Z80(bus);
        var input = (opcode & 1) == 0;
        var direction = (opcode & 8) == 0 ? 1 : -1;
        foreach (var pc in new ushort[] { 0x00ff, 0x0800, 0x2000, 0x2800 })
        foreach (var c in new byte[] { 0, 1, 127, 128, 254, 255 })
        foreach (var data in new byte[] { 0, 1, 127, 128, 254, 255 })
        for (var b = 0; b < 256; b++)
        {
            cpu.Reset(); bus.Memory[pc] = 0xed; bus.Memory[unchecked((ushort)(pc + 1))] = (byte)opcode;
            cpu.R.PC = pc; cpu.R.B = unchecked((byte)(b + 1)); cpu.R.C = c;
            cpu.R.HL = (ushort)(0x6000 | c); bus.Memory[cpu.R.HL] = data; bus.Input = data;
            var sum = data + ((c + direction) & 255); // L_after is same as C +/- direction in this setup
            var carry = sum > 255;
            var half = carry;
            var pv = Even((sum & 7) ^ b);
            if (b != 0)
            {
                int k;
                if (!carry) k = b & 7;
                else if (data >= 128) { half = (b & 15) == 0; k = (b - 1) & 7; }
                else { half = (b & 15) == 15; k = (b + 1) & 7; }
                pv = pv ^ Even(k) ^ true;
            }
            var expected = (b & 0x80) | (b == 0 ? 0x40 : 0) | (data >= 128 ? 2 : 0)
                | (carry ? 1 : 0) | (half ? 16 : 0) | (pv ? 4 : 0)
                | (b != 0 ? (pc >> 8) & 0x28 : b & 0x28);
            var oldPort = cpu.R.BC;
            Assert.Equal(b != 0 ? 21 : 16, cpu.Step());
            Assert.Equal((byte)b, cpu.R.B);
            Assert.Equal((byte)expected, cpu.R.F);
            Assert.Equal(b != 0 ? pc : unchecked((ushort)(pc + 2)), cpu.R.PC);
            Assert.Equal(input ? oldPort : (ushort)((b << 8) | c), bus.LastPort);
        }
    }

    [Fact]
    public void RepeatFlagsAreEstablishedBeforeInterruptEntry()
    {
        var bus = new BlockBus(); bus.Memory[0x2800] = 0xed; bus.Memory[0x2801] = 0xb0;
        var cpu = new Z80(bus);
        cpu.R.PC = 0x2800; cpu.R.HL = 0x6000; cpu.R.DE = 0x7000; cpu.R.BC = 2;
        cpu.R.IFF1 = true; cpu.R.IM = 1;
        Assert.Equal(21, cpu.Step());
        var repeatFlags = cpu.R.F;
        cpu.IntLine = true;
        Assert.Equal(13, cpu.Step());
        Assert.Equal(repeatFlags, cpu.R.F);
        Assert.Equal((ushort)0x38, cpu.R.PC);
    }

    private static bool Even(int n) => BitOperations.PopCount((uint)(n & 255)) % 2 == 0;
    private sealed class BlockBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public byte Input;
        public ushort LastPort;
        public byte Read(ushort address) => Memory[address];
        public void Write(ushort address, byte value) => Memory[address] = value;
        public byte In(ushort port) { LastPort = port; return Input; }
        public void Out(ushort port, byte value) => LastPort = port;
        public byte IntVector() => 0xff;
        public void Contend(ushort address, int count) { }
    }
}
