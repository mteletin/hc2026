using Hc2000.Machine.Network;

namespace Hc2000.App.Network;

internal interface ICpNetRemoteConnection
{
    Task<CpNetFrame> ExchangeAsync(CpNetFrame request, CancellationToken cancellation);
    Task CloseAsync();
}

internal sealed class CpNetConnectCleanupException(Exception inner) : IOException("CP/NET connection cleanup failed.", inner);

// One explicitly selected endpoint. No DNS or socket work happens until the first exchange;
// the runtime must already hold its external-state and host-gate claims at that point.
internal sealed class CpNetRemoteSession
{
    internal enum ConnectState { NotStarted, Connecting, Connected, Failed }
    private readonly object sync = new();
    private readonly string host;
    private readonly int port;
    private readonly Func<string, int, CancellationToken, Task<ICpNetRemoteConnection>> connect;
    private readonly CancellationTokenSource cancellation = new();
    private Task<ICpNetRemoteConnection>? connecting;
    private Task? closing;

    public ConnectState State
    {
        get
        {
            lock (sync)
                return connecting is null ? ConnectState.NotStarted :
                    !connecting.IsCompleted ? ConnectState.Connecting :
                    connecting.IsCompletedSuccessfully ? ConnectState.Connected : ConnectState.Failed;
        }
    }

    public string? VerifiedHost
    {
        get
        {
            lock (sync)
                return closing is null && connecting?.IsCompletedSuccessfully == true &&
                    connecting.Result is CpNetTlsConnection ? host : null;
        }
    }

    public CpNetRemoteSession(string host, int port,
        Func<string, int, CancellationToken, Task<ICpNetRemoteConnection>>? connect = null)
    {
        if (!CpNetTlsConnection.IsValidHost(host)) throw new ArgumentException("Invalid operator-selected host.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.host = host;
        this.port = port;
        this.connect = connect ?? ConnectTlsAsync;
    }

    public async Task<CpNetFrame> ExchangeAsync(CpNetFrame request, CancellationToken cancellationToken)
    {
        Task<ICpNetRemoteConnection> pending;
        CancellationTokenSource linked;
        lock (sync)
        {
            if (closing is not null) throw new InvalidOperationException("Session is closed.");
            cancellationToken.ThrowIfCancellationRequested();
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
            if (connecting is null)
            {
                connecting = Task.Run(() => connect(host, port, cancellation.Token));
                _ = connecting.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            pending = connecting;
        }
        using (linked)
        {
            var connection = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return await connection.ExchangeAsync(request, linked.Token).ConfigureAwait(false);
        }
    }

    public Task CloseAsync()
    {
        lock (sync)
        {
            var pending = connecting;
            return closing ??= Task.Run(() => CloseCoreAsync(pending));
        }
    }

    private async Task CloseCoreAsync(Task<ICpNetRemoteConnection>? pending)
    {
        Exception? cancelFailure = null;
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { cancelFailure = ex; }
        try
        {
            if (pending is not null)
            {
                ICpNetRemoteConnection? connection;
                try { connection = await pending.ConfigureAwait(false); }
                catch (CpNetConnectCleanupException) { throw; }
                catch { connection = null; } // A failed connector must clean up its own partial resources.
                if (connection is not null) await connection.CloseAsync().ConfigureAwait(false);
            }
        }
        finally { cancellation.Dispose(); }
        if (cancelFailure is not null) throw new InvalidOperationException("CP/NET cancellation failed.", cancelFailure);
    }

    private static async Task<ICpNetRemoteConnection> ConnectTlsAsync(string host, int port, CancellationToken cancellation) =>
        await CpNetTlsConnection.ConnectAsync(host, port, cancellation).ConfigureAwait(false);
}
