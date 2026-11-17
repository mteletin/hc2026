using System.Buffers.Binary;

namespace Hc2000.Machine.Expansion;

/// <summary>One bounded X2 upload chain on the shared physical DMA. Plane bytes remain arena-owned.</summary>
internal enum X2UploadPhase : byte { Idle = 0, Open = 1, Complete = 2, Cancelled = 3 }

/// <summary>One bounded X2 upload chain on the shared physical DMA. Plane bytes remain arena-owned.</summary>
internal sealed class ExpansionX2Upload : IX0DmaEndpoint
{
    private readonly Memory<byte> target;
    private readonly Func<bool>? canBegin;
    private readonly Action<byte[]>? onConsoleData;
    private X2UploadPhase phase;
    private int start, total, progress, segment;
    private bool reserved;
    private bool targetIsConsole;

    internal ExpansionX2Upload(ushort cardId, Memory<byte> target, Func<bool>? canBegin = null, Action<byte[]>? onConsoleData = null)
    {
        if (cardId == 0 || target.Length != ExpansionX2Surface.Width * ExpansionX2Surface.Height)
            throw new ArgumentException("Invalid X2 upload endpoint.");
        Identity = new(cardId, 1);
        this.target = target;
        this.canBegin = canBegin;
        this.onConsoleData = onConsoleData;
    }

    public X0DmaCardIdentity Identity { get; }
    public bool AllowsOfflineSnapshot => true;
    internal bool CanPresent => phase is X2UploadPhase.Idle or X2UploadPhase.Complete;
    internal bool IsQuiescent => phase is not X2UploadPhase.Open && !reserved;
    internal X2UploadPhase Phase => phase;
    internal int Start => start;
    internal int Total => total;
    internal int Progress => progress;
    internal int Segment => segment;
    internal bool Reserved => reserved;

    internal bool Begin(int offset, int length)
    {
        if (phase == X2UploadPhase.Open || reserved || canBegin is not null && !canBegin() ||
            offset < 0 || length <= 0 || offset > target.Length - length) return false;
        if (phase == X2UploadPhase.Cancelled && (offset != 0 || length != target.Length))
            return false;
        targetIsConsole = false;
        start = offset; total = length; progress = 0; segment = 0; phase = X2UploadPhase.Open;
        return true;
    }

    internal bool BeginConsole(int length)
    {
        if (phase == X2UploadPhase.Open || reserved || canBegin is not null && !canBegin() ||
            length <= 0 || length > 65536) return false;
        targetIsConsole = true;
        start = 0; total = length; progress = 0; segment = 0; phase = X2UploadPhase.Open;
        return true;
    }

    internal bool PrepareSegment(int length)
    {
        if (phase != X2UploadPhase.Open || reserved || segment != 0 || length <= 0 ||
            length > ExpansionDma.PageBytes || length > total - progress) return false;
        segment = length;
        return true;
    }

    internal void CancelPreparation()
    {
        if (!reserved) segment = 0;
    }

    internal bool Abort()
    {
        if (phase != X2UploadPhase.Open || reserved) return false;
        phase = X2UploadPhase.Cancelled; segment = 0;
        return true;
    }

    internal void ResetFenceAfterClear()
    {
        if (phase == X2UploadPhase.Cancelled)
        {
            phase = X2UploadPhase.Idle;
            start = total = progress = segment = 0;
            reserved = false;
        }
    }

    internal void Reset()
    {
        if (phase == X2UploadPhase.Open || reserved)
        {
            reserved = false;
            segment = 0;
            phase = X2UploadPhase.Cancelled;
        }
    }

    public bool SupportsDirection(byte direction) => direction == X0DmaDirection.RamToCard;

    public bool TryAdmit(uint length)
    {
        if (phase != X2UploadPhase.Open || reserved || segment == 0 || length != (uint)segment) return false;
        reserved = true;
        return true;
    }

    public byte[] CaptureSource(uint length) => throw new ArgumentException("X2 upload is RAM-to-card only.", nameof(length));

