using System.Collections.ObjectModel;
using Hc2000.Disasm;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;

namespace Hc2000.App.Debugging;

public enum BreakpointKind { Pc, MemoryRead, MemoryWrite, PortIn, PortOut, Paging }
public enum MemoryAccess { Read, Write }
public sealed record Breakpoint(BreakpointKind Kind, ushort Start = 0, ushort End = 65535)
{
    public bool Matches(BreakpointKind kind, ushort address) => Kind == kind && address >= Start && address <= End;
}

/// <summary>Value copy at an instruction boundary. Null means unavailable, not zero.</summary>
public sealed record RegisterSnapshot(ushort Pc, ushort Sp, ushort? Af = null, ushort? Bc = null,
    ushort? De = null, ushort? Hl = null, ushort? AfPrime = null, ushort? BcPrime = null,
    ushort? DePrime = null, ushort? HlPrime = null, ushort? Ix = null, ushort? Iy = null,
    ushort? Wz = null, byte? I = null, byte? R = null, byte? Im = null, bool? Iff1 = null,
    bool? Iff2 = null, bool? Halted = null, bool? Q = null);

public sealed record DebuggerTrigger(BreakpointKind Kind, ushort Address, byte? Value, long CpuTStates,
    PagingLatches? PreviousPaging = null, PagingLatches? Paging = null);

public sealed record DebuggerSnapshot(string Source, long CpuTStates, RegisterSnapshot Registers,
    ReadOnlyCollection<byte> Memory, ReadOnlyCollection<MemoryLocation> Locations, PagingLatches Latches,
    FdcDiagnosticSnapshot? Fdc, bool ShouldStop, DebuggerTrigger? Trigger, ReadOnlyCollection<Breakpoint> Breakpoints)
{
    public DisassemblyResult Disassemble(int start, int length, ControlFile? control = null)
    {
        if (start < 0 || start > 65535 || length < 1 || length > 65536 - start) throw new ArgumentOutOfRangeException(nameof(length));
        return new Disassembler().Disassemble(Memory.Skip(start).Take(length).ToArray(), start, control);
    }
}

