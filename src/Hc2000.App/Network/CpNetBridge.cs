using Hc2000.App.Input;

namespace Hc2000.App.Network;

public enum CpNetAction { Connect, Disconnect, Observe }

public sealed record CpNetCommand : InputCommand
{
    internal CpNetCommand(CpNetBridge bridge, long epoch, long request, CpNetAction action, string host, int port)
    {
        Bridge = bridge; Epoch = epoch; Request = request; Action = action; Host = host; Port = port;
    }
    internal CpNetBridge Bridge { get; }
    internal long Epoch { get; }
    internal long Request { get; }
    internal CpNetAction Action { get; }
    internal string Host { get; }
    internal int Port { get; }
}

// One outstanding explicit operator request in the same ordered FIFO as reset and snapshots.
// Epochs prevent a command queued before reset from activating the replacement machine.
internal sealed class CpNetBridge(InputQueue queue)
{
    private readonly object sync = new();
    private TaskCompletionSource<CpNetSessionStatus>? pending;
    private long epoch, sequence;
    private bool bound, disconnected;

    public void Bind()
    {
        lock (sync)
        {
            if (disconnected || bound) throw new InvalidOperationException("Network bridge cannot bind this runtime.");
            epoch = checked(epoch + 1);
            bound = true;
        }
    }

    public Task<CpNetSessionStatus> RequestAsync(CpNetAction action, string host = "", int port = 0)
    {
        if (!Enum.IsDefined(action) || host is null || host.Length > 253)
            throw new ArgumentException("Invalid network command.");
        lock (sync)
        {
            if (disconnected || !bound) throw new InvalidOperationException("Network runtime is not available.");
            if (pending is not null) throw new InvalidOperationException("A network command is already pending.");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { queue.Enqueue(new CpNetCommand(this, epoch, checked(++sequence), action, host, port)); }
            catch { pending = null; throw; }
            return pending.Task;
        }
    }

    public bool IsCurrent(CpNetCommand command)
    {
        lock (sync)
            return bound && !disconnected && ReferenceEquals(command.Bridge, this) &&
                command.Epoch == epoch && command.Request == sequence && pending is not null;
    }

    public void Publish(CpNetCommand command, CpNetSessionStatus status)
    {
        lock (sync)
        {
            if (!IsCurrent(command)) return;
            var completion = pending;
            pending = null;
            completion!.TrySetResult(status);
        }
    }

    public void Retire()
    {
        lock (sync)
        {
            bound = false;
            epoch = checked(epoch + 1);
            pending?.TrySetException(new InvalidOperationException("Network runtime was retired."));
            pending = null;
        }
    }

    public void Disconnect()
    {
        lock (sync)
        {
            disconnected = true;
            Retire();
        }
    }
}
