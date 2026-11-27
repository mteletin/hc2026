using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Z80;

namespace Hc2000.Machine.Expansion;

/// <summary>
/// Relaxed main quanta (hc2000-fast-emulation). The owner, 2026-11-26: "the original needs to keep timings correct
/// but when we go off spec we don't need that anymore... starting with 140MHz and upwards we can drop perfect HC2000 and
/// Z80 timings if that helps". At those clocks the main Z80 is not driven one scheduled bus effect at a time: between two
/// events of anything else it runs whole plain instructions on a bus of its own, and stops before the first instruction
/// that touches a port, HALTs, sits on an IF1 trap address, or could end at or after the next event (or the frame's end).
/// Every other device (units, cards, DMA, the frame clock) therefore still meets the main's memory and ports in the
/// machine's order; the instructions themselves are the same Z80. What is dropped: the video, which samples lazily at the
/// next event, may show a screen write of the quantum a little early, and there is no contention.
/// </summary>
public sealed partial class ExpansionFrontierDriver
{
    /// <summary>From this main clock up the main runs relaxed quanta (the owner's 140 MHz line).</summary>
    public const long RelaxedMainHz = 140_000_000;
    private const int LongestMainInstruction = 23, MainPrefixTStates = 4, MainMaxPrefixes = 8;

    /// <summary>The most instructions one quantum runs.</summary>
    public int QuantumInstructions { get; set; } = 1 << 20;

    /// <summary>Whether the main clock in force is off spec enough to run quanta: at least <see cref="RelaxedMainHz"/>
    /// T-states a second, exactly (master Hz over master ticks per T-state).</summary>
    public bool MainRelaxed => FrameTiming.MasterHz * mapper.Denominator >= RelaxedMainHz * mapper.Numerator;

    private MainQuantumBus? quantumBus;
    // A quantum that stopped for the next event is not tried again before that event has happened.
    private LogicalTime quantumBlockedUntil;

    /// <summary>Runs one quantum of the main when it can; true when it ran an instruction, so the frame loop takes its
    /// next event again.</summary>
    private bool RunMainQuantum()
    {
        if (tokens[0] == 0 || OnM1Fetch is not null || scheduler.LogicalNow < quantumBlockedUntil || !MainRelaxed) return false;
        // Interrupt acceptance, NMI and HALT stay on the frontier path (SkipMainHalt covers a long HALT); the lines
        // cannot change inside a quantum, which ends before the next event.
        if (main.Halted || main.NmiPendingState || main.IntLine || clock.IntAsserted || controller.RowInterrupt?.LineAsserted == true) return false;
        if (!(main.AtInstructionBoundary || main.FrontierUnstarted)) return false;
        if (Mmu is not null && Mmu.Faulted(ExpansionMmu.MainUnit)) return false;
        var bus = quantumBus ??= new MainQuantumBus(this);
        var guarded = bus.Begin();
        var count = 0;
        try
        {
            if (MainNextVisible(bus, main.R.PC) < 0) return false;
            Cancel(0);
            var limit = LogicalTime.Tick(burstHorizon);
            if (scheduler.TryPeekNextTime(out var next) && next < limit) limit = next;
            // The last T-state the quantum may reach: its exact time stays strictly before the limit.
            var start = main.TStates;
            long low = 0, high = 64;
            while (mapper.ExactTimeAt(checked(start + high)) < limit) { low = high; high = checked(high * 2); }
            while (high - low > 1)
            {
                var mid = low + (high - low) / 2;
                if (mapper.ExactTimeAt(checked(start + mid)) < limit) low = mid; else high = mid;
            }
            var stop = start + low;
            if (start + LongestMainInstruction < stop)
            {
                if (main.FrontierUnstarted) main.DiscardUnstartedFrontier();
                var previous = main.SwapBus(bus);
                try
                {
                    while (count < QuantumInstructions)
                    {
                        var prefixes = MainNextVisible(bus, main.R.PC);
                        if (prefixes < 0) break;
                        if (main.TStates + LongestMainInstruction + MainPrefixTStates * prefixes > stop) { quantumBlockedUntil = limit; break; }
                        if (!guarded) main.Step();
                        else
                        {
                            // A slot of the main's managed map refuses some access: an instruction the bus refuses
                            // part-way is put back whole and left to the frontier path, which records the denial exactly.
                            var checkpoint = main.Checkpoint();
                            bus.Instruction();
                            try { main.Step(); }
                            catch (QuantumRefused) { bus.UndoInstruction(); main.Rollback(checkpoint); break; }
                        }
                        count++;
                    }
                }
                finally { main.SwapBus(previous); }
            }
            else quantumBlockedUntil = limit;
            if (count > 0) LandMainAtItsTime();
            else Synchronize();
        }
        finally { bus.End(); }
        return count > 0;
    }

