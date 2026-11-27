using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Z80.Frontier;

namespace Hc2000.Machine.Expansion;

/// <summary>The machine owner dispatches one queued causal effect per runnable CPU, never Step.
/// Scheduler callbacks only commit effects/reconcile future events; they never AdvanceTo.</summary>
public sealed partial class ExpansionFrontierDriver
{
    public const int FirstCpuDeviceId = 1000;
    /// <summary>Composed units: 16 compute + the driver count this composition enabled; device ids FirstCpuDeviceId..FirstCpuDeviceId + UnitCount.</summary>
    public int UnitCount => controller.UnitCount;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly DeviceScheduler scheduler;
    private readonly FrameClock clock;
    private CpuTimeMapper mapper;
    private readonly ExpansionController controller;
    private readonly HcMemory memory;
    private readonly PortDecoder ports;
    private readonly Z80.Z80 main;
    private readonly long[] tokens = new long[1 + ExpansionArena.ComputeCount + ExpansionArena.DriverCount];
    // The admitted due of each CPU frontier: integer master ticks for the side units (their own
    // faithful clock), the exact logical time for the main CPU (any admitted rate).
    private readonly LogicalTime[] due = new LogicalTime[1 + ExpansionArena.ComputeCount + ExpansionArena.DriverCount];
    private readonly ulong[] epochs = new ulong[1 + ExpansionArena.ComputeCount + ExpansionArena.DriverCount];
    private readonly CpuPortView mainPorts;
    private readonly CpuPortView?[] unitPorts = new CpuPortView?[1 + ExpansionArena.ComputeCount + ExpansionArena.DriverCount];
    private readonly ulong[] viewEpochs = new ulong[1 + ExpansionArena.ComputeCount + ExpansionArena.DriverCount];
    private readonly ulong[] viewOwnerEpochs = new ulong[1 + ExpansionArena.ComputeCount + ExpansionArena.DriverCount];
    private readonly ulong[] viewIncarnations = new ulong[1 + ExpansionArena.ComputeCount + ExpansionArena.DriverCount];

    /// <summary>The X0 shared platform composed on top of this expansion (host composition only).</summary>
    private ExpansionX0? x0;
    public ExpansionX0? X0
    {
        get => x0;
        set { x0 = value; controller.Services = value?.Services; }
    }

    internal ExpansionX2Card? X2 { get; set; }
    internal ExpansionX3Card? X3 { get; set; }
    internal ExpansionX4Card? X4 { get; set; }

    /// <summary>The expansion arena (host composition only): managed arena accesses use the
    /// holder's current rights, so a page the holder no longer owns faults.</summary>
    public ExpansionArena? Arena { get; set; }

    /// <summary>The full-X5 managed map (host composition only, X5 profile only; null = the
    /// compatibility profile whose main path is the faithful projection verbatim). When composed,
    /// the main and driver units route their memory through their managed slots; a denial records
    /// the holder's fault and pauses it BEFORE its bus effect (the effect is never committed).</summary>
    public ExpansionMmu? Mmu { get; set; }
    public Action<ushort, Z80.Z80>? OnM1Fetch;

    /// <summary>Compute units run bursts on the plain core, side by side on host threads, and a steadily halted main skips
    /// its halt cycles (hc2000-fast-emulation, doc design). On by default: nothing a guest sees depends on it (the
    /// equivalence test runs both ways); false runs the frontier model alone, the reference.</summary>
    public bool Bursts { get; set; } = true;
    /// <summary>The most instructions one burst runs.</summary>
    public int BurstInstructions { get; set; } = 1 << 20;
    // The exact time of the event being dispatched: a controller operation the main performs happens at it.
    private LogicalTime dispatchTime;
    // The tick the frame being run ends at (0 outside TryRunFrame): a burst ends before it, so the unit is where the
    // frontier model has it whenever a frame is complete (a snapshot, a host look at the machine).
    private long burstHorizon;
    // Units CatchUp rolled back during the current dispatch (see Synchronize).
    private readonly bool[] caughtUp = new bool[1 + ExpansionArena.ComputeCount + ExpansionArena.DriverCount];

