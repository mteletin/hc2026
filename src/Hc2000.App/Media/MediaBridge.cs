using Hc2000.App.Input;

namespace Hc2000.App.Media;

/// <summary>One outstanding immutable media request in the shared input/control FIFO.
/// Connect Publish to MediaSession.Apply on the worker, and Disconnect on worker exit.</summary>
public sealed class MediaBridge(InputQueue queue)
{
    private readonly object gate = new();
    private TaskCompletionSource<MediaReply>? pending;
    private long sequence;
    private bool disconnected;
    public bool HasPending { get { lock (gate) return pending is not null; } }

    public Task<MediaReply> RequestAsync(MediaCommand command)
    {
        lock (gate)
        {
            if (disconnected) throw new InvalidOperationException("Media runtime disconnected.");
            if (pending is not null) throw new InvalidOperationException("A media request is already pending.");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { queue.Enqueue(command with { RequestId = ++sequence }); }
            catch { pending = null; throw; }
            return pending.Task;
        }
    }

    public void Publish(MediaReply reply)
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
            pending?.TrySetException(new InvalidOperationException("Media runtime stopped."));
            pending = null;
        }
    }
}
