namespace Hc2000.Machine.Io;

[Flags]
public enum PortAccess { Read = 1, Write = 2, ReadWrite = Read | Write }

public enum CpuPortRole { Main, Compute, Driver }

/// <summary>Host-owned identity, not a guest selector or a permission capability.</summary>
public readonly record struct CpuPortOrigin(uint UnitId, ulong Epoch, CpuPortRole Role);

/// <summary>
/// Pure, bounded owner-thread admission. Implementations validate current identity,
/// epoch, role, direction and endpoint ownership from host-owned facts on every call.
/// </summary>
public interface IPortOriginPolicy
{
    bool Allows(in CpuPortOrigin origin, ushort port, PortAccess access);
}

/// <summary>An immutable host-origin binding to one decoder, with no raw endpoint access.</summary>
public sealed class CpuPortView
{
    private readonly PortDecoder decoder;
    private readonly CpuPortOrigin origin;

    internal CpuPortView(PortDecoder decoder, CpuPortOrigin origin)
    {
        this.decoder = decoder;
        this.origin = origin;
    }

    internal CpuPortView BindTo(PortDecoder ports)
    {
        if (!ReferenceEquals(decoder, ports))
            throw new ArgumentException("Port view belongs to another decoder.", nameof(ports));
        return this;
    }

    public byte Read(ushort port, long masterTick, bool m1 = false) =>
        decoder.Read(in origin, port, masterTick, m1);

    public void Write(ushort port, byte value, long masterTick, bool m1 = false) =>
        decoder.Write(in origin, port, value, masterTick, m1);
}

/// <summary>Called on the emulation thread after the caller drains device time to masterTick.</summary>
public interface IPortDevice
{
    byte Read(ushort port, long masterTick);
    void Write(ushort port, byte value, long masterTick);
}

public sealed class PortDevice(Func<ushort, long, byte>? read = null,
    Action<ushort, byte, long>? write = null) : IPortDevice
{
    public byte Read(ushort port, long masterTick) => read?.Invoke(port, masterTick) ?? 0xff;
    public void Write(ushort port, byte value, long masterTick) => write?.Invoke(port, value, masterTick);
}

public sealed record PortMapping(ushort Match, ushort Mask, IPortDevice Device, PortAccess Access);

/// <summary>Profile-supplied partial decode. Directional overlaps are construction errors, not priority rules.</summary>
public sealed class PortDecoder
{
    private readonly PortMapping[] mappings;
    private readonly IPortOriginPolicy policy;
    private readonly CpuPortOrigin mainOrigin;

    public PortDecoder(IEnumerable<PortMapping> mappings)
        : this(mappings, new LegacyMainPolicy(), new CpuPortOrigin(0, 0, CpuPortRole.Main))
    {
    }

    public PortDecoder(IEnumerable<PortMapping> mappings, IPortOriginPolicy policy, CpuPortOrigin mainOrigin)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(policy);
        this.policy = policy;
        this.mainOrigin = mainOrigin;
        this.mappings = mappings.ToArray();
        for (var i = 0; i < this.mappings.Length; i++)
        {
            var row = this.mappings[i];
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(row.Device);
            if ((row.Match & ~row.Mask) != 0 || row.Access is < PortAccess.Read or > PortAccess.ReadWrite)
                throw new ArgumentException("Invalid port match/mask/access.", nameof(mappings));
            for (var j = 0; j < i; j++)
            {
                var other = this.mappings[j];
                if ((row.Access & other.Access) != 0 &&
                    ((row.Match ^ other.Match) & row.Mask & other.Mask) == 0)
                    throw new ArgumentException("Overlapping port mappings in the same direction.", nameof(mappings));
            }
        }
    }

    public CpuPortView CreateView(CpuPortOrigin origin) => new(this, origin);

    public byte Read(ushort port, long masterTick, bool m1 = false) =>
        Read(in mainOrigin, port, masterTick, m1);

    internal byte Read(in CpuPortOrigin origin, ushort port, long masterTick, bool m1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(masterTick);
        if (!m1)
            foreach (var row in mappings)
                if ((row.Access & PortAccess.Read) != 0 && (port & row.Mask) == row.Match)
                {
                    if (policy.Allows(in origin, port, PortAccess.Read))
                    {
                        // Forward the exact admitted origin exactly once; never fall back to the
                        // legacy overload after an origin-aware delivery (see IOriginPortDevice).
                        if (row.Device is IOriginPortDevice originDevice)
                            return originDevice.Read(in origin, port, masterTick);
                        return row.Device.Read(port, masterTick);
                    }
                    return (byte)0xff;
                }
        return 0xff;
    }

    public void Write(ushort port, byte value, long masterTick, bool m1 = false) =>
        Write(in mainOrigin, port, value, masterTick, m1);

    internal void Write(in CpuPortOrigin origin, ushort port, byte value, long masterTick, bool m1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(masterTick);
        if (m1) return;
        foreach (var row in mappings)
            if ((row.Access & PortAccess.Write) != 0 && (port & row.Mask) == row.Match)
            {
                if (policy.Allows(in origin, port, PortAccess.Write))
                {
                    // Forward the exact admitted origin exactly once; never fall back to the
                    // legacy overload after an origin-aware delivery (see IOriginPortDevice).
                    if (row.Device is IOriginPortDevice originDevice)
                        originDevice.Write(in origin, port, value, masterTick);
                    else
                        row.Device.Write(port, value, masterTick);
                }
                return;
            }
    }

    /// <summary>INTA floats high; it never invokes port devices.</summary>
    public static byte IntVector() => 0xff;

    private sealed class LegacyMainPolicy : IPortOriginPolicy
    {
        public bool Allows(in CpuPortOrigin origin, ushort port, PortAccess access) =>
            origin == new CpuPortOrigin(0, 0, CpuPortRole.Main);
    }
}