    public ExpansionFrontierDriver(DeviceScheduler scheduler, FrameClock clock, CpuTimeMapper mapper,
        ExpansionController controller, HcMemory memory, PortDecoder ports, Z80.Z80 main)
    {
        ArgumentNullException.ThrowIfNull(scheduler); ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(mapper); ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(ports); ArgumentNullException.ThrowIfNull(main);
        if (scheduler.Now != 0 || main.TStates != 0 || mapper.CpuTStates != 0)
            throw new ArgumentException("Compose the initial expansion frontier at a fresh epoch.");
        this.scheduler = scheduler; this.clock = clock; this.mapper = mapper; this.controller = controller;
        this.memory = memory; this.ports = ports; this.main = main;
        mainPorts = ports.CreateView(controller.MainOrigin);
        controller.BeforeUnitOperation = CatchUp;
        controller.UnitTouched = id => burstCandidates |= 1u << id;
        for (ushort id = 0; id <= UnitCount; id++)
        {
            var captured = id;
            scheduler.RegisterDevice(FirstCpuDeviceId + id, e => Dispatch(captured, e));
        }
        Synchronize();
    }

    /// <summary>Owner composition/host controls may reconcile at the current common frontier.
    /// Ordinary root requests reconcile in the same main CPU callback, before later unit IDs.</summary>
    public void Synchronize()
    {
        VerifyOwner();
        for (ushort id = 0; id <= UnitCount; id++)
        {
            var unit = id == 0 ? null : controller.GetUnit(id);
            if (unit is not null && !unit.Runnable) { Cancel(id); continue; }
            // A faulted main stays at its uncommitted denial: no future frontier until the host
            // machine reset (fresh Z80 frontier) plus the root fault clear (controller RESET).
            if (id == 0 && Mmu is not null && Mmu.Faulted(ExpansionMmu.MainUnit)) { Cancel(0); continue; }
            var epoch = unit?.ExecutionEpoch ?? controller.MainOrigin.Epoch;
            if (id != 0 && (viewEpochs[id] != epoch || viewOwnerEpochs[id] != controller.OwnershipEpoch ||
                viewIncarnations[id] != controller.PortIncarnation))
            {
                unitPorts[id] = ports.CreateView(new(id, epoch, id <= ExpansionArena.ComputeCount ? CpuPortRole.Compute : CpuPortRole.Driver,
                    controller.OwnershipEpoch, controller.PortIncarnation));
                viewEpochs[id] = epoch; viewOwnerEpochs[id] = controller.OwnershipEpoch;
                viewIncarnations[id] = controller.PortIncarnation;
            }
            long tick; LogicalTime exact;
            if (unit is null)
            {
                // Main frontier at its exact logical time (floor + fraction), any admitted rate;
                // at faithful 5/1 the fraction is zero and this is the legacy integer tick.
                exact = mapper.ExactTimeAt(main.PeekNextFrontier().AtCpuTState);
                tick = exact.WholeTicks;
            }
            else
            {
                // Side units keep their own faithful clock; the main rate never accelerates them.
                tick = unit.NextMasterTick;
                exact = LogicalTime.Tick(tick);
            }
            if (tokens[id] != 0 && epochs[id] == epoch && due[id] == exact) continue;
            Cancel(id);
            if (unit is not null && caughtUp[id] && exact <= scheduler.LogicalNow)
            {
                // A unit rolled back and replayed to just before the main's operation (CatchUp) may have its next effect
                // at this very time, ordered after the main's (a higher device id). That effect is private (it lies in the
                // rolled-back burst's window), so committing it now, right after the operation, is the frontier model's
                // result; then the unit is scheduled as usual.
                try
                {
                    while (unit.Runnable && unit.ExecutionEpoch == epoch && LogicalTime.Tick(unit.NextMasterTick) <= scheduler.LogicalNow)
                        if (!DispatchUnitEffect(id, unit, unit.NextMasterTick)) break;
                }
                finally { caughtUp[id] = false; }
                id--;
                continue;
            }
            caughtUp[id] = false;
            if (exact <= scheduler.LogicalNow) throw new InvalidOperationException("A new CPU frontier must be strictly future.");
            due[id] = exact; epochs[id] = epoch;
            tokens[id] = scheduler.Schedule(new(tick, EventPhase.Cpu, FirstCpuDeviceId + id, 0,
                scheduler.Generation(FirstCpuDeviceId + id), DueTime: exact));
        }
    }
    /// <summary>One bus effect of side unit <paramref name="id"/> at <paramref name="dueTick"/>: performed, committed, its
    /// lifecycle change raised on the row (compute units), acknowledged. False when a map denial left it uncommitted.</summary>
    private bool DispatchUnitEffect(ushort id, ExpansionComputeUnit unit, long dueTick)
    {
        var effect = unit.PeekNextFrontier();
        var view = unitPorts[id] ?? throw new InvalidOperationException("Missing current unit port binding.");
        var value = (byte)0; var commit = true;
        if (effect.Kind is FrontierEffectKind.M1Read or FrontierEffectKind.MemoryRead)
            commit = UnitRead(unit, effect.Address, effect.Kind == FrontierEffectKind.M1Read, out value);
        else if (effect.Kind == FrontierEffectKind.PortRead) value = view.Read(effect.Address, dueTick);
        else if (effect.Kind == FrontierEffectKind.InterruptAck) value = PortDecoder.IntVector();
        if (effect.Kind == FrontierEffectKind.MemoryWrite && !UnitWrite(unit, effect.Address, effect.WriteValue)) commit = false;
        if (effect.Kind == FrontierEffectKind.PortWrite) view.Write(effect.Address, effect.WriteValue, dueTick);
        // A map denial faults the driver at its uncommitted effect (no bus effect, no value).
        if (!commit) return false;
        var before = unit.Lifecycle;
        unit.CommitFrontier(value, dueTick);
        // 7F9D reports compute unit lifecycle transitions only; driver IDs 17-24 are not
        // representable in the row interrupt contract and their lifecycle remains internal.
        if (id <= ExpansionArena.ComputeCount && unit.Lifecycle != before)
        {
            switch (unit.Lifecycle)
            {
                case ExpansionLifecycle.Halted:
                case ExpansionLifecycle.ServiceIdle:
                    controller.RowInterrupt?.Raise(ExpansionRowInterruptSource.UnitDone, unit.Id);
                    break;
                case ExpansionLifecycle.WaitingMailbox:
                    controller.RowInterrupt?.Raise(ExpansionRowInterruptSource.UnitMailboxWait, unit.Id);
                    break;
            }
        }
        controller.AcknowledgeFrontier(id, dueTick);
        return true;
    }

