using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Hc2000.Machine.Expansion;

namespace Hc2000.App.Network;

/// <summary>The host side of the X6 network card (hc2000-ssh-terminal): it answers each request frame the card
/// carries, at once, and owns the sessions behind them. Connections leave only through the machine's
/// <see cref="OutboundGateway"/>; the guest never sees a socket, a key or an encrypted byte.
/// <para>Ops, version 1 (request [1, op, length, slot] + payload; reply [1, op, result, length] + payload):</para>
/// <list type="bullet">
/// <item>0x00 HELLO: payload version 1, slots, the largest SEND/RECV payload, gateway on (1) or off (0).</item>
/// <item>0x01 DEFAULT: the App's default connection: present (1) or not (0), login 0 password / 1 key, then the
/// text user@host:port (at most 58 characters, cut with '~'). The host never holds the password.</item>
/// <item>0x17 USE DEFAULT (slot): stages the default into the slot's fields (host, port, SSH, user, key name), as
/// PARAM would; the guest adds its screen size and, for a password login, the typed password, then OPENs.</item>
/// <item>0x18 SHARE (slot): the session's drive share: stage (0 none, 1 asked, 2 ready, 3 mounted, 4 unavailable),
/// the port the server bound (u16), then the shares, or the reason when unavailable (hc2000-remote-drive-mount).</item>
/// <item>0x10 PARAM (slot 1-4): field, bytes. Field 0 clears the slot; 1 host, 4 user, 5 password and 6 key name
/// append text, so long values span frames; 2 port (u16); 3 kind (0 TCP, 1 SSH, 2 TLS); 7 columns; 8 rows; 9 shared drives
/// (text, appended like the host: <c>B:RW,A:RO</c>, checked at OPEN, SSH only; see <see cref="X6DriveShares"/>). The key name is
/// the host's name for an SSH private key; the guest never holds the key.</item>
/// <item>0x11 OPEN (slot): starts connecting with the slot's fields; poll STATUS.</item>
/// <item>0x12 STATUS (slot): state (<see cref="X6SessionState"/>), bytes waiting (u16), then the reason as text. An
/// open SSH session's reason names the host key it trusted ("open SHA256:..."), an open TLS session its protocol and
/// the certificate's subject ("open Tls13 CN=...").</item>
/// <item>0x13 SEND (slot): the bytes to send; the reply's payload is the count taken (all or nothing).</item>
/// <item>0x14 RECV (slot): payload[0] = the most bytes wanted; the reply carries up to that many (none: empty).</item>
/// <item>0x15 RESIZE (slot): columns, rows (the terminal size an SSH session reports).</item>
/// <item>0x16 CLOSE (slot): closes and frees the slot.</item>
/// </list></summary>
public sealed class X6NetRuntime : IDisposable
{
    // One session per card in v1 (decision hc2000-x6-net-device-v1); the slot is in every frame, so more are additive.
    public const int Slots = 1, MaximumData = ExpansionX6NetCard.MaximumFrame - 4, ReceiveLimit = 64 * 1024;
    public const byte Hello = 0x00, Default = 0x01, UseDefault = 0x17, Share = 0x18, Param = 0x10, Open = 0x11, Status = 0x12, Send = 0x13, Recv = 0x14, Resize = 0x15, Close = 0x16;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private readonly OutboundGateway gateway;
    private readonly Func<X6SessionParameters, IX6Channel?, CancellationToken, Task<IX6Channel>>? sshConnector;
    private readonly RemoteCertificateValidationCallback? tlsValidation;
    private readonly X6DefaultConnection? defaults;
    private readonly Session?[] sessions = new Session?[Slots];
    private readonly X6SessionParameters[] parameters = Enumerable.Range(0, Slots).Select(_ => new X6SessionParameters()).ToArray();

