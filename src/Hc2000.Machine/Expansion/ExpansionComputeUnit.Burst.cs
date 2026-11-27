using Hc2000.Z80;
using Hc2000.Z80.Frontier;

namespace Hc2000.Machine.Expansion;

/// <summary>
/// Compute-unit bursts (hc2000-fast-emulation, doc design): a Running unit at an instruction boundary runs whole
/// instructions on the plain core over its own pages, unscheduled, and stops before the first instruction that would
/// touch a port or HALT, or that could end at or after the horizon. Its intermediate effects are private (the driver
/// lets only a unit with no lent or exchanged page burst), so the frontier model's result is unchanged; the one way
/// the outside reaches into a burst, a controller operation on the unit at a time the burst has run past, rolls the
/// burst back (<see cref="RollbackBurst"/>) for the driver to replay on the frontier path.
/// </summary>
public sealed partial class ExpansionComputeUnit
{
    /// <summary>The longest instruction is 23 T-states; each DD/FD prefix before it adds 4.</summary>
    private const int LongestInstruction = 23, PrefixTStates = 4, MaxPrefixes = 8;
    /// <summary>A burst stops once it has logged this many writes (the undo log's size bound).</summary>
    internal const int UndoLimit = 1 << 18;

    private sealed class OpenBurst
    {
        public required FrontierState Start;
        public required long LastMasterTick, ClockRemainder, CommittedTStates;
        public long EndTick;
        public readonly List<int> Undo = [];
    }

    private OpenBurst? openBurst;

    /// <summary>The tick of the last effect committed in the machine's own order: before an open burst, the tick the
    /// unit had when it began; otherwise <see cref="LastMasterTick"/>. A controller operation that leaves the unit's
    /// execution alone (QUERY, MAIL SEND, MAIL ACK) checks against this one.</summary>
    internal long OrderedLastMasterTick => openBurst?.LastMasterTick ?? LastMasterTick;

    /// <summary>The tick of an open burst's last effect, or null.</summary>
    internal long? BurstEndTick => openBurst?.EndTick;

    /// <summary>The burst is past: the machine's order has reached its end, nothing can roll it back any more.</summary>
    internal void CloseBurst() => openBurst = null;

    internal bool CanBurst => Lifecycle == ExpansionLifecycle.Running && openBurst is null &&
        requestedMailboxEffect == ExpansionMailboxEffect.None && (core.AtInstructionBoundary || core.FrontierUnstarted);

    private long TickAt(long tStates) =>
        checked(masterAnchor + (checked((tStates - cpuAnchor) * clockNumerator) + clockDenominator - 1) / clockDenominator);

    /// <summary>Runs a burst whose every instruction ends before <paramref name="horizonTick"/>; returns the
    /// instructions it ran (0: nothing changed). The bookkeeping after it is what CommitFrontier would have left.</summary>
    internal int RunBurst(long horizonTick, int maxInstructions)
    {
        if (!PrepareBurst()) return 0;
        ExecuteBurst(horizonTick, maxInstructions);
        return FinishBurst();
    }

    private OpenBurst? preparing;
    private long preparedTStates;
    private int preparedCount;

    /// <summary>Owner thread: the start of a burst (the core back at its instruction boundary, its state kept for a
    /// rollback, the direct page path opened). False when the unit cannot burst now.</summary>
    internal bool PrepareBurst()
    {
        if (!CanBurst) return false;
        if (core.FrontierUnstarted) core.DiscardUnstartedFrontier();
        preparing = new OpenBurst
        {
            Start = core.CaptureFrontier(), LastMasterTick = LastMasterTick, ClockRemainder = clockRemainder, CommittedTStates = CommittedTStates,
        };
        preparedTStates = core.TStates;
        preparedCount = 0;
        bus.Begin(memory, preparing.Undo);
        return true;
    }