    public void Commit(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!reserved || phase != X2UploadPhase.Open || data.Length != segment) throw new InvalidOperationException("Invalid X2 upload commit.");
        if (targetIsConsole)
        {
            onConsoleData?.Invoke(data);
        }
        else
        {
            data.CopyTo(target.Span.Slice(start + progress, segment));
        }
        progress += segment; segment = 0; reserved = false;
        if (progress == total) phase = X2UploadPhase.Complete;
    }

    public void CancelReservation()
    {
        if (!reserved) throw new InvalidOperationException("No X2 upload reservation.");
        reserved = false; segment = 0; phase = X2UploadPhase.Cancelled;
    }

    public byte[] CaptureSnapshotState()
    {
        var state = new byte[20];
        state[0] = 1; state[1] = (byte)phase; state[2] = reserved ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(4), start);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(8), total);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(12), progress);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(16), segment);
        return state;
    }

    public bool CanRestoreSnapshotState(byte[] state) => TryRead(state, out _, out _, out _, out _, out _, out _);

    public bool CanRestoreTransfer(byte[] state, byte direction, uint count, bool pending) =>
        TryRead(state, out _, out _, out _, out _, out var savedSegment, out var savedReserved) &&
        savedReserved == pending && (!pending || SupportsDirection(direction) && count == (uint)savedSegment);

    internal bool CanRestoreWithEngine(byte[] state, X2EngineMetadata engine)
    {
        if (!TryRead(state, out var savedPhase, out _, out _, out _, out _, out _)) return false;
        if (savedPhase == X2UploadPhase.Open && (engine.State == X2CommandState.Pending || engine.PresentPending)) return false;
        if (savedPhase is not (X2UploadPhase.Idle or X2UploadPhase.Complete) &&
            (engine.PresentPending || engine.State == X2CommandState.Pending && engine.Active.Kind == X2CommandKind.Present))
            return false;
        return true;
    }

    public void RestoreSnapshotState(byte[] state)
    {
        if (!TryRead(state, out var restoredPhase, out var restoredStart, out var restoredTotal,
            out var restoredProgress, out var restoredSegment, out var restoredReserved))
            throw new ArgumentException("Invalid X2 upload state.", nameof(state));
        phase = restoredPhase; start = restoredStart; total = restoredTotal; progress = restoredProgress;
        segment = restoredSegment; reserved = restoredReserved;
    }

    internal bool TryRead(byte[]? state, out X2UploadPhase savedPhase, out int savedStart, out int savedTotal,
        out int savedProgress, out int savedSegment, out bool savedReserved)
    {
        savedPhase = X2UploadPhase.Idle; savedStart = savedTotal = savedProgress = savedSegment = 0; savedReserved = false;
        if (state is not { Length: 20 } || state[0] != 1 || state[1] > (byte)X2UploadPhase.Cancelled || state[2] > 1 || state[3] != 0) return false;
        savedPhase = (X2UploadPhase)state[1]; savedReserved = state[2] != 0;
        savedStart = BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(4));
        savedTotal = BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(8));
        savedProgress = BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(12));
        savedSegment = BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(16));
        if (savedPhase == X2UploadPhase.Idle)
            return savedStart == 0 && savedTotal == 0 && savedProgress == 0 && savedSegment == 0 && !savedReserved;
        if (savedStart < 0 || savedTotal <= 0 || savedTotal > target.Length || savedStart > target.Length - savedTotal ||
            savedProgress < 0 || savedProgress > savedTotal || savedSegment < 0 || savedSegment > ExpansionDma.PageBytes ||
            savedSegment > savedTotal - savedProgress) return false;
        if (savedPhase == X2UploadPhase.Open)
            return savedProgress < savedTotal && (!savedReserved || savedSegment > 0);
        return !savedReserved && savedSegment == 0 &&
            (savedPhase == X2UploadPhase.Complete ? savedProgress == savedTotal : savedProgress < savedTotal);
    }
}
