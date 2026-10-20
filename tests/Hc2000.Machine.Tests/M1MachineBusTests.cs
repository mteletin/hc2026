using Hc2000.Machine;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Z80;
using Xunit;
using Z80Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Tests;

/// <summary>
/// Machine-side M1 fetch through the accepted observer contract: the adapter's
/// <see cref="HcMachineBus.FetchM1"/> carries the exact CPU counter, drains the shared
/// scheduler at the mapped tick before the page lookup, and resolves the opcode through
/// the IF1 mapping; ordinary reads, HALT refresh and interrupt acknowledge never infer
/// a fetch (hc2000-z80-m1-observer-contract).
/// </summary>
public sealed class M1MachineBusTests
{
    private const ushort Program = 0x5000;
    private const int Device = 7;

    /// <summary>Records every M1 fetch and ordinary read/write while delegating to the machine bus.</summary>
    private sealed class Recording(HcMachineBus inner) : IM1FetchBus
    {
        public List<(ushort Address, bool Start, long Time, byte Value)> Fetches { get; } = [];
        public List<(ushort Address, byte Value)> Reads { get; } = [];
        public List<(ushort Address, byte Value)> Writes { get; } = [];
        public int Contends;
        public bool DuringFetch;

        public byte Read(ushort address)
        {
            var value = inner.Read(address);
            Reads.Add((address, value));
            return value;
        }

        public void Write(ushort address, byte value)
        {
            inner.Write(address, value);
            Writes.Add((address, value));
        }

        public byte In(ushort port) => inner.In(port);

        public void Out(ushort port, byte value) => inner.Out(port, value);

        public void Contend(ushort address, int tstates)
        {
            inner.Contend(address, tstates);
            Contends++;
        }

        public byte IntVector() => inner.IntVector();

        public byte FetchM1(ushort address, bool instructionStart, long cpuTStates)
        {
            DuringFetch = true;
            try
            {
                var value = inner.FetchM1(address, instructionStart, cpuTStates);
                Fetches.Add((address, instructionStart, cpuTStates, value));
                return value;
            }
            finally
            {
                DuringFetch = false;
            }
        }
    }

    /// <summary>Plain bus without the M1 capability, for state parity.</summary>
    private sealed class PlainBus(HcMemory memory) : IBus
    {
        public byte Read(ushort addr) => memory.Read(addr);
        public void Write(ushort addr, byte value) => memory.Write(addr, value);
        public byte In(ushort port) => 0xff;
        public void Out(ushort port, byte value) { }
        public void Contend(ushort addr, int tstates) { }
        public byte IntVector() => 0xff;
    }

    private static (Z80Cpu Cpu, Recording Bus, HcMemory Memory, DeviceScheduler Scheduler) Machine() =>
        Machine(MemoryTests.Create(new()));

    private static (Z80Cpu Cpu, Recording Bus, HcMemory Memory, DeviceScheduler Scheduler) Machine(HcMemory memory)
    {
        var scheduler = new DeviceScheduler();
        var time = new CpuDeviceTimeAdapter(scheduler, new CpuTimeMapper());
        var bus = new Recording(new HcMachineBus(memory, new PortDecoder([]), time, scheduler));
        var cpu = new Z80Cpu(bus);
        return (cpu, bus, memory, scheduler);
    }

    private static void Load(HcMemory memory, Z80Cpu cpu, params byte[] program)
    {
        program.CopyTo(memory.Ram.Span[Program..]);
        cpu.R.PC = Program;
    }

