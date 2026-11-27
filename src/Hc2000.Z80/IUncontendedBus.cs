namespace Hc2000.Z80;

/// <summary>
/// A bus without contended cycles that is not array-backed (the expansion driver's relaxed main quantum): the CPU skips
/// every <see cref="IBus.Contend"/> call, as it does for an <see cref="IArrayBus"/> whose <c>Contends</c> is false, and
/// still reads and writes through the bus.
/// </summary>
public interface IUncontendedBus : IBus;