/// <summary>Passive projection, owned exclusively by the emulation thread. Feed synchronized,
/// post-access events once. UI gets copied snapshots, never live components. No runtime/CPU is created here.</summary>
public sealed class DebuggerSession
{
    private readonly string source;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private ExecutionRecorder? recorder;
    private readonly List<Breakpoint> breakpoints = [];
    private DebuggerTrigger? pending;
    private bool stopped;
    private (ushort Pc, long Time)? resumeBoundary;
    private long lastTime;
    public DebuggerSnapshot? Snapshot { get; private set; }
    public ExecutionRecorder? Recorder
    {
        get => recorder;
        set { VerifyOwner(); recorder = value; }
    }

    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Debugger mutations belong to the owning emulation thread.");
    }

    public DebuggerSession(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Identify observation provenance.", nameof(source));
        this.source = source;
    }

    public void AddBreakpoint(Breakpoint breakpoint)
    {
        VerifyOwner();
        if (!Enum.IsDefined(breakpoint.Kind) || breakpoint.Start > breakpoint.End) throw new ArgumentException("Invalid breakpoint.", nameof(breakpoint));
        breakpoints.Add(breakpoint);
    }
    public void ClearBreakpoints() { VerifyOwner(); breakpoints.Clear(); }
    public void Resume()
    {
        VerifyOwner();
        resumeBoundary = stopped && Snapshot is { } s ? (s.Registers.Pc, s.CpuTStates) : null;
        pending = null; stopped = false;
    }

    private void Time(long cpuTStates)
    {
        VerifyOwner();
        if (cpuTStates < lastTime) throw new ArgumentOutOfRangeException(nameof(cpuTStates), "Ordered absolute CPU timestamps required. Create a new session on CPU reset.");
        lastTime = cpuTStates;
    }

    private void Hit(DebuggerTrigger trigger)
    {
        if (pending is null && breakpoints.Any(b => b.Matches(trigger.Kind, trigger.Address))) pending = trigger;
    }

    public void ObserveMemory(HcMemory effectiveMemory, ushort address, byte value, MemoryAccess access, long cpuTStates)
    {
        if (!Enum.IsDefined(access)) throw new ArgumentOutOfRangeException(nameof(access));
        Time(cpuTStates);
        Hit(new(access == MemoryAccess.Read ? BreakpointKind.MemoryRead : BreakpointKind.MemoryWrite, address, value, cpuTStates));
        Recorder?.Observe(effectiveMemory, address, access == MemoryAccess.Read ? ExecutionFlags.Read : ExecutionFlags.Write,
            access == MemoryAccess.Read ? value : null);
    }

    public void ObserveM1(HcMemory effectiveMemory, ushort address, bool instructionStart, long cpuTStates, byte returnedByte)
    {
        Time(cpuTStates);
        Recorder?.Observe(effectiveMemory, address, ExecutionFlags.M1 | (instructionStart ? ExecutionFlags.Start : 0), returnedByte);
    }

    public void ObserveTarget(HcMemory effectiveMemory, ushort resolvedTarget, long cpuTStates)
    {
        Time(cpuTStates); Recorder?.Observe(effectiveMemory, resolvedTarget, ExecutionFlags.Target);
    }

    public void ObservePort(ushort port, byte value, bool output, long cpuTStates)
    {
        Time(cpuTStates); Hit(new(output ? BreakpointKind.PortOut : BreakpointKind.PortIn, port, value, cpuTStates));
    }

    public void ObservePaging(PagingLatches previous, PagingLatches current, long cpuTStates)
    {
        Time(cpuTStates);
        if (previous != current) Hit(new(BreakpointKind.Paging, 0, null, cpuTStates, previous, current));
    }

    /// <summary>Called before the next Step. Bus breakpoints never abort a half-executed instruction.
    /// Snapshot timestamp/PC describe this boundary; Trigger retains the earlier bus event independently.</summary>
    public DebuggerSnapshot Boundary(RegisterSnapshot registers, HcMemory memory, Fdc8272? fdc, long cpuTStates)
    {
        Time(cpuTStates);
        // Rechecking the same stopped boundary after Resume must release it once;
        // a later loop back to the PC still hits, even if no register other than time changed.
        if (resumeBoundary != (registers.Pc, cpuTStates)) Hit(new(BreakpointKind.Pc, registers.Pc, null, cpuTStates));
        resumeBoundary = null;
        stopped |= pending is not null;
        var values = new byte[65536]; var locations = new MemoryLocation[65536];
        for (var a = 0; a < 65536; a++)
        {
            // Direct storage inspection, NOT IBus.Read or I/O. HcMemory has no observer/device side effects.
            values[a] = memory.Read((ushort)a); locations[a] = memory.Resolve((ushort)a);
        }
        Snapshot = new(source, cpuTStates, registers, Array.AsReadOnly(values), Array.AsReadOnly(locations),
            memory.Latches, fdc?.Inspect(), stopped, pending, Array.AsReadOnly(breakpoints.ToArray()));
        return Snapshot;
    }

    /// <summary>Register view of a drained logical .hcs CPU state, for debugger snapshots captured off the machine.</summary>
    public static RegisterSnapshot RegisterView(CpuLogicalState state) => new(
        Pc: state.Pc, Sp: state.Sp, Af: state.Af, Bc: state.Bc, De: state.De, Hl: state.Hl,
        AfPrime: state.AfShadow, BcPrime: state.BcShadow, DePrime: state.DeShadow, HlPrime: state.HlShadow,
        Ix: state.Ix, Iy: state.Iy, Wz: state.Wz, I: state.I, R: state.R, Im: (byte)state.Im,
        Iff1: state.Iff1, Iff2: state.Iff2, Halted: state.Halted, Q: state.Q != 0);
}