    /// <summary>-1 when the main's instruction at <paramref name="pc"/> must stay on the frontier path (a port access,
    /// HALT, an IF1 trap address, code the managed map does not let it execute, a long prefix chain); otherwise the count
    /// of its DD/FD prefixes.</summary>
    private int MainNextVisible(MainQuantumBus bus, ushort pc)
    {
        var traps = memory.Profile.If1Traps;
        var prefixes = 0;
        while (true)
        {
            if (pc == traps.PageInFirst || pc == traps.PageInSecond || pc == traps.PageOut) return -1;
            if (!bus.TryCode(pc, out var op)) return -1;
            if (op is not (0xDD or 0xFD))
            {
                if (op is 0x76 or 0xDB or 0xD3) return -1;
                if (op is 0xED or 0xCB)
                {
                    var after = unchecked((ushort)(pc + 1));
                    if (after == traps.PageInFirst || after == traps.PageInSecond || after == traps.PageOut) return -1;
                    if (op == 0xED)
                    {
                        if (!bus.TryCode(after, out var e)) return -1;
                        if (e is >= 0x40 and <= 0x7F && (e & 7) <= 1) return -1;                              // IN r,(C) / OUT (C),r
                        if (e is 0xA2 or 0xA3 or 0xAA or 0xAB or 0xB2 or 0xB3 or 0xBA or 0xBB) return -1;         // block I/O
                    }
                }
                return prefixes;
            }
            if (++prefixes > MainMaxPrefixes) return -1;
            pc = unchecked((ushort)(pc + 1));
        }
    }

    /// <summary>The bus refused an access inside a guarded quantum instruction.</summary>
    private sealed class QuantumRefused : Exception;

    /// <summary>The main's bus during a quantum: its memory exactly as the frontier path reaches it (the faithful memory
    /// model, or the managed map's slots as they stand at the quantum's start), no ports, no interrupt vector.</summary>
    private sealed class MainQuantumBus(ExpansionFrontierDriver driver) : IM1FetchBus, IUncontendedBus
    {
        private readonly HcMemory memory = driver.memory;
        // Per 16 KB slot of the managed map: kind, target and the accesses it lets the main make.
        private readonly ExpansionSlotKind[] kinds = new ExpansionSlotKind[ExpansionMmu.SlotCount];
        private readonly ushort[] targets = new ushort[ExpansionMmu.SlotCount];
        private readonly ExpansionSlotCaps[] allowed = new ExpansionSlotCaps[ExpansionMmu.SlotCount];
        private ExpansionMmu? mmu;
        private bool guarded;
        // The faithful memory model's 8 KB pages as the latches stand at the quantum's start: a page that maps linearly
        // onto one store is read and written straight from its array (null: through the memory model).
        private readonly byte[]?[] pageArrays = new byte[8][];
        private readonly int[] pageOffsets = new int[8], pageMasks = new int[8];
        private readonly bool[] pageWritable = new bool[8];
        private If1TrapProfile traps = null!;
        private readonly List<(ushort Address, byte Old)> undo = [];

        /// <summary>Takes the managed map as it stands; true when some slot refuses an access, so each instruction is
        /// guarded.</summary>
        public bool Begin()
        {
            mmu = driver.Mmu;
            guarded = false;
            traps = memory.Profile.If1Traps;
            for (var page = 0; page < 8; page++)
            {
                pageArrays[page] = memory.TryDirectPage(page, out var array, out var offset, out var mask, out var writable) ? array : null;
                pageOffsets[page] = offset; pageMasks[page] = mask; pageWritable[page] = writable;
            }
            if (mmu is null) return false;
            for (var slot = 0; slot < ExpansionMmu.SlotCount; slot++)
            {
                var address = (ushort)(slot * ExpansionMmu.SlotBytes);
                var caps = (ExpansionSlotCaps)0;
                var kind = ExpansionSlotKind.Absent; ushort target = 0;
                foreach (var access in (ReadOnlySpan<ExpansionSlotCaps>)[ExpansionSlotCaps.Read, ExpansionSlotCaps.Write, ExpansionSlotCaps.Execute])
                {
                    if (!mmu.TryPeek(ExpansionMmu.MainUnit, address, access, out var r)) continue;
                    if (r.Kind == ExpansionSlotKind.Arena && !driver.Arena!.MainCanMap((ushort)r.Target)) continue;
                    if (r.Kind == ExpansionSlotKind.FirmwareRom && access == ExpansionSlotCaps.Write) continue;
                    caps |= access; kind = r.Kind; target = (ushort)r.Target;
                }
                kinds[slot] = kind; targets[slot] = target; allowed[slot] = caps;
                if (caps != (ExpansionSlotCaps.Read | ExpansionSlotCaps.Write | ExpansionSlotCaps.Execute)) guarded = true;
            }
            return guarded;
        }

