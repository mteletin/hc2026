namespace Hc2000.Z80.Frontier;

/// <summary>Detached, bounded state of the nmos-z80-frontier model, version 1.</summary>
public sealed record FrontierState(int Version, Registers Registers, long TStates, bool Halted,
    bool IntLine, bool NmiPending, int EiDelay, bool LdAirPending, bool InterruptBug,
    PrefixFamily Family, bool OpcodePresent, byte Opcode, ushort Stage, byte TemporaryCount,
    byte[] Temporaries, FrontierEffect? Pending, bool Immediate, bool Refresh);
