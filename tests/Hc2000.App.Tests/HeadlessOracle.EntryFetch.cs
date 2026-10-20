using System.Collections.Concurrent;
using Hc2000.Machine.Memory;
using Hc2000.Z80;
using Xunit;
using CpuZ80 = Hc2000.Z80.Z80;

namespace Hc2000.App.Tests;

public sealed partial class HeadlessOracle
{
    private const int EntryFetchLimit = 8192;
    private readonly record struct EntryFetch(byte C, long CpuTStates, byte Opcode, MemoryLocation Mapping);

    // Test-only witness v2. E107 in this faithful harness is not an IF1 trap.
    // An ENTRY FETCH is not completed CONOUT, a glyph, or a cause of malformed output.
    private sealed class EntryFetchBus(IM1FetchBus inner, Func<byte> character,
        Func<ushort, MemoryLocation> resolve, ConcurrentQueue<EntryFetch> records, Action overflow) : IM1FetchBus
    {
        public byte Read(ushort address) => inner.Read(address);
        public void Write(ushort address, byte value) => inner.Write(address, value);
        public byte In(ushort port) => inner.In(port);
        public void Out(ushort port, byte value) => inner.Out(port, value);
        public void Contend(ushort address, int ticks) => inner.Contend(address, ticks);
        public byte IntVector() => inner.IntVector();
        public byte FetchM1(ushort address, bool instructionStart, long cpuTStates)
        {
            var observe = instructionStart && address == 0xe107;
            var c = observe ? character() : (byte)0;
            var opcode = inner.FetchM1(address, instructionStart, cpuTStates);
            if (observe)
            {
                if (records.Count >= EntryFetchLimit) overflow();
                else records.Enqueue(new(c, cpuTStates, opcode, resolve(address)));
            }
            return opcode;
        }
    }

    // Synthetic bus fixture only: production Z80 supplies the interrupt/HALT contract.
    private sealed class EntryFixtureBus : IM1FetchBus
    {
        public readonly List<string> Calls = new();
        public Func<ushort, bool, long, byte> Fetch = (_, _, _) => 0;
        public byte Read(ushort address) { Calls.Add($"read:{address:X4}"); return 0; }
        public void Write(ushort address, byte value) => Calls.Add($"write:{address:X4}:{value:X2}");
        public byte In(ushort port) { Calls.Add($"in:{port:X4}"); return 0x12; }
        public void Out(ushort port, byte value) => Calls.Add($"out:{port:X4}:{value:X2}");
        public void Contend(ushort address, int ticks) => Calls.Add($"contend:{address:X4}:{ticks}");
        public byte IntVector() { Calls.Add("vector"); return 0xff; }
        public byte FetchM1(ushort address, bool start, long ticks)
        { Calls.Add($"fetch:{address:X4}:{start}:{ticks}"); return Fetch(address, start, ticks); }
    }

    [Fact]
    public void EntryFetchWitnessSnapshotsCThenOneFetchThenResolvedMapping()
    {
        var order = new List<string>();
        byte c = 0x41;
        var location = new MemoryLocation(MemoryStore.Rom, 0x107, false);
        var records = new ConcurrentQueue<EntryFetch>();
        var inner = new EntryFixtureBus { Fetch = (address, start, ticks) =>
        {
            order.Add("fetch");
            Assert.Equal((ushort)0xe107, address); Assert.True(start); Assert.Equal(123L, ticks);
            c = 0x42; location = new(MemoryStore.Ram, 0x6107, true);
            return 0xc3;
        }};
        var bus = new EntryFetchBus(inner, () => { order.Add("C"); return c; },
            address => { Assert.Equal((ushort)0xe107, address); order.Add("mapping"); return location; }, records,
            () => Assert.Fail("No overflow expected"));
        Assert.Equal((byte)0xc3, bus.FetchM1(0xe107, true, 123));
        Assert.Equal(new[] { "C", "fetch", "mapping" }, order);
        Assert.Equal(new EntryFetch(0x41, 123, 0xc3, location), Assert.Single(records));
        Assert.Equal(new[] { "fetch:E107:True:123" }, inner.Calls); // no Read or extra Fetch
    }

