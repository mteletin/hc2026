using Hc2000.Machine.Timing;
using Hc2000.Z80.Frontier;

namespace Hc2000.Machine.Expansion;

// Detached bounded metadata only: the existing HCS RAM asset owns all mutable RAM bytes.
internal sealed record ExpansionSnapshotState(int Version, string MainKind, FrontierState Main,
    ExpansionUnitState[] Units, ExpansionRootState Root);
// ExchangeOwner, HandoverHalt and ExchangePreviousBank are the side-Z80 job-exchange
// reservation (hc2000-side-z80-job-exchange-v1): the exchange-window ownership byte,
// the handover-HALT flag and the remembered previous latch bank. All three are zero in
// this slice and enforced zero by ExpansionSchema.Validate.
internal sealed record ExpansionUnitState(string Kind, ExpansionPageGrant Grant, ulong ExecutionEpoch,
    ExpansionLifecycle Lifecycle, ExpansionLifecycle SavedLifecycle, long CpuAnchor, long MasterAnchor,
    long LastMasterTick, long Remainder, ExpansionMailboxEffect RequestedMailboxEffect,
    FrontierState Cpu, ExpansionMailboxState Mailbox, byte ExchangeOwner, bool HandoverHalt, ushort ExchangePreviousBank);
internal sealed record ExpansionMailboxState(byte[] Inbox, byte[] Outbox, byte[] Staging,
    int InboxCursor, int StagingCount, bool HasInbox, bool HasOutbox, bool Collecting, bool Overflow, byte LastError);
internal sealed record ExpansionRootState(ushort Selected, ushort Latched, byte[] Staging,
    bool Collecting, bool Overflow, byte[]? Response, int ResponseCursor, ushort PendingId,
    byte PendingOperation, ExpansionResult LastResult);

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
    internal ExpansionUnitState CaptureState(ExpansionPageGrant grant) => new("compute", grant, ExecutionEpoch,
        Lifecycle, savedLifecycle, cpuAnchor, masterAnchor, LastMasterTick, ClockRemainder,
        requestedMailboxEffect, core.CaptureFrontier(), Mailbox.CaptureState(), (byte)0, false, (ushort)0);

    internal void AdoptState(ExpansionUnitState s, ExpansionMemoryLease lease)
    {
        core.RestoreFrontier(s.Cpu); memory = lease; ExecutionEpoch = s.ExecutionEpoch;
        Lifecycle = s.Lifecycle; savedLifecycle = s.SavedLifecycle; cpuAnchor = s.CpuAnchor;
        masterAnchor = s.MasterAnchor; LastMasterTick = s.LastMasterTick;
        requestedMailboxEffect = s.RequestedMailboxEffect; Mailbox.AdoptState(s.Mailbox);
    }
}

public sealed partial class ExpansionArena
{
    internal void AdoptGrants(ExpansionUnitState[] states, ushort restoredMainBank)
    {
        VerifyOwner(); Array.Clear(pageOwners); mainBank = restoredMainBank;
        foreach (var s in states)
        {
            var grant = s.Grant; grants[grant.UnitId - 1] = grant;
            for (var i = 0; i < 4; i++) pageOwners[grant.Pages[i]] = (byte)grant.UnitId;
        }
    }
}

public sealed partial class ExpansionController
{
    internal ExpansionRootState CaptureState() => new(selected, latched, staging.AsSpan(0, stagingCount).ToArray(),
        collecting, overflow, response?.ToArray(), responseCursor, pendingId, pendingOperation, lastResult);

    internal ExpansionUnitState[] CaptureUnits() => units.Select(u => u.CaptureState(arena.GetGrant(u.Id))).ToArray();

    internal void AdoptState(ExpansionSnapshotState s, ushort mainBank)
    {
        VerifyOwner();
        // Prepare every lease before publication. Each lease checks the adopted grant on access.
        var leases = s.Units.Select(u => new ExpansionMemoryLease(arena, u.Grant)).ToArray();
        arena.AdoptGrants(s.Units, mainBank);
        for (var i = 0; i < units.Length; i++) units[i].AdoptState(s.Units[i], leases[i]);
        var r = s.Root; selected = r.Selected; latched = r.Latched;
        Array.Clear(staging); r.Staging.CopyTo(staging, 0); stagingCount = r.Staging.Length;
        collecting = r.Collecting; overflow = r.Overflow; response = r.Response; responseCursor = r.ResponseCursor;
        pendingId = r.PendingId; pendingOperation = r.PendingOperation; lastResult = r.LastResult;
    }
}

public sealed partial class ExpansionFrontierDriver
{
    internal ExpansionSnapshotState CaptureState()
    {
        VerifyOwner();
        return new(1, "main", main.CaptureFrontier(), controller.CaptureUnits(), controller.CaptureState());
    }

    internal void AdoptState(ExpansionSnapshotState s, ushort mainBank, IReadOnlyList<DeviceEvent> events)
    {
        VerifyOwner(); controller.AdoptState(s, mainBank); main.RestoreFrontier(s.Main);
        Array.Clear(tokens); Array.Clear(due); Array.Clear(epochs); Array.Clear(viewEpochs); Array.Clear(computePorts);
        foreach (var e in events)
        {
            if (e.DeviceId is < FirstCpuDeviceId or > FirstCpuDeviceId + ExpansionArena.ComputeCount) continue;
            var id = e.DeviceId - FirstCpuDeviceId; tokens[id] = e.InsertionSequence; due[id] = e.DueTick;
            epochs[id] = id == 0 ? 1 : s.Units[id - 1].ExecutionEpoch;
            if (id != 0)
            {
                computePorts[id] = ports.CreateView(new((ushort)id, epochs[id], Io.CpuPortRole.Compute));
                viewEpochs[id] = epochs[id];
            }
        }
    }
}
