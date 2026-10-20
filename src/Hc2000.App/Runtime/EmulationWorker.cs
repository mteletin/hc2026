using Hc2000.Machine;
using Hc2000.Machine.Video;
using LiveMachine = Hc2000.Machine.Machine;

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
    private int disposed;
    private long sessionGeneration = 1;
    private int observationSessionAvailable;
    public IHostClock HostClock { get; }
    internal long SessionGeneration => Interlocked.Read(ref sessionGeneration);
    internal bool ObservationSessionAvailable => Volatile.Read(ref observationSessionAvailable) != 0 && Volatile.Read(ref disposed) == 0;
    // Bounded diagnostic invalidation only; never a UI callback or guest command.
    internal event Action? SessionRetired;
    public Task Completion => completion.Task;
    public VideoFrame? TakeLatestFrame() => Interlocked.Exchange(ref latest, null);

    public EmulationWorker(Func<LiveMachine> factory, Action<RuntimeContext> boundary,
        Func<MachineFrameResult, int, CancellationToken, bool>? audio = null,
        Action<RuntimeContext>? cleanup = null, IHostClock? hostClock = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(boundary);
        HostClock = hostClock ?? new MonotonicHostClock();
        var thread = new Thread(() => Run(factory, boundary, audio, cleanup, HostClock))
        { IsBackground = true, Name = "HC2000 emulation" };
        thread.Start();
    }
    private void Run(Func<LiveMachine> factory, Action<RuntimeContext> boundary,
        Func<MachineFrameResult, int, CancellationToken, bool>? audio,
        Action<RuntimeContext>? cleanup, IHostClock hostClock)
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
                return !cancellation.IsCancellationRequested && context.PermitStep();
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
                        context = new(factory());
                        Volatile.Write(ref observationSessionAvailable, 1);
                        context.SetSpeed(speed);
                        if (wasPaused) context.Pause();
                    }
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
