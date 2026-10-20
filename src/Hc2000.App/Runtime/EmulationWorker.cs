using Hc2000.Machine;
using Hc2000.Machine.Video;
using LiveMachine = Hc2000.Machine.Machine;
using Hc2000.App.Debugging;

namespace Hc2000.App.Runtime;

/// <summary>Owner-only lifecycle policy. Shared ordered input/control queue calls these
/// methods in its boundary consumer, never directly from the UI.</summary>
public sealed class RuntimeContext(LiveMachine machine)
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private bool paused;
    private int steps;
    private SpeedMode speed = SpeedMode.Normal;
    private bool stopping;
    private bool resetting;
    public LiveMachine Machine { get { Verify(); return machine; } }
    public bool Paused { get { Verify(); return paused; } }
    public SpeedMode Speed { get { Verify(); return speed; } }
    public bool Stopping { get { Verify(); return stopping; } }
    internal bool Resetting { get { Verify(); return resetting; } }
    public void Reset() { Verify(); resetting = true; }
    private void Verify()
    {
        if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Runtime controls must be consumed by the owner.");
    }
    public void Pause() { Verify(); paused = true; steps = 0; }
    public void Resume() { Verify(); paused = false; steps = 0; }
    public void Step() { Verify(); paused = true; steps = checked(steps + 1); }
    public void SetSpeed(SpeedMode value)
    {
        Verify(); if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); speed = value;
    }
    public void Shutdown() { Verify(); stopping = true; }
    internal bool PermitStep()
    {
        Verify(); if (stopping || resetting) return false;
        if (!paused) return true;
        if (steps == 0) return false;
        steps--; return true;
    }
}

