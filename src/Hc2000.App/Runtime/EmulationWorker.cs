using System.Collections.Concurrent;
using Hc2000.Machine;
using Hc2000.Machine.Timing;
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
    private readonly ConcurrentQueue<VideoFrame> recycleQueue = new();
    private volatile LiveMachine? activeMachine;
    private PublishedVideoFrame? latest;
    private PublishedVideoFrame? latestExpansion;
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
    public VideoFrame? TakeLatestFrame() => TakeLatestStampedFrame()?.Frame;
    public VideoFrame? TakeLatestExpansionFrame() => TakeLatestStampedExpansionFrame()?.Frame;
    internal PublishedVideoFrame? TakeLatestStampedFrame() => Interlocked.Exchange(ref latest, null);
    internal PublishedVideoFrame? TakeLatestStampedExpansionFrame() => Interlocked.Exchange(ref latestExpansion, null);

    /// <summary>Hands a frame the UI took back for reuse once no part of the UI needs it. Thread-safe:
    /// queues the frame for recycling on the emulation thread, or drains directly if the worker has completed.</summary>
    public void Recycle(VideoFrame? frame)
    {
        if (frame is null) return;
        recycleQueue.Enqueue(frame);
        if (completion.Task.IsCompleted && activeMachine is { } machine)
        {
            while (recycleQueue.TryDequeue(out var f))
                machine.Recycle(f);
        }
    }

    /// <summary>Total frame pixel arrays allocated by this worker's live machine.</summary>
    public int FrameBuffersAllocated => activeMachine?.FrameBuffersAllocated ?? 0;

    /// <summary>Total frame pixel arrays waiting in pool for reuse in this worker's live machine.</summary>
    public int FrameBuffersKept => activeMachine?.FrameBuffersKept ?? 0;

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
        Func<RuntimeContext, DebuggerSnapshot?>? debugSnapshot = null,
        Func<RuntimeContext, MachineFrameResult, double, CancellationToken, bool>? afterFrame = null,
        CpuRateMeter? rateMeter = null, Func<string?>? mainCpuSwitchStatus = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(boundary);
        HostClock = hostClock ?? new MonotonicHostClock();
        this.rateMeter = rateMeter;
        this.mainCpuSwitchStatus = mainCpuSwitchStatus;
        var thread = new Thread(() => Run(factory, boundary, audio, cleanup, HostClock, executionAllowed, debugSnapshot, afterFrame, rateMeter))
        { IsBackground = true, Name = "HC2000 emulation" };
        // A test run may ask which owners are still alive and where each was made (HC2000_WORKER_AUDIT=1): a worker
        // nobody ended keeps emulating at full speed beside every later test.
        if (Audit is not null)
        {
            Audit[this] = Environment.StackTrace;
            _ = completion.Task.ContinueWith(_ => Audit.TryRemove(this, out string? _), TaskScheduler.Default);
        }
        // A test run names the test that is making this worker, so a worker its test never ended is that test's
        // failure and is ended there, instead of slowing every test after it.
        if (Owner?.Invoke() is { } owner)
        {
            Owned[this] = owner;
            _ = completion.Task.ContinueWith(_ => Owned.TryRemove(this, out object? _), TaskScheduler.Default);
        }
        thread.Start();
    }

    /// <summary>Set by a test assembly only: who is creating workers now (the running test), or null.</summary>
    internal static Func<object?>? Owner;
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<EmulationWorker, object> Owned = new();

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<EmulationWorker, string>? Audit =
        Environment.GetEnvironmentVariable("HC2000_WORKER_AUDIT") == "1" ? new() : null;

    private readonly CpuRateMeter? rateMeter;
    private readonly Func<string?>? mainCpuSwitchStatus;

    /// <summary>
    /// The honest main-CPU rate sample for the UI (hc2000-accelerated-clock-domains): requested vs
    /// achieved T/s over the meter's stated window, frame cadence, warm-up and overload state.
    /// Read on the UI thread; null when the worker was composed without a meter.
    /// </summary>
    public CpuRateSample? RateLatest => rateMeter?.Latest;

    /// <summary>
    /// The last live main-CPU clock switch outcome, owner-written at the instruction boundary that
    /// applied it (applied or refused with the reason), or null while none has been requested.
    /// A refusal keeps the previous selection and machine state; the UI surfaces this line.
    /// </summary>
    public string? MainCpuSwitchStatus => mainCpuSwitchStatus?.Invoke();
    private void Run(Func<LiveMachine> factory, Action<RuntimeContext> boundary,
        Func<MachineFrameResult, int, CancellationToken, bool>? audio,
        Action<RuntimeContext>? cleanup, IHostClock hostClock, Func<bool>? executionAllowed,
        Func<RuntimeContext, DebuggerSnapshot?>? debugSnapshot,
        Func<RuntimeContext, MachineFrameResult, double, CancellationToken, bool>? afterFrame,
        CpuRateMeter? rateMeter)
    {
        RuntimeContext? context = null;
        Exception? failure = null;
        try
        {
            context = new(factory());
            activeMachine = context.Machine;
            Volatile.Write(ref observationSessionAvailable, 1);
            var pacer = new FramePacer(hostClock);
            void DrainRecycleQueue(LiveMachine machine)
            {
                while (recycleQueue.TryDequeue(out var frame))
                    machine.Recycle(frame);
            }
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
                var frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
                DrainRecycleQueue(context.Machine);
                if (!context.Machine.TryRunFrame(Boundary, out var result))
                {
                    // A reset is consumed at an instruction boundary. Retire the old keyboard
                    // before constructing fresh CPU/RAM/latches on this same emulation owner.
                    if (context.Resetting && !context.Stopping && !cancellation.IsCancellationRequested)
                    {
                        RetireObservationSession();
                        var wasPaused = context.Paused;
                        var speed = context.Speed;
                        DrainRecycleQueue(context.Machine);
                        cleanup?.Invoke(context);
                        Interlocked.Exchange(ref latest, null);
                        Interlocked.Exchange(ref latestExpansion, null);
                        Interlocked.Exchange(ref latestDebug, null);
                        context = new(factory());
                        activeMachine = context.Machine;
                        Volatile.Write(ref observationSessionAvailable, 1);
                        context.SetSpeed(speed);
                        if (wasPaused) context.Pause();
                    }
                    CaptureDebugSnapshot();
                    DrainRecycleQueue(context.Machine);
                    pacer.Rebase();
                    if (cancellation.Token.WaitHandle.WaitOne(2)) break;
                    continue; // paused queue pumping, no CPU steps/time advancement
                }
                var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                var generation = SessionGeneration;
                // A replaced frame is not handed back here: a reader may still hold it (the X2 benchmark's progress and
                // command checks read published frames), so only a frame's own consumer hands it back
                // (hc2000-app-frame-recycling).
                Interlocked.Exchange(ref latest, new PublishedVideoFrame(generation, result!.Video));
                Interlocked.Exchange(ref latestExpansion, result.ExpansionVideo is { } expansionVideo
                    ? new PublishedVideoFrame(generation, expansionVideo) : null);
                // Honest rate reporting (hc2000-accelerated-clock-domains): one observation per
                // rendered frame, on the owner, from the committed CPU counter and the same
                // monotonic clock the pacer paces from. Observation only: it never skips or
                // re-paces guest work. A reset/restore adopts a fresh machine, whose changed
                // identity rebases the meter's window.
                rateMeter?.Sample(hostClock.Now, context.Machine, context.Machine.CommittedCpuTStates);
                if (afterFrame is not null && afterFrame(context, result, elapsedMs, cancellation.Token))
                    pacer.Rebase();
                bool audioPaced = audio?.Invoke(result, (int)context.Speed, cancellation.Token) ?? false;
                pacer.Pace(context.Speed, audioPaced, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EmulationWorker] Unhandled exception in worker thread: {ex}");
            failure = ex;
        }
        finally
        {
            RetireObservationSession();
            try
            {
                if (context is not null)
                {
                    while (recycleQueue.TryDequeue(out var frame))
                        context.Machine.Recycle(frame);
                    cleanup?.Invoke(context);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EmulationWorker] Unhandled exception in worker cleanup: {ex}");
                failure ??= ex;
            }
            activeMachine = null;
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

internal sealed record PublishedVideoFrame(long Generation, VideoFrame Frame);

/// <summary>A debugger boundary copy stamped with the owner generation and capture sequence it was
/// taken at, and whether the machine was paused when the owner captured it. The sequence advances
/// once per real capture and is the freshness token; the generation changes on reset or load, so a
/// copy from the retired machine can be told apart from one of the replacement.</summary>
internal sealed record DebugEvidence(DebuggerSnapshot Snapshot, Guid SessionId, long Generation, long Sequence, bool Paused);
