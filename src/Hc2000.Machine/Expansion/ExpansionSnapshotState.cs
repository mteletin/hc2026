using System.Text.Json.Serialization;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Snapshots;
using Hc2000.Z80.Frontier;

namespace Hc2000.Machine.Expansion;

// Detached bounded metadata only: the existing HCS RAM asset owns all mutable RAM bytes.
internal sealed record ExpansionSnapshotState(int Version, string MainKind, FrontierState Main,
    ExpansionUnitState[] Units, ExpansionRootState Root);
// This detached component owns metadata only; the existing HCS RAM asset owns the bytes.
internal sealed record ExpansionX5State(int Version, ExpansionMmuState[] Maps,
    ExpansionUnitState[] Drivers, byte[] DeviceOwners, ulong OwnerEpoch, ExpansionOwnedCard[] Cards);
// HandoverHalt remains reserved zero: small units hand over through Idle alone.
// ExchangeOwner identifies the one live latch flip; bank zero is a legal previous bank.
// ClockTarget is additive (Version 1): absent in legacy documents, which read as the faithful
// default; omitting it on write keeps legacy documents byte-identical through the round-trip.
internal sealed record ExpansionUnitState(string Kind, ExpansionPageGrant Grant, ulong ExecutionEpoch,
    ExpansionLifecycle Lifecycle, ExpansionLifecycle SavedLifecycle, long CpuAnchor, long MasterAnchor,
    long LastMasterTick, long Remainder, ExpansionMailboxEffect RequestedMailboxEffect,
    FrontierState Cpu, ExpansionMailboxState Mailbox, byte ExchangeOwner, bool HandoverHalt,
    ushort ExchangePreviousBank,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MainCpuSpeed? ClockTarget = null);
internal sealed record ExpansionMailboxState(byte[] Inbox, byte[] Outbox, byte[] Staging,
    int InboxCursor, int StagingCount, bool HasInbox, bool HasOutbox, bool Collecting, bool Overflow, byte LastError);
internal sealed record ExpansionRootState(ushort Selected, ushort Latched, byte[] Staging,
    bool Collecting, bool Overflow, byte[]? Response, int ResponseCursor, ushort PendingId,
    byte PendingOperation, ExpansionResult LastResult,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpansionRowInterruptState? RowInterrupt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpansionPageLoan[]? Loans = null);
// Additive (Version 1): the row's status/ack register (enable bit + bounded pending queue) and the
// arena's live page loans. Legacy documents carry neither; both default to null and read as
// "no row interrupt / no loans".
internal sealed record ExpansionRowInterruptState(bool Enabled, ushort[] Queue);

public sealed partial class ExpansionMailbox
{
    internal ExpansionMailboxState CaptureState() => new(inbox.ToArray(), outbox.ToArray(), staging.ToArray(),
        inboxCursor, stagingCount, HasInbox, HasOutbox, collecting, overflow, lastError);

    internal static void ValidateState(ExpansionMailboxState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Inbox is not { Length: FrameBytes } || s.Outbox is not { Length: FrameBytes } ||
            s.Staging is not { Length: FrameBytes } || s.InboxCursor is < 0 or > FrameBytes ||
            s.StagingCount is < 0 or > FrameBytes || s.LastError is not (0 or 1 or 3) ||
            (!s.HasInbox && (s.InboxCursor != 0 || s.Inbox.Any(b => b != 0))) ||
            (!s.HasOutbox && s.Outbox.Any(b => b != 0)) ||
            (s.HasInbox && !ValidFrame(s.Inbox)) || (s.HasOutbox && !ValidFrame(s.Outbox)) ||
            (!s.Collecting && (s.StagingCount != 0 || s.Overflow)) ||
            (s.Overflow && s.StagingCount != FrameBytes) || s.Staging.AsSpan(s.StagingCount).ContainsAnyExcept((byte)0))
            throw new ArgumentException("Invalid local mailbox state.");
    }

    internal void AdoptState(ExpansionMailboxState s)
    {
        s.Inbox.CopyTo(inbox, 0); s.Outbox.CopyTo(outbox, 0); s.Staging.CopyTo(staging, 0);
        inboxCursor = s.InboxCursor; stagingCount = s.StagingCount; HasInbox = s.HasInbox;
        HasOutbox = s.HasOutbox; collecting = s.Collecting; overflow = s.Overflow; lastError = s.LastError;
    }
}