    /// <param name="sshConnector">Opens an SSH channel over a gateway connection (kind 1); null refuses SSH.</param>
    /// <param name="tlsValidation">Decides about a TLS server's certificate (kind 2); null is the host system's trust.</param>
    public X6NetRuntime(OutboundGateway gateway,
        Func<X6SessionParameters, IX6Channel?, CancellationToken, Task<IX6Channel>>? sshConnector = null,
        RemoteCertificateValidationCallback? tlsValidation = null, X6DefaultConnection? defaults = null)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.sshConnector = sshConnector;
        this.tlsValidation = tlsValidation;
        this.defaults = defaults;
    }

    /// <summary>The card's host handler: one request frame in, its reply frame out. Never blocks.</summary>
    public byte[] Handle(byte[] request)
    {
        var op = request[1];
        var slot = request[3];
        var payload = request.AsSpan(4, request[2]);
        if (op == Hello)
            return ExpansionX6NetCard.Reply(op, X6NetResult.Ok, [1, Slots, MaximumData, (byte)(gateway.Enabled ? 1 : 0)]);
        if (op == Default)
        {
            if (defaults?.Current is not { } target) return ExpansionX6NetCard.Reply(op, X6NetResult.Ok, [0, 0]);
            var text = target.Text.Length > MaximumData - 2 ? target.Text[..(MaximumData - 3)] + "~" : target.Text;
            return ExpansionX6NetCard.Reply(op, X6NetResult.Ok, [1, (byte)(target.KeyName is null ? 0 : 1), .. Encoding.ASCII.GetBytes(text)]);
        }
        if (slot is < 1 or > Slots) return Reply(op, X6NetResult.Invalid);
        var index = slot - 1;
        switch (op)
        {
            case Param:
                lock (parameters) return parameters[index].Apply(payload) ? Reply(op, X6NetResult.Ok) : Reply(op, X6NetResult.Invalid);
            case Open:
                return OpenSession(index);
            case Share:
            {
                Session? sharing;
                lock (sessions) sharing = sessions[index];
                if (sharing?.Parameters.ShareState is not { } state) return ExpansionX6NetCard.Reply(op, X6NetResult.Ok, [0, 0, 0]);
                var (stage, port, text) = state.Snapshot();
                var words = Encoding.ASCII.GetBytes(text.Length > MaximumData - 3 ? text[..(MaximumData - 3)] : text);
                return ExpansionX6NetCard.Reply(op, X6NetResult.Ok, [(byte)stage, (byte)port, (byte)(port >> 8), .. words]);
            }
            case UseDefault:
                if (defaults?.Current is not { } chosen) return Reply(op, X6NetResult.Invalid);
                lock (parameters) parameters[index].Stage(chosen);
                return Reply(op, X6NetResult.Ok);
            case Status:
                return StatusOf(index);
            case Send:
            {
                var session = Live(index);
                if (session is null) return Reply(op, X6NetResult.NoSession);
                return session.TrySend(payload.ToArray())
                    ? ExpansionX6NetCard.Reply(op, X6NetResult.Ok, [(byte)payload.Length])
                    : ExpansionX6NetCard.Reply(op, X6NetResult.Busy, [0]);
            }
            case Recv:
            {
                var session = Live(index);
                if (session is null) return Reply(op, X6NetResult.NoSession);
                var wanted = payload.Length == 1 ? Math.Min((int)payload[0], MaximumData) : MaximumData;
                return ExpansionX6NetCard.Reply(op, X6NetResult.Ok, session.Take(wanted));
            }
            case Resize:
            {
                var session = Live(index);
                if (session is null) return Reply(op, X6NetResult.NoSession);
                if (payload.Length != 2 || payload[0] == 0 || payload[1] == 0) return Reply(op, X6NetResult.Invalid);
                session.Resize(payload[0], payload[1]);
                return Reply(op, X6NetResult.Ok);
            }
            case Close:
            {
                Session? session;
                lock (sessions) { session = sessions[index]; sessions[index] = null; }
                session?.Close();
                return Reply(op, session is null ? X6NetResult.NoSession : X6NetResult.Ok);
            }
            default:
                return Reply(op, X6NetResult.Unsupported);
        }
    }

    /// <summary>A session has news for the guest: the card's event port bit 7, so its driver can idle on one port read.</summary>
    public bool EventPending { get { lock (sessions) return sessions.Any(session => session?.News == true); } }

    /// <summary>Open sessions; while any exists the machine holds live external state (no snapshot).</summary>
    public int OpenSessions { get { lock (sessions) return sessions.Count(session => session is not null); } }

    public void Dispose()
    {
        Session?[] all;
        lock (sessions) { all = sessions.ToArray(); Array.Clear(sessions); }
        foreach (var session in all) session?.Dispose();
    }

    private byte[] OpenSession(int index)
    {
        X6SessionParameters chosen;
        lock (parameters) chosen = parameters[index].Copy();
        if (string.IsNullOrWhiteSpace(chosen.Host) || chosen.Port == 0 || chosen.Kind > 2) return Reply(Open, X6NetResult.Invalid);
        // Shared drives ride an SSH session only, and must name drives this machine can share.
        if (chosen.Shares.Trim().Length != 0 && (chosen.Kind != 1 || X6DriveShares.Parse(chosen.Shares, out _).IsEmpty))
            return Reply(Open, X6NetResult.Invalid);
        // The session's share, if it names one: the SSH side runs it, the guest and the panel read it.
        chosen.ShareState = chosen.Shares.Trim().Length == 0 ? null : new DriveShareState(chosen.Shares.Trim().ToUpperInvariant());
        if (!gateway.Admits(chosen.Host, chosen.Port, chosen.Purpose)) return Reply(Open, X6NetResult.GatewayOff);
        if (chosen.Kind == 1 && sshConnector is null) return Reply(Open, X6NetResult.Unsupported);
        Session session;
        lock (sessions)
        {
            if (sessions[index] is not null) return Reply(Open, X6NetResult.NoSlot);
            session = new Session(chosen);
            sessions[index] = session;
        }
        session.Start(gateway, sshConnector, tlsValidation);
        return Reply(Open, X6NetResult.Ok);
    }

    private byte[] StatusOf(int index)
    {
        Session? session;
        lock (sessions) session = sessions[index];
        if (session is null) return ExpansionX6NetCard.Reply(Status, X6NetResult.Ok, [(byte)X6SessionState.Free, 0, 0]);
        var (state, waiting, reason) = session.Observe();
        // The first SSH session that reaches its far end becomes the default when none is set.
        if (state == X6SessionState.Open && !session.Remembered)
        {
            session.Remembered = true;
            defaults?.RememberIfUnset(session.Parameters);
        }
        var text = Encoding.ASCII.GetBytes(reason.Length > MaximumData - 3 ? reason[..(MaximumData - 3)] : reason);
        var payload = new byte[3 + text.Length];
        payload[0] = (byte)state;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)Math.Min(waiting, ushort.MaxValue));
        text.CopyTo(payload, 3);
        return ExpansionX6NetCard.Reply(Status, X6NetResult.Ok, payload);
    }

    private Session? Live(int index)
    {
        lock (sessions) return sessions[index];
    }

    private static byte[] Reply(byte op, X6NetResult result) => ExpansionX6NetCard.Reply(op, result, []);

    /// <summary>One session: the connection (TCP, or SSH over TCP), a receive buffer filled by a read loop, and a
    /// send queue drained by a write loop. All state is behind its own lock.</summary>
    private sealed class Session(X6SessionParameters parameters) : IDisposable
    {
        public X6SessionParameters Parameters => parameters;
        public bool Remembered { get; set; }
        private readonly object sync = new();
        private readonly Queue<byte> received = new();
        private readonly Queue<byte[]> outgoing = new();
        private readonly SemaphoreSlim sendSignal = new(0);
        private readonly CancellationTokenSource stop = new();
        private X6SessionState state = X6SessionState.Connecting, observed = X6SessionState.Connecting;
        private string reason = "connecting to " + parameters.Host;
        private IX6Channel? channel;
        private OutboundConnection? connection;
        private int queued, inFlight;
        private static readonly TimeSpan FlushLimit = TimeSpan.FromSeconds(5);

        public void Start(OutboundGateway gateway, Func<X6SessionParameters, IX6Channel?, CancellationToken, Task<IX6Channel>>? ssh,
            RemoteCertificateValidationCallback? tlsValidation) =>
            _ = Task.Run(async () =>
            {
                try
                {
                    connection = await gateway.ConnectAsync(parameters.Host, parameters.Port,
                        parameters.Purpose, ConnectTimeout, stop.Token).ConfigureAwait(false);
                    if (!connection.Connected) { Fail(connection.Refusal!); return; }
                    var tcp = new X6StreamChannel(connection.Stream!);
                    channel = parameters.Kind switch
                    {
                        1 => await ssh!(parameters, tcp, stop.Token).ConfigureAwait(false),
                        2 => await X6TlsChannel.ConnectAsync(connection.Stream!, parameters.Host, tlsValidation, stop.Token).ConfigureAwait(false),
                        _ => tcp,
                    };
                    lock (sync)
                    {
                        if (state != X6SessionState.Connecting) return;
                        state = X6SessionState.Open;
                        reason = channel.OpenReason;
                    }
                    // Either loop failing ends the session with its reason (a lost connection fails the read while the
                    // write loop still waits for bytes); a read that ended cleanly has set ClosedByPeer itself.
                    Task[] loops = [ReadLoop(channel), WriteLoop(channel)];
                    await (await Task.WhenAny(loops).ConfigureAwait(false)).ConfigureAwait(false);
                    await Task.WhenAll(loops).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { Fail("failed: " + error.Message); }
            });

        private async Task ReadLoop(IX6Channel open)
        {
            var buffer = new byte[4096];
            while (!stop.IsCancellationRequested)
            {
                lock (sync)
                    if (received.Count >= ReceiveLimit)
                    {
                        Monitor.Wait(sync, 50);
                        continue;
                    }
                var read = await open.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    lock (sync)
                        if (state == X6SessionState.Open) { state = X6SessionState.ClosedByPeer; reason = "closed by the far end"; }
                    return;
                }
                lock (sync)
                    for (var i = 0; i < read; i++) received.Enqueue(buffer[i]);
            }
        }

        private async Task WriteLoop(IX6Channel open)
        {
            while (!stop.IsCancellationRequested)
            {
                await sendSignal.WaitAsync(stop.Token).ConfigureAwait(false);
                byte[] chunk;
                lock (sync)
                {
                    if (outgoing.Count == 0) continue;
                    chunk = outgoing.Dequeue();
                    queued -= chunk.Length;
                    inFlight = chunk.Length;
                }
                await open.WriteAsync(chunk, stop.Token).ConfigureAwait(false);
                lock (sync) { inFlight = 0; Monitor.PulseAll(sync); }
            }
        }

        /// <summary>The guest's CLOSE: the slot is free at once, and bytes a SEND already accepted still reach the far
        /// end before the connection goes (found by the throughput test: they used to be dropped). With nothing
        /// queued it closes now; otherwise the flush runs off the emulation thread, bounded by <see cref="FlushLimit"/>.</summary>
        public void Close()
        {
            bool now;
            lock (sync) now = state != X6SessionState.Open || (outgoing.Count == 0 && inFlight == 0);
            if (now)
            {
                Dispose();
                return;
            }
            _ = Task.Run(() =>
            {
                var until = DateTime.UtcNow + FlushLimit;
                lock (sync)
                    while ((outgoing.Count > 0 || inFlight > 0) && state == X6SessionState.Open && DateTime.UtcNow < until)
                        Monitor.Wait(sync, 50);
                Dispose();
            });
        }

        public bool TrySend(byte[] data)
        {
            lock (sync)
            {
                if (state != X6SessionState.Open || queued + data.Length > ReceiveLimit) return false;
                if (data.Length == 0) return true;
                outgoing.Enqueue(data);
                queued += data.Length;
            }
            sendSignal.Release();
            return true;
        }

        public byte[] Take(int wanted)
        {
            lock (sync)
            {
                var count = Math.Min(wanted, received.Count);
                var data = new byte[count];
                for (var i = 0; i < count; i++) data[i] = received.Dequeue();
                Monitor.PulseAll(sync);
                return data;
            }
        }

        public void Resize(byte columns, byte rows) => channel?.Resize(columns, rows);

        public (X6SessionState State, int Waiting, string Reason) Observe()
        {
            lock (sync)
            {
                observed = state;
                return (state, received.Count, reason);
            }
        }

        /// <summary>Bytes are waiting, or the state changed since the guest last asked for it.</summary>
        public bool News { get { lock (sync) return received.Count > 0 || state != observed; } }

        private void Fail(string why)
        {
            lock (sync)
            {
                if (state is X6SessionState.Closed) return;
                state = X6SessionState.Failed;
                reason = why;
            }
        }

        public void Dispose()
        {
            lock (sync) { state = X6SessionState.Closed; reason = "closed"; Monitor.PulseAll(sync); }
            stop.Cancel();
            channel?.Dispose();
            connection?.Dispose();
        }
    }
}

