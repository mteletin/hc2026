namespace Hc2000.Machine.Io;

/// <summary>
/// Optional additive endpoint extension: the admitted, host-bound origin is forwarded to
/// origin-aware port devices. Implementations still implement the two inherited legacy
/// <see cref="IPortDevice"/> methods; for an origin-required endpoint those methods must
/// refuse direct unscoped host use without state effects, rather than silently inventing
/// a main origin.
/// </summary>
public interface IOriginPortDevice : IPortDevice
{
    byte Read(in CpuPortOrigin origin, ushort port, long masterTick);
    void Write(in CpuPortOrigin origin, ushort port, byte value, long masterTick);
}
