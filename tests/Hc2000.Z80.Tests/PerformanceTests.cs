using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Z80.Tests;

[Collection("Z80 timing gates")]
public class PerformanceTests(ITestOutputHelper output)
{
#if DEBUG
    [Theory(Skip = "Performance gates require Release.")]
#else
    [Theory]
#endif
    [Trait("speed", "slow")]
    [InlineData("NOP")]
    [InlineData("mixed")]
    [InlineData("RRCA")]
    public void ReleaseLoopIsAllocationFreeAndAbove175Mhz(string workload)
    {
        var mixed = workload == "mixed";
        var bus = new PlainBus();
        if (workload == "RRCA") Array.Fill(bus.Memory, (byte)0x0f);
        if (mixed) new byte[] { 0x7e, 0x80, 0xab, 0x04, 0xc3, 0x00, 0x00 }.CopyTo(bus.Memory, 0);
        var cpu = new Z80(bus); cpu.R.HL = 0x8000;
        // A single short batch can end before the tiered-JIT background delay.
        // Warm each workload for the same minimum duration outside measurement.
        // Each batch is a multiple of five, preserving the mixed loop's phase.
        var warmup = Stopwatch.StartNew();
        do
        {
            for (var i = 0; i < 1_000_000; i++) cpu.Step();
        } while (warmup.Elapsed < TimeSpan.FromSeconds(1));
        var before = cpu.TStates;
        var watch = new Stopwatch();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        for (var i = 0; i < 20_000_000; i++) cpu.Step();
        watch.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var cycles = cpu.TStates - before;
        var mhz = cycles / watch.Elapsed.TotalSeconds / 1_000_000;
        output.WriteLine($"mode=default callbacks, {(mixed ? "LD A,(HL); ADD A,B; XOR E; INC B; JP 0" : workload + " 64KiB ring")}: steps=20000000, TStates={cycles}, seconds={watch.Elapsed.TotalSeconds:F6}, MHz={mhz:F3}, per-thread bytes={bytes}");
        Assert.Equal(mixed ? 116_000_000L : 80_000_000L, cycles);
        Assert.Equal(0L, bytes);
        Assert.True(mhz > 175, $"{mhz:F3} MHz equivalent is not >175 MHz (50x3.5 MHz).");
    }

    private sealed class PlainBus : IBus, IArrayBus
    {
        public byte[] Memory { get; } = new byte[65536];
        public bool Contends => false;
        public byte Read(ushort addr) => Memory[addr];
        public void Write(ushort addr, byte value) => Memory[addr] = value;
        public byte In(ushort port) => 0xff;
        public void Out(ushort port, byte value) { }
        public void Contend(ushort addr, int tstates) { }
        public byte IntVector() => 0xff;
    }

}
