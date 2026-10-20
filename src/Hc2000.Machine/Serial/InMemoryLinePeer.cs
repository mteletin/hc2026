using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Serial;

public sealed record PeerOutput(long MasterTick, bool Data, bool Handshake);

/// <summary>
/// Offline raw bit-line peer for serial or ZX-Net-style tests. Explicit input timestamps
/// belong to this machine epoch. A host/independent machine bridge is NOT implemented.
/// Handshake means DTR on input, CTS (serial) or sync (network) on output.
/// </summary>
public sealed class InMemoryLinePeer : ISerialPeer, INetworkPeer
{
    private readonly DeviceScheduler scheduler;
    private readonly int deviceId;
    private readonly List<PeerOutput> outputs = new();
    public bool Input { get; private set; } = true;
    public bool Rx => Input;
    public bool Dtr { get; private set; }
    public IReadOnlyList<PeerOutput> Outputs => outputs.AsReadOnly();

    public InMemoryLinePeer(DeviceScheduler scheduler, int deviceId)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        this.scheduler = scheduler;
        this.deviceId = deviceId;
        scheduler.RegisterDevice(deviceId, item =>
        {
            Input = (item.Payload & 1) != 0;
            Dtr = (item.Payload & 2) != 0;
        });
    }

    public void QueueInput(long masterTick, bool data, bool dtr = false) => scheduler.Schedule(
        new(masterTick, EventPhase.ExternalInput, deviceId, 0, scheduler.Generation(deviceId),
            (data ? 1 : 0) | (dtr ? 2 : 0)));

    public void Write(bool data, bool handshake, long masterTick)
    {
        if (masterTick != scheduler.Now) throw new ArgumentException("Synchronize scheduler before peer writes.", nameof(masterTick));
        if (outputs.Count == 0 || outputs[^1].Data != data || outputs[^1].Handshake != handshake)
            outputs.Add(new(masterTick, data, handshake));
    }

    public void Reset()
    {
        scheduler.InvalidateDevice(deviceId);
        Input = true;
        Dtr = false;
        outputs.Clear();
    }
}
