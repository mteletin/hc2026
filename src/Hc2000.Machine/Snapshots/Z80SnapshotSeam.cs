using Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Snapshots;

/// <summary>Logical snapshot adapter for the composition's existing CPU at a drained boundary.
/// No stepping, ROM entry selection, RAM injection or second register file.</summary>
public sealed class Z80SnapshotSeam(Cpu cpu) : ICpuSnapshotSeam
{
    private readonly Cpu cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    public CpuLogicalState Capture()
    {
        if (cpu.EiDelayRaw is < 0 or > 255)
            throw new InvalidOperationException("CPU EI delay cannot be represented in a snapshot.");
        var r = cpu.R;
        return new(r.AF, r.BC, r.DE, r.HL, r.IX, r.IY, r.SP, r.PC,
            r.AFShadow, r.BCShadow, r.DEShadow, r.HLShadow, r.WZ, r.I, r.R, r.Q,
            r.IFF1, r.IFF2, cpu.Halted, cpu.IntLine, cpu.NmiPendingState,
            cpu.LdAirPending, cpu.NmosLdAirInterruptBug, r.IM, (byte)cpu.EiDelayRaw, cpu.TStates);
    }

    public void Restore(CpuLogicalState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Im is < 0 or > 2 || s.TStates < 0)
            throw new ArgumentException("Invalid logical CPU state.", nameof(s));
        cpu.R.AF = s.Af; cpu.R.BC = s.Bc; cpu.R.DE = s.De; cpu.R.HL = s.Hl;
        cpu.R.IX = s.Ix; cpu.R.IY = s.Iy; cpu.R.SP = s.Sp; cpu.R.PC = s.Pc;
        cpu.R.AFShadow = s.AfShadow; cpu.R.BCShadow = s.BcShadow;
        cpu.R.DEShadow = s.DeShadow; cpu.R.HLShadow = s.HlShadow;
        cpu.R.WZ = s.Wz; cpu.R.I = s.I; cpu.R.R = s.R; cpu.R.Q = s.Q;
        cpu.R.IM = (byte)s.Im; cpu.R.IFF1 = s.Iff1; cpu.R.IFF2 = s.Iff2;
        cpu.Halted = s.Halted; cpu.IntLine = s.IntLine;
        cpu.NmiPendingState = s.NmiPending; cpu.LdAirPending = s.LastLdAiOrR;
        cpu.NmosLdAirInterruptBug = s.NmosLdAirInterruptBug;
        cpu.EiDelayRaw = s.EiDelay; cpu.TStates = s.TStates;
    }
}
