namespace Hc2000.Machine.Expansion;

/// <summary>Full-X5 slot target kinds. Legacy is the main-only faithful projection (latches and IF1
/// semantics stay in the memory model). Absent is NOT a float: any access to it is a denial and
/// pauses the holder with a fault (architecture contract 6.1/6.2; 6.2: "R/W/X/Absent denial").</summary>
public enum ExpansionSlotKind : byte
{
    Absent = 0,
    BaseRam = 1,
    Arena = 2,
    FirmwareRom = 3,
    OptionRom = 4,
    Legacy = 5
}

/// <summary>R/W/X capability bits: Read=1, Write=2, Execute=4. A slot's caps never exceed the
/// physical rights of its target, and a readonly slot never upgrades physical rights.</summary>
[Flags]
public enum ExpansionSlotCaps : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
    All = Read | Write | Execute
}

/// <summary>One 16-KiB logical slot: target kind, caps and the kind-dependent target word
/// (BaseRam page 0..3, Arena page 0..FFFF, FirmwareRom 16-KiB slice, OptionRom (card, page),
/// Legacy 0, Absent 0).</summary>
public readonly record struct ExpansionMmuSlot(ExpansionSlotKind Kind, ExpansionSlotCaps Caps, uint Target);

/// <summary>A recorded denial: the fault address, the denied access and the map epoch under which
/// it was refused. Kept with the exact uncommitted continuation by the owner.</summary>
public readonly record struct ExpansionMmuFault(ushort Address, ExpansionSlotCaps Access, ulong MapEpoch);

/// <summary>An allowed managed access, routed by the machine owner: target kind, kind-dependent
/// target word and the in-page offset. Legacy resolves to the faithful path unchanged.</summary>
public readonly record struct ExpansionMmuResolution(ExpansionSlotKind Kind, uint Target, ushort Intra);

/// <summary>The full-X5 managed map: four independently described 16-KiB logical slots for the MAIN
/// CPU and each DRIVER unit. Root-authored map transactions preflight every slot, permissions,
/// physical grants and ranges, and change nothing on refusal. A denied access is recorded as a
/// fault before any bus effect and never fabricates one. Compute units never own a map.
/// No bus, controller, scheduler or Machine reference: the machine owner composes it and routes
/// the effects it resolves.</summary>
public sealed class ExpansionMmu
{
    public const int SlotCount = 4;
    public const int SlotBytes = ExpansionArena.PageBytes;
    public const ushort MainUnit = 0;
    public const int DriverFirst = ExpansionArena.DriverFirst;
    public const int DriverCount = ExpansionArena.DriverCount;
    public const int DriverLast = DriverFirst + DriverCount - 1;
    public const uint BaseRamPages = 4;
    public const uint FirmwareRomSlices = 2; // the 32-KiB v2 ROM in 16-KiB slices

    private sealed class MapState
    {
        public readonly ExpansionMmuSlot[] Slots = new ExpansionMmuSlot[SlotCount];
        public ulong MapEpoch = 1;
        public ExpansionMmuFault? Fault;
    }

    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly ExpansionArena arena;
    private readonly MapState[] holders = new MapState[1 + DriverCount];

    /// <summary>Host composition only. Main starts on the saved compatibility projection
    /// (four Legacy slots, i.e. the faithful path); drivers start 4 x Absent until root maps them.</summary>
    public ExpansionMmu(ExpansionArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        this.arena = arena;
        for (var i = 0; i < holders.Length; i++) holders[i] = new MapState();
        for (var i = 0; i < SlotCount; i++)
            holders[0].Slots[i] = new(ExpansionSlotKind.Legacy, ExpansionSlotCaps.All, 0);
    }

    public bool IsMapHolder(ushort unitId) => unitId == MainUnit || unitId is >= DriverFirst and <= DriverLast;
    public ulong MapEpoch(ushort unitId)
    {
        VerifyOwner();
        return holders[Index(unitId)].MapEpoch;
    }
    public bool Faulted(ushort unitId)
    {
        VerifyOwner();
        return holders[Index(unitId)].Fault is not null;
    }
    public ExpansionMmuFault? Fault(ushort unitId)
    {
        VerifyOwner();
        return holders[Index(unitId)].Fault;
    }
    public ExpansionMmuSlot[] SlotsOf(ushort unitId)
    {
        VerifyOwner();
        var slots = holders[Index(unitId)].Slots;
        var copy = new ExpansionMmuSlot[SlotCount];
        slots.CopyTo(copy, 0);
        return copy;
    }

