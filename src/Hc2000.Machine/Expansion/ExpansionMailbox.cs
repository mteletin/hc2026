using System.Buffers.Binary;

namespace Hc2000.Machine.Expansion;

/// <summary>Root receipt codes; the local mailbox's narrower errors are encoded separately.</summary>
public enum ExpansionResult : byte
{
    Accepted, Busy, StaleEpoch, InvalidFrame, InvalidState, DeniedOrigin, Unsupported, ResourceLimit, NotFound
}

/// <summary>A plain record of requested control, applied by the owner at true instruction retirement.</summary>
public enum ExpansionMailboxEffect : byte { None, Wait, Idle }

/// <summary>One compute's bounded message state. No CPU, peer, controller or scheduler references.</summary>
public sealed partial class ExpansionMailbox
{
    public const int PayloadBytes = 64;
    public const int FrameBytes = 73;
    private readonly byte[] inbox = new byte[FrameBytes];
    private readonly byte[] outbox = new byte[FrameBytes];
    private readonly byte[] staging = new byte[FrameBytes];
    private int inboxCursor;
    private int stagingCount;
    private bool collecting;
    private bool overflow;
    // Local error encoding is not the root result enum (InvalidFrame=3 in both by coincidence).
    private byte lastError;
    public bool HasInbox { get; private set; }
    public bool HasOutbox { get; private set; }
    internal bool HasWork => HasInbox || HasOutbox || collecting;

    public byte ReadStatus(bool waiting = false, bool serviceIdle = false) => (byte)(
        (HasInbox ? 1 : 0) | (HasOutbox ? 0 : 2) | (collecting ? 4 : 0) |
        (lastError << 3) | (waiting ? 0x40 : 0) | (serviceIdle ? 0x80 : 0));

    public ExpansionResult SendInbox(ReadOnlySpan<byte> frame)
    {
        if (!ValidFrame(frame)) return ExpansionResult.InvalidFrame;
        if (HasInbox) return ExpansionResult.Busy;
        frame.CopyTo(inbox);
        inboxCursor = 0;
        HasInbox = true;
        return ExpansionResult.Accepted;
    }

    public ExpansionResult ReadOutbox(out byte[]? frame)
    {
        frame = HasOutbox ? outbox.ToArray() : null;
        return HasOutbox ? ExpansionResult.Accepted : ExpansionResult.Busy;
    }

    public ExpansionResult AcknowledgeOutbox(ulong requestId)
    {
        if (requestId == 0) return ExpansionResult.InvalidFrame;
        if (!HasOutbox || BinaryPrimitives.ReadUInt64LittleEndian(outbox.AsSpan(1)) != requestId)
            return ExpansionResult.InvalidState;
        HasOutbox = false;
        Array.Clear(outbox);
        return ExpansionResult.Accepted;
    }

    public byte ReadLocalByte() => HasInbox && inboxCursor < FrameBytes ? inbox[inboxCursor++] : (byte)0xff;

    public void WriteLocalByte(byte value)
    {
        if (!collecting) { lastError = 3; return; }
        if (stagingCount == FrameBytes)
        {
            overflow = true;
            lastError = 3;
            return;
        }
        staging[stagingCount++] = value;
    }

    public ExpansionMailboxEffect WriteLocalControl(byte command)
    {
        switch (command)
        {
            case 1: // TX_BEGIN cannot silently abort an existing partial/retryable frame.
                if (collecting) { lastError = 1; break; }
                ClearStaging();
                collecting = true;
                lastError = 0;
                break;
            case 2:
                if (!collecting || overflow || stagingCount != FrameBytes || !ValidFrame(staging))
                { lastError = 3; break; }
                if (HasOutbox) { lastError = 1; break; }
                staging.CopyTo(outbox, 0);
                HasOutbox = true;
                ClearStaging();
                lastError = 0;
                break;
            case 3:
                ClearStaging();
                lastError = 0;
                break;
            case 4:
                inboxCursor = 0;
                lastError = 0;
                break;
            case 5:
                HasInbox = false;
                inboxCursor = 0;
                Array.Clear(inbox);
                lastError = 0;
                break;
            case 6:
                lastError = 0;
                return ExpansionMailboxEffect.Wait;
            case 7:
                lastError = 0;
                return ExpansionMailboxEffect.Idle;
            default:
                lastError = 3;
                break;
        }
        return ExpansionMailboxEffect.None;
    }

    public void Clear()
    {
        HasInbox = HasOutbox = false;
        inboxCursor = 0;
        lastError = 0;
        Array.Clear(inbox);
        Array.Clear(outbox);
        ClearStaging();
    }

    private void ClearStaging()
    {
        collecting = overflow = false;
        stagingCount = 0;
        Array.Clear(staging);
    }

    private static bool ValidFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameBytes || frame[0] > PayloadBytes ||
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Slice(1, 8)) == 0) return false;
        for (var index = 9 + frame[0]; index < FrameBytes; index++)
            if (frame[index] != 0) return false;
        return true;
    }
}
