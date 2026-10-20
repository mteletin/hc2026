using System.Buffers.Binary;

namespace Hc2000.Machine.Floppy;

public sealed partial class BlockDiskPort
{
    public sealed record GuestSwitchRequest(Guid SessionNonce, ulong RequestId, byte PrimaryUnit,
        ushort ResetMask, uint CGeneration, uint DGeneration, uint EGeneration, uint FGeneration);
    public readonly record struct GuestSwitchCommitResult(bool Committed, uint Generation, string? Error);

    // Only this controller can mint authority. Observations and wire bytes are never tickets.
    public sealed class GuestSwitchTicket
    {
        private GuestSwitchTicket() { }
        internal static GuestSwitchTicket Issue() => new();
    }

    private enum GuestPhase { Idle, Requested, Held, Committed, Cancelled, CommittedHeld, CancelledHeld }
    private Guid guestSession;
    private bool guestRestored;
    private ulong guestSequence;
    private GuestSwitchRequest? guestRequest;
    private GuestSwitchTicket? guestTicket;
    private GuestPhase guestPhase;
    public bool GuestAckEnabled { get; }
    public bool GuestSwitchHoldsCpu
    {
        get { VerifyOwner(); return guestPhase is GuestPhase.Held or GuestPhase.CommittedHeld or GuestPhase.CancelledHeld; }
    }

    private void RequireGuestAck()
    {
        VerifyOwner();
        if (!GuestAckEnabled) throw new InvalidOperationException("Guest ACK is disabled.");
    }

    private void RequireNoGuestSwitch()
    {
        if (guestRequest is not null) throw new InvalidOperationException("Drain or retire the guest switch first.");
    }

    public void BindGuestAckSession(Guid sessionNonce)
    {
        RequireGuestAck();
        RequireNoGuestSwitch();
        if (guestRestored) throw new InvalidOperationException("Restored guests are ineligible for guest ACK.");
        if (sessionNonce == Guid.Empty || sessionNonce == guestSession)
            throw new ArgumentException("A fresh nonempty host session is required.", nameof(sessionNonce));
        guestSession = lastGuestSession = sessionNonce;
        completedGuestCooperation = false;
    }

    /// <summary>Explicit old-owner retirement, not normal pending cancellation or successful finish.</summary>
    public void InvalidateGuestAckSession()
    {
        VerifyOwner();
        guestSession = Guid.Empty;
        guestRequest = null;
        guestTicket = null;
        guestPhase = GuestPhase.Idle;
        coldRestoreBoundary = false;
        completedGuestCooperation = false;
    }

    private void RetireGuestAckForRestore()
    {
        InvalidateGuestAckSession();
        guestRestored = true;
    }

    public GuestSwitchRequest BeginGuestSwitch(byte primaryUnit, ushort resetMask)
    {
        RequireGuestAck();
        coldRestoreBoundary = false;
        RequireNoGuestSwitch();
        if (guestSession == Guid.Empty) throw new InvalidOperationException("Bind a fresh live session first.");
        if (primaryUnit is < 2 or > 5) throw new ArgumentOutOfRangeException(nameof(primaryUnit));
        if (resetMask is not (4 or 8 or 16 or 32 or 60) || (resetMask & (1 << primaryUnit)) == 0)
            throw new ArgumentOutOfRangeException(nameof(resetMask));
        var sequence = checked(guestSequence + 1);
        uint Generation(int index) => (resetMask & (1 << (index + 2))) != 0 ? slots![index].Generation : 0;
        guestRequest = new(guestSession, sequence, primaryUnit, resetMask,
            Generation(0), Generation(1), Generation(2), Generation(3));
        guestSequence = sequence;
        guestPhase = GuestPhase.Requested;
        completedGuestCooperation = false;
        return guestRequest;
    }

    public void CancelPendingGuestSwitch(Guid sessionNonce, ulong requestId)
    {
        RequireGuestAck();
        if (guestPhase != GuestPhase.Requested || guestRequest is not { } request ||
            sessionNonce != request.SessionNonce || requestId != request.RequestId)
            throw new InvalidOperationException("Not the current pending request.");
        guestPhase = GuestPhase.Cancelled;
        status = 0x0e;
    }

    public bool TryGetHeldGuestSwitch(out GuestSwitchTicket ticket)
    {
        RequireGuestAck();
        ticket = guestTicket!;
        return guestTicket is not null && GuestSwitchHoldsCpu;
    }

    private GuestSwitchRequest RequireTicket(GuestSwitchTicket ticket, GuestPhase phase)
    {
        RequireGuestAck();
        if (ticket is null || !ReferenceEquals(ticket, guestTicket) || guestRequest is null ||
            guestRequest.SessionNonce != guestSession || guestPhase != phase)
            throw new InvalidOperationException("Not the current issued ticket/outcome.");
        return guestRequest;
    }

