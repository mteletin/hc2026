using Hc2000.App.Input;

namespace Hc2000.App.Network;

internal enum CpNetServerAction { Start, Stop, Observe }

public sealed record CpNetServerCommand : InputCommand
{
    internal CpNetServerCommand(CpNetServerBridge bridge, long epoch, long request, CpNetServerAction action)
        => (Bridge, Epoch, Request, Action) = (bridge, epoch, request, action);
    internal CpNetServerBridge Bridge { get; }
    internal long Epoch { get; }
    internal long Request { get; }
    internal CpNetServerAction Action { get; }
}

// Pending activation is owned here until the owner takes it, including stale/reset requests.
internal sealed class CpNetServerBridge(InputQueue queue)
{
    private readonly object sync = new();
    private TaskCompletionSource<CpNetServerStatus>? pending;
    private CpNetServerActivation? activation;
    private long epoch, sequence;
    private bool bound, disconnected;

    public void Bind()
    {
        lock (sync)
        {
            if (disconnected || bound) throw new InvalidOperationException("Server bridge cannot bind this runtime.");
            epoch = checked(epoch + 1);
            bound = true;
        }
    }

    public Task<CpNetServerStatus> RequestAsync(CpNetServerAction action, CpNetServerActivation? settings = null)
    {
        lock (sync)
        {
            var adopted = false;
            try
            {
                if (!Enum.IsDefined(action) || (action == CpNetServerAction.Start) != (settings is not null))
                    throw new ArgumentException("Invalid server command.");
                if (disconnected || !bound) throw new InvalidOperationException("Server runtime is not available.");
                if (pending is not null)
                {
                    if (ReferenceEquals(activation, settings)) settings = null;
                    throw new InvalidOperationException("A server command is already pending.");
                }
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                activation = settings;
                adopted = true;
                queue.Enqueue(new CpNetServerCommand(this, epoch, checked(++sequence), action));
                return pending.Task;
            }
            catch
            {
                if (adopted) { activation = null; pending = null; }
                settings?.Dispose();
                throw;
            }
        }
    }

    public bool IsCurrent(CpNetServerCommand command)
    {
        lock (sync) return bound && !disconnected && ReferenceEquals(command.Bridge, this) &&
            command.Epoch == epoch && command.Request == sequence && pending is not null;
    }

    public CpNetServerActivation? TakeActivation(CpNetServerCommand command)
    {
        lock (sync)
        {
            if (!IsCurrent(command)) return null;
            var result = activation;
            activation = null;
            return result;
        }
    }

    public void Publish(CpNetServerCommand command, CpNetServerStatus status)
    {
        lock (sync)
        {
            if (!IsCurrent(command)) return;
            activation?.Dispose();
            activation = null;
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
            activation?.Dispose(); activation = null;
            pending?.TrySetException(new InvalidOperationException("Server runtime was retired."));
            pending = null;
        }
    }

    public void Disconnect()
    {
        lock (sync) { disconnected = true; Retire(); }
    }
}
