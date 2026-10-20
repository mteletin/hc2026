namespace Hc2000.Z80.Frontier;

public enum FrontierEffectKind : byte { Internal, M1Read, MemoryRead, MemoryWrite, PortRead, PortWrite, InterruptAck }

/// <summary>A bus effect to be performed once by the machine owner, never by the CPU.</summary>
public readonly record struct FrontierEffect(long AtCpuTState, FrontierEffectKind Kind, ushort Address,
    byte WriteValue = 0, bool InstructionStart = false);

public readonly record struct FrontierCommit(bool Retired);
