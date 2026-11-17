using Hc2000.Machine.Io;

namespace Hc2000.Machine.Timing;

/// <summary>
/// Host-local date and time port on extended machines (epic hc2000-host-clock-time).
/// Port 0x709D (in the safe 0x..9D extension family).
/// One read sequence supplies year (2 bytes, little-endian), month (1..12), day (1..31),
/// hour (0..23), minute (0..59), second (0..59), and day-of-week (0=Sunday..6=Saturday).
/// Reading byte 0 captures an atomic snapshot of the host local civil time into an internal buffer;
/// subsequent reads return bytes from that same snapshot so reads never straddle a second rollover.
/// Any write to the port forces latching a fresh snapshot and resets the read index to 0.
/// An inactivity gap (longer than 70,000 master ticks / ~20ms) also resets the read index to 0.
/// </summary>
public sealed class HostClockPort : IPortDevice
{
    public const ushort Address = 0x709d;
    public const long InactivityResetTicks = 70000; // ~20 ms at 3.5 MHz

    private readonly Func<DateTime> timeProvider;
    private readonly byte[] buffer = new byte[8];
    private int index;
    private long lastReadTick = -1;

    public HostClockPort(Func<DateTime>? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? (() => DateTime.Now);
    }

    public PortMapping Mapping() => new(Address, 0xffff, this, PortAccess.ReadWrite);

    public void Latch()
    {
        var now = timeProvider();
        var year = (ushort)Math.Clamp(now.Year, 1, 9999);
        buffer[0] = (byte)(year & 0xff);
        buffer[1] = (byte)(year >> 8);
        buffer[2] = (byte)now.Month;
        buffer[3] = (byte)now.Day;
        buffer[4] = (byte)now.Hour;
        buffer[5] = (byte)now.Minute;
        buffer[6] = (byte)now.Second;
        buffer[7] = (byte)now.DayOfWeek;
        index = 0;
    }

    public byte Read(ushort port, long masterTick)
    {
        if (lastReadTick >= 0 && masterTick - lastReadTick > InactivityResetTicks)
            index = 0;

        lastReadTick = masterTick;

        if (index == 0)
            Latch();

        var b = buffer[index];
        index = (index + 1) % buffer.Length;
        return b;
    }

    public void Write(ushort port, byte value, long masterTick)
    {
        lastReadTick = masterTick;
        Latch();
    }
}