    /// <summary>A halted main with its INT line low repeats identical halt cycles (a read of PC, 4 T-states, one refresh)
    /// until an event raises a line: the frame clock's INT, a row source (a unit, a DMA, a Present), an NMI. Nothing can
    /// raise one before the next queued event, so every halt cycle that ends before it is committed at once
    /// (hc2000-fast-emulation, the main CPU). The halt cycles' reads of PC have no effect. Run from the frame loop, not
    /// inside an event: the scheduler then lands on the main's new time, so guest time, the CPU's clock and the
    /// scheduler agree (a snapshot's save boundary, a speed switch's safe boundary). True when it skipped.</summary>
    private bool SkipMainHalt()
    {
        if (tokens[0] == 0 || !main.SteadyHalt || main.IntLine || clock.IntAsserted || controller.RowInterrupt?.LineAsserted == true) return false;
        if (Mmu is not null && Mmu.Faulted(ExpansionMmu.MainUnit)) return false;
        Cancel(0);
        if (!scheduler.TryPeekNextTime(out var next)) { Synchronize(); return false; }
        var start = main.TStates;
        // The largest n whose n-th halt cycle still ends strictly before the next event.
        long low = 0, high = 1;
        while (mapper.ExactTimeAt(checked(start + 4 * high)) < next) { low = high; high = checked(high * 2); }
        while (high - low > 1)
        {
            var mid = low + (high - low) / 2;
            if (mapper.ExactTimeAt(checked(start + 4 * mid)) < next) low = mid; else high = mid;
        }
        if (low < 2) { Synchronize(); return false; }
        main.SkipHaltCycles(low);
        LandMainAtItsTime();
        return true;
    }

