using System.Net.Sockets;

namespace Hc2000.App.Network;

/// <summary>One line of the gateway's log: when, where to, what for, and what happened.</summary>
public sealed record OutboundLogEntry(DateTimeOffset At, string Host, int Port, string Purpose, string Result);

/// <summary>The machine's one door out (hc2000-ssh-terminal): outbound TCP to a host and port the guest names, NAT
/// style. It only ever connects out; nothing here listens, so nothing can reach the machine from outside. It is off
/// unless the owner switched it on for this machine; switching it off refuses new connections and closes the open
/// ones. Every attempt, its result and every close is logged, in memory and to an optional sink (the machine
/// folder's log file). Thread-safe: the UI switches it, the network runtime connects through it.</summary>
public sealed class OutboundGateway
{
    public const string Off = "refused: the gateway is off";
    private readonly object sync = new();
    private readonly List<OutboundLogEntry> log = [];
    private readonly List<TcpClient> open = [];
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<OutboundLogEntry>? sink;
    private bool enabled;

    public OutboundGateway(bool enabled = false, Action<OutboundLogEntry>? sink = null, Func<DateTimeOffset>? clock = null)
    {
        this.enabled = enabled;
        this.sink = sink;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised after each log line is added (on the thread that added it).</summary>
    public event Action<OutboundLogEntry>? Logged;

    public bool Enabled { get { lock (sync) return enabled; } }

    public int OpenConnections { get { lock (sync) return open.Count; } }

    public IReadOnlyList<OutboundLogEntry> Log { get { lock (sync) return log.ToArray(); } }

    /// <summary>The owner's switch. Off closes every open connection.</summary>
    public void SetEnabled(bool value)
    {
        TcpClient[] closing;
        lock (sync)
        {
            if (enabled == value) return;
            enabled = value;
            closing = value ? [] : open.ToArray();
            if (!value) open.Clear();
        }
        foreach (var client in closing) client.Dispose();
        Record("-", 0, "gateway", value ? "switched on" : $"switched off, {closing.Length} closed");
    }

    /// <summary>Whether a connection may be attempted now. Off, the refusal is logged like any other attempt, so the
    /// owner sees what the guest tried while the gateway was closed.</summary>
    public bool Admits(string host, int port, string purpose)
    {
        if (Enabled) return true;
        Refuse(host, port, purpose, Off);
        return false;
    }

    /// <summary>The owner's "close connections": every open connection is closed and the gateway stays as it is.</summary>
    public int CloseAll()
    {
        TcpClient[] closing;
        lock (sync)
        {
            closing = open.ToArray();
            open.Clear();
        }
        foreach (var client in closing) client.Dispose();
        Record("-", 0, "gateway", $"closed by the owner, {closing.Length} closed");
        return closing.Length;
    }

    /// <summary>Opens a TCP connection out to host:port, or refuses (gateway off, bad address, unreachable).</summary>
    public async Task<OutboundConnection> ConnectAsync(string host, int port, string purpose, TimeSpan timeout,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || port is < 1 or > 65535)
            return Refuse(host ?? "", port, purpose, "refused: not a host and port");
        if (!Enabled) return Refuse(host, port, purpose, Off);
        var client = new TcpClient();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(timeout);
            await client.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            client.Dispose();
            return Refuse(host, port, purpose, error is OperationCanceledException ? "failed: timed out" : "failed: " + error.Message);
        }
        lock (sync)
        {
            if (!enabled)
            {
                client.Dispose();
                return Refuse(host, port, purpose, Off);
            }
            open.Add(client);
        }
        Record(host, port, purpose, "connected");
        return new(this, client, host, port, purpose);
    }

    /// <summary>A line about a connection that is not the gateway's own doing (an SSH host-key decision, a key that
    /// could not be read), so the owner reads the whole story in one log.</summary>
    public void Note(string host, int port, string purpose, string result) => Record(host, port, purpose, result);

    internal void Closed(OutboundConnection connection)
    {
        bool known;
        lock (sync) known = open.Remove(connection.Client);
        if (known) Record(connection.Host, connection.Port, connection.Purpose, "closed");
    }

    private OutboundConnection Refuse(string host, int port, string purpose, string result)
    {
        Record(host, port, purpose, result);
        return new(result);
    }

    private void Record(string host, int port, string purpose, string result)
    {
        var entry = new OutboundLogEntry(clock(), host, port, purpose, result);
        lock (sync) log.Add(entry);
        sink?.Invoke(entry);
        Logged?.Invoke(entry);
    }
}

/// <summary>A connection the gateway opened, or the reason it did not.</summary>
public sealed class OutboundConnection : IDisposable
{
    private readonly OutboundGateway? gateway;
    private int disposed;

    internal OutboundConnection(string refusal) => Refusal = refusal;

    internal OutboundConnection(OutboundGateway gateway, TcpClient client, string host, int port, string purpose)
    {
        this.gateway = gateway;
        Client = client;
        Host = host;
        Port = port;
        Purpose = purpose;
        Stream = client.GetStream();
    }

    /// <summary>Null when connected; otherwise what the log says about the refusal.</summary>
    public string? Refusal { get; }
    public bool Connected => Refusal is null;
    public NetworkStream? Stream { get; }
    internal TcpClient Client { get; } = null!;
    public string Host { get; } = "";
    public int Port { get; }
    public string Purpose { get; } = "";

    public void Dispose()
    {
        if (Refusal is not null || Interlocked.Exchange(ref disposed, 1) != 0) return;
        Client.Dispose();
        gateway!.Closed(this);
    }
}
