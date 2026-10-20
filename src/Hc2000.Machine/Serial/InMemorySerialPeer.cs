using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Serial;

public sealed record SerialByte(long MasterTick, byte Value);
public sealed record SerialOutput(long MasterTick, bool Tx, bool Cts);

/// <summary>
/// Pure offline 8N1 peer, with explicit baud (a peer convention, not HC2000 hardware).
/// Samples guest TX at bit centres, rejects bad start/stop bits. CTS gates new inbound
/// frames, never truncates an active frame. All deadlines use independent rational master time.
/// </summary>
public sealed class InMemorySerialPeer : ISerialPeer
{
    private const int Send = 1, Sample = 2;
    private readonly DeviceScheduler scheduler;
    private readonly int deviceId;
    private readonly long masterHz, baud;
    private readonly Queue<byte> incoming = new();
    private readonly List<SerialByte> received = new();
    private readonly List<SerialOutput> outputs = new();
    private RationalClock? sendClock, sampleClock;
    private bool tx, cts, sending, sampling; // Match initial zero board latches; no fabricated start edge.
    private byte sendingByte, sampledByte;
    private int sendBit, sampleBit;
    public bool Rx { get; private set; } = true;
    public bool Dtr => true;
    public int FramingErrors { get; private set; }
    public IReadOnlyList<SerialByte> Received => received.AsReadOnly();
    public IReadOnlyList<SerialOutput> Outputs => outputs.AsReadOnly();
    public int QueuedBytes => incoming.Count;

    public InMemorySerialPeer(DeviceScheduler scheduler, int deviceId, long masterHz, long baud)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(masterHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baud);
        // Every half-bit must have a distinct scheduler deadline.
        if ((Int128)baud * 2 > masterHz) throw new ArgumentOutOfRangeException(nameof(baud));
        this.scheduler = scheduler;
        this.deviceId = deviceId;
        this.masterHz = masterHz;
        this.baud = baud;
        scheduler.RegisterDevice(deviceId, OnEvent);
    }

    /// <summary>Emulation-thread ingress only; first edge is strictly after Now.</summary>
    public void QueueReceive(byte value)
    {
        incoming.Enqueue(value);
        TryStartSend();
    }

    public void Write(bool tx, bool cts, long masterTick)
    {
        if (masterTick != scheduler.Now) throw new ArgumentException("Synchronize scheduler before peer writes.", nameof(masterTick));
        if (this.tx != tx || this.cts != cts) outputs.Add(new(masterTick, tx, cts));
        bool falling = this.tx && !tx;
        this.tx = tx;
        this.cts = cts;
        if (falling && !sampling)
        {
            sampling = true;
            sampledByte = 0;
            sampleBit = 0;
            sampleClock = new(masterTick, masterHz, checked(baud * 2));
            Schedule(sampleClock.Advance(), Sample, EventPhase.Controller);
        }
        TryStartSend();
    }

    /// <summary>Controller-only reset: cancels latent work without changing global time.</summary>
    public void Reset()
    {
        scheduler.InvalidateDevice(deviceId);
        incoming.Clear();
        received.Clear();
        outputs.Clear();
        Rx = true;
        tx = cts = sending = sampling = false;
        sendClock = sampleClock = null;
        sendingByte = sampledByte = 0;
        sendBit = sampleBit = FramingErrors = 0;
    }

    private void Schedule(long tick, int kind, EventPhase phase) => scheduler.Schedule(
        new(tick, phase, deviceId, kind, scheduler.Generation(deviceId)));

    private void TryStartSend()
    {
        if (sending || !cts || incoming.Count == 0) return;
        sending = true;
        sendingByte = incoming.Dequeue();
        sendBit = 0;
        sendClock = new(checked(scheduler.Now + 1), masterHz, baud);
        Schedule(checked(scheduler.Now + 1), Send, EventPhase.SignalEdge);
    }

    private void OnEvent(DeviceEvent item)
    {
        if (item.Kind == Sample)
        {
            if (sampleBit == 0 && tx) { sampling = false; return; } // false start
            if (sampleBit is >= 1 and <= 8 && tx) sampledByte |= (byte)(1 << (sampleBit - 1));
            if (sampleBit == 9)
            {
                if (tx) received.Add(new(item.DueTick, sampledByte));
                else FramingErrors = checked(FramingErrors + 1);
                sampling = false;
                return;
            }
            sampleBit++;
            Schedule(sampleClock!.Advance(2), Sample, EventPhase.Controller);
            return;
        }

        if (sendBit == 10)
        {
            if (cts && incoming.Count > 0)
            {
                sendingByte = incoming.Dequeue();
                sendBit = 0; // contiguous frames retain the unrounded phase
            }
            else { sending = false; sendClock = null; return; }
        }
        Rx = sendBit switch { 0 => false, 9 => true, _ => (sendingByte & (1 << (sendBit - 1))) != 0 };
        sendBit++;
        Schedule(sendClock!.Advance(), Send, EventPhase.SignalEdge);
    }
}