    [Fact]
    public void OpcodeFetchIsInstructionStartAtTheExactCounterAndTheByteIsExecuted()
    {
        var (cpu, bus, memory, scheduler) = Machine();
        Load(memory, cpu, 0x3e, 0x42); // LD A,42h

        Assert.Equal(7, cpu.Step());

        Assert.Single(bus.Fetches);
        Assert.Equal((Program, true, 4L, (byte)0x3e), bus.Fetches[0]);
        Assert.Equal(new (ushort, byte)[] { (Program + 1, 0x42) }, bus.Reads); // operand stays an ordinary read
        Assert.Empty(bus.Writes);
        Assert.Equal(0x42, cpu.R.A);
        Assert.Equal(Program + 2, cpu.R.PC);
        Assert.Equal(20L, scheduler.Now); // 5:1 mapper, fetch at counter 4
        Assert.Equal(0xff, bus.IntVector());
    }

    [Fact]
    public void RepeatedPrefixesAreFetchesButOnlyTheFirstIsInstructionStart()
    {
        var (cpu, bus, memory, _) = Machine();
        Load(memory, cpu, 0xdd, 0xfd, 0xdd, 0x00); // DD FD DD NOP

        Assert.Equal(16, cpu.Step());

        Assert.Equal(new (ushort, bool, long, byte)[]
        {
            (Program, true, 4L, (byte)0xdd),
            (Program + 1, false, 8L, (byte)0xfd),
            (Program + 2, false, 12L, (byte)0xdd),
            (Program + 3, false, 16L, (byte)0x00),
        }, bus.Fetches);
        Assert.Empty(bus.Reads);
    }

    [Fact]
    public void DdCbFinalOpcodeByteIsAnOrdinaryRead()
    {
        var (cpu, bus, memory, _) = Machine();
        Load(memory, cpu, 0xdd, 0xcb, 0x00, 0x06); // DD CB d 06: r=06 is the (HL) slot -> RLC (IX+d)
        cpu.R.IX = Program;

        Assert.Equal(23, cpu.Step());

        Assert.Equal(new (ushort, bool, long, byte)[]
        {
            (Program, true, 4L, (byte)0xdd),
            (Program + 1, false, 8L, (byte)0xcb),
        }, bus.Fetches);
        Assert.Equal(new (ushort, byte)[]
        {
            (Program + 2, (byte)0x00),
            (Program + 3, (byte)0x06),
            (Program, (byte)0xdd), // the read-modify-write read
        }, bus.Reads);
        Assert.Equal(0xbb, memory.Ram.Span[Program]); // 0xdd RLC -> 0xbb: the RMW went through the machine memory, register form untouched
        Assert.Equal(0, cpu.R.B);
    }

    [Fact]
    public void HaltRefreshRemainsAnOrdinaryReadAndNmiResumesWithInstructionStart()
    {
        var (cpu, bus, memory, _) = Machine();
        Load(memory, cpu, 0x76); // HALT

        Assert.Equal(4, cpu.Step());
        Assert.Single(bus.Fetches);
        Assert.Equal((Program, true, 4L, (byte)0x76), bus.Fetches[0]);
        Assert.True(cpu.Halted);

        Assert.Equal(4, cpu.Step()); // refresh cycle: ordinary read, no PC advance
        Assert.Equal(4, cpu.Step()); // second refresh
        Assert.Single(bus.Fetches);
        Assert.Equal(new ushort[] { Program + 1, Program + 1 }, bus.Reads.Select(r => r.Address).ToArray());
        Assert.Equal((byte)3, cpu.R.R);

        cpu.Nmi();
        Assert.Equal(11, cpu.Step());
        Assert.Single(bus.Fetches); // the acknowledge itself never fetches
        Assert.Equal(0x66, cpu.R.PC); // NMI vector; the pushed return address is checked below
        Assert.False(cpu.Halted);

        cpu.Step();
        Assert.Equal(2, bus.Fetches.Count);
        Assert.Equal((0x66, true, 27L, (byte)0x86), bus.Fetches[1]);
        Assert.Equal(0xfffd, cpu.R.SP);
        Assert.Equal(0x50, memory.Read(0xfffe));
        Assert.Equal(0x01, memory.Read(0xfffd));
    }