    private bool GenerationsMatch(GuestSwitchRequest request)
    {
        ReadOnlySpan<uint> generations = [request.CGeneration, request.DGeneration, request.EGeneration, request.FGeneration];
        for (var i = 0; i < 4; i++)
            if ((request.ResetMask & (1 << (i + 2))) != 0 && slots![i].Generation != generations[i]) return false;
        return true;
    }

    public GuestSwitchCommitResult CommitGuestSwitch(GuestSwitchTicket ticket, ReadOnlySpan<byte> image,
        bool writeProtected, bool discardDirty)
    {
        RequireRestoredCooperation();
        var request = RequireTicket(ticket, GuestPhase.Held);
        var slot = Slot(request.PrimaryUnit);
        if (!GenerationsMatch(request)) return new(false, slot.Generation, "Old generations changed.");
        try { slot.Attach(image, writeProtected, discardDirty); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
        { return new(false, slot.Generation, e.Message); }
        guestPhase = GuestPhase.CommittedHeld;
        return new(true, slot.Generation, null);
    }

    public GuestSwitchCommitResult DetachGuestSwitch(GuestSwitchTicket ticket, bool discardDirty)
    {
        RequireRestoredCooperation();
        var request = RequireTicket(ticket, GuestPhase.Held);
        var slot = Slot(request.PrimaryUnit);
        if (!GenerationsMatch(request)) return new(false, slot.Generation, "Old generations changed.");
        try { slot.Detach(discardDirty); }
        catch (InvalidOperationException e) { return new(false, slot.Generation, e.Message); }
        guestPhase = GuestPhase.CommittedHeld;
        return new(true, slot.Generation, null);
    }

    public void CancelGuestSwitch(GuestSwitchTicket ticket)
    {
        RequireTicket(ticket, GuestPhase.Held);
        guestPhase = GuestPhase.CancelledHeld;
    }

    /// <summary>Owner calls only after actual outcome accounting and native cleanup/drain.</summary>
    public void FinishGuestSwitch(GuestSwitchTicket ticket)
    {
        RequireGuestAck();
        if (guestPhase is not (GuestPhase.CommittedHeld or GuestPhase.CancelledHeld))
            throw new InvalidOperationException("No internally established outcome to account.");
        RequireTicket(ticket, guestPhase);
        guestPhase = guestPhase == GuestPhase.CommittedHeld ? GuestPhase.Committed : GuestPhase.Cancelled;
        guestTicket = null;
        status = guestPhase == GuestPhase.Committed ? (byte)0 : (byte)0x0e;
    }

    private byte[] GuestBlock()
    {
        var block = new byte[512];
        block[0] = 1;
        if (guestRequest is not { } request) return block;
        block[1] = guestPhase switch
        {
            GuestPhase.Requested => 1,
            GuestPhase.Held or GuestPhase.CommittedHeld or GuestPhase.CancelledHeld => 2,
            GuestPhase.Committed => 3,
            GuestPhase.Cancelled => 4,
            _ => 0
        };
        block[2] = request.PrimaryUnit;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), request.ResetMask);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), request.RequestId);
        request.SessionNonce.TryWriteBytes(block.AsSpan(16, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(32), request.CGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(36), request.DGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(40), request.EGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(44), request.FGeneration);
        return block;
    }

    private byte ExecuteGuestAck(ReadOnlySpan<byte> command)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(command[4..]) != 0) return 0x02;
        var dma = BinaryPrimitives.ReadUInt16LittleEndian(command[8..]);
        if (!transfer.ValidDma(dma)) return 0x09;
        if (command[1] == 4)
        {
            if (guestRequest is not null && command[2] != guestRequest.PrimaryUnit) return 0x0e;
            transfer.WriteDma(dma, GuestBlock());
            if (guestPhase is GuestPhase.Committed or GuestPhase.Cancelled)
            {
                guestRequest = null;
                guestPhase = GuestPhase.Idle;
            }
            return 0;
        }
        var block = transfer.ReadDma(dma);
        if (block[0] != 1 || block[3] != 0 || block[6] != 0 || block[7] != 0 ||
            block.AsSpan(48).ContainsAnyExcept((byte)0)) return 0x02;
        if (guestPhase != GuestPhase.Requested || guestRequest is not { } request ||
            command[2] != request.PrimaryUnit ||
            BinaryPrimitives.ReadUInt32LittleEndian(command[12..]) != BinaryPrimitives.ReadUInt32LittleEndian(GuestBlock().AsSpan(32 + (request.PrimaryUnit - 2) * 4)) ||
            !block.AsSpan().SequenceEqual(GuestBlock())) return 0x0e;
        if (!GenerationsMatch(request))
        {
            // Correctly correlated but obsolete: invalidate rather than silently retarget.
            guestRequest = null;
            guestPhase = GuestPhase.Idle;
            return 0x0d;
        }
        guestTicket = GuestSwitchTicket.Issue();
        guestPhase = GuestPhase.Held;
        uncooperated = false;
        completedGuestCooperation = true;
        return 0;
    }
}