    /// <summary>One staged map transaction for a map holder: all four targets, permissions, grants
    /// and ranges preflight; any refusal leaves the map, epoch and fault untouched and reports its
    /// code. The committing caller passes the holder's expected map epoch; unit/execution epochs are
    /// checked by the owning controller.</summary>
    public ExpansionResult TryMap(ushort unitId, ulong expectedMapEpoch, ReadOnlySpan<ExpansionMmuSlot> slots)
    {
        VerifyOwner();
        if (!IsMapHolder(unitId)) return ExpansionResult.InvalidState;
        if (slots.Length != SlotCount) return ExpansionResult.InvalidState;
        var state = holders[Index(unitId)];
        if (state.MapEpoch != expectedMapEpoch) return ExpansionResult.StaleEpoch;
        if (state.Fault is not null) return ExpansionResult.InvalidState; // recovery is a root reset first
        var next = new ExpansionMmuSlot[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            var result = ValidateSlot(unitId, slots[i]);
            if (result != ExpansionResult.Accepted) return result;
            next[i] = slots[i];
        }
        for (var i = 0; i < SlotCount; i++) state.Slots[i] = next[i];
        state.MapEpoch = checked(state.MapEpoch + 1);
        return ExpansionResult.Accepted;
    }

    /// <summary>Range-check and capability/grant check one slot of one holder. Duplicate readonly
    /// aliases of a page the holder is entitled to are legal; a slot never manufactures a grant.</summary>
    private ExpansionResult ValidateSlot(ushort unitId, ExpansionMmuSlot slot)
    {
        var result = ValidateSlotShape(unitId, slot);
        if (result != ExpansionResult.Accepted) return result;
        if (slot.Kind == ExpansionSlotKind.Arena && slot.Caps != ExpansionSlotCaps.None &&
            (unitId == MainUnit ? !arena.MainCanMap((ushort)slot.Target)
                : !arena.GetGrant(unitId).Owns((ushort)slot.Target))) return ExpansionResult.InvalidState;
        return ExpansionResult.Accepted;
    }

    private static ExpansionResult ValidateSlotShape(ushort unitId, ExpansionMmuSlot slot)
    {
        if (((byte)slot.Caps & ~0b111) != 0) return ExpansionResult.InvalidState;
        switch (slot.Kind)
        {
            case ExpansionSlotKind.Legacy:
                if (unitId != MainUnit) return ExpansionResult.Unsupported;
                return slot.Caps == ExpansionSlotCaps.All && slot.Target == 0
                    ? ExpansionResult.Accepted : ExpansionResult.InvalidState;
            case ExpansionSlotKind.Absent:
                return slot.Caps == ExpansionSlotCaps.None && slot.Target == 0
                    ? ExpansionResult.Accepted : ExpansionResult.InvalidState;
            case ExpansionSlotKind.BaseRam:
                if (slot.Target >= BaseRamPages) return ExpansionResult.InvalidState;
                // v1: base RAM is the main's physical; drivers map it readonly only.
                return (slot.Caps & ExpansionSlotCaps.Write) != 0 && unitId != MainUnit
                    ? ExpansionResult.InvalidState : ExpansionResult.Accepted;
            case ExpansionSlotKind.Arena:
                if (slot.Target > ushort.MaxValue) return ExpansionResult.InvalidState;
                return ExpansionResult.Accepted;
            case ExpansionSlotKind.FirmwareRom:
                if (slot.Target >= FirmwareRomSlices) return ExpansionResult.InvalidState;
                return (slot.Caps & ExpansionSlotCaps.Write) != 0
                    ? ExpansionResult.InvalidState : ExpansionResult.Accepted; // ROM cannot acquire W
            case ExpansionSlotKind.OptionRom:
                return ExpansionResult.Unsupported; // v1: no card has registered an option ROM
            default:
                return ExpansionResult.Unsupported;
        }
    }

