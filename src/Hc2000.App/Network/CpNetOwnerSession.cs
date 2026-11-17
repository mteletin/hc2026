namespace Hc2000.App.Network;

internal enum CpNetSessionState { Off, Armed, Connecting, Connected, Closing, Failed }
internal sealed record CpNetSessionStatus(CpNetSessionState State, bool CanConnect, string? Error = null, string? VerifiedHost = null);

// The emulation owner handles explicit operator commands and observes completion at boundaries.
// Connection attempts begin only after the runtime acquired its snapshot and host-gate claims.
internal sealed class CpNetOwnerSession(CpNetRuntime runtime,
    Func<string, int, CancellationToken, Task<ICpNetRemoteConnection>>? connect = null)
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private CpNetRemoteSession? remote;
    private string? failure;
    private bool stopping;

    public CpNetSessionStatus Start(string host, int port)
    {
        VerifyOwner();
        Pump();
        if (runtime.HasExternalState) return new(CpNetSessionState.Closing, false, "The previous connection is still active or closing.");
        if (!runtime.GuestAdmissionAvailable)
            return new(CpNetSessionState.Failed, false, "Cold restart required before another network connection.");
        CpNetRemoteSession next;
        try { next = new(host, port, connect); }
        catch (ArgumentException) { return new(CpNetSessionState.Failed, true, "Invalid server or port."); }
        if (!runtime.Start(next.ExchangeAsync, next.CloseAsync))
            return new(CpNetSessionState.Failed, false, "A previous network session is still closing or failed to close.");
        remote = next;
        failure = null;
        stopping = false;
        return Observe();
    }

    public CpNetSessionStatus Stop()
    {
        VerifyOwner();
        failure = null;
        stopping = true;
        runtime.Stop();
        return Observe();
    }

    public void Pump()
    {
        VerifyOwner();
        runtime.Pump();
        if (remote is null) return;
        if (!runtime.HasExternalState)
        {
            if (!stopping) failure ??= "Connection interrupted.";
            remote = null;
            stopping = false;
            return;
        }
        if (runtime.IsClosing)
        {
            if (runtime.CloseFailed) failure = "Connection could not close safely.";
            else if (!stopping) failure ??= "Connection interrupted.";
            return;
        }
        if (remote.State == CpNetRemoteSession.ConnectState.Failed)
        {
            failure = "Connection failed; check the server, port and TLS identity.";
            runtime.Stop();
        }
    }

    public CpNetSessionStatus Observe()
    {
        VerifyOwner();
        Pump();
        if (failure is not null) return new(CpNetSessionState.Failed, runtime.CanStart, failure);
        if (remote is null) return new(CpNetSessionState.Off, runtime.CanStart);
        if (runtime.IsClosing) return new(CpNetSessionState.Closing, false);
        var state = remote.State switch
        {
            CpNetRemoteSession.ConnectState.NotStarted => CpNetSessionState.Armed,
            CpNetRemoteSession.ConnectState.Connected => CpNetSessionState.Connected,
            _ => CpNetSessionState.Connecting
        };
        return new(state, false, VerifiedHost: remote.VerifiedHost);
    }

    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("CP/NET operator session belongs to the emulation owner.");
    }
}
