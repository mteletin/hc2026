using Hc2000.Machine.Expansion;
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
    VideoFrame Video, ReadOnlyCollection<BeeperEdge> BeeperEdges)
{
    public VideoFrame? ExpansionVideo { get; init; }
}

/// <summary>Single-owner composition of the existing CPU step/time and device seams.
/// The caller supplies the one CPU and bus, already wired to the same time adapter.
/// No CPU implementation, ROM substitution or host clock is created here.
/// What the composition hands over with <see cref="Own{T}"/> (its memory) is released when the machine is disposed.</summary>
public sealed class Machine : IDisposable
{
    private readonly List<IDisposable> owned = [];
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly FrameRunner runner;
    private readonly CpuDeviceTimeAdapter adapter;
    private readonly DeviceScheduler scheduler;
    private readonly Ula ula;
    private readonly Action step;
    private readonly Func<long> cpuTStates;
    private readonly ExpansionFrontierDriver? expansion;
    internal ExpansionX3Card? ExpansionX3 { get { VerifyOwner(); return expansion?.X3; } }
    /// <summary>Compute units run bursts on the plain core, a halted main skips its halt cycles, and a main at 140 MHz and
    /// up runs relaxed quanta between events (hc2000-fast-emulation); true by default, false runs the frontier model alone
    /// (the reference). Nothing a guest program can read depends on it; at 140 MHz and up the picture may show a screen
    /// write a little early (the owner's off-spec rule, 2026-11-26).</summary>
    public bool ExpansionBursts
    {
        get { VerifyOwner(); return expansion?.Bursts ?? false; }
        set { VerifyOwner(); if (expansion is not null) expansion.Bursts = value; }
    }
    /// <summary>A side Z80's clock target (design contract 4), or null without the expansion.</summary>
    public MainCpuSpeed? ExpansionUnitClock(ushort id)
    {
        VerifyOwner();
        return expansion?.UnitClock(id).Target;
    }
    private readonly List<BeeperEdge> edges = [];
    private long startTick;
    private readonly Queue<(VideoFrame Video, VideoFrame? ExpansionVideo)> pending = new();
    public Machine(DeviceScheduler scheduler, FrameClock clock, CpuDeviceTimeAdapter adapter,
        Ula ula, Action step, Func<long> cpuTStates, ExpansionFrontierDriver? expansion = null)
    {
        this.scheduler = scheduler; this.adapter = adapter; runner = new(clock, adapter);
        this.ula = ula; this.step = step; this.cpuTStates = cpuTStates; this.expansion = expansion;
        startTick = scheduler.Now;
        ula.FrameReady += frame => pending.Enqueue((frame, expansion?.X2?.Engine.PublishFrame(frame.MasterTick)));
        ula.BeeperChanged += edges.Add;
    }
    /// <summary>The composition hands the machine a resource that lives and dies with it, such as its memory.</summary>
    public T Own<T>(T resource) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(resource);
        owned.Add(resource);
        return resource;
    }

    /// <summary>Releases what the machine owns (its paged RAM goes back to <see cref="PagedRamPool"/>). The machine
    /// must not run afterwards. A second call does nothing.</summary>
    public void Dispose()
    {
        foreach (var resource in owned) resource.Dispose();
        owned.Clear();
    }

    /// <summary>Hands a frame this machine published back for reuse, once its consumer is done with it: its pixel arrays
    /// go to the next frames (<see cref="FrameBuffers"/>). Only frames handed back are reused; a frame that is not this
    /// machine's, or was handed back already, is ignored. Do not read the frame afterwards.</summary>
    public void Recycle(VideoFrame? frame)
    {
        if (frame is null) return;
        ula.Buffers.Return(frame);
        expansion?.X2?.Engine.Buffers.Return(frame);
    }

    /// <summary>Hands back both frames of one <see cref="TryRunFrame"/> result (<see cref="Recycle(VideoFrame)"/>).</summary>
    public void Recycle(MachineFrameResult? result)
    {
        if (result is null) return;
        Recycle(result.Video);
        Recycle(result.ExpansionVideo);
    }

    /// <summary>How many frame pixel arrays have been allocated fresh by this machine's video producers.</summary>
    public int FrameBuffersAllocated => ula.Buffers.Allocated + (expansion?.X2?.Engine.Buffers.Allocated ?? 0);

    /// <summary>How many frame pixel arrays wait for reuse now in this machine's pools.</summary>
    public int FrameBuffersKept => ula.Buffers.Kept + (expansion?.X2?.Engine.Buffers.Kept ?? 0);

    /// <summary>Retires the machine when some of its parts may outlive it (the App's worker at a reset or a snapshot load):
    /// its memory is released without being handed to another machine (<see cref="HcMemory.Retire"/>); anything else it
    /// owns is disposed. A second call does nothing.</summary>
    public void Retire()
    {
        foreach (var resource in owned)
            if (resource is HcMemory memory) memory.Retire();
            else resource.Dispose();
        owned.Clear();
    }

    private TapeTransport? tape;
    private MachineSnapshot? snapshotState;
    private HcsWavPolicy? snapshotPolicy;
    public TapeTransport? Tape { get { VerifyOwner(); return tape; } }
    public TapeVolumeSession? TapeVolume { get; private set; }
    public TapeVolumeSession EnableTapeVolume(MachineProfile profile, HcMemory memory, Func<char, bool>? slotPresent = null)
    {
        VerifyOwner();
        if (tape is null || TapeVolume is not null || snapshotState is not null)
            throw new InvalidOperationException("Compose one volume after tape and before snapshots.");
        // slotPresent reports the letters another attached medium already owns (the composite's
        // C–F raw HDD volumes), so the tape volume mounts at the first legal free letter (G:).
        return TapeVolume = new(profile, tape, memory, slotPresent);
    }
    /// <summary>Logical-state snapshot seam. The composition owner attaches
    /// the live machine profile, clock, memory, CPU time mapper, CPU seam and the ULA flash device id
    /// once, after all optional components (tape, floppy) are composed. <see cref="Save"/> and
    /// <see cref="Load"/> then operate through this one attachment; no per-call configuration.</summary>
    public MachineSnapshot AttachSnapshotState(MachineProfile profile, FrameClock clock, HcMemory memory,
        CpuTimeMapper mapper, ICpuSnapshotSeam cpu, int flashDeviceId, FloppyController? floppy = null,
        HcsWavPolicy? wavPolicy = null, BlockDiskPort? hdd = null)
    {
        VerifyOwner();
        if (snapshotState is not null) throw new InvalidOperationException("Snapshot state is already attached.");
        // Paged profiles use generic blob caps, not the nonpaged WAV-original exception.
        // Keep a caller's explicit policy and share this exact value with preview, Save and Load.
        snapshotPolicy = wavPolicy ?? (profile.Memory.RamPaged is null ? HcsWavPolicy.Default : null);
        snapshotState = new(profile, scheduler, clock, mapper, memory, ula, ula.Keyboard, cpu,
            flashDeviceId, tape, floppy, TapeVolume, expansion, hdd);
        if (tape is not null) tape.PersistenceCheck = candidate => snapshotState.CheckCassette(candidate, snapshotPolicy);
        return snapshotState;
    }

    /// <summary>Owner-only read-only admission composed into the existing session Save preflight.</summary>
    public void ValidateSessionSnapshotSave()
    {
        VerifyOwner();
        if (snapshotState is null) throw new InvalidOperationException("Snapshot state is not attached.");
        snapshotState.ValidateSessionSnapshotSave(snapshotPolicy);
    }

    /// <summary>Checks the retiring live machine before a detached snapshot is adopted.</summary>
    public void ValidateSessionSnapshotLoad()
    {
        VerifyOwner();
        if (snapshotState is null) throw new InvalidOperationException("Snapshot state is not attached.");
        snapshotState.ValidateSessionSnapshotLoad();
    }

    /// <summary>Host composition holds this while a non-rewindable resource is live.</summary>
    public IDisposable AcquireExternalState()
    {
        VerifyOwner();
        if (snapshotState is null) throw new InvalidOperationException("Snapshot state is not attached.");
        return snapshotState.AcquireExternalState();
    }

    /// <summary>Captures the logical machine state as a versioned .hcs document at a drained
    /// instruction boundary. The FDC must be idle; a non-idle FDC fails with StateUnsupported.</summary>
    public byte[] Save()
    {
        VerifyOwner();
        if (snapshotState is null) throw new InvalidOperationException("Snapshot state is not attached.");
        return snapshotState.Save(snapshotPolicy);
    }

    /// <summary>Adopts a versioned .hcs document onto a fresh machine: validates the container and
    /// profile, cross-checks the state against this machine's composition, then restores every
    /// component. The caller owns the bytes; the machine keeps no copy of the file.</summary>
    public void Load(byte[] file)
    {
        using var stream = new MemoryStream(file, writable: false);
        Load(stream);
    }

    /// <summary>Adopts a versioned .hcs document from a seekable stream without copying the whole
    /// file first; large blobs are restored through bounded pooled chunks. The caller owns the
    /// stream and it must stay open for the duration of the call; the machine keeps no reference.</summary>
    public void Load(Stream file)
    {
        VerifyOwner();
        if (snapshotState is null) throw new InvalidOperationException("Snapshot state is not attached.");
        snapshotState.Load(file, snapshotPolicy);
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

    /// <summary>
    /// The committed main-CPU counter (T-states admitted at the current boundary, on the rate in force).
    /// Reading it is the emulation owner's work; host-side rate reporting (the honest achieved-speed meter)
    /// samples it per frame and never drives the machine from it.
    /// </summary>
    public long CommittedCpuTStates { get { VerifyOwner(); return adapter.Mapper.CpuTStates; } }

    /// <summary>
    /// Switches the main CPU clock to an admitted <see cref="MainCpuSpeed"/> at a safe CPU/frontier
    /// boundary, serialized on the emulation owner. The exact old logical time and the committed CPU
    /// counter become the switch anchor; the new rate continues from that anchor without reinterpreting
    /// the phase. The main frontier is reconciled atomically; side units keep their own faithful clock
    /// and pending device events stay admitted. A boundary where the guest time and the committed
    /// counter's exact time differ (a faulted main, a non-quiescent advance) is refused and the
    /// previous selection and state are preserved. The device epoch, video and interrupt cadence never
    /// move: they stay on the unchanged 17,734,475 Hz master timeline. The side Z80s still on the machine's
    /// previous clock follow it (hc2000-speed-140-turbo-280), at once when stable, else at their next fresh
    /// epoch; a unit a program gave its own clock keeps it.
    /// </summary>
    public void SetMainCpuTarget(MainCpuSpeed target)
    {
        VerifyOwner();
        if (!AtSafeSpeedBoundary(scheduler, adapter.Mapper))
            throw new InvalidOperationException("A speed change is safe only at a quiescent boundary where the guest time equals the committed CPU's exact time.");
        var mapper = adapter.Mapper;
        var anchor = scheduler.LogicalNow;
        var committed = mapper.CpuTStates;
        var (numerator, denominator) = target.MasterTicksPerTState();
        if (numerator == mapper.Numerator && denominator == mapper.Denominator) return; // already at this rate
        var previous = MainCpuSpeedInfo.All.Cast<MainCpuSpeed?>()
            .FirstOrDefault(speed => speed!.Value.MasterTicksPerTState() == (mapper.Numerator, mapper.Denominator));
        var next = new CpuTimeMapper(numerator, denominator);
        next.Adopt(committed, anchor);
        adapter.InstallMapper(next);
        expansion?.InstallMapper(next);
        snapshotState?.InstallMapper(next);
        // A legacy cpu.turbo rate is no admitted choice: units were never on it, so none follows from it.
        if (previous is { } from) expansion?.FollowMachineClock(from, target);
    }

    /// <summary>
    /// The safe boundary for a runtime speed switch: the scheduler is quiescent and the guest time is
    /// exactly the committed CPU counter's exact time. A faulted expansion main that advanced its frame's
    /// devices past the committed frontier, or any drift between guest time and the committed counter,
    /// fails it; the previous selection and state are then preserved.
    /// </summary>
    internal static bool AtSafeSpeedBoundary(DeviceScheduler scheduler, CpuTimeMapper mapper)
        => !scheduler.Advancing && scheduler.LogicalNow == mapper.ExactTimeAt(mapper.CpuTStates);

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
        if (pending.Count == 0 && !(expansion is null
            ? runner.TryRunFrame(step, cpuTStates, instructionBoundary)
            : expansion.TryRunFrame(instructionBoundary))) return false;
        var frame = pending.Dequeue();
        result = new(startTick, scheduler.Now, frame.Video, Array.AsReadOnly(edges.ToArray()))
            { ExpansionVideo = frame.ExpansionVideo };
        edges.Clear(); startTick = scheduler.Now;
        return true;
    }
}
