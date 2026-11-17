namespace Hc2000.App.Network;

internal enum CpNetServerState { Unknown, Stopped, Starting, Listening, Stopping, Failed }
internal sealed record CpNetServerStatus(CpNetServerState State, bool CanStart, int? LoggedIn = null,
    string? Address = null, string? Error = null, CpNetPublicIdentity? Identity = null);

// Commands and state observations run only on the emulation owner; activation and drain do not.
internal sealed class CpNetServerOwnerSession(CpNetInboundRuntime runtime)
{
    private readonly int owner = Environment.CurrentManagedThreadId;

    public CpNetServerStatus Start(CpNetServerActivation activation)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(activation);
        try
        {
            runtime.Pump();
            if (!runtime.Start(activation.StartAsync))
            {
                activation.Dispose();
                return Observe();
            }
            return Observe();
        }
        catch
        {
            activation.Dispose();
            throw;
        }
    }

    public CpNetServerStatus Stop()
    {
        VerifyOwner();
        runtime.Stop();
        runtime.ClearFailure();
        return Observe();
    }

    public CpNetServerStatus Observe()
    {
        VerifyOwner();
        runtime.Pump();
        if (runtime.CloseFailed) return new(CpNetServerState.Failed, false, Error: "Server could not close safely.");
        if (runtime.Failed) return new(CpNetServerState.Failed, runtime.CanStart, Error: "Server could not start or stopped unexpectedly.");
        if (runtime.IsClosing) return new(CpNetServerState.Stopping, false);
        if (runtime.Listening)
        {
            var run = runtime.Run;
            return new(CpNetServerState.Listening, false, run?.LoggedIn ?? 0, run?.EndPoint.ToString(), Identity: run?.PublicIdentity);
        }
        return runtime.HasExternalState ? new(CpNetServerState.Starting, false, 0) : new(CpNetServerState.Stopped, runtime.CanStart, 0);
    }

    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("CP/NET server session belongs to the emulation owner.");
    }
}