/// <summary>A session's state as STATUS reports it.</summary>
public enum X6SessionState : byte { Free = 0, Connecting = 1, Open = 2, ClosedByPeer = 3, Failed = 4, Closed = 5 }

/// <summary>A byte channel a session reads and writes: the TCP stream itself, or an SSH session over it.</summary>
public interface IX6Channel : IDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation);
    /// <summary>The terminal size changed (an SSH window-change; nothing for plain TCP).</summary>
    void Resize(int columns, int rows);

    /// <summary>What STATUS says once the session is open; an SSH session names the host key it trusted.</summary>
    string OpenReason => "open";
}

/// <summary>A TLS client session over a gateway connection (kind 2): the certificate is checked against the host
/// system's trust (or the runtime's callback) for the host name the guest gave; a refused certificate fails the
/// session with the reason.</summary>
internal sealed class X6TlsChannel(SslStream stream) : IX6Channel
{
    public string OpenReason { get; private init; } = "open";

    public static async Task<IX6Channel> ConnectAsync(Stream tcp, string host, RemoteCertificateValidationCallback? validation,
        CancellationToken cancellation)
    {
        var tls = new SslStream(tcp, leaveInnerStreamOpen: false, validation);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, limit.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException)
        {
            tls.Dispose();
            throw new IOException("certificate refused");
        }
        catch
        {
            tls.Dispose();
            throw;
        }
        return new X6TlsChannel(tls) { OpenReason = $"open {tls.SslProtocol} {tls.RemoteCertificate?.Subject}" };
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation) => stream.ReadAsync(buffer, cancellation);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation) => stream.WriteAsync(data, cancellation);
    public void Resize(int columns, int rows) { }
    public void Dispose() => stream.Dispose();
}

