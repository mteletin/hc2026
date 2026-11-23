namespace Hc2000.Machine.Expansion;

/// <summary>The expansion row's interrupt sources (design contract 2/3): one machine INT source for the
/// whole row, a bounded FIFO of pending sources that the main Z80 drains by reading the 7F9D status/ack
/// register until it returns zero. The register accumulates sources regardless of the enable bit; the
/// enable bit only gates the row's contribution to the machine INT line, and acceptance stays gated by
/// the Z80's own IFF1/EI, so a source raised while EI is off waits in the queue like the frame-interrupt
/// line it joins (the frame-presented entry is the VGA presentation fence, not the 50/60 Hz frame
/// interrupt).</summary>
public enum ExpansionRowInterruptSource : byte
{
    UnitDone = 1,
    UnitMailboxWait = 2,
    DmaComplete = 3,
    FramePresented = 4,
}

public sealed partial class ExpansionRowInterrupt
{
    private const int Capacity = 256;
    private readonly Queue<ushort> queue = new();
    private bool enabled;

    /// <summary>Whether the row contributes to the machine INT line: the enable bit gates the line,
    /// the pending queue does not.</summary>
    public bool LineAsserted => enabled && queue.Count > 0;

    /// <summary>How many sources are waiting; zero means the register reads back zero.</summary>
    public int Pending => queue.Count;

    /// <summary>Raised when the machine INT line transitions (asserting or deasserting), so the
    /// composition can update the Z80's int line from the same event the frame clock uses.</summary>
    public event Action? LineChanged;

    /// <summary>Enable bit (row controller verbs 6/7, main-only): gates the line only; a queue that
    /// fills while disabled asserts the line the moment the bit is set.</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            LineChanged?.Invoke();
        }
    }

    /// <summary>Record a pending source and assert the line if the enable bit allows. Ids 1-16 name
    /// the unit (unit-done / unit-mailbox-wait) or the X0 channel sequence number (dma-complete);
    /// frame-presented carries id 0. The queue is bounded and overflow drops the oldest: the row never
    /// crashes on a flood, and every drop is a source the main would have drained had it kept up.</summary>
    public void Raise(ExpansionRowInterruptSource source, ushort id)
    {
        if (source is < ExpansionRowInterruptSource.UnitDone or > ExpansionRowInterruptSource.FramePresented ||
            id > 16)
            throw new ArgumentException("The row interrupt source or id is outside the register.", nameof(source));
        if (queue.Count == Capacity) queue.Dequeue();
        queue.Enqueue(checked((ushort)(((byte)source << 5) | id)));
        LineChanged?.Invoke();
    }

    /// <summary>7F9D read: return the next pending source and ack it (remove it from the queue);
    /// zero when drained. The read's bits are the source byte alone: bits 0-4 the id (1-16 for the unit
    /// and dma classes, 0 for frame-presented), bits 5-6 the source class, bit 7 reserved zero; the
    /// 16-bit port returns that byte in the low half and zero in the high. Reading until zero drains
    /// the register.</summary>
    public ushort Next()
    {
        if (queue.Count == 0) return 0;
        var source = queue.Dequeue();
        LineChanged?.Invoke();
        return source;
    }

    /// <summary>Reset the row: the queue and the enable bit go to zero, the line deasserts.</summary>
    public void Reset()
    {
        var wasAsserted = LineAsserted;
        queue.Clear();
        enabled = false;
        if (wasAsserted) LineChanged?.Invoke();
    }
}
