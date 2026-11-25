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

    private void Cancel(ushort id)
    {
        if (tokens[id] == 0) return;
        scheduler.Cancel(tokens[id]); scheduler.InvalidateDevice(FirstCpuDeviceId + id);
        tokens[id] = 0; epochs[id] = 0; due[id] = default;
    }
    /// <summary>Installs the mapper a runtime speed switch built and anchored at the committed boundary
    /// (see <c>Machine.SetMainCpuTarget</c>): the main frontier is re-queued at its new exact logical time,
    /// side units keep their own faithful integer ticks, and pending device events stay admitted.</summary>
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
            scheduler.AdvanceTo(next);
        }
        return true;
    }
    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("CPU effects belong to the emulation owner.");
    }
}