internal sealed class X6StreamChannel(Stream stream) : IX6Channel
{
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation) => stream.ReadAsync(buffer, cancellation);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation) => stream.WriteAsync(data, cancellation);
    public void Resize(int columns, int rows) { }
    public void Dispose() => stream.Dispose();
}

/// <summary>What one slot will connect to, filled field by field by PARAM.</summary>
public sealed class X6SessionParameters
{
    /// <summary>The longest host name a guest may give (decision hc2000-x6-net-device-v1); the host resolves it.</summary>
    public const int MaximumHost = 64;

    public string Host { get; private set; } = "";
    public ushort Port { get; private set; }
    public byte Kind { get; private set; }
    public string User { get; private set; } = "";
    public string Password { get; private set; } = "";
    /// <summary>The host's name for the SSH private key to log in with (field 6); empty for none.</summary>
    public string KeyName { get; private set; } = "";
    /// <summary>The drives this session shares with the remote host (field 9, e.g. <c>B:RW,A:RO</c>); empty shares
    /// nothing (hc2000-remote-drive-mount).</summary>
    public string Shares { get; private set; } = "";
    /// <summary>The live state of this session's share; set at OPEN when <see cref="Shares"/> names drives.</summary>
    public DriveShareState? ShareState { get; internal set; }
    public byte Columns { get; private set; } = 80;
    public byte Rows { get; private set; } = 24;
    /// <summary>What the gateway's log calls a connection of this kind.</summary>
    public string Purpose => Kind switch { 1 => "ssh", 2 => "tls", _ => "tcp" };