public sealed partial class ExpansionComputeUnit
{
    internal ExpansionUnitState CaptureState(ExpansionPageGrant grant, byte exchangeOwner, ushort previousBank) => new(Kind, grant, ExecutionEpoch,
        Lifecycle, savedLifecycle, cpuAnchor, masterAnchor, LastMasterTick, ClockRemainder,
        requestedMailboxEffect, core.CaptureFrontier(), Mailbox.CaptureState(), exchangeOwner, false, previousBank, ClockTarget);

    internal void AdoptState(ExpansionUnitState s, ExpansionMemoryLease lease)
    {
        core.RestoreFrontier(s.Cpu); memory = lease; ExecutionEpoch = s.ExecutionEpoch;
        Lifecycle = s.Lifecycle; savedLifecycle = s.SavedLifecycle; cpuAnchor = s.CpuAnchor;
        masterAnchor = s.MasterAnchor; LastMasterTick = s.LastMasterTick;
        requestedMailboxEffect = s.RequestedMailboxEffect; Mailbox.AdoptState(s.Mailbox);
        SetClockTarget((byte)(s.ClockTarget ?? MainCpuSpeed.Faithful)); clockRemainder = s.Remainder;
    }
}

public sealed partial class ExpansionArena
{
    internal byte[] PrepareGrantAdoption(ExpansionUnitState[] states)
    {
        VerifyOwner();
        var nextGrants = grants.ToArray();
        foreach (var state in states) nextGrants[state.Grant.UnitId - 1] = state.Grant;
        var nextOwners = new byte[pageOwners.Length];
        foreach (var grant in nextGrants)
        {
            if (grant.UnitId == 0) continue;
            for (var i = 0; i < 4; i++)
            {
                if (nextOwners[grant.Pages[i]] != 0 || deviceClaims.ContainsKey(grant.Pages[i]))
                    throw new ArgumentException("Overlapping arena or card grants.");
                nextOwners[grant.Pages[i]] = (byte)grant.UnitId;
            }
        }
        for (var page = 0; page < pageOwners.Length; page++)
            if (pageOwners[page] != nextOwners[page] && pageEpochs[page] == ulong.MaxValue)
                throw new ArgumentException("Arena page epoch exhausted.");
        return nextOwners;
    }

    internal void AdoptGrants(ExpansionUnitState[] states, ushort restoredMainBank)
    {
        var nextOwners = PrepareGrantAdoption(states);
        for (var page = 0; page < pageOwners.Length; page++)
            if (pageOwners[page] != nextOwners[page]) pageEpochs[page]++;
        nextOwners.CopyTo(pageOwners, 0);
        mainBank = restoredMainBank;
        exchangeOwner = exchangePreviousBank = 0;
        foreach (var s in states)
        {
            var grant = s.Grant; grants[grant.UnitId - 1] = grant;
            if (s.ExchangeOwner != 0)
            { exchangeOwner = s.ExchangeOwner; exchangePreviousBank = s.ExchangePreviousBank; }
        }
    }
}

public sealed partial class ExpansionController
{
    internal byte[] PrepareGrantAdoption(ExpansionSnapshotState compute, ExpansionX5State? x5)
    {
        if (x5 is not null)
        {
            if (portIncarnation == ulong.MaxValue) throw new ArgumentException("X5 port incarnation exhausted.");
            ValidateOwnedCardRoster(x5);
        }
        return arena.PrepareGrantAdoption(x5 is null ? compute.Units : compute.Units.Concat(x5.Drivers).ToArray());
    }

    internal ExpansionRootState CaptureState() => new(selected, latched, staging.AsSpan(0, stagingCount).ToArray(),
        collecting, overflow, response?.ToArray(), responseCursor, pendingId, pendingOperation, lastResult,
        RowInterrupt is { } row ? row.CaptureState() : null,
        arena.CaptureLoans() is { Length: > 0 } loans ? loans : null);

    internal ExpansionUnitState[] CaptureUnits() => units.Select(u => u.CaptureState(arena.GetGrant(u.Id),
        arena.OwnsExchange(u.Id) ? (byte)u.Id : (byte)0, arena.PreviousExchangeBank(u.Id))).ToArray();