    /// <summary>After the main ran ahead of the scheduler on its own (a halt skip, a quantum), strictly before the next
    /// event: the mapper takes its T-states and the scheduler lands on their exact time (no event lies in between, so
    /// nothing is dispatched); then its next frontier is scheduled.</summary>
    private void LandMainAtItsTime()
    {
        mapper.Map(main.TStates);
        scheduler.AdvanceTo(mapper.ExactTime());
        Synchronize();
    }

    private readonly List<ExpansionComputeUnit> ready = [];
    // Units whose readiness may have changed since RunReadyBursts last looked (bit = unit id): an event of theirs was
    // dispatched, or an operation touched them. Scanning only these keeps a busy main's loop cheap.
    private uint burstCandidates = ~0u;

    /// <summary>The compute units that can burst now: Running at an instruction boundary (their next effect, an
    /// instruction start, is scheduled but not yet dispatched), isolated (no lent page, exchange or pending operation),
    /// not map holders, and due before the frame's end. Starting a unit's burst here instead of at its last retire
    /// gives the same result, since nothing of it happened in between. Units' bursts run side by side on host threads
    /// (each touches only its own core and pages); the owner thread opens and closes them. True when any ran, so the
    /// frame loop takes its next event again.</summary>
    private bool RunReadyBursts()
    {
        if (burstCandidates == 0) return false;
        ready.Clear();
        var candidates = burstCandidates;
        burstCandidates = 0;
        for (ushort id = 1; id <= ExpansionArena.ComputeCount && id <= UnitCount; id++)
        {
            if ((candidates & (1u << id)) == 0 || tokens[id] == 0) continue;
            var unit = controller.GetUnit(id);
            if (!unit.CanBurst || due[id].WholeTicks >= burstHorizon || (Mmu is not null && Mmu.IsMapHolder(id)) || !controller.UnitIsolated(id))
                continue;
            if (unit.PrepareBurst()) ready.Add(unit);
        }
        if (ready.Count == 0) return false;
        var horizon = burstHorizon; var max = BurstInstructions;
        if (ready.Count == 1) ready[0].ExecuteBurst(horizon, max);
        else Parallel.ForEach(ready, unit => unit.ExecuteBurst(horizon, max));
        var ran = false;
        foreach (var unit in ready) ran |= unit.FinishBurst() > 0;
        if (ran) Synchronize();
        return ran;
    }

    /// <summary>Brings every unit whose burst ran past the machine's present back to the frontier model's place there: it
    /// is rolled back and replayed on the frontier path up to now, as before a main operation on it (CatchUp). A snapshot
    /// taken between events then finds no unit ahead of the scheduler (a save in the middle of a frame met units already at
    /// the frame's end and was refused). Returns whether a unit was settled. Owner thread, between events.</summary>
    public bool SettleBursts()
    {
        VerifyOwner();
        var now = scheduler.LogicalNow;
        var saved = dispatchTime;
        var settled = false;
        dispatchTime = now;
        try
        {
            for (ushort id = 1; id <= ExpansionArena.ComputeCount && id <= UnitCount; id++)
            {
                if (controller.GetUnit(id).BurstEndTick is not { } end || LogicalTime.Tick(end) <= now) continue;
                CatchUp(id);
                burstCandidates |= 1u << id;
                settled = true;
            }
        }
        finally { dispatchTime = saved; }
        if (settled) Synchronize();
        return settled;
    }

    /// <summary>Before the main's controller operation on unit <paramref name="id"/> (at <see cref="dispatchTime"/>):
    /// a burst that ran up to or past that time is rolled back and the unit replayed on the frontier path up to just
    /// before it, so the operation meets the unit exactly where the frontier model has it. Its effects in that window
    /// are private (a burst runs only private instructions), so the replay commits them without the scheduler.</summary>
    private void CatchUp(ushort id)
    {
        if (id == 0 || id > ExpansionArena.ComputeCount) return;
        var unit = controller.GetUnit(id);
        if (unit.BurstEndTick is not { } end) return;
        if (LogicalTime.Tick(end) < dispatchTime) { unit.CloseBurst(); return; }
        unit.RollbackBurst();
        caughtUp[id] = true;
        while (unit.Runnable && LogicalTime.Tick(unit.NextMasterTick) < dispatchTime)
            if (!DispatchUnitEffect(id, unit, unit.NextMasterTick)) break;
    }

