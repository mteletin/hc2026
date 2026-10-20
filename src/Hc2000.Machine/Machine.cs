using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using System.Collections.ObjectModel;

namespace Hc2000.Machine;

public sealed record MachineFrameResult(long StartMasterTick, long EndMasterTick,
    VideoFrame Video, ReadOnlyCollection<BeeperEdge> BeeperEdges);

/// <summary>Single-owner composition of the existing CPU step/time and device seams.
/// The caller supplies the one CPU and bus, already wired to the same time adapter.
/// No CPU implementation, ROM substitution or host clock is created here.</summary>
public sealed class Machine
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly FrameRunner runner;
    private readonly DeviceScheduler scheduler;
    private readonly Ula ula;
    private readonly Action step;
    private readonly Func<long> cpuTStates;
    private readonly List<BeeperEdge> edges = [];
    private long startTick;
    private readonly Queue<VideoFrame> pending = new();
    public Machine(DeviceScheduler scheduler, FrameClock clock, CpuDeviceTimeAdapter adapter,
        Ula ula, Action step, Func<long> cpuTStates)
    {
        this.scheduler = scheduler; runner = new(clock, adapter);
        this.ula = ula; this.step = step; this.cpuTStates = cpuTStates;
        startTick = scheduler.Now;
        ula.FrameReady += pending.Enqueue;
        ula.BeeperChanged += edges.Add;
    }
    private TapeTransport? tape;
    private MachineSnapshot? snapshotState;
    public TapeTransport? Tape { get { VerifyOwner(); return tape; } }
    /// <summary>Logical-state snapshot seam (KB hc2000 doc spec-04-machine). The composition owner attaches
    /// the live machine profile, clock, memory, CPU time mapper, CPU seam and the ULA flash device id
    /// once, after all optional components (tape, floppy) are composed. <see cref="Save"/> and
    /// <see cref="Load"/> then operate through this one attachment; no per-call configuration.</summary>
    public MachineSnapshot AttachSnapshotState(MachineProfile profile, FrameClock clock, HcMemory memory,
        CpuTimeMapper mapper, ICpuSnapshotSeam cpu, int flashDeviceId, FloppyController? floppy = null)
    {
        VerifyOwner();
        if (snapshotState is not null) throw new InvalidOperationException("Snapshot state is already attached.");
        snapshotState = new(profile, scheduler, clock, mapper, memory, ula, ula.Keyboard, cpu,
            flashDeviceId, tape, floppy);
        if (tape is not null) tape.PersistenceCheck = snapshotState.CheckCassette;
        return snapshotState;
    }

    /// <summary>Captures the logical machine state as a versioned .hcs document at a drained
    /// instruction boundary. The FDC must be idle; a non-idle FDC fails with StateUnsupported.</summary>
    public byte[] Save()
    {
        VerifyOwner();
        if (snapshotState is null) throw new InvalidOperationException("Snapshot state is not attached.");
        return snapshotState.Save(HcsWavPolicy.Default);
    }

    /// <summary>Adopts a versioned .hcs document onto a fresh machine: validates the container and
    /// profile, cross-checks the state against this machine's composition, then restores every
    /// component. The caller owns the bytes; the machine keeps no copy of the file.</summary>
    public void Load(byte[] file)
    {
        VerifyOwner();
        if (snapshotState is null) throw new InvalidOperationException("Snapshot state is not attached.");
        snapshotState.Load(file, HcsWavPolicy.Default);
        // Presentation/audio buffers are not logical snapshot state. Begin the next host
        // interval at the adopted epoch, never synthesize catch-up from construction tick0.
        startTick = scheduler.Now;
        pending.Clear(); edges.Clear();
    }
    /// <summary>Optional composition seam. The composition owner allocates a noncolliding device ID.
    /// Host commands use the existing instruction-boundary queue, never UI pulse generation.</summary>
    public TapeTransport EnableTape(int stableDeviceId)
    {
        VerifyOwner();
        if (tape is not null) throw new InvalidOperationException("Tape is already composed.");
        tape = new(scheduler, ula, stableDeviceId);
        ula.BeeperChanged += edge => tape.OnMicChanged(edge.MasterTick, edge.Microphone);
        return tape;
    }
    public long MasterTick { get { VerifyOwner(); return scheduler.Now; } }
    public KeyboardMatrix Keyboard { get { VerifyOwner(); return ula.Keyboard; } }
    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Live machine access belongs to the emulation owner.");
    }
    public VideoFrame RunFrame(Action? instructionBoundary = null)
    {
        TryRunFrame(() => { instructionBoundary?.Invoke(); return true; }, out var result);
        return result!.Video;
    }
    /// <summary>May suspend at an instruction boundary (pause/shutdown), retaining partial
    /// frame and audio. A later call continues the same frame. No mid-Step abort.</summary>
    public bool TryRunFrame(Func<bool> instructionBoundary, out MachineFrameResult? result)
    {
        VerifyOwner(); result = null;
        if (pending.Count == 0 && !runner.TryRunFrame(step, cpuTStates, instructionBoundary)) return false;
        var frame = pending.Dequeue();
        result = new(startTick, scheduler.Now, frame, Array.AsReadOnly(edges.ToArray()));
        edges.Clear(); startTick = scheduler.Now;
        return true;
    }
}