    [Theory]
    [InlineData(0xe107, false)]
    [InlineData(0xe106, true)]
    public void EntryFetchWitnessFiltersAddressAndInstructionStart(int address, bool start)
    {
        var records = new ConcurrentQueue<EntryFetch>();
        var inner = new EntryFixtureBus { Fetch = (_, _, _) => 0x7f };
        var bus = new EntryFetchBus(inner, () => throw new InvalidOperationException("No C sampling"),
            _ => throw new InvalidOperationException("No mapping sampling"), records, () => Assert.Fail());
        Assert.Equal((byte)0x7f, bus.FetchM1((ushort)address, start, 9));
        Assert.Empty(records); Assert.Single(inner.Calls);
    }

    [Fact]
    public void EntryFetchWitnessPropagatesInnerExceptionWithoutRecordOrResolve()
    {
        var records = new ConcurrentQueue<EntryFetch>();
        var failure = new InvalidOperationException("synthetic fetch failure");
        var inner = new EntryFixtureBus { Fetch = (_, _, _) => throw failure };
        var bus = new EntryFetchBus(inner, () => 0x41,
            _ => throw new InvalidOperationException("No post-failure mapping"), records, () => Assert.Fail());
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => bus.FetchM1(0xe107, true, 1)));
        Assert.Empty(records); Assert.Single(inner.Calls);
    }

    [Fact]
    public void EntryFetchWitnessForwardsAllOtherBusOperationsUnchanged()
    {
        var inner = new EntryFixtureBus(); var records = new ConcurrentQueue<EntryFetch>();
        var bus = new EntryFetchBus(inner, () => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), records, () => Assert.Fail());
        Assert.Equal((byte)0, bus.Read(0xe107)); bus.Write(0xe107, 0x34);
        Assert.Equal((byte)0x12, bus.In(0x1234)); bus.Out(0x5678, 0x56);
        bus.Contend(0xe107, 7); Assert.Equal((byte)0xff, bus.IntVector());
        Assert.Equal(new[] { "read:E107", "write:E107:34", "in:1234", "out:5678:56", "contend:E107:7", "vector" }, inner.Calls);
        Assert.Empty(records);
    }

    [Fact]
    public void EntryFetchWitnessKeepsEqualLaterEntriesAndReportsBoundedOverflow()
    {
        var records = new ConcurrentQueue<EntryFetch>(); var dropped = 0;
        var inner = new EntryFixtureBus();
        var bus = new EntryFetchBus(inner, () => 0x41, _ => new(MemoryStore.Ram, 0x6107, true),
            records, () => dropped++);
        for (var i = 0; i < EntryFetchLimit + 1; i++) bus.FetchM1(0xe107, true, 42);
        Assert.Equal(EntryFetchLimit, records.Count); Assert.Equal(1, dropped);
        Assert.Equal(EntryFetchLimit + 1, inner.Calls.Count);
        Assert.True(records.All(r => r == new EntryFetch(0x41, 42, 0, new(MemoryStore.Ram, 0x6107, true))));
    }

    [Theory]
    [InlineData("IRQ")]
    [InlineData("NMI")]
    [InlineData("HALT")]
    public void EntryFetchWitnessExcludesInterruptAcknowledgeAndHaltRefresh(string boundary)
    {
        CpuZ80? cpu = null;
        var records = new ConcurrentQueue<EntryFetch>(); var inner = new EntryFixtureBus();
        var bus = new EntryFetchBus(inner, () => cpu!.R.C, _ => new(MemoryStore.Ram, 0x6107, true),
            records, () => Assert.Fail());
        cpu = new CpuZ80(bus); cpu.R.PC = 0xe107; cpu.R.SP = 0x9000;
        if (boundary == "IRQ") { cpu.R.IM = 1; cpu.R.IFF1 = true; cpu.IntLine = true; }
        else if (boundary == "NMI") cpu.Nmi();
        else cpu.Halted = true;
        cpu.Step();
        Assert.Empty(records); Assert.DoesNotContain(inner.Calls, call => call.StartsWith("fetch:", StringComparison.Ordinal));
        // Arrange a separate normal NOP entry with the same production CPU, not a second runner.
        cpu.IntLine = false; cpu.Halted = false; cpu.R.PC = 0xe107; cpu.R.C = 0x41;
        cpu.Step(); Assert.Single(records); Assert.Equal((byte)0x41, records.Single().C);
    }
}
