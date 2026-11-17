namespace Hc2000.Machine.Expansion;

internal sealed record X3FifoState(int Version, ulong Generation, byte[] Contents, int Reserved);

/// <summary>A channel's bounded sample FIFO. The card must cancel any shared-DMA reservation
/// before resetting a generation; reservation commits are atomic at the controller frontier.</summary>
internal sealed class ExpansionX3Fifo
{
    internal const int Capacity = 16384;
    private readonly byte[] data = new byte[Capacity];
    private int head, count, reserved;

    internal ulong Generation { get; private set; } = 1;
    internal int Count => count;
    internal int Free => Capacity - count - reserved;
    internal int Reserved => reserved;

    internal bool TryReserve(ulong generation, int length)
    {
        if (generation != Generation || reserved != 0 || length <= 0 ||
            length > ExpansionX3Sample.MaximumSegment || length > Capacity - count) return false;
        reserved = length;
        return true;
    }

    internal void Commit(ulong generation, ReadOnlySpan<byte> bytes)
    {
        if (generation != Generation || reserved == 0 || bytes.Length != reserved)
            throw new InvalidOperationException("Stale or mismatched X3 FIFO reservation.");
        var tail = (head + count) % Capacity;
        var first = Math.Min(bytes.Length, Capacity - tail);
        bytes[..first].CopyTo(data.AsSpan(tail));
        bytes[first..].CopyTo(data);
        count += bytes.Length;
        reserved = 0;
    }

    internal bool Cancel(ulong generation)
    {
        if (generation != Generation || reserved == 0) return false;
        reserved = 0;
        return true;
    }

    internal bool TryRead(out byte sample)
    {
        sample = 0;
        if (count == 0) return false;
        sample = data[head];
        head = (head + 1) % Capacity;
        count--;
        return true;
    }

    internal bool Reset()
    {
        if (reserved != 0 || Generation == ulong.MaxValue) return false;
        Generation++;
        head = count = 0;
        return true;
    }

    internal X3FifoState Capture()
    {
        var bytes = new byte[count];
        var first = Math.Min(count, Capacity - head);
        data.AsSpan(head, first).CopyTo(bytes);
        data.AsSpan(0, count - first).CopyTo(bytes.AsSpan(first));
        return new(1, Generation, bytes, reserved);
    }

    internal static bool ValidState(X3FifoState? state) => state is
        { Version: 1, Generation: > 0, Contents: not null, Reserved: >= 0 and <= ExpansionX3Sample.MaximumSegment } &&
        state.Contents.Length <= Capacity && state.Reserved <= Capacity - state.Contents.Length;

    internal void Restore(X3FifoState state)
    {
        if (!ValidState(state)) throw new ArgumentException("Invalid X3 FIFO state.", nameof(state));
        state.Contents.CopyTo(data, 0);
        head = 0;
        count = state.Contents.Length;
        reserved = state.Reserved;
        Generation = state.Generation;
    }
}
