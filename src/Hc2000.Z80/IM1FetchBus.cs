namespace Hc2000.Z80;

/// <summary>Optional memory-backed opcode fetch capability; the base bus remains unchanged.</summary>
public interface IM1FetchBus : IBus
{
    /// <summary>
    /// Replaces one ordinary Read at the original post-counter access boundary.
    /// instructionStart marks only the first fetch of a normal instruction;
    /// cpuTStates is the exact absolute CPU counter at this access.
    /// HALT refresh and interrupt acknowledge are excluded. Implementations must
    /// not change CPU state or add CPU cycles; the returned byte is executed.
    /// </summary>
    byte FetchM1(ushort address, bool instructionStart, long cpuTStates);
}