    internal X6SessionParameters Copy() => (X6SessionParameters)MemberwiseClone();

    /// <summary>USE DEFAULT: the default's host, port, user and key, as an SSH session; no password.</summary>
    internal void Stage(X6DefaultTarget target)
    {
        Host = target.Host; Port = target.Port; Kind = 1; User = target.User;
        Password = ""; KeyName = target.KeyName ?? "";
    }

    internal bool Apply(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return false;
        var value = payload[1..];
        var added = Encoding.ASCII.GetString(value);
        string Text(string current, int limit) =>
            current.Length + added.Length <= limit && added.All(c => c is >= ' ' and <= '~') ? current + added : throw new FormatException();
        try
        {
            switch (payload[0])
            {
                case 0:
                    if (value.Length != 0) return false;
                    Host = User = Password = KeyName = Shares = ""; Port = 0; Kind = 0; Columns = 80; Rows = 24;
                    return true;
                case 1: Host = Text(Host, MaximumHost); return true;
                case 2: if (value.Length != 2) return false; Port = BinaryPrimitives.ReadUInt16LittleEndian(value); return Port != 0;
                case 3: if (value.Length != 1 || value[0] > 2) return false; Kind = value[0]; return true;
                case 4: User = Text(User, 64); return true;
                case 5: Password = Text(Password, 128); return true;
                case 6: KeyName = Text(KeyName, 64); return true;
                case 7: if (value.Length != 1 || value[0] == 0) return false; Columns = value[0]; return true;
                case 8: if (value.Length != 1 || value[0] == 0) return false; Rows = value[0]; return true;
                case 9: Shares = Text(Shares, 64); return true;
                default: return false;
            }
        }
        catch (FormatException) { return false; }
    }
}