/// <summary>Exactly one background owner. UI polls a capacity-one immutable frame mailbox;
/// it never waits for the machine or receives its live object. Factory and cleanup run here.</summary>
public sealed class EmulationWorker : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private VideoFrame? latest;
    // The retained copy carries the owner generation and capture sequence it was taken at, so a
    // reader can tell a copy taken after its request from the one retained before it. CPU T-states
    // are not that token: they freeze while paused and restart at zero after a reset.
    private DebugEvidence? latestDebug;
    private int debugRequested;
    private long debugCaptureSequence;
    private int disposed;
    private long sessionGeneration = 1;
    private int observationSessionAvailable;
    public IHostClock HostClock { get; }
    // A replacement worker is a different machine-runtime session even if its generation starts at 1.
    internal Guid DebugSessionId { get; } = Guid.NewGuid();
    internal long SessionGeneration => Interlocked.Read(ref sessionGeneration);
    internal bool ObservationSessionAvailable => Volatile.Read(ref observationSessionAvailable) != 0 && Volatile.Read(ref disposed) == 0;
    // Bounded diagnostic invalidation only; never a UI callback or guest command.
    internal event Action? SessionRetired;
    public Task Completion => completion.Task;
    public VideoFrame? TakeLatestFrame() => Interlocked.Exchange(ref latest, null);

    /// <summary>Asks the worker for a debugger boundary copy. The capture runs on the
    /// emulation owner at the next instruction boundary, or in the paused pump when stopped.
    /// A second call while one is outstanding does not stack: the flag is a single slot.</summary>
    public void RequestDebugSnapshot() => Interlocked.CompareExchange(ref debugRequested, 1, 0);

    /// <summary>True while a capture demand is waiting for, or being copied by, the owner.
    /// Requests coalesce until the owner publishes (0 idle, 1 requested, 2 capturing).</summary>
    internal bool DebugDemandPending => Volatile.Read(ref debugRequested) != 0;

    /// <summary>Latest published debugger boundary copy; kept so a re-opened window starts with state.</summary>
    public DebuggerSnapshot? TakeLatestDebugSnapshot() => Volatile.Read(ref latestDebug)?.Snapshot;

    /// <summary>The retained copy with the generation and sequence it was captured at, read atomically
    /// so the stamps cannot be observed apart from the copy they belong to.</summary>
    internal DebugEvidence? TakeLatestDebugEvidence() => Volatile.Read(ref latestDebug);

    public EmulationWorker(Func<LiveMachine> factory, Action<RuntimeContext> boundary,
        Func<MachineFrameResult, int, CancellationToken, bool>? audio = null,
        Action<RuntimeContext>? cleanup = null, IHostClock? hostClock = null,
        Func<bool>? executionAllowed = null,
        Func<RuntimeContext, DebuggerSnapshot?>? debugSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(boundary);
        HostClock = hostClock ?? new MonotonicHostClock();
        var thread = new Thread(() => Run(factory, boundary, audio, cleanup, HostClock, executionAllowed, debugSnapshot))
        { IsBackground = true, Name = "HC2000 emulation" };
        thread.Start();
    }
    private void Run(Func<LiveMachine> factory, Action<RuntimeContext> boundary,
        Func<MachineFrameResult, int, CancellationToken, bool>? audio,
        Action<RuntimeContext>? cleanup, IHostClock hostClock, Func<bool>? executionAllowed,
        Func<RuntimeContext, DebuggerSnapshot?>? debugSnapshot)
    {
        RuntimeContext? context = null;
        Exception? failure = null;
        try
        {
            context = new(factory());
            Volatile.Write(ref observationSessionAvailable, 1);
            var pacer = new FramePacer(hostClock);
            bool Boundary()
            {
                boundary(context);
                CaptureDebugSnapshot();
                // Keep pumping owner controls while the native guest switch is held.
                // Check before PermitStep: a held CPU must not consume a queued single step.
                return !cancellation.IsCancellationRequested && (executionAllowed?.Invoke() ?? true) && context.PermitStep();
            }
            // Request-gated: at most one 64 KB boundary copy per UI request, owner-thread only.
            void CaptureDebugSnapshot()
            {
                // A reset request must be satisfied by the replacement, not the retiring context.
                if (context.Resetting || debugSnapshot is null ||
                    Interlocked.CompareExchange(ref debugRequested, 2, 1) != 1) return;
                try
                {
                    var captured = debugSnapshot(context);
                    // An unavailable copy consumes the demand without advancing freshness.
                    if (captured is null) return;
                    var stamped = new DebugEvidence(captured, DebugSessionId, SessionGeneration,
                        Interlocked.Increment(ref debugCaptureSequence), context.Paused);
                    Interlocked.Exchange(ref latestDebug, stamped);
                }
                finally { Volatile.Write(ref debugRequested, 0); }
            }
            while (!cancellation.IsCancellationRequested && !context.Stopping)
            {
                if (!context.Machine.TryRunFrame(Boundary, out var result))
                {
                    // A reset is consumed at an instruction boundary. Retire the old keyboard
                    // before constructing fresh CPU/RAM/latches on this same emulation owner.
                    if (context.Resetting && !context.Stopping && !cancellation.IsCancellationRequested)
                    {
                        RetireObservationSession();
                        var wasPaused = context.Paused;
                        var speed = context.Speed;
                        cleanup?.Invoke(context);
                        Interlocked.Exchange(ref latest, null);
                        Interlocked.Exchange(ref latestDebug, null);
                        context = new(factory());
                        Volatile.Write(ref observationSessionAvailable, 1);
                        context.SetSpeed(speed);
                        if (wasPaused) context.Pause();
                    }
                    CaptureDebugSnapshot();
                    pacer.Rebase();
                    if (cancellation.Token.WaitHandle.WaitOne(2)) break;
                    continue; // paused queue pumping, no CPU steps/time advancement
                }
                Interlocked.Exchange(ref latest, result!.Video);
                bool audioPaced = audio?.Invoke(result, (int)context.Speed, cancellation.Token) ?? false;
                pacer.Pace(context.Speed, audioPaced, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            RetireObservationSession();
            try { if (context is not null) cleanup?.Invoke(context); }
            catch (Exception ex) { failure ??= ex; }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
    }
    private void RetireObservationSession()
    {
        // Clears old retained evidence before reset/load adoption or final cleanup.
        // Diagnostics cannot replace the worker's actual failure/completion result.
        Volatile.Write(ref observationSessionAvailable, 0);
        try { SessionRetired?.Invoke(); }
        catch { /* Diagnostic failure is not an emulation failure. */ }
        Interlocked.Increment(ref sessionGeneration);
    }

    /// <summary>Nonblocking cancellation. Await Completion off the UI thread for shutdown evidence.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            cancellation.Cancel();
            _ = completion.Task.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}

/// <summary>A debugger boundary copy stamped with the owner generation and capture sequence it was
/// taken at, and whether the machine was paused when the owner captured it. The sequence advances
/// once per real capture and is the freshness token; the generation changes on reset or load, so a
/// copy from the retired machine can be told apart from one of the replacement.</summary>
internal sealed record DebugEvidence(DebuggerSnapshot Snapshot, Guid SessionId, long Generation, long Sequence, bool Paused);
