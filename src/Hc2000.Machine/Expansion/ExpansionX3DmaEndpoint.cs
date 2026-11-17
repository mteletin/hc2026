using System.Buffers.Binary;

namespace Hc2000.Machine.Expansion;

internal readonly record struct X3EndpointSnapshot(int Channel, X3SampleSegment Segment, ulong Generation, bool Admitted);

/// <summary>One X3 card endpoint on the shared physical DMA. A selected channel's segment is
/// staged without reserving FIFO space; the DMA admission owns the actual reservation.</summary>
internal sealed class ExpansionX3DmaEndpoint : IX0DmaEndpoint
{
    private readonly ExpansionX3Playback playback;
    private readonly Action<int, long> completed;
    private readonly Func<long> now;
    private int channel = -1;
    private X3SampleSegment segment;
    private ulong generation;
    private bool admitted;

    internal ExpansionX3DmaEndpoint(ushort cardId, ExpansionX3Playback playback, Func<long> now, Action<int, long> completed)
    {
        if (cardId == 0) throw new ArgumentOutOfRangeException(nameof(cardId));
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(completed);
        Identity = new(cardId, 1); this.playback = playback; this.now = now; this.completed = completed;
    }

    public X0DmaCardIdentity Identity { get; }
    public bool AllowsOfflineSnapshot => false;
    public bool SupportsDirection(byte direction) => direction == X0DmaDirection.RamToCard;
    internal int SelectedChannel => channel;

    internal bool Prepare(int selected, out X3SampleSegment prepared)
    {
        prepared = default;
        if (channel != -1 || !playback.PreviewRefill(selected, out var candidate, out var currentGeneration)) return false;
        channel = selected; segment = candidate; generation = currentGeneration; prepared = candidate;
        return true;
    }

    internal bool Abandon()
    {
        if (channel == -1 || admitted) return false;
        Clear();
        return true;
    }

    public bool TryAdmit(uint length)
    {
        if (channel == -1 || admitted || length != (uint)segment.Count ||
            !playback.ReserveRefill(channel, segment, generation)) return false;
        admitted = true;
        return true;
    }

    public byte[] CaptureSource(uint length) => throw new ArgumentException("X3 refills are RAM-to-card only.", nameof(length));

    public void Commit(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!admitted) throw new InvalidOperationException("No X3 DMA reservation.");
        var selected = channel;
        playback.CommitRefill(selected, generation, data);
        Clear();
        completed(selected, now());
    }

    public void CancelReservation()
    {
        if (!admitted || !playback.CancelRefill(channel, generation)) throw new InvalidOperationException("No X3 DMA reservation.");
        Clear();
    }

    // The owner validates this endpoint against the detached playback and DMA records before
    // enabling offline HCS; the endpoint alone cannot grant that cross-component authority.
    public byte[] CaptureSnapshotState()
    {
        if (channel != -1 && !admitted) throw new InvalidOperationException("X3 staging is transient.");
        var state = new byte[24]; state[0] = 1;
        if (admitted)
        {
            state[1] = 1; state[2] = (byte)channel; state[3] = segment.Ended ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(4), segment.Page);
            BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(6), segment.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(8), (uint)segment.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(12), segment.NextPosition);
            BinaryPrimitives.WriteUInt64LittleEndian(state.AsSpan(16), generation);
        }
        return state;
    }

    internal static bool Decode(byte[]? state, out X3EndpointSnapshot snapshot)
    {
        snapshot = default;
        if (state is not { Length: 24 } || state[0] != 1 || state[1] > 1) return false;
        if (state[1] == 0)
        {
            if (state.AsSpan(2).IndexOfAnyExcept((byte)0) >= 0) return false;
            snapshot = new(-1, default, 0, false); return true;
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(8));
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(6));
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(16));
        if (state[2] >= ExpansionX3Clock.ChannelCount || state[3] > 1 || count is 0 or > ExpansionX3Sample.MaximumSegment ||
            offset >= ExpansionDma.PageBytes || count + offset > ExpansionDma.PageBytes || generation == 0) return false;
        snapshot = new(state[2], new(BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(4)), offset, (int)count,
            BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(12)), state[3] != 0), generation, true);
        return true;
    }

    public bool CanRestoreSnapshotState(byte[] state) => Decode(state, out _);
    public bool CanRestoreTransfer(byte[] state, byte direction, uint count, bool pending) => Decode(state, out var decoded) &&
        decoded.Admitted == pending && (!pending || direction == X0DmaDirection.RamToCard && count == decoded.Segment.Count);
    public void RestoreSnapshotState(byte[] state)
    {
        if (!Decode(state, out var decoded)) throw new ArgumentException("Invalid X3 endpoint state.", nameof(state));
        channel = decoded.Channel; segment = decoded.Segment; generation = decoded.Generation; admitted = decoded.Admitted;
    }

    private void Clear() { channel = -1; segment = default; generation = 0; admitted = false; }
}
