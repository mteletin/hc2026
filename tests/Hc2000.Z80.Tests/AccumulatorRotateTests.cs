using Xunit;

namespace Hc2000.Z80.Tests;

public class AccumulatorRotateTests
{
    [Theory]
    [InlineData(0x07, 0)] [InlineData(0x0f, 1)]
    [InlineData(0x17, 2)] [InlineData(0x1f, 3)]
    public void EveryAccumulatorAndFlagCombinationHasNmosAccumulatorFlags(int opcode, int operation)
    {
        var bus = new RamBus(); bus.Memory[0] = (byte)opcode;
        var cpu = new Z80(bus);
        for (var a = 0; a < 256; a++)
        for (var flags = 0; flags < 256; flags++)
        {
            cpu.Reset(); cpu.R.A = (byte)a; cpu.R.F = (byte)flags; cpu.R.Q = (byte)flags;
            // Independently express the shift/wrap/old-carry rules arithmetically.
            var left = (operation & 1) == 0;
            var outgoing = left ? a / 128 : a % 2;
            var incoming = operation < 2 ? outgoing : flags % 2;
            var result = (byte)(left ? (a * 2 + incoming) % 256 : a / 2 + incoming * 128);
            var expected = (byte)((flags & 0xc4) | (result & 0x28) | outgoing);
            Assert.Equal(4, cpu.Step());
            Assert.Equal(result, cpu.R.A);
            Assert.Equal(expected, cpu.R.F);
            Assert.Equal(expected, cpu.R.Q);
            Assert.Equal((ushort)1, cpu.R.PC);
            Assert.Equal((byte)1, cpu.R.R);
            Assert.Equal(4L, cpu.TStates);
        }
    }

    private sealed class RamBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public byte Read(ushort address) => Memory[address];
        public void Write(ushort address, byte value) => Memory[address] = value;
        public byte In(ushort port) => 0xff;
        public void Out(ushort port, byte value) { }
        public void Contend(ushort address, int count) { }
        public byte IntVector() => 0xff;
    }
}