    [Fact]
    public void Im1InterruptAcknowledgeIsExcludedAndTheNextInstructionStarts()
    {
        var (cpu, bus, memory, _) = Machine();
        Load(memory, cpu, 0x00);
        cpu.R.IFF1 = true;
        cpu.IntLine = true;

        Assert.Equal(13, cpu.Step());
        Assert.Empty(bus.Fetches);
        Assert.Equal(0x38, cpu.R.PC);
        Assert.False(cpu.R.IFF1);
        Assert.Equal(0xfffd, cpu.R.SP);
        Assert.Equal(0x50, memory.Read(0xfffe));
        Assert.Equal(0x00, memory.Read(0xfffd));

        cpu.IntLine = false;
        Assert.Equal(4, cpu.Step());
        Assert.Single(bus.Fetches);
        Assert.Equal((0x38, true, 17L, (byte)0x58), bus.Fetches[0]);
    }

    [Fact]
    public void Im2VectorReadsAreOrdinaryReads()
    {
        var (cpu, bus, memory, _) = Machine();
        Load(memory, cpu, 0x00);
        cpu.R.IM = 2;
        cpu.R.I = 0x50; // vector address = (I << 8) | IntVector() = 0x50ff
        cpu.R.IFF1 = true;
        cpu.IntLine = true;
        memory.Ram.Span[0x50ff] = 0x10;
        memory.Ram.Span[0x5100] = 0x20;

        Assert.Equal(19, cpu.Step());
        Assert.Empty(bus.Fetches);
        Assert.Equal(0x2010, cpu.R.PC);
        Assert.Equal(new (ushort, byte)[] { (0x50ff, 0x10), (0x5100, 0x20) }, bus.Reads);
    }

    [Fact]
    public void TrapAddressesPageOnSecondFetchAndOffAtPageOut()
    {
        var (cpu, bus, memory, _) = Machine(MemoryWithPageOutByte(0x00));

        // Page in: 0x0008 executes the shadow-rom byte (0xa8 = XOR B), not the basic-rom byte (0x28 = INC C).
        cpu.R.PC = 0x0008;
        cpu.R.B = 0x05;
        Assert.False(memory.Latches.Shadow);
        Assert.Equal(4, cpu.Step());
        Assert.Single(bus.Fetches);
        Assert.Equal((0x0008, true, 4L, (byte)0xa8), bus.Fetches[0]);
        Assert.True(memory.Latches.Shadow);
        Assert.Equal(0x0009, cpu.R.PC);
        Assert.Equal(0xfa, cpu.R.A); // 0xff ^ 0x05: the shadow byte ran; the basic 0x28 would leave A = 0xff

        // Second page in: 0x1708 executes the shadow-rom byte (0x20 = JR NZ,e in this core), not the basic one (0xa0 = AND B).
        cpu.R.PC = 0x1708;
        Assert.Equal(12, cpu.Step()); // taken: 4 M1 + 3 operand + 5 branch delay
        Assert.Equal(2, bus.Fetches.Count);
        Assert.Equal((0x1708, true, 8L, (byte)0x20), bus.Fetches[1]);
        Assert.True(memory.Latches.Shadow);
        Assert.Equal(0x172b, cpu.R.PC); // 0x170a + 0x21: the JR base is the address after the operand, which came from the shadow rom

        // Page out: 0x0700 returns the IF1 byte before clearing Shadow; following accesses see BASIC ROM.
        cpu.R.PC = 0x0700;
        Assert.Equal(4, cpu.Step());
        Assert.Equal(3, bus.Fetches.Count);
        Assert.Equal((0x0700, true, 20L, (byte)0x00), bus.Fetches[2]);
        Assert.False(memory.Latches.Shadow);
        Assert.Equal(0x0701, cpu.R.PC);
        Assert.Equal(0x05, cpu.R.B); // IF1 NOP ran; BASIC LD B,H did not.

        // Ordinary reads never page.
        Assert.Equal(0x28, memory.Read(0x0008));
        Assert.Equal(0x44, memory.Read(0x0700));
        Assert.False(memory.Latches.Shadow);
    }

