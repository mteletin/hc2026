using Xunit;

namespace Hc2000.Z80.Tests;

public class NoopBusParityTests
{
    [Theory]
    [InlineData("prefix-memory", 32)]
    [InlineData("io-repeat", 300)]
    [InlineData("memory-repeat", 10)]
    [InlineData("halt-wrap", 5)]
    [InlineData("ei-halt", 6)]
    [InlineData("im0", 3)]
    [InlineData("im1", 3)]
    [InlineData("im2", 3)]
    [InlineData("nmi-halt", 5)]
    public void PairedModesPreserveEveryStepStateAccessAndTimestamp(string scenario, int steps)
    {
        var normalBus = new CountingBus();
        var fastBus = new PureBus();
        var normal = new Z80(normalBus);
        var fast = new Z80(fastBus);
        normalBus.Cpu = normal; fastBus.Cpu = fast;
        Configure(normal, normalBus, scenario);
        Configure(fast, fastBus, scenario);
        for (var step = 0; step < steps; step++)
        {
            if (scenario == "nmi-halt" && step == 1) { normal.Nmi(); fast.Nmi(); }
            normalBus.Accesses.Clear(); fastBus.Accesses.Clear();
            Assert.Equal(normal.Step(), fast.Step());
            Assert.Equal(normal.TStates, fast.TStates);
            Assert.Equal(normal.R, fast.R); // includes shadows, Q, WZ and R
            Assert.Equal(normal.Halted, fast.Halted);
            Assert.Equal(normal.IntLine, fast.IntLine);
            Assert.Equal(normal.EiPending, fast.EiPending);
            Assert.Equal(normal.LdAirPending, fast.LdAirPending);
            Assert.Equal(normalBus.Memory, fastBus.Memory);
            Assert.Equal(normalBus.Accesses, fastBus.Accesses);
        }
        Assert.NotEmpty(normalBus.Contentions);
        var calls = normalBus.Contentions.Count;
        normal.Reset(); fast.Reset();
        normalBus.Accesses.Clear(); fastBus.Accesses.Clear();
        Assert.Equal(normal.Step(), fast.Step());
        Assert.Equal(normal.R, fast.R);
        Assert.Equal(normalBus.Accesses, fastBus.Accesses);
        Assert.True(normalBus.Contentions.Count > calls); // Reset preserves default callbacks
    }

    [Fact]
    public void DefaultCallbacksRemainExactAndCoresAreIndependent()
    {
        var bus = new CountingBus(); var cpu = new Z80(bus); bus.Cpu = cpu;
        bus.Memory[0] = 0x36; bus.Memory[1] = 0x42; cpu.R.HL = 0x8000;
        var fastBus = new PureBus(); var fast = new Z80(fastBus); fastBus.Cpu = fast;
        Assert.Equal(10, cpu.Step());
        Assert.Equal(new[] { "0 C 0000 4", "4 C 0001 3", "7 C 8000 3" }, bus.Contentions);
        Assert.Equal(new[] { "4 R 0000 36", "7 R 0001 42", "10 W 8000 42" }, bus.Accesses);
        Assert.Equal(0L, fast.TStates);
        Assert.Equal((byte)0, fastBus.Memory[0x8000]);
        fast.Step();
        Assert.Equal(10L, cpu.TStates);
    }

    private static void Configure(Z80 cpu, AccessBus bus, string scenario)
    {
        cpu.R.HL = 0x8000; cpu.R.DE = 0x8100; cpu.R.BC = 0x0234;
        cpu.R.SP = 0x9000; cpu.R.I = 0x12;
        bus.Memory[0x8000] = 0x81; bus.Memory[0x8001] = 0x42;
        byte[] program = scenario switch
        {
            "prefix-memory" => [0x3e, 0x12, 0x77, 0x7e, 0xdd, 0xdd, 0x21, 0x10, 0x80, 0xdd, 0x36, 1, 0x7f, 0xdd, 0xcb, 1, 0x46, 0xfd, 0x21, 0xfe, 0xff, 0xfd, 0xcb, 2, 0x06, 0xcb, 0x11, 0xed, 0x44, 0x76],
            "io-repeat" => [0xed, 0xb2, 0xed, 0xbb, 0xdb, 0x34, 0xd3, 0x35, 0x76],
            "memory-repeat" => [0xed, 0xb0, 0xed, 0xb8],
            "ei-halt" => [0xfb, 0x76],
            "halt-wrap" or "nmi-halt" => [0x76],
            _ => [0x00]
        };
        cpu.R.PC = scenario == "halt-wrap" ? (ushort)0xffff : (ushort)0;
        for (var i = 0; i < program.Length; i++) bus.Memory[unchecked((ushort)(cpu.R.PC + i))] = program[i];
        if (scenario == "halt-wrap") cpu.R.R = 0xff;
        if (scenario is "im0" or "im1" or "im2" or "nmi-halt") cpu.R.IFF1 = cpu.R.IFF2 = true;
        if (scenario is "im0" or "im1" or "im2" or "ei-halt") cpu.IntLine = true;
        cpu.R.IM = scenario == "im0" ? (byte)0 : scenario == "im2" ? (byte)2 : (byte)1;
        bus.Vector = scenario == "im0" ? (byte)0xef : (byte)0xff;
        bus.Memory[0x12ff] = 0x78; bus.Memory[0x1300] = 0x56;
        bus.Memory[0x66] = 0xed; bus.Memory[0x67] = 0x45; // RETN
    }

    // Both are ordinary IBus implementations. No callback elision: retained
    // regressions compare traced contention against a genuinely empty callback.
    private abstract class AccessBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public readonly List<string> Accesses = [];
        public Z80 Cpu = null!;
        public byte Vector;
        public byte Read(ushort address) { var v = Memory[address]; Accesses.Add($"{Cpu.TStates} R {address:X4} {v:X2}"); return v; }
        public void Write(ushort address, byte value) { Accesses.Add($"{Cpu.TStates} W {address:X4} {value:X2}"); Memory[address] = value; }
        public byte In(ushort port) { Accesses.Add($"{Cpu.TStates} IN {port:X4} 0A"); return 0x0a; }
        public void Out(ushort port, byte value) => Accesses.Add($"{Cpu.TStates} OUT {port:X4} {value:X2}");
        public byte IntVector() { Accesses.Add($"{Cpu.TStates} ACK {Vector:X2}"); return Vector; }
        public abstract void Contend(ushort address, int count);
    }
    private sealed class CountingBus : AccessBus
    {
        public readonly List<string> Contentions = [];
        public override void Contend(ushort address, int count) => Contentions.Add($"{Cpu.TStates} C {address:X4} {count}");
    }
    private sealed class PureBus : AccessBus
    {
        public override void Contend(ushort address, int count) { }
    }
}
