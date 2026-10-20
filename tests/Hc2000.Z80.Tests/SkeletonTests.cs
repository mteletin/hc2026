using Xunit;

namespace Hc2000.Z80.Tests;

public class SkeletonTests
{
    [Fact]
    public void PairAndByteViewsShareStorage()
    {
        var r = new Registers { AF = 0x1234, BC = 0x5678, DE = 0x9abc, HL = 0xdef0, IX = 0x1122, IY = 0x3344 };
        Assert.Equal((byte)0x12, r.A);
        Assert.Equal((byte)0x34, r.F);
        Assert.Equal((byte)0x56, r.B);
        Assert.Equal((byte)0x78, r.C);
        Assert.Equal((byte)0x9a, r.D);
        Assert.Equal((byte)0xbc, r.E);
        Assert.Equal((byte)0xde, r.H);
        Assert.Equal((byte)0xf0, r.L);
        Assert.Equal((byte)0x11, r.IXH);
        Assert.Equal((byte)0x22, r.IXL);
        Assert.Equal((byte)0x33, r.IYH);
        Assert.Equal((byte)0x44, r.IYL);
        r.A = r.B = r.D = r.H = r.IXH = r.IYH = 0xab;
        r.F = r.C = r.E = r.L = r.IXL = r.IYL = 0xcd;
        Assert.Equal((ushort)0xabcd, r.AF);
        Assert.Equal(r.AF, r.BC);
        Assert.Equal(r.AF, r.DE);
        Assert.Equal(r.AF, r.HL);
        Assert.Equal(r.AF, r.IX);
        Assert.Equal(r.AF, r.IY);
        r.MEMPTR = 0x1234;
        Assert.Equal((ushort)0x1234, r.WZ);
        r.WZ = 0xabcd;
        Assert.Equal((ushort)0xabcd, r.MEMPTR);
    }

    [Fact]
    public void ResetClearsStateAndPendingNmiWithoutTouchingBus()
    {
        var bus = new RecordingBus();
        var cpu = new Z80(bus)
        {
            R = new Registers { PC = 123, R = 255, I = 255, IM = 2, IFF1 = true, IFF2 = true,
                WZ = 123, Q = 255, AFShadow = 1, BCShadow = 2, DEShadow = 3, HLShadow = 4 },
            TStates = 999, Halted = true, IntLine = true
        };
        cpu.Nmi();
        cpu.Reset();
        Assert.Equal(new Registers { AF = 0xffff, SP = 0xffff }, cpu.R);
        Assert.Equal(0L, cpu.TStates);
        Assert.False(cpu.Halted);
        Assert.False(cpu.IntLine);
        Assert.Empty(bus.Events);
        Assert.Equal(4, cpu.Step());
    }

    [Theory]
    [InlineData(0x00, 0x01)]
    [InlineData(0x7f, 0x00)]
    [InlineData(0x80, 0x81)]
    [InlineData(0xff, 0x80)]
    public void NopFetchCountsM1AndPreservesRefreshBit7(int before, int after)
    {
        var bus = new RecordingBus();
        var cpu = new Z80(bus);
        bus.Cpu = cpu;
        cpu.R.PC = 0xffff;
        cpu.R.R = (byte)before;
        cpu.R.Q = 255;
        cpu.R.WZ = 0x1234;
        cpu.TStates = (long)int.MaxValue + 10;
        var start = cpu.TStates;
        Assert.Equal(4, cpu.Step());
        Assert.Equal(start + 4, cpu.TStates);
        Assert.Equal((ushort)0, cpu.R.PC);
        Assert.Equal((byte)after, cpu.R.R);
        Assert.Equal((byte)0, cpu.R.Q);
        Assert.Equal((ushort)0x1234, cpu.R.WZ);
        Assert.Equal([ $"C:FFFF:4:{start}", $"R:FFFF:{start + 4}" ], bus.Events);
    }

    [Fact]
    public void HaltedFetchDoesNotAdvancePcButRefreshKeepsCounting()
    {
        var bus = new RecordingBus();
        var cpu = new Z80(bus) { Halted = true };
        cpu.R.PC = 0x1234;
        bus.Memory[0x1234] = 0x76;
        Assert.Equal(4, cpu.Step());
        Assert.Equal(4, cpu.Step());
        Assert.Equal((ushort)0x1234, cpu.R.PC);
        Assert.Equal((byte)2, cpu.R.R);
        Assert.Equal(8L, cpu.TStates);
    }

    [Fact]
    public void CoresDoNotShareRegistersTimingOrBus()
    {
        var a = new Z80(new RecordingBus());
        var b = new Z80(new RecordingBus());
        a.R.AFShadow = 0x1234;
        a.Step();
        Assert.Equal((ushort)0, b.R.AFShadow);
        Assert.Equal((ushort)0, b.R.PC);
        Assert.Equal(0L, b.TStates);
    }

    [Fact]
    public void ConstructorRejectsMissingBus() => Assert.Throws<ArgumentNullException>(() => new Z80(null!));

    private sealed class RecordingBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public readonly List<string> Events = [];
        public Z80? Cpu;
        public byte Read(ushort addr)
        {
            Events.Add($"R:{addr:X4}:{Cpu?.TStates ?? 0}");
            return Memory[addr];
        }
        public void Contend(ushort addr, int tstates) => Events.Add($"C:{addr:X4}:{tstates}:{Cpu?.TStates ?? 0}");
        public void Write(ushort addr, byte value) => throw new InvalidOperationException("NOP must not write.");
        public byte In(ushort port) => throw new InvalidOperationException("NOP must not read I/O.");
        public void Out(ushort port, byte value) => throw new InvalidOperationException("NOP must not write I/O.");
        public byte IntVector() => throw new InvalidOperationException("NOP must not acknowledge INT.");
    }
}
