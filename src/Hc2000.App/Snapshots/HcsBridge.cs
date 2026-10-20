using Hc2000.App.Input;
using Hc2000.Machine.Snapshots;

namespace Hc2000.App.Snapshots;

/// <summary>One outstanding immutable snapshot request in the shared input/control FIFO.
/// Connect Publish to HcsSession.Apply on the worker, and Disconnect on worker exit.</summary>
public sealed class HcsBridge(InputQueue queue)
{
    private readonly object gate = new();
    private TaskCompletionSource<HcsReply>? pending;
    private long sequence;
    private bool disconnected;
    private HcsLimits limits = queue.SnapshotLimits;

    public HcsLimits Limits { get { lock (gate) return limits; } }

    internal void ValidateAdmission(HcsLimits admission)
    {
        lock (gate)
            if (disconnected || !ReferenceEquals(limits, admission) || !ReferenceEquals(limits, queue.SnapshotLimits))
                throw new InvalidOperationException("Stale snapshot runtime.");
    }

    internal void BindAdmission(InputQueue target, HcsLimits admission)
    {
        lock (gate)
        {
            if (!ReferenceEquals(target, queue) || disconnected || pending is not null)
                throw new InvalidOperationException("Snapshot bridge cannot bind this runtime.");
            limits = admission;
        }
    }

    public Task<HcsReply> RequestAsync(HcsCommand command)
    {
        lock (gate)
        {
            if (disconnected) throw new InvalidOperationException("Snapshot runtime disconnected.");
            if (pending is not null) throw new InvalidOperationException("A snapshot request is already pending.");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { queue.EnqueueSnapshot(command with { RequestId = ++sequence }, limits); }
            catch { pending = null; throw; }
            return pending.Task;
        }
    }

    public void Publish(HcsReply reply)
    {
        lock (gate)
        {
            if (reply.RequestId != sequence) return;
            var completion = pending; pending = null;
            completion?.TrySetResult(reply);
        }
    }

    public void Disconnect()
    {
        lock (gate)
        {
            disconnected = true;
            pending?.TrySetException(new InvalidOperationException("Snapshot runtime stopped."));
            pending = null;
        }
    }
}