    /// <summary>Resolve one managed access before its bus effect. A denial records the fault
    /// (first denial wins) and returns false WITHOUT touching the bus; no Z80 fault vector is
    /// injected and earlier legal effects are not rolled back. A faulted holder is recovered by a
    /// root reset, which clears the fault; a fault never widens rights.</summary>
    public bool TryResolve(ushort unitId, ushort address, ExpansionSlotCaps access, out ExpansionMmuResolution resolution)
    {
        VerifyOwner();
        resolution = default;
        if (!IsMapHolder(unitId)) return false;
        var state = holders[Index(unitId)];
        var slot = state.Slots[address >> 14];
        if (slot.Kind == ExpansionSlotKind.Legacy)
        {
            // The faithful projection: latches, mirrors and IF1 semantics live in the memory model.
            resolution = new(ExpansionSlotKind.Legacy, 0, (ushort)(address & (SlotBytes - 1)));
            return true;
        }
        if (slot.Kind == ExpansionSlotKind.Absent || (slot.Caps & access) == 0)
        {
            state.Fault ??= new(address, access, state.MapEpoch);
            return false;
        }
        resolution = new(slot.Kind, slot.Target, (ushort)(address & (SlotBytes - 1)));
        return true;
    }

    /// <summary>Record a denial on a route failure after a slot already resolved (the grant lost
    /// the page since the map preflighted it): same first-denial-wins record as the TryResolve
    /// denials, so the snapshot and the root recovery path see one source of truth.</summary>
    internal void RecordFault(ushort unitId, ushort address, ExpansionSlotCaps access)
    {
        VerifyOwner();
        if (!IsMapHolder(unitId)) return;
        var state = holders[Index(unitId)];
        state.Fault ??= new(address, access, state.MapEpoch);
    }

    /// <summary>Root recovery path (controller reset / machine reset): clears the recorded fault.
    /// Never a guest-reachable effect.</summary>
    internal void ClearFault(ushort unitId)
    {
        VerifyOwner();
        if (!IsMapHolder(unitId)) return;
        holders[Index(unitId)].Fault = null;
    }

    public ExpansionMmuState Capture(ushort unitId)
    {
        VerifyOwner();
        var state = holders[Index(unitId)];
        return new(state.Slots[0], state.Slots[1], state.Slots[2], state.Slots[3], state.MapEpoch, state.Fault);
    }

    /// <summary>Validate structural slot legality, epoch and fault before changing a holder. A
    /// saved slot may outlive its original grant; accesses still check live authority at the bus.</summary>
    public ExpansionResult Adopt(ushort unitId, in ExpansionMmuState state)
    {
        VerifyOwner();
        if (!ValidDetachedState(unitId, state)) return ExpansionResult.InvalidState;
        var slots = new[] { state.Slot0, state.Slot1, state.Slot2, state.Slot3 };
        var target = holders[Index(unitId)];
        for (var i = 0; i < SlotCount; i++) target.Slots[i] = slots[i];
        target.MapEpoch = state.MapEpoch;
        target.Fault = state.Fault;
        return ExpansionResult.Accepted;
    }

    /// <summary>Validate snapshot metadata without a live arena or any state mutation. A map can
    /// outlive its grant, so authority is checked on each bus effect rather than guessed here.</summary>
    internal static bool ValidDetachedState(ushort unitId, in ExpansionMmuState state)
    {
        if (unitId != MainUnit && unitId is not (>= DriverFirst and <= DriverLast)) return false;
        if (state.MapEpoch is 0 or ulong.MaxValue) return false;
        foreach (var slot in new[] { state.Slot0, state.Slot1, state.Slot2, state.Slot3 })
            if (ValidateSlotShape(unitId, slot) != ExpansionResult.Accepted) return false;
        return state.Fault is not { } fault || fault.MapEpoch == state.MapEpoch &&
            fault.Access is ExpansionSlotCaps.Read or ExpansionSlotCaps.Write or ExpansionSlotCaps.Execute;
    }

    private int Index(ushort unitId)
    {
        if (!IsMapHolder(unitId)) throw new ArgumentOutOfRangeException(nameof(unitId));
        return unitId == MainUnit ? 0 : unitId - (DriverFirst - 1);
    }

    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != owner)
            throw new InvalidOperationException("Managed map access belongs to the emulation owner.");
    }
}

/// <summary>Detached bounded map state for one holder: the four slots, the map epoch and the
/// pending fault, if any.</summary>
public readonly record struct ExpansionMmuState(ExpansionMmuSlot Slot0, ExpansionMmuSlot Slot1,
    ExpansionMmuSlot Slot2, ExpansionMmuSlot Slot3, ulong MapEpoch, ExpansionMmuFault? Fault);