        public void End() { mmu = null; undo.Clear(); Array.Clear(pageArrays); }

        // The faithful memory model, through the page table where it can.
        private byte LegacyRead(ushort address)
        {
            var page = address >> 13;
            return pageArrays[page] is { } array ? array[pageOffsets[page] + (address & pageMasks[page])] : memory.Read(address);
        }

        private byte LegacyReadM1(ushort address) =>
            address == traps.PageInFirst || address == traps.PageInSecond || address == traps.PageOut ? memory.ReadM1(address) : LegacyRead(address);

        private void LegacyWrite(ushort address, byte value)
        {
            var page = address >> 13;
            if (pageArrays[page] is { } array)
            {
                if (pageWritable[page]) array[pageOffsets[page] + (address & pageMasks[page])] = value;
            }
            else memory.Write(address, value);
        }
        public void Instruction() => undo.Clear();
        public void UndoInstruction()
        {
            for (var i = undo.Count - 1; i >= 0; i--) Store(undo[i].Address, undo[i].Old);
            undo.Clear();
        }

        /// <summary>The code byte at <paramref name="address"/> without side effects (no IF1 paging), when the main may
        /// execute it.</summary>
        public bool TryCode(ushort address, out byte value)
        {
            if (mmu is null) { value = LegacyRead(address); return true; }
            var slot = address >> 14;
            if ((allowed[slot] & ExpansionSlotCaps.Execute) == 0) { value = 0; return false; }
            value = Load(address, m1: false);
            return true;
        }

        public byte Read(ushort address) => Access(address, ExpansionSlotCaps.Read, m1: false);
        public byte FetchM1(ushort address, bool instructionStart, long cpuTStates) => Access(address, ExpansionSlotCaps.Execute, m1: true);

        public void Write(ushort address, byte value)
        {
            if (mmu is null) { LegacyWrite(address, value); return; }
            if ((allowed[address >> 14] & ExpansionSlotCaps.Write) == 0) throw new QuantumRefused();
            if (guarded) undo.Add((address, Load(address, m1: false)));
            Store(address, value);
        }

        private byte Access(ushort address, ExpansionSlotCaps access, bool m1)
        {
            if (mmu is null) return m1 ? LegacyReadM1(address) : LegacyRead(address);
            if ((allowed[address >> 14] & access) == 0) throw new QuantumRefused();
            return Load(address, m1);
        }

        private byte Load(ushort address, bool m1)
        {
            var slot = address >> 14; var intra = (ushort)(address & (ExpansionMmu.SlotBytes - 1));
            return kinds[slot] switch
            {
                ExpansionSlotKind.Legacy => m1 ? LegacyReadM1(address) : LegacyRead(address),
                ExpansionSlotKind.BaseRam => memory.BaseRamRead(checked((ushort)(targets[slot] * ExpansionMmu.SlotBytes + intra))),
                ExpansionSlotKind.Arena => memory.ExpansionArenaSpan(targets[slot], intra, 1)[0],
                ExpansionSlotKind.FirmwareRom => memory.Rom.Span[targets[slot] * ExpansionMmu.SlotBytes + intra],
                _ => throw new QuantumRefused(),
            };
        }

        private void Store(ushort address, byte value)
        {
            var slot = address >> 14; var intra = (ushort)(address & (ExpansionMmu.SlotBytes - 1));
            switch (kinds[slot])
            {
                case ExpansionSlotKind.Legacy: LegacyWrite(address, value); break;
                case ExpansionSlotKind.BaseRam: memory.BaseRamWrite(checked((ushort)(targets[slot] * ExpansionMmu.SlotBytes + intra)), value); break;
                case ExpansionSlotKind.Arena: memory.ExpansionArenaCopy(targets[slot], intra, [value]); break;
                default: throw new QuantumRefused();
            }
        }

        // Ports and the interrupt vector never belong to a quantum (MainNextVisible stops before them; no line is raised).
        public byte In(ushort port) => throw new InvalidOperationException("A main quantum reached a port read.");
        public void Out(ushort port, byte value) => throw new InvalidOperationException("A main quantum reached a port write.");
        public byte IntVector() => throw new InvalidOperationException("A main quantum accepted an interrupt.");
        public void Contend(ushort address, int tstates) { }
    }
}
