using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Network;

internal interface ICpNetInboundListener
{
    Task Completion { get; }
    Task CloseAsync();
}

internal sealed class CpNetStartCleanupException(Exception inner, object? retained = null) : IOException("CP/NET server start cleanup failed.", inner)
{
    // Keep resources reachable for a failed cleanup while the owner retains its guard.
    public object? Retained { get; } = retained;
}

// Owner-thread lease and role claim for one starting, live or retiring listener run.
internal sealed class CpNetInboundRuntime(LiveMachine machine, CpNetHostGate hostGate) : ICpNetRetiringRuntime
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private IDisposable? lease;
    private CpNetHostGate.Claim? claim;
    private CancellationTokenSource? cancellation;
    private Task<ICpNetInboundListener>? starting;
    private Task? closing;
    private Task hostRetirement = Task.CompletedTask;
    private Func<bool>? canStart;
    private bool failed;

    public bool HasExternalState { get { VerifyOwner(); return lease is not null; } }
    public bool IsClosing { get { VerifyOwner(); return closing is not null; } }
    public bool CloseFailed { get { VerifyOwner(); return closing?.IsFaulted == true || closing?.IsCanceled == true; } }
    public bool Failed { get { VerifyOwner(); return failed; } }
    public bool Listening { get { VerifyOwner(); return closing is null && starting?.IsCompletedSuccessfully == true &&
        !starting.Result.Completion.IsCompleted; } }
    public CpNetServerRun? Run { get { VerifyOwner(); return Listening ? starting!.Result as CpNetServerRun : null; } }
    public bool CanStart { get { VerifyOwner(); return lease is null && canStart?.Invoke() != false &&
        hostGate.State == CpNetHostGate.GateState.Available; } }

    public void SetStartGuard(Func<bool> guard)
    {
        VerifyOwner();
        canStart = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public void ClearFailure() { VerifyOwner(); failed = false; }

    // The factory must clean partial resources on failure, or throw CpNetStartCleanupException.
    public bool Start(Func<CancellationToken, Task<ICpNetInboundListener>> factory)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(factory);
        if (!CanStart) return false;
        claim = hostGate.TryClaim();
        if (claim is null) return false;
        try
        {
            lease = machine.AcquireExternalState();
            cancellation = new CancellationTokenSource();
            failed = false;
            var token = cancellation.Token;
            starting = Task.Run(() => factory(token));
            _ = starting.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }
        catch
        {
            cancellation?.Dispose(); cancellation = null;
            lease?.Dispose(); lease = null;
            claim.Abort(); claim = null;
            throw;
        }
    }

    public void Stop()
    {
        VerifyOwner();
        if (lease is not null && closing is null) BeginClose();
        Pump();
    }

    public void Pump()
    {
        VerifyOwner();
        if (lease is null) return;
        if (closing is null && starting is { IsCompleted: true })
        {
            if (!starting.IsCompletedSuccessfully || starting.Result.Completion.IsCompleted)
            {
                failed = true;
                BeginClose();
            }
        }
        if (closing is null || !closing.IsCompletedSuccessfully || !hostRetirement.IsCompletedSuccessfully) return;
        lease.Dispose();
        lease = null;
        starting = null;
        closing = null;
    }

    private void BeginClose()
    {
        var pending = starting!;
        var source = cancellation!;
        closing = Task.Run(async () =>
        {
            Exception? cancellationFailure = null;
            try { await source.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { cancellationFailure = error; }
            try
            {
                ICpNetInboundListener? listener;
                try { listener = await pending.ConfigureAwait(false); }
                catch (CpNetStartCleanupException) { throw; }
                catch { listener = null; }
                if (listener is not null) await listener.CloseAsync().ConfigureAwait(false);
            }
            finally { source.Dispose(); }
            if (cancellationFailure is not null) throw new InvalidOperationException("CP/NET server cancellation failed.", cancellationFailure);
        });
        claim!.Retire(closing, closing);
        hostRetirement = claim.Retirement;
        claim = null;
        cancellation = null;
        _ = closing.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("CP/NET server runtime belongs to the emulation owner.");
    }
}
