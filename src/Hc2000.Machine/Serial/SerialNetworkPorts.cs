using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Serial;

/// <summary>Injected logical lines, mutated only on the machine's emulation thread. No host I/O.</summary>
public interface ISerialPeer
{
    bool Rx { get; }
    bool Dtr { get; }
    void Write(bool tx, bool cts, long masterTick);
}

public interface INetworkPeer
{
    bool Input { get; }
    void Write(bool select, bool sync, long masterTick);
}

/// <summary>
/// Spec04 F7/EF latch seam. Call at absolute master timestamps (not CPU T-states).
/// The board bit-bangs; it has no UART, automatic baud, or byte buffering.
/// WAIT is the asserted logical level of EF b5, not an invented network delay.
/// </summary>
public sealed class SerialNetworkPorts
{
    private readonly DeviceScheduler scheduler;
    private readonly ISerialPeer? serial;
    private readonly INetworkPeer? network;

    public SerialNetworkPorts(DeviceScheduler scheduler, ISerialPeer? serial = null, INetworkPeer? network = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        this.scheduler = scheduler;
        this.serial = serial;
        this.network = network;
        Publish(); // Initial zero latches are synchronous, never an epoch-zero event.
    }

    public byte F7Latch { get; private set; }
    public byte EfLatch { get; private set; }
    public bool WaitAsserted => (EfLatch & 0x20) != 0;

    public byte ReadF7(long masterTick)
    {
        scheduler.AdvanceTo(masterTick);
        // Undriven bits pull high; disconnected RX/NET idle high.
        return (byte)(0x7e | ((serial?.Rx ?? true) ? 0x80 : 0) | ((network?.Input ?? true) ? 1 : 0));
    }

    public byte ReadEf(long masterTick)
    {
        scheduler.AdvanceTo(masterTick);
        return (byte)(0xf7 | ((serial?.Dtr ?? false) ? 8 : 0));
    }

    public void WriteF7(byte value, long masterTick)
    {
        scheduler.AdvanceTo(masterTick);
        F7Latch = value;
        Publish();
    }

    public void WriteEf(byte value, long masterTick)
    {
        scheduler.AdvanceTo(masterTick);
        EfLatch = value;
        Publish();
    }

    public void Reset(long masterTick)
    {
        scheduler.AdvanceTo(masterTick);
        F7Latch = EfLatch = 0;
        Publish();
    }

    private void Publish()
    {
        serial?.Write((F7Latch & 1) != 0, (EfLatch & 0x10) != 0, scheduler.Now);
        network?.Write((F7Latch & 1) != 0, WaitAsserted, scheduler.Now);
    }
}
