using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Z80.Tests;

[Collection("Z80 timing gates")]
public class ZexTests(ITestOutputHelper output)
{
    [Z80AssetTheory]
    [Trait("speed", "slow")]
    [InlineData("zexdoc.com", 600)]
    [InlineData("zexall.com", 600)]
    public void EveryExerciserGroupIsOk(string file, int maxSeconds)
    {
        var root = Z80AssetFactAttribute.AssetRoot()!;
        var result = Run(File.ReadAllBytes(Path.Combine(root, "zex", file)), TimeSpan.FromSeconds(maxSeconds));
        output.WriteLine($"{file}: mode=default callbacks, elapsed {result.Elapsed.TotalSeconds:F3}s, TStates={result.TStates}, steps={result.Steps}\n{result.Text}");
        Assert.True(result.Complete, $"{file} did not terminate within {maxSeconds}s; output:\n{result.Text}");
        Assert.DoesNotContain("ERROR", result.Text);
        Assert.Contains("Tests complete", result.Text);
        var groups = GroupLines(result.Text);
        Assert.Equal(67, groups.Length); // each pinned source has exactly 67 tmsg descriptors
        Assert.All(groups, group => Assert.EndsWith("OK", group.TrimEnd()));
        // ZEXDOC speed gate: machine-relative throughput, not an absolute wall-clock bound.
        // The spec's requirement (01-z80.md) is >=50x the 3.5 MHz real CPU, i.e. >175 MHz
        // equivalent, measured the way PerformanceTests measures it (emulated T-states per
        // host second). The earlier '< 60 s' assert was host-absolute: it bound wall-clock
        // time to emulate this fixed 5,764,169,610-step binary, so it depended on the host
        // CPU rather than the core. On a correct core (exactness is gated separately: this
        // run reports TStates=46734977142 / steps=5764169610) a slower desktop simply takes
        // more host seconds - it does not change what the core computes. Gating on the run's
        // MHz-equivalent (TStates / host-seconds) keeps the same calibration basis as the spec
        // and PerformanceTests while making the gate independent of host clock speed.
        if (file == "zexdoc.com")
        {
            var mhz = result.TStates / result.Elapsed.TotalSeconds / 1_000_000;
            Assert.True(mhz > 175, $"ZEXDOC throughput gate: {mhz:F3} MHz equivalent is not >175 MHz (50x3.5 MHz). Elapsed {result.Elapsed.TotalSeconds:F3}s, TStates={result.TStates}.");
        }
    }

    private static string[] GroupLines(string text)
        => text.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            // Padding shrinks to two dots for the longest pinned group titles.
            .Where(line => line.Contains("..", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void GroupParserIncludesLongTitlesWithOnlyTwoPaddingDots()
    {
        var groups = GroupLines("Z80 instruction exerciser\n\raluop a,<b,c,d,e,h,l,(hl),a>..  OK\n\rshf/rot <b,c,d,e,h,l,(hl),a>..  OK\n\rneg...........................  OK\n\rTests complete");
        Assert.Equal(3, groups.Length);
        Assert.All(groups, group => Assert.EndsWith("OK", group.TrimEnd()));
    }

    private static RunResult Run(byte[] program, TimeSpan maxTime)
    {
        var bus = new BdosBus();
        program.CopyTo(bus.Memory, 0x100);
        bus.Memory[5] = 0xc9; // trap CALL 5, then ordinary RET restores the caller
        var cpu = new Z80(bus);
        cpu.R.PC = 0x100; cpu.R.SP = 0xf000;
        var text = new StringBuilder();
        long steps = 0;
        var watch = Stopwatch.StartNew();
        while (cpu.R.PC != 0)
        {
            if ((steps & 0xffff) == 0 && watch.Elapsed > maxTime)
                return new RunResult(false, text.ToString(), watch.Elapsed, cpu.TStates, steps);
            if (cpu.R.PC == 5)
            {
                switch (cpu.R.C)
                {
                    case 2: text.Append((char)cpu.R.E); break;
                    case 9:
                        var address = cpu.R.DE;
                        var terminated = false;
                        for (var count = 0; count < 65536; count++)
                        {
                            var value = bus.Memory[address++];
                            if (value == '$') { terminated = true; break; }
                            text.Append((char)value);
                        }
                        if (!terminated) throw new InvalidOperationException("Unterminated BDOS function 9 string.");
                        break;
                    default: throw new InvalidOperationException($"Unexpected BDOS function {cpu.R.C}.");
                }
            }
            cpu.Step();
            steps++;
        }
        return new RunResult(true, text.ToString(), watch.Elapsed, cpu.TStates, steps);
    }

    private sealed record RunResult(bool Complete, string Text, TimeSpan Elapsed, long TStates, long Steps);
    private sealed class BdosBus : IBus, IArrayBus
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
