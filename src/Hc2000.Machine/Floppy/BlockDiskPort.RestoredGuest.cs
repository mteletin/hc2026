namespace Hc2000.Machine.Floppy;

public sealed partial class BlockDiskPort
{
    private bool coldRestoreBoundary;
    private bool uncooperated;
    private bool completedGuestCooperation;
    private Guid lastGuestSession;
    public bool Uncooperated { get { VerifyOwner(); return uncooperated; } }

    public void ValidateRestoredGuestAckRearm()
    {
        RequireGuestAck();
        RequireNoGuestSwitch();
        if (!guestRestored || !coldRestoreBoundary || pointer != 0 || status != 0)
            throw new InvalidOperationException("Guest re-arm requires the cold empty-restore boundary before execution or Begin.");
        for (int i = 0; i < slots!.Length; i++)
            if (slots[i].Dirty)
                throw new InvalidOperationException($"Drive {(char)('C' + i)}: is dirty at guest re-arm.");
    }

    /// <summary>One cold restore boundary, after fresh owner-selected media adoption, before execution.</summary>
    public void RearmRestoredGuestAckSession(Guid sessionNonce)
    {
        ValidateRestoredGuestAckRearm();
        if (sessionNonce == Guid.Empty || sessionNonce == lastGuestSession)
            throw new ArgumentException("A fresh nonempty host session is required.", nameof(sessionNonce));
        guestSession = lastGuestSession = sessionNonce;
        coldRestoreBoundary = false;
        completedGuestCooperation = false;
        uncooperated = true;
        guestRestored = false; // Only this authorized cold seam clears restored ineligibility.
        // Reset/Bind may stage again, but neither clears Uncooperated or grants Save admission.
    }

    private void RequireRestoredCooperation()
    {
        VerifyOwner();
        if (uncooperated) throw new InvalidOperationException("Uncooperated restored binding requires a post-re-arm guest ACK cycle.");
    }

    /// <summary>Session snapshot admission only; never gates per-drive media persistence.</summary>
    public void ValidateSessionSnapshotSave()
    {
        RequireGuestAck();
        for (int i = 0; i < slots!.Length; i++)
            if (slots[i].Dirty)
                throw new InvalidOperationException($"Session snapshot Save refused: drive {(char)('C' + i)}: is dirty.");
        if (guestPhase is GuestPhase.Requested or GuestPhase.Held or GuestPhase.CommittedHeld or GuestPhase.CancelledHeld)
            throw new InvalidOperationException("Session snapshot Save refused: a guest switch is staged or held.");
        RequireRestoredCooperation();
        if (!completedGuestCooperation)
            throw new InvalidOperationException("Session snapshot Save requires an immediately preceding guest cooperation cycle.");
    }

    /// <summary>Called immediately before an actual guest instruction, not during paused queue pumping.</summary>
    public void NotifyGuestInstruction()
    {
        VerifyOwner();
        coldRestoreBoundary = false;
        completedGuestCooperation = false;
    }
}