    [Fact]
    public void PageOutRetExecutesShadowOpcodeAndPopsReturnAddress()
    {
        var memory = MemoryWithPageOutByte(0xc9);
        var (cpu, bus, _, _) = Machine(memory);
        cpu.R.PC = 0x0700;
        cpu.R.SP = 0x8000;
        memory.Ram.Span[0x8000] = 0x55;
        memory.Ram.Span[0x8001] = 0x16;
        memory.SetShadow(true);

        Assert.Equal(10, cpu.Step());

        Assert.Equal((0x0700, true, 4L, (byte)0xc9), Assert.Single(bus.Fetches));
        Assert.False(memory.Latches.Shadow);
        Assert.Equal(0x1655, cpu.R.PC);
        Assert.Equal(0x8002, cpu.R.SP);
        Assert.Equal(0x44, memory.Read(0x0700));
    }

    private static HcMemory MemoryWithPageOutByte(byte pageOutByte)
    {
        var seeded = MemoryTests.Create(new());
        var rom = seeded.Rom.ToArray();
        var if1 = seeded.ShadowRom.ToArray();
        if1[0x0700] = pageOutByte;
        return new HcMemory(new(), rom[..0x4000], rom[0x4000..0x8000], if1);
    }

    [Fact]
    public void TheSchedulerDrainsDueEventsInsideFetchM1BeforeThePageLookup()
    {
        var (cpu, bus, memory, scheduler) = Machine();
        var events = new List<(long Tick, bool DuringFetch)>();
        scheduler.RegisterDevice(Device, e => events.Add((scheduler.Now, bus.DuringFetch)));
        scheduler.Schedule(new DeviceEvent(20, EventPhase.ExternalInput, Device, 1));
        scheduler.Schedule(new DeviceEvent(40, EventPhase.ExternalInput, Device, 1));
        Load(memory, cpu, 0x00);

        cpu.Step(); // M1 fetch at counter 4 maps to tick 20

        Assert.Single(events);
        Assert.Equal((20L, true), events[0]); // delivered while the fetch was in flight
        Assert.Equal(20L, scheduler.Now);
        Assert.Single(scheduler.PendingEvents);

        cpu.Step(); // next M1 fetch at counter 8 maps to tick 40

        Assert.Equal(2, events.Count);
        Assert.Equal((40L, true), events[1]);
        Assert.Equal(40L, scheduler.Now);
        Assert.Empty(scheduler.PendingEvents);
    }

    [Fact]
    public void MachineStateMatchesAPlainBusRunningTheSameProgram()
    {
        var (cpu, bus, memory, _) = Machine();
        Load(memory, cpu, 0x3e, 0x42, 0x01, 0x34, 0x12, 0xdd, 0xcb, 0x00, 0x06, 0x00);
        for (var i = 0; i < 4; i++) cpu.Step();

        var plainMemory = MemoryTests.Create(new());
        var plain = new Z80Cpu(new PlainBus(plainMemory));
        Load(plainMemory, plain, 0x3e, 0x42, 0x01, 0x34, 0x12, 0xdd, 0xcb, 0x00, 0x06, 0x00);
        for (var i = 0; i < 4; i++) plain.Step();

        Assert.Equal(plain.R, cpu.R);
        Assert.Equal(plain.TStates, cpu.TStates);
        Assert.Equal(new ushort[] { Program, Program + 2, Program + 5, Program + 6, Program + 9 },
            bus.Fetches.Select(f => f.Address).ToArray());
        Assert.Equal(new[] { true, true, true, false, true }, bus.Fetches.Select(f => f.Start).ToArray());
        Assert.Equal(new[] { 1, 3, 4, 7, 8, -Program }, bus.Reads.Select(r => (int)r.Address - Program).ToArray()); // last: the DDCB read-modify-write at IX+d = 0x0000
    }
}