    internal ExpansionX5State CaptureX5State()
    {
        VerifyOwner();
        var mmu = Mmu ?? throw new InvalidOperationException("An X5 snapshot requires a managed map.");
        var driverCount = units.Length - ExpansionArena.ComputeCount;
        var maps = new ExpansionMmuState[driverCount + 1];
        maps[0] = mmu.Capture(ExpansionMmu.MainUnit);
        for (var index = 0; index < driverCount; index++)
            maps[index + 1] = mmu.Capture((ushort)(ExpansionArena.DriverFirst + index));
        var drivers = units.Skip(ExpansionArena.ComputeCount).Select(unit => unit.CaptureState(arena.GetGrant(unit.Id),
            arena.OwnsExchange(unit.Id) ? (byte)unit.Id : (byte)0, arena.PreviousExchangeBank(unit.Id))).ToArray();
        return new(3, maps, drivers, deviceOwners.ToArray(), ownerEpoch, CaptureOwnedCardRoster());
    }

    internal void AdoptState(ExpansionSnapshotState s, ushort mainBank)
    {
        VerifyOwner();
        if (s.Units.Length != units.Length) throw new ArgumentException("Expansion roster mismatch.");
        AdoptUnitsAndRoot(s.Units, s.Root, mainBank);
    }

    // The caller must first validate both full components, scheduler and root against the HCS.
    // Validate ownership again here before publishing any grant, unit, map or root state.
    internal void AdoptX5State(ExpansionSnapshotState compute, ExpansionX5State x5, ushort mainBank)
    {
        VerifyOwner();
        var mmu = Mmu ?? throw new InvalidOperationException("An X5 restore requires a managed map.");
        if (x5.Drivers.Length != units.Length - ExpansionArena.ComputeCount)
            throw new ArgumentException("X5 driver roster mismatch.");
        ExpansionX5Schema.ValidateOwnership(compute, x5, mainBank);
        ValidateOwnedCardRoster(x5);
        if (portIncarnation == ulong.MaxValue) throw new ArgumentException("X5 port incarnation exhausted.");
        var allUnits = compute.Units.Concat(x5.Drivers).ToArray();
        AdoptUnitsAndRoot(allUnits, compute.Root, mainBank);
        if (mmu.Adopt(ExpansionMmu.MainUnit, x5.Maps[0]) != ExpansionResult.Accepted)
            throw new InvalidOperationException("Validated X5 main map was refused.");
        for (var index = 0; index < x5.Drivers.Length; index++)
            if (mmu.Adopt((ushort)(ExpansionArena.DriverFirst + index), x5.Maps[index + 1]) != ExpansionResult.Accepted)
                throw new InvalidOperationException("Validated X5 driver map was refused.");
        x5.DeviceOwners.CopyTo(deviceOwners, 0); ownerEpoch = x5.OwnerEpoch; portIncarnation++;
    }

    private void AdoptUnitsAndRoot(ExpansionUnitState[] states, ExpansionRootState root, ushort mainBank)
    {
        // Prepare every lease before publication. Each lease checks the adopted grant on access.
        var leases = states.Select(u => new ExpansionMemoryLease(arena, u.Grant)).ToArray();
        arena.AdoptGrants(states, mainBank);
        for (var i = 0; i < units.Length; i++) units[i].AdoptState(states[i], leases[i]);
        var r = root; selected = r.Selected; latched = r.Latched;
        Array.Clear(staging); r.Staging.CopyTo(staging, 0); stagingCount = r.Staging.Length;
        collecting = r.Collecting; overflow = r.Overflow; response = r.Response; responseCursor = r.ResponseCursor;
        pendingId = r.PendingId; pendingOperation = r.PendingOperation; lastResult = r.LastResult;
        if (r.RowInterrupt is { } rowState) RowInterrupt?.AdoptState(rowState);
        if (r.Loans is { } loans) arena.AdoptLoans(loans);
    }
}

public sealed partial class ExpansionRowInterrupt
{
    internal ExpansionRowInterruptState CaptureState() => new(Enabled, queue.ToArray());

    internal void AdoptState(ExpansionRowInterruptState s)
    {
        queue.Clear();
        foreach (var source in s.Queue) queue.Enqueue(source);
        Enabled = s.Enabled;
    }
}

public sealed partial class ExpansionArena
{
    internal ExpansionPageLoan[] CaptureLoans() => pageLoans.Values.ToArray();