    private void Cancel(ushort id)
    {
        if (tokens[id] == 0) return;
        scheduler.Cancel(tokens[id]); scheduler.InvalidateDevice(FirstCpuDeviceId + id);
        tokens[id] = 0; epochs[id] = 0; due[id] = default;
    }
    /// <summary>Installs the mapper a runtime speed switch built and anchored at the committed boundary
    /// (see <c>Machine.SetMainCpuTarget</c>): the main frontier is re-queued at its new exact logical time,
    /// side units keep their own faithful integer ticks, and pending device events stay admitted.</summary>
    /// <summary>The side Z80s on the machine's previous clock follow its new one (see ExpansionController.FollowMachineClock).</summary>
    internal void FollowMachineClock(MainCpuSpeed from, MainCpuSpeed to) { VerifyOwner(); controller.FollowMachineClock(from, to); }

    internal void InstallMapper(CpuTimeMapper next)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(next);
        mapper = next;
        Synchronize();
    }
    private void Dispatch(ushort id, DeviceEvent item)
    {
        VerifyOwner();
        var itemTime = item.DueTime is { } exact ? exact : LogicalTime.Tick(item.DueTick);
        if (tokens[id] != item.InsertionSequence || due[id] != itemTime || item.Phase != EventPhase.Cpu || item.Kind != 0)
            throw new InvalidOperationException("CPU queue identity differs from its admitted frontier.");
        tokens[id] = 0; due[id] = default;
        dispatchTime = itemTime;
        // A unit's own next event is after its burst: the machine's order has caught up with it.
        if (id != 0) { controller.GetUnit(id).CloseBurst(); burstCandidates |= 1u << id; }
        if (id == 0)
        {
            var effect = main.PeekNextFrontier();
            if (mapper.ExactTimeAt(effect.AtCpuTState) != itemTime) throw new InvalidOperationException("Main CPU clock mismatch.");
            var value = (byte)0; var commit = true;
            if (effect.Kind is FrontierEffectKind.M1Read or FrontierEffectKind.MemoryRead)
                commit = MainRead(effect.Address, effect.Kind == FrontierEffectKind.M1Read, out value);
            else if (effect.Kind == FrontierEffectKind.PortRead) value = mainPorts.Read(effect.Address, item.DueTick);
            else if (effect.Kind == FrontierEffectKind.InterruptAck) value = PortDecoder.IntVector();
            if (effect.Kind == FrontierEffectKind.MemoryWrite && !MainWrite(effect.Address, effect.WriteValue)) commit = false;
            if (effect.Kind == FrontierEffectKind.PortWrite) mainPorts.Write(effect.Address, effect.WriteValue, item.DueTick);
            // A denial keeps the main frontier at its uncommitted effect: no bus effect, no value.
            if (commit)
            {
                if (effect.Kind == FrontierEffectKind.M1Read) OnM1Fetch?.Invoke(effect.Address, main);
                mapper.Map(effect.AtCpuTState);
                // The machine INT line is the frame clock OR the expansion row (contract 2),
                // read at this effect's tick; the frontier model gets the line here, not from
                // the edge events that update the non-frontier Z80 field.
                main.CommitFrontier(value, clock.IntAsserted || controller.RowInterrupt?.LineAsserted == true);
            }
        }
        else
        {
            var unit = controller.GetUnit(id);
            if (!unit.Runnable || epochs[id] != unit.ExecutionEpoch) throw new InvalidOperationException("A stale unit event reached dispatch.");
            // A unit faster than the master tick (28 and 280 MHz: 0.625 and 0.0625 ticks a T-state) has several bus
            // effects inside one tick. They share the tick (NextMasterTick's ceiling boundary), so they are committed here,
            // in order, until the unit's next effect is strictly future or it stops; scheduling one of them at the current
            // tick is refused (Synchronize), which crashed every frame of a 28 MHz unit. Slower units never take a second
            // turn: at 14 MHz and below consecutive effects are more than one tick apart.
            var epoch = unit.ExecutionEpoch;
            do
            {
                if (!DispatchUnitEffect(id, unit, item.DueTick)) return;
            }
            while (unit.Runnable && unit.ExecutionEpoch == epoch && unit.NextMasterTick == item.DueTick);
        }
        Synchronize();
    }

    // Full-X5 managed main/driver path (design section 5; the compatibility profile never composes
    // the Mmu, so every guard below is absent there and the faithful path is byte-identical). A
    // denial is recorded by the engine and pauses the holder BEFORE any bus effect: no read value,
    // no write, no commit; the frontier stays at its uncommitted effect.
    private bool MainRead(ushort address, bool m1, out byte value)
    {
        value = (byte)0;
        if (Mmu is null)
        {
            value = m1 ? memory.ReadM1(address) : memory.Read(address);
            return true;
        }
        if (!Mmu.TryResolve(ExpansionMmu.MainUnit, address, m1 ? ExpansionSlotCaps.Execute : ExpansionSlotCaps.Read, out var r)) return false;
        switch (r.Kind)
        {
            case ExpansionSlotKind.Legacy:
                value = m1 ? memory.ReadM1(address) : memory.Read(address); return true; // faithful semantics verbatim
            case ExpansionSlotKind.BaseRam:
                value = memory.BaseRamRead(ManagedOffset(r)); return true;
            case ExpansionSlotKind.Arena:
                if (!Arena!.MainCanMap((ushort)r.Target))
                { Mmu.RecordFault(ExpansionMmu.MainUnit, address, m1 ? ExpansionSlotCaps.Execute : ExpansionSlotCaps.Read); return false; }
                value = memory.ExpansionArenaSpan((ushort)r.Target, r.Intra, 1)[0]; return true;
            case ExpansionSlotKind.FirmwareRom:
                value = memory.Rom.Span[RomOffset(r)]; return true; // ROM is read-only; a W slot never maps
            default:
                throw new InvalidOperationException("A main managed slot resolved to no backing store.");
        }
    }
    private bool MainWrite(ushort address, byte value)
    {
        if (Mmu is null)
        {
            memory.Write(address, value);
            return true;
        }
        if (!Mmu.TryResolve(ExpansionMmu.MainUnit, address, ExpansionSlotCaps.Write, out var r)) return false;
        switch (r.Kind)
        {
            case ExpansionSlotKind.Legacy:
                memory.Write(address, value); return true;
            case ExpansionSlotKind.BaseRam:
                memory.BaseRamWrite(ManagedOffset(r), value); return true;
            case ExpansionSlotKind.Arena:
                if (!Arena!.MainCanMap((ushort)r.Target))
                { Mmu.RecordFault(ExpansionMmu.MainUnit, address, ExpansionSlotCaps.Write); return false; }
                Span<byte> buffer = stackalloc byte[1]; buffer[0] = value;
                memory.ExpansionArenaCopy((ushort)r.Target, r.Intra, buffer); return true;
            default:
                throw new InvalidOperationException("A main managed write resolved to a store that cannot take it.");
        }
    }
    private bool UnitRead(ExpansionComputeUnit unit, ushort address, bool m1, out byte value)
    {
        value = (byte)0;
        if (Mmu is null || !Mmu.IsMapHolder(unit.Id))
        {
            value = unit.ReadMemory(address); return true; // compute units keep their lease
        }
        var access = m1 ? ExpansionSlotCaps.Execute : ExpansionSlotCaps.Read;
        if (!Mmu.TryResolve(unit.Id, address, access, out var r)) { unit.Fault(); return false; }
        switch (r.Kind)
        {
            case ExpansionSlotKind.BaseRam:
                value = memory.BaseRamRead(ManagedOffset(r)); return true;
            case ExpansionSlotKind.Arena:
                try { value = Arena!.RouteRead(unit.Id, r.Target, r.Intra); }
                catch (InvalidOperationException) { Mmu!.RecordFault(unit.Id, address, access); unit.Fault(); return false; } // the lease no longer owns the page
                return true;
            case ExpansionSlotKind.FirmwareRom:
                value = memory.Rom.Span[RomOffset(r)]; return true;
            default:
                throw new InvalidOperationException("A driver managed slot resolved to no backing store.");
        }
    }
    private bool UnitWrite(ExpansionComputeUnit unit, ushort address, byte value)
    {
        // Lent-page denial (design contract 1): the unit cannot change a page while it is lent to a
        // card. The fault leaves the unit at its uncommitted store; the arena re-checks on both paths.
        if (Mmu is null || !Mmu.IsMapHolder(unit.Id))
        {
            if (LentUnitPage(unit.Id, address)) { unit.Fault(); return false; }
            unit.WriteMemory(address, value); return true; // compute units keep their lease
        }
        if (!Mmu.TryResolve(unit.Id, address, ExpansionSlotCaps.Write, out var r)) { unit.Fault(); return false; }
        switch (r.Kind)
        {
            case ExpansionSlotKind.Arena:
                if (Arena!.PageIsLent((ushort)r.Target))
                { Mmu.RecordFault(unit.Id, address, ExpansionSlotCaps.Write); unit.Fault(); return false; }
                try { Arena.RouteWrite(unit.Id, r.Target, r.Intra, value); }
                catch (InvalidOperationException) { Mmu!.RecordFault(unit.Id, address, ExpansionSlotCaps.Write); unit.Fault(); return false; } // never write a page the lease lost
                return true;
            default:
                throw new InvalidOperationException("A driver managed write resolved to a store that cannot take it.");
        }
    }
    /// <summary>Whether the unit's lease maps this logical address to a page currently lent to a card.</summary>
    private bool LentUnitPage(ushort unitId, ushort address)
    {
        if (Arena is null) return false;
        var grant = Arena.GetGrant(unitId);
        return Arena.PageIsLent(grant.Pages[address >> 14]);
    }
    /// <summary>Per-unit clock report (design contract 4): the target and the committed T-state meter;
    /// the achieved throughput is CommittedTStates over the elapsed master ticks - measured, not
    /// promised. 280 MHz is a target: a unit that cannot keep it reports what it achieved.</summary>
    public (MainCpuSpeed Target, long CommittedTStates) UnitClock(ushort id)
    {
        var unit = controller.GetUnit(id);
        return (unit.ClockTarget, unit.CommittedTStates);
    }
    private static ushort ManagedOffset(in ExpansionMmuResolution r) =>
        checked((ushort)(checked((int)r.Target * ExpansionMmu.SlotBytes) + r.Intra));
    private static int RomOffset(in ExpansionMmuResolution r) =>
        checked(checked((int)r.Target * ExpansionMmu.SlotBytes) + r.Intra);
    public bool TryRunFrame(Func<bool> commonFrontier)
    {
        VerifyOwner(); ArgumentNullException.ThrowIfNull(commonFrontier);
        var requested = checked(clock.CompletedFrames + 1);
        Synchronize();
        burstHorizon = checked(requested * FrameTiming.FrameTicks);
        try
        {
        while (clock.CompletedFrames < requested)
        {
            if (!commonFrontier()) return false;
            var next = default(LogicalTime); var has = false;
            for (var id = 0; id < tokens.Length; id++) if (tokens[id] != 0 && (!has || due[id] < next))
            { next = due[id]; has = true; }
            // A faulted main has no frontier; the frame's devices still drain to its boundary.
            if (!has && Mmu?.Faulted(ExpansionMmu.MainUnit) == true)
            { next = LogicalTime.Tick(checked(requested * FrameTiming.FrameTicks)); has = true; }
            if (!has || next <= scheduler.LogicalNow) throw new InvalidOperationException("The main CPU has no future frontier.");
            if (Bursts && (RunReadyBursts() || SkipMainHalt() || RunMainQuantum())) continue;
            scheduler.AdvanceTo(next);
        }
        }
        finally { burstHorizon = 0; }
        return true;
    }
    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("CPU effects belong to the emulation owner.");
    }
}
