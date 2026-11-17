using Hc2000.Machine.Network;

namespace Hc2000.App.Network;

// Single owner-thread mailbox. Host work runs off the emulation thread; the owner polls for
// a result and is responsible for admitting it at an ordered machine-time boundary.
internal sealed class CpNetExchangeSession
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly Func<CpNetFrame, CancellationToken, Task<CpNetFrame>> exchange;
    private readonly CancellationTokenSource cancellation = new();
    private Task<CpNetFrame>? pending;
    private CpNetFrame? pendingRequest;
    private bool disconnected;
    private Task quiescence = Task.CompletedTask;

    // The owner must retain the external-state lease until cancellation callbacks and any
    // abandoned exchange have actually finished, even after the stream close completes.
    public Task Quiescence { get { VerifyOwner(); return quiescence; } }

    public CpNetExchangeSession(Func<CpNetFrame, CancellationToken, Task<CpNetFrame>> exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        this.exchange = exchange;
    }

    public bool TryBegin(CpNetFrame request)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(request);
        if (disconnected || pending is not null || request.Format != 0) return false;
        pendingRequest = request;
        pending = Task.Run(() => exchange(request, cancellation.Token));
        return true;
    }

    // Only a completed result is adopted. A fault may mean the peer acted, so the entire
    // session is retired and the caller must detach its stream, not retry the operation.
    public bool TryTake(out CpNetPeerResult result)
    {
        VerifyOwner();
        result = default;
        if (disconnected || pending is null || !pending.IsCompleted) return false;
        var completed = pending;
        var request = pendingRequest!;
        pending = null;
        pendingRequest = null;
        if (!completed.IsCompletedSuccessfully || completed.Result is not { } response ||
            response.Format != 1 || response.Destination != request.Source || response.Source != request.Destination)
        {
            if (completed.IsFaulted) _ = completed.Exception;
            disconnected = true;
            result = CpNetPeerResult.AmbiguousOutcome();
            return true;
        }
        result = CpNetPeerResult.Accept(response.Bytes);
        return true;
    }

    // Returns whether an outstanding operation was abandoned; its outcome is unknown.
    // CancelAsync avoids running host cancellation callbacks on the emulation thread.
    public bool Disconnect()
    {
        VerifyOwner();
        if (disconnected) return false;
        disconnected = true;
        var abandoned = pending is not null;
        var work = pending;
        if (pending is not null)
        {
            _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            pending = null;
            pendingRequest = null;
        }
        quiescence = work is null ? cancellation.CancelAsync() : Task.WhenAll(work, cancellation.CancelAsync());
        _ = quiescence.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return abandoned;
    }

    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("CP/NET session must be used by its owner thread.");
    }
}
