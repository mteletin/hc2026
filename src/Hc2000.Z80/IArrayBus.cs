namespace Hc2000.Z80;

/// <summary>
/// Array-backed fast path for the Z80. When a bus implements this interface the
/// CPU reads and writes the memory array directly instead of going through the
/// <see cref="IBus.Read"/>/<see cref="IBus.Write"/> virtual dispatch.
/// </summary>
public interface IArrayBus : IBus
{
    /// <summary>Backing memory array, 65536 bytes.</summary>
    byte[] Memory { get; }

    /// <summary>
    /// <c>false</c> when the bus has no contended cycles (the common case for
    /// ROM-only or single-CPU emulators). The CPU skips all
    /// <see cref="IBus.Contend"/> calls for such buses.
    /// </summary>
    bool Contends { get; }
}