    /// <summary>Any thread: the burst's instructions. It touches only this unit's core, bus, undo log and pages, so
    /// units' bursts may run side by side on host threads.</summary>
    internal void ExecuteBurst(long horizonTick, int maxInstructions)
    {
        var burst = preparing ?? throw new InvalidOperationException("Prepare the burst first.");
        var count = 0;
        while (count < maxInstructions && burst.Undo.Count < UndoLimit)
        {
            var prefixes = NextVisible(core.R.PC);
            if (prefixes < 0) break;
            if (TickAt(core.TStates + LongestInstruction + PrefixTStates * prefixes) >= horizonTick) break;
            core.Step();
            count++;
        }
        preparedCount = count;
    }

    /// <summary>Owner thread: closes the page path and, when the burst ran, leaves the bookkeeping CommitFrontier would
    /// have left and keeps the burst open for a rollback. Returns the instructions it ran.</summary>
    internal int FinishBurst()
    {
        var burst = preparing ?? throw new InvalidOperationException("Prepare the burst first.");
        preparing = null;
        bus.End();
        if (preparedCount == 0) return 0;
        CommittedTStates = checked(CommittedTStates + (core.TStates - preparedTStates));
        clockRemainder = checked((core.TStates - cpuAnchor) * clockNumerator) % clockDenominator;
        LastMasterTick = TickAt(core.TStates);
        burst.EndTick = LastMasterTick;
        openBurst = burst;
        return preparedCount;
    }

    /// <summary>Puts the unit back where its open burst began: memory (the undo log, newest first), core and
    /// bookkeeping. The driver then replays it on the frontier path.</summary>
    internal void RollbackBurst()
    {
        if (openBurst is not { } burst) return;
        for (var i = burst.Undo.Count - 1; i >= 0; i--) memory.Write((ushort)(burst.Undo[i] >> 8), (byte)burst.Undo[i]);
        core.RestoreFrontier(burst.Start);
        LastMasterTick = burst.LastMasterTick; clockRemainder = burst.ClockRemainder; CommittedTStates = burst.CommittedTStates;
        openBurst = null;
    }

    /// <summary>-1 when the instruction at <paramref name="pc"/> must not run in a burst (a port access, HALT, or a
    /// longer prefix chain than any program needs); otherwise the count of its DD/FD prefixes. Reads through the open
    /// burst's bus (the unit's own pages).</summary>
    private int NextVisible(ushort pc)
    {
        var prefixes = 0;
        var op = bus.Read(pc);
        while (op is 0xDD or 0xFD)
        {
            if (++prefixes > MaxPrefixes) return -1;
            pc = unchecked((ushort)(pc + 1));
            op = bus.Read(pc);
        }
        if (op is 0x76 or 0xDB or 0xD3) return -1;
        if (op == 0xED)
        {
            var e = bus.Read(unchecked((ushort)(pc + 1)));
            if (e is >= 0x40 and <= 0x7F && (e & 7) <= 1) return -1;              // IN r,(C) / OUT (C),r
            if (e is 0xA2 or 0xA3 or 0xAA or 0xAB or 0xB2 or 0xB3 or 0xBA or 0xBB) return -1; // block I/O
        }
        return prefixes;
    }

    /// <summary>The unit core's bus: closed (every access is the machine owner's, through the frontier) except during
    /// a burst, when it reads and writes the unit's own pages and logs each write's old byte.</summary>
    private sealed class UnitBus : IBus
    {
        private byte[]? ram;
        private int[] bases = new int[4];
        private List<int>? undo;
        public void Begin(ExpansionMemoryLease memory, List<int> log) { (ram, bases) = memory.Direct(); undo = log; }
        public void End() { ram = null; undo = null; }
        public byte Read(ushort addr) => ram is { } r ? r[bases[addr >> 14] + (addr & 0x3fff)] : throw Forbidden();
        public void Write(ushort addr, byte value)
        {
            if (ram is not { } r) throw Forbidden();
            var at = bases[addr >> 14] + (addr & 0x3fff);
            undo!.Add((addr << 8) | r[at]);
            r[at] = value;
        }
        public byte In(ushort port) => throw Forbidden();
        public void Out(ushort port, byte value) => throw Forbidden();
        public void Contend(ushort addr, int tstates) { if (ram is null) throw Forbidden(); }
        public byte IntVector() => throw Forbidden();
        private static InvalidOperationException Forbidden() => new("Compute effects belong to the machine owner.");
    }
}