    /// <summary>Restore the live loans after the grants they rest on are adopted: every loan must
    /// still name a page the unit owns at the captured epochs, with no page or card lent twice.</summary>
    internal void AdoptLoans(ExpansionPageLoan[] loans)
    {
        VerifyOwner();
        pageLoans.Clear();
        foreach (var loan in loans)
        {
            if (loan.UnitId is < 1 or > ComputeCount + DriverCount || !grantFor(loan.UnitId).Owns(loan.Page) ||
                loan.CardId == 0 || pageLoans.ContainsKey(loan.Page) ||
                pageOwners[loan.Page] != loan.UnitId ||
                loan.MapEpoch != grants[loan.UnitId - 1].MapEpoch || loan.PageEpoch != pageEpochs[loan.Page])
                throw new ArgumentException("Invalid arena page loan.");
            pageLoans[loan.Page] = loan;
        }
    }

    private ExpansionPageGrant grantFor(ushort unitId) => grants[unitId - 1];
}

public sealed partial class ExpansionFrontierDriver
{
    internal byte[] PrepareGrantAdoption(ExpansionSnapshotState compute, ExpansionX5State? x5) =>
        controller.PrepareGrantAdoption(compute, x5);

    // The X5 snapshot identity needs one managed map shared by the driver's memory routing and
    // the controller's roster capture/adoption. HCS Save and Load preflight this before any
    // capture or mutation, so a machine composed without it is refused before the document
    // is examined or the scheduler, RAM or CPU change.
    internal bool ManagedMapComposed => Mmu is not null && ReferenceEquals(Mmu, controller.Mmu);

    internal ExpansionSnapshotState CaptureState()
    {
        VerifyOwner();
        return new(1, "main", main.CaptureFrontier(), controller.CaptureUnits(), controller.CaptureState());
    }

    // The step-1 component keeps its 16 compute units; the x5 component carries the active
    // driver roster, the main/driver managed maps and the device owners.
    internal (ExpansionSnapshotState Compute, ExpansionX5State X5) CaptureX5FullState()
    {
        VerifyOwner();
        var full = CaptureState();
        var x5 = controller.CaptureX5State();
        return (full with { Units = full.Units.Take(ExpansionArena.ComputeCount).ToArray() }, x5);
    }

    internal void AdoptState(ExpansionSnapshotState s, ushort mainBank, IReadOnlyList<DeviceEvent> events)
    {
        VerifyOwner(); controller.AdoptState(s, mainBank); main.RestoreFrontier(s.Main);
        RestoreTokens(s.Units, events);
    }

    // The caller must have validated both components, the driver timing, the controller root and
    // the scheduler events against the HCS before this mutation.
    internal void AdoptX5FullState(ExpansionSnapshotState s, ExpansionX5State x5, ushort mainBank, IReadOnlyList<DeviceEvent> events)
    {
        VerifyOwner(); controller.AdoptX5State(s, x5, mainBank); main.RestoreFrontier(s.Main);
        RestoreTokens(s.Units.Concat(x5.Drivers).ToArray(), events);
    }

    private void RestoreTokens(ExpansionUnitState[] allUnits, IReadOnlyList<DeviceEvent> events)
    {
        Array.Clear(tokens); Array.Clear(due); Array.Clear(epochs); Array.Clear(viewEpochs); Array.Clear(viewOwnerEpochs);
        Array.Clear(viewIncarnations); Array.Clear(unitPorts);
        foreach (var e in events)
        {
            var last = FirstCpuDeviceId + UnitCount;
            if (e.DeviceId < FirstCpuDeviceId || e.DeviceId > last) continue;
            var id = e.DeviceId - FirstCpuDeviceId; tokens[id] = e.InsertionSequence;
            due[id] = e.DueTime is { } exact ? exact : LogicalTime.Tick(e.DueTick); // the event carries the admitted exact due
            epochs[id] = id == 0 ? 1 : allUnits[id - 1].ExecutionEpoch;
            if (id != 0)
            {
                unitPorts[id] = ports.CreateView(new((ushort)id, epochs[id], id <= ExpansionArena.ComputeCount ? Io.CpuPortRole.Compute : Io.CpuPortRole.Driver,
                    controller.OwnershipEpoch, controller.PortIncarnation));
                viewEpochs[id] = epochs[id];
                viewOwnerEpochs[id] = controller.OwnershipEpoch;
                viewIncarnations[id] = controller.PortIncarnation;
            }
        }
    }
}
