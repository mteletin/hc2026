using System.Text;
using System.Threading.Channels;

namespace Hc2000.App.Network.Ssh;

/// <summary>Who an SSH session logs in as and how, the terminal it asks for, and who decides about the host key.</summary>
public sealed class SshClientOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string User { get; init; }

    /// <summary>For password (or keyboard-interactive) login; null skips both.</summary>
    public string? Password { get; init; }

    /// <summary>For publickey login, tried before the password; null skips it.</summary>
    public SshPrivateKey? Key { get; init; }

    public string Terminal { get; init; } = "xterm-256color";
    public int Columns { get; init; } = 80;
    public int Rows { get; init; } = 24;

    /// <summary>Asked once per connection, after the server has proved it holds the key: true trusts it, false refuses
    /// the host. Trust on first use, a known-hosts file or a question to the owner is the caller's policy.</summary>
    public required Func<SshHostKey, CancellationToken, ValueTask<bool>> TrustHostKey { get; init; }

    /// <summary>A drive share's remote forward and its file channel; null asks for none.</summary>
    public SshForwardOptions? Forward { get; init; }
}

/// <summary>A drive share's transport (decision hc2000-ssh-client-bcl-v1, amendment 2): once the shell runs, a remote
/// forward (RFC 4254 §7.1) on the server's own 127.0.0.1 at a port the server picks, and the file channel that a
/// connection to that port comes back on (§7.2, forwarded-tcpip), one at a time. Nothing else is forwarded, and the
/// client never connects anywhere for it. The calls come in order, never on the session's reader; they should not block.</summary>
public sealed class SshForwardOptions
{
    /// <summary>The server bound the forward: the port the remote's <c>sshfs -o directport=</c> connects to.</summary>
    public Action<int>? Bound { get; init; }

    /// <summary>There is no share and the terminal goes on: "share unavailable: " and why ("forward refused" from a
    /// server that does not forward, or the session's own reason when it ended first).</summary>
    public Action<string>? Unavailable { get; init; }

    /// <summary>A file channel opened: its bytes both ways as a stream, flow-controlled, with no SSH framing in them (the
    /// SFTP version exchange comes first). The stream is the caller's; disposing it closes the channel.</summary>
    public required Action<Stream> Opened { get; init; }

    /// <summary>The file channel is gone, and why: the server or the caller closed it, the forward was cancelled, or the
    /// session ended or was lost. Exactly once per opened channel, after its stream has ended; this is what puts the
    /// disk back in the drive.</summary>
    public required Action<string> Closed { get; init; }
}

/// <summary>An SSH-2 client session (RFC 4251-4254) over a TCP channel: ecdh-sha2-nistp256/384/521 key exchange,
/// ECDSA or RSA host keys, AES-GCM or AES-CTR with HMAC-SHA2, publickey then password (or keyboard-interactive) login,
/// and one session channel with a pty and a shell, whose bytes it carries as an <see cref="IX6Channel"/>. For a drive
/// share it also asks for a remote forward and answers the one file channel that comes back on it
/// (<see cref="SshForwardOptions"/>); channels are routed by our id, each with its own windows. A server's re-key is
/// followed; its global requests and channel requests are declined; a banner comes out before the shell's first bytes.
/// Any failure ends the session with a short reason (<see cref="SshException"/>).</summary>
public sealed class SshClient : IX6Channel
{
    public const string Version = "SSH-2.0-HC2000_1.0";

    /// <summary>Where the remote forward listens on the server: its own loopback, never a wider address.</summary>
    public const string ForwardAddress = "127.0.0.1";
    private const uint Window = 2 * 1024 * 1024, MaximumPacket = 32 * 1024;
    private readonly SshTransport transport;
    private readonly SshClientOptions options;
    /// <summary>Requests waiting for their answer, channel and global alike: a server answers in the order asked.</summary>
    private readonly Queue<(TaskCompletionSource<byte[]?> Reply, bool Global)> replies = new();
    private readonly CancellationTokenSource stop = new();
    private readonly object sync = new();
    /// <summary>The channel table, by our id: the session is 0, each file channel takes the next id.</summary>
    private readonly Dictionary<uint, ChannelEnd> channels = [];
    private readonly ChannelEnd session = new(0);
    private string serverVersion = "";
    private byte[]? sessionId;
    private bool strict;
    private string? failure;
    private ChannelEnd? file;
    private uint nextChannel = 1;
    private int? forwardPort;
    private int disposed;
    private Task resizes = Task.CompletedTask, notices = Task.CompletedTask;

    private SshClient(IX6Channel tcp, SshClientOptions options)
    {
        transport = new SshTransport(tcp);
        this.options = options;
        channels.Add(session.Ours, session);
    }

    /// <summary>The host key the caller trusted.</summary>
    public SshHostKey? HostKey { get; private set; }

    /// <summary>What the last key exchange agreed on.</summary>
    internal SshAlgorithms? Algorithms { get; private set; }

    /// <summary>Key exchanges done: the first, then one per re-key.</summary>
    public int KeyExchanges { get; private set; }

    /// <summary>The server's login banner, cleaned of control characters; it is also the stream's first bytes.</summary>
    public string? Banner { get; private set; }

    /// <summary>The shell's exit status, once the server sent it.</summary>
    public uint? ExitStatus { get; private set; }

    /// <summary>What STATUS shows once open: the fingerprint of the host key that was trusted.</summary>
    public string OpenReason => "open " + HostKey?.Fingerprint;

    /// <summary>The port the server bound for the drive share's forward, while it stands.</summary>
    public int? ForwardPort
    {
        get { lock (sync) return forwardPort; }
    }

    /// <summary>Runs the whole handshake (versions, key exchange, host key trust, login, session channel, pty, shell)
    /// and returns the open session, or fails with the reason.</summary>
    public static async Task<SshClient> ConnectAsync(IX6Channel tcp, SshClientOptions options, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        ArgumentNullException.ThrowIfNull(options);
        var client = new SshClient(tcp, options);
        try
        {
            await client.HandshakeAsync(cancellation).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation) => ReadAsync(session, buffer, cancellation);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation) => WriteAsync(session, data, cancellation);

    /// <summary>Tells the server the terminal's new size (window-change); sent in order, in the background.</summary>
    public void Resize(int columns, int rows)
    {
        var request = new SshWriter(SshMessage.ChannelRequest).UInt32(session.Theirs).String("window-change").Boolean(false)
            .UInt32((uint)columns).UInt32((uint)rows).UInt32(0).UInt32(0).ToArray();
        lock (sync)
            resizes = resizes.ContinueWith(_ => QuietlyAsync(() => SendOnAsync(session, request, stop.Token)), TaskScheduler.Default).Unwrap();
    }

    /// <summary>Stops the drive share and leaves the terminal as it is: the server stops listening
    /// (cancel-tcpip-forward), an open file channel closes ("forward cancelled"), and no other is taken.</summary>
    public async Task CancelForwardAsync(CancellationToken cancellation)
    {
        int port;
        ChannelEnd? open;
        lock (sync)
        {
            if (forwardPort is not { } bound) return;
            (port, forwardPort, open) = (bound, null, file);
        }
        if (open is not null) CloseChannel(open, "forward cancelled");
        await transport.SendAsync(CancelForward(port), cancellation).ConfigureAwait(false);
    }

    /// <summary>Ends the session without blocking: the forward is cancelled and a DISCONNECT goes out first when the
    /// line is free, then the connection closes.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Fail("closed");
        stop.Cancel();
        int? port;
        lock (sync) (port, forwardPort) = (forwardPort, null);
        if (serverVersion.Length > 0 && transport.WriteLock.Wait(0)) _ = GoodbyeAsync(port);
        else transport.Dispose();
    }

    /// <summary>cancel-tcpip-forward for a forward that stood, then DISCONNECT, by application (RFC 4253 §11.1), given a
    /// quarter of a second; the write lock is held.</summary>
    private async Task GoodbyeAsync(int? port)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            if (port is { } bound) await transport.WritePacketAsync(CancelForward(bound), limit.Token).ConfigureAwait(false);
            await transport.WritePacketAsync(new SshWriter(SshMessage.Disconnect).UInt32(11).String("closed").String("").ToArray(),
                limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            transport.WriteLock.Release();
            transport.Dispose();
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellation)
    {
        serverVersion = await transport.ExchangeVersionsAsync(Version, cancellation).ConfigureAwait(false);
        await RekeyAsync(null, cancellation).ConfigureAwait(false);
        await transport.SendAsync(new SshWriter(SshMessage.ServiceRequest).String("ssh-userauth").ToArray(), cancellation).ConfigureAwait(false);
        if ((await NextAsync(cancellation).ConfigureAwait(false))[0] != SshMessage.ServiceAccept) throw new SshException("auth refused");
        await AuthenticateAsync(cancellation).ConfigureAwait(false);
        if (Banner is not null)
        {
            // The banner reads like the shell's first bytes, but was never in the window.
            var bytes = Encoding.UTF8.GetBytes(Banner);
            session.Incoming.Writer.TryWrite(bytes);
            session.Unacknowledged = -bytes.Length;
        }
        await OpenSessionAsync(cancellation).ConfigureAwait(false);
        _ = Task.Run(ReadLoopAsync, CancellationToken.None);
        // A refused pty still gives a shell, as with OpenSSH; the terminal modes are left to the server.
        await RequestAsync(new SshWriter(SshMessage.ChannelRequest).UInt32(session.Theirs).String("pty-req").Boolean(true)
            .String(options.Terminal).UInt32((uint)options.Columns).UInt32((uint)options.Rows).UInt32(0).UInt32(0).String("\0"u8),
            global: false, cancellation).ConfigureAwait(false);
        if (await RequestAsync(new SshWriter(SshMessage.ChannelRequest).UInt32(session.Theirs).String("shell").Boolean(true), global: false,
                cancellation).ConfigureAwait(false) is null)
            throw new SshException("shell refused");
        // The share is asked for in the background: whatever the server says, the terminal is already up.
        if (options.Forward is { } forward) _ = ForwardAsync(forward);
    }

    /// <summary>tcpip-forward on <see cref="ForwardAddress"/> port 0 (RFC 4254 §7.1): the server picks the port and
    /// names it in its answer. A refusal, or the session ending first, is told as the share being unavailable.</summary>
    private async Task ForwardAsync(SshForwardOptions forward)
    {
        string reason;
        try
        {
            var reply = await RequestAsync(new SshWriter(SshMessage.GlobalRequest).String("tcpip-forward").Boolean(true)
                .String(ForwardAddress).UInt32(0), global: true, stop.Token).ConfigureAwait(false);
            if (reply is null) reason = "forward refused";
            else
            {
                var port = new SshReader(reply, 1).UInt32();
                lock (sync)
                {
                    reason = failure ?? (port is 0 or > ushort.MaxValue ? "bad forward port" : "");
                    if (reason.Length == 0)
                    {
                        forwardPort = (int)port;
                        Notify(() => forward.Bound?.Invoke((int)port));
                        return;
                    }
                }
            }
        }
        catch (SshException error) { reason = error.Message; }
        catch (OperationCanceledException) { reason = "closed"; }
        Notify(() => forward.Unavailable?.Invoke("share unavailable: " + reason));
    }

    private async Task RekeyAsync(byte[]? serverInit, CancellationToken cancellation)
    {
        await transport.WriteLock.WaitAsync(cancellation).ConfigureAwait(false);
        try { await KeyExchangeAsync(serverInit, cancellation).ConfigureAwait(false); }
        finally { transport.WriteLock.Release(); }
    }

    /// <summary>One ecdh-sha2-* key exchange (RFC 4253 §7-8, RFC 5656 §4), the first or a re-key the server started
    /// with <paramref name="serverInit"/>. Nothing else is sent meanwhile: the caller holds the write lock.</summary>
    private async Task KeyExchangeAsync(byte[]? serverInit, CancellationToken cancellation)
    {
        var initial = sessionId is null;
        var ours = SshKexInit.Build(initial ? [.. SshKex.KeyExchanges, SshKex.StrictClient] : SshKex.KeyExchanges,
            SshSignatures.Algorithms, SshKex.Ciphers, SshKex.Macs);
        await transport.WritePacketAsync(ours.Payload, cancellation).ConfigureAwait(false);
        serverInit ??= await NextKexAsync(SshMessage.KexInit, false, cancellation).ConfigureAwait(false);
        var server = SshKexInit.Parse(serverInit);
        if (initial)
        {
            strict = server.KeyExchanges.Contains(SshKex.StrictServer);
            if (strict && transport.ReceiveSequence != 1) throw new SshException("key exchange out of order");
        }
        var strictly = initial && strict;
        var algorithms = SshAlgorithms.Negotiate(ours, server);
        using var ecdh = new SshEcdh(algorithms.Kex);
        await transport.WritePacketAsync(new SshWriter(SshMessage.KexEcdhInit).String(ecdh.PublicPoint).ToArray(), cancellation)
            .ConfigureAwait(false);
        var reply = new SshReader(await NextKexAsync(SshMessage.KexEcdhReply, strictly, cancellation).ConfigureAwait(false), 1);
        var hostKey = reply.String();
        var serverPoint = reply.String();
        var signature = reply.String();
        var secret = ecdh.Agree(serverPoint);
        var hash = SshKex.ExchangeHash(ecdh.Hash, Version, serverVersion, ours.Payload, serverInit, hostKey, ecdh.PublicPoint,
            serverPoint, secret);
        if (!SshSignatures.Verify(hostKey, algorithms.HostKey, signature, hash)) throw new SshException("host key signature bad");
        if (initial)
        {
            HostKey = new SshHostKey(options.Host, options.Port, SshSignatures.KeyTypeOf(hostKey), hostKey);
            if (!await options.TrustHostKey(HostKey, cancellation).ConfigureAwait(false)) throw new SshException("host key refused");
            sessionId = hash;
        }
        else if (!hostKey.AsSpan().SequenceEqual(HostKey!.Blob)) throw new SshException("host key changed in re-key");
        await transport.WritePacketAsync([SshMessage.NewKeys], cancellation).ConfigureAwait(false);
        transport.UseSendCipher(SshKex.CreateCipher(algorithms, ecdh.Hash, secret, hash, sessionId!, clientToServer: true), strict);
        await NextKexAsync(SshMessage.NewKeys, strictly, cancellation).ConfigureAwait(false);
        transport.UseReceiveCipher(SshKex.CreateCipher(algorithms, ecdh.Hash, secret, hash, sessionId!, clientToServer: false), strict);
        Algorithms = algorithms;
        KeyExchanges++;
    }

    /// <summary>The next packet of a key exchange, which must be the one expected; under strict key exchange the first
    /// exchange takes nothing else in between.</summary>
    private async Task<byte[]> NextKexAsync(byte expected, bool strictly, CancellationToken cancellation)
    {
        while (true)
        {
            var message = await transport.ReadPacketAsync(cancellation).ConfigureAwait(false);
            if (message[0] == expected) return message;
            if (message[0] == SshMessage.Disconnect) throw Disconnected(message);
            if (!strictly && message[0] is SshMessage.Ignore or SshMessage.Debug) continue;
            throw new SshException("key exchange failed");
        }
    }

    /// <summary>The next message for the session. Transport chatter is dealt with here: ignored, a server's KEXINIT
    /// runs a re-key, a global request is declined, a banner is kept.</summary>
    private async Task<byte[]> NextAsync(CancellationToken cancellation)
    {
        while (true)
        {
            var message = await transport.ReadPacketAsync(cancellation).ConfigureAwait(false);
            switch (message[0])
            {
                case SshMessage.Disconnect:
                    throw Disconnected(message);
                case SshMessage.Ignore or SshMessage.Debug or SshMessage.Unimplemented or SshMessage.ExtInfo:
                    continue;
                case SshMessage.KexInit:
                    await RekeyAsync(message, cancellation).ConfigureAwait(false);
                    continue;
                case SshMessage.GlobalRequest:
                {
                    var request = new SshReader(message, 1);
                    request.Text();
                    if (request.Boolean()) await transport.SendAsync([SshMessage.RequestFailure], cancellation).ConfigureAwait(false);
                    continue;
                }
                case SshMessage.UserauthBanner:
                    Banner += Clean(new SshReader(message, 1).Text());
                    continue;
                default:
                    return message;
            }
        }
    }

    /// <summary>Login (RFC 4252): "none" first, to learn the methods; then the key, for each algorithm it signs with;
    /// then the password, or keyboard-interactive answered with it.</summary>
    private async Task AuthenticateAsync(CancellationToken cancellation)
    {
        SshWriter Request(string method) =>
            new SshWriter(SshMessage.UserauthRequest).String(options.User).String("ssh-connection").String(method);
        var methods = await TryAsync("none", Request("none"), cancellation).ConfigureAwait(false);
        if (methods is null) return;
        var tried = false;
        if (options.Key is { } key && methods.Contains("publickey"))
            foreach (var algorithm in key.Algorithms)
            {
                tried = true;
                var body = Request("publickey").Boolean(true).String(algorithm).String(key.PublicBlob).ToArray();
                var signature = key.Sign(algorithm, new SshWriter().String(sessionId!).Raw(body).ToArray());
                methods = await TryAsync("publickey", new SshWriter().Raw(body).String(signature), cancellation).ConfigureAwait(false);
                if (methods is null) return;
                if (!methods.Contains("publickey")) break;
            }
        if (options.Password is { } password)
        {
            var method = methods.Contains("password") ? "password" : methods.Contains("keyboard-interactive") ? "keyboard-interactive" : null;
            if (method is not null)
            {
                tried = true;
                var request = method == "password" ? Request(method).Boolean(false).String(password) : Request(method).String("").String("");
                if ((methods = await TryAsync(method, request, cancellation).ConfigureAwait(false)) is null) return;
            }
        }
        throw new SshException(tried ? "auth failed" : "auth failed, server offers " + string.Join(',', methods));
    }

    /// <summary>Sends one login request and follows it to its end: null when the server let us in, otherwise the
    /// methods it still takes.</summary>
    private async Task<string[]?> TryAsync(string method, SshWriter request, CancellationToken cancellation)
    {
        await transport.SendAsync(request.ToArray(), cancellation).ConfigureAwait(false);
        while (true)
        {
            var message = await NextAsync(cancellation).ConfigureAwait(false);
            switch (message[0])
            {
                case SshMessage.UserauthSuccess:
                    return null;
                case SshMessage.UserauthFailure:
                    return new SshReader(message, 1).NameList();
                case SshMessage.UserauthInfoRequest when method == "keyboard-interactive":
                    await transport.SendAsync(Answers(message), cancellation).ConfigureAwait(false);
                    break;
                case SshMessage.UserauthPasswordChange when method == "password":
                    throw new SshException("password expired");
                default:
                    throw new SshException("unexpected auth reply");
            }
        }
    }

    /// <summary>Keyboard-interactive (RFC 4256): a single prompt is answered with the password; no prompt, with none.</summary>
    private byte[] Answers(byte[] request)
    {
        var reader = new SshReader(request, 1);
        reader.Text();
        reader.Text();
        reader.Text();
        var prompts = reader.UInt32();
        if (prompts > 1) throw new SshException("auth wants more than a password");
        var answer = new SshWriter(SshMessage.UserauthInfoResponse).UInt32(prompts);
        if (prompts == 1) answer.String(options.Password!);
        return answer.ToArray();
    }

    private async Task OpenSessionAsync(CancellationToken cancellation)
    {
        await transport.SendAsync(new SshWriter(SshMessage.ChannelOpen).String("session").UInt32(session.Ours).UInt32(Window)
            .UInt32(MaximumPacket).ToArray(), cancellation).ConfigureAwait(false);
        var message = await NextAsync(cancellation).ConfigureAwait(false);
        var reader = new SshReader(message, 1);
        if (message[0] == SshMessage.ChannelOpenFailure)
        {
            reader.UInt32();
            reader.UInt32();
            throw new SshException("session refused: " + reader.Text());
        }
        if (message[0] != SshMessage.ChannelOpenConfirmation) throw new SshException("session refused");
        reader.UInt32();
        session.Theirs = reader.UInt32();
        session.TheirWindow = reader.UInt32();
        session.TheirPacket = Math.Clamp(reader.UInt32(), 1, MaximumPacket);
    }

    /// <summary>A channel request, or a <paramref name="global"/> one: the server's answer when it said yes, null when
    /// it said no.</summary>
    private async Task<byte[]?> RequestAsync(SshWriter request, bool global, CancellationToken cancellation)
    {
        var reply = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (failure is not null) throw new SshException(failure);
            replies.Enqueue((reply, global));
        }
        await transport.SendAsync(request.ToArray(), cancellation).ConfigureAwait(false);
        return await reply.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Reads the server's messages for as long as the session lives.</summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var message = await NextAsync(stop.Token).ConfigureAwait(false);
                var reader = new SshReader(message, 1);
                switch (message[0])
                {
                    case SshMessage.ChannelData:
                        Deliver(Find(reader.UInt32()), reader.String());
                        break;
                    case SshMessage.ChannelExtendedData:
                    {
                        var channel = Find(reader.UInt32());
                        reader.UInt32();
                        // A TCP channel has no stderr (RFC 4254 §5.2): only the session's reaches a reader.
                        if (channel == session) Deliver(channel, reader.String());
                        break;
                    }
                    case SshMessage.ChannelWindowAdjust:
                        Widen(Find(reader.UInt32()), reader.UInt32());
                        break;
                    case SshMessage.ChannelSuccess or SshMessage.ChannelFailure:
                        Answer(message, global: false);
                        break;
                    case SshMessage.RequestSuccess or SshMessage.RequestFailure:
                        Answer(message, global: true);
                        break;
                    case SshMessage.ChannelRequest:
                    {
                        var channel = Find(reader.UInt32());
                        var name = reader.Text();
                        var wantReply = reader.Boolean();
                        if (name == "exit-status" && channel == session) ExitStatus = reader.UInt32();
                        if (wantReply)
                            await QuietlyAsync(() => SendOnAsync(channel, new SshWriter(SshMessage.ChannelFailure).UInt32(channel.Theirs).ToArray(),
                                stop.Token)).ConfigureAwait(false);
                        break;
                    }
                    case SshMessage.ChannelEof:
                        Find(reader.UInt32()).Incoming.Writer.TryComplete();
                        break;
                    case SshMessage.ChannelClose:
                    {
                        var channel = Find(reader.UInt32());
                        await ClosedByServerAsync(channel).ConfigureAwait(false);
                        if (channel != session) break;
                        // The terminal is over and nothing reads on: a file channel ends with it.
                        ChannelEnd? open;
                        lock (sync) open = file;
                        if (open is not null) Abandon(open, "session closed");
                        return;
                    }
                    case SshMessage.ChannelOpen:
                        await AnswerOpenAsync(reader).ConfigureAwait(false);
                        break;
                    default:
                        await transport.SendAsync(new SshWriter(SshMessage.Unimplemented).UInt32(transport.ReceiveSequence - 1).ToArray(),
                            stop.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Fail(error switch
            {
                SshException => error.Message,
                IOException or ObjectDisposedException => "connection lost",
                _ => "session error: " + error.Message,
            });
        }
    }

    /// <summary>A channel the server opens. Only the file channel is taken: a forwarded-tcpip connection to the address
    /// and port we forwarded (RFC 4254 §7.2), while none is open. Everything else is refused with the reason, direct-tcpip
    /// included, which a client refuses (§7.2) and no server sends for a remote forward.</summary>
    private async Task AnswerOpenAsync(SshReader reader)
    {
        var type = reader.Text();
        var theirs = reader.UInt32();
        var window = reader.UInt32();
        var packet = reader.UInt32();
        (uint Code, string Reason) refusal = (1, "not here");
        ChannelEnd? channel = null;
        if (type == "forwarded-tcpip" && options.Forward is not null)
        {
            var address = reader.Text();
            var port = reader.UInt32();
            lock (sync)
            {
                if (failure is not null || forwardPort is null) refusal = (1, "no forward");
                else if (address != ForwardAddress || port != forwardPort) refusal = (1, "not the forwarded port");
                else if (file is not null) refusal = (4, "file channel busy");
                else
                {
                    channel = file = new ChannelEnd(nextChannel++)
                    {
                        Theirs = theirs,
                        TheirWindow = window,
                        TheirPacket = Math.Clamp(packet, 1, MaximumPacket),
                    };
                    channels.Add(channel.Ours, channel);
                }
            }
        }
        if (channel is null)
        {
            await transport.SendAsync(new SshWriter(SshMessage.ChannelOpenFailure).UInt32(theirs).UInt32(refusal.Code)
                .String(refusal.Reason).String("").ToArray(), stop.Token).ConfigureAwait(false);
            return;
        }
        await transport.SendAsync(new SshWriter(SshMessage.ChannelOpenConfirmation).UInt32(theirs).UInt32(channel.Ours).UInt32(Window)
            .UInt32(MaximumPacket).ToArray(), stop.Token).ConfigureAwait(false);
        lock (sync)
        {
            if (channel.Failure is not null) return;
            channel.Announced = true;
            var stream = new ChannelStream(this, channel);
            Notify(() =>
            {
                // A file service that cannot take the channel lets it go, so the slot frees.
                try { options.Forward!.Opened(stream); }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            });
        }
    }

    /// <summary>The server closed a channel: its reader ends, its writers stop, and it is closed back unless we closed
    /// it first. A file channel leaves the table, which frees the slot for the next.</summary>
    private async Task ClosedByServerAsync(ChannelEnd channel)
    {
        bool reply;
        lock (sync)
        {
            channel.TheirClose = true;
            Wake(channel);
            reply = !channel.OurClose;
            channel.OurClose = true;
            if (channel != session)
            {
                channels.Remove(channel.Ours);
                if (file == channel) file = null;
            }
        }
        channel.Incoming.Writer.TryComplete();
        if (reply)
            await transport.SendAsync(new SshWriter(SshMessage.ChannelClose).UInt32(channel.Theirs).ToArray(), stop.Token).ConfigureAwait(false);
        TellClosed(channel, "closed by the server");
    }

    /// <summary>We close a file channel (its stream was disposed, or the forward cancelled): its reader ends, and it
    /// stays in the table until the server's CLOSE comes back.</summary>
    private void CloseChannel(ChannelEnd channel, string reason)
    {
        bool send;
        lock (sync)
        {
            if (channel.OurClose) return;
            channel.OurClose = true;
            Wake(channel);
            send = channel.Failure is null;
        }
        channel.Incoming.Writer.TryComplete();
        if (send) _ = QuietlyAsync(() => transport.SendAsync(new SshWriter(SshMessage.ChannelClose).UInt32(channel.Theirs).ToArray(), stop.Token));
        TellClosed(channel, reason);
    }

    /// <summary>A channel the session can no longer carry: its reader and writers get <paramref name="reason"/>.</summary>
    private void Abandon(ChannelEnd channel, string reason)
    {
        lock (sync)
        {
            channel.Failure ??= reason;
            Wake(channel);
        }
        channel.Incoming.Writer.TryComplete();
        TellClosed(channel, reason);
    }

    /// <summary>Tells the share's caller that a file channel it was given is gone: once, whichever way it went.</summary>
    private void TellClosed(ChannelEnd channel, string reason)
    {
        lock (sync)
        {
            if (!channel.Announced || channel.Ended) return;
            channel.Ended = true;
            Notify(() => options.Forward!.Closed(reason));
        }
    }

    /// <summary>Tells the share's caller something, in order and off the reader; a caller's own failure stays its own.</summary>
    private void Notify(Action notice)
    {
        lock (sync) notices = notices.ContinueWith(_ => notice(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>A reply to the oldest request waiting, if it is of the kind this reply answers.</summary>
    private void Answer(byte[] message, bool global)
    {
        TaskCompletionSource<byte[]?> reply;
        lock (sync)
        {
            if (!replies.TryPeek(out var oldest) || oldest.Global != global) return;
            reply = replies.Dequeue().Reply;
        }
        reply.TrySetResult(message[0] is SshMessage.ChannelSuccess or SshMessage.RequestSuccess ? message : null);
    }

    private ChannelEnd Find(uint ours)
    {
        lock (sync) return channels.TryGetValue(ours, out var channel) ? channel : throw new SshException("no such channel");
    }

    private async ValueTask<int> ReadAsync(ChannelEnd channel, Memory<byte> buffer, CancellationToken cancellation)
    {
        while (channel.PendingOffset == channel.Pending.Length)
        {
            if (!await channel.Incoming.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
            {
                lock (sync) if (channel.Failure is not null) throw new SshException(channel.Failure);
                return 0;
            }
            if (channel.Incoming.Reader.TryRead(out var next)) (channel.Pending, channel.PendingOffset) = (next, 0);
        }
        var count = Math.Min(buffer.Length, channel.Pending.Length - channel.PendingOffset);
        channel.Pending.AsMemory(channel.PendingOffset, count).CopyTo(buffer);
        channel.PendingOffset += count;
        await AcknowledgeAsync(channel, count).ConfigureAwait(false);
        return count;
    }

    private async ValueTask WriteAsync(ChannelEnd channel, ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        while (!data.IsEmpty)
        {
            var count = await ReserveAsync(channel, data.Length, cancellation).ConfigureAwait(false);
            await SendOnAsync(channel, new SshWriter(SshMessage.ChannelData).UInt32(channel.Theirs).String(data.Span[..count]).ToArray(),
                cancellation).ConfigureAwait(false);
            data = data[count..];
        }
    }

    /// <summary>Sends a message on a channel we have not closed: nothing of ours follows our CLOSE, which the write lock
    /// orders after it.</summary>
    private async Task SendOnAsync(ChannelEnd channel, byte[] message, CancellationToken cancellation)
    {
        await transport.WriteLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock (sync) if (channel.OurClose) throw new SshException(channel.Failure ?? ClosedReason(channel));
            await transport.WritePacketAsync(message, cancellation).ConfigureAwait(false);
        }
        finally { transport.WriteLock.Release(); }
    }

    /// <summary>Data from the server, which must fit the window we gave it.</summary>
    private static void Deliver(ChannelEnd channel, byte[] data)
    {
        if (Interlocked.Add(ref channel.OurWindow, -data.Length) < 0) throw new SshException("server overran the window");
        if (data.Length > 0) channel.Incoming.Writer.TryWrite(data);
    }

    /// <summary>Gives the window back once half of it has been read, so the server keeps sending.</summary>
    private async Task AcknowledgeAsync(ChannelEnd channel, int count)
    {
        channel.Unacknowledged += count;
        if (channel.Unacknowledged < Window / 2) return;
        var adjust = channel.Unacknowledged;
        channel.Unacknowledged = 0;
        Interlocked.Add(ref channel.OurWindow, adjust);
        await QuietlyAsync(() => SendOnAsync(channel, new SshWriter(SshMessage.ChannelWindowAdjust).UInt32(channel.Theirs)
            .UInt32((uint)adjust).ToArray(), stop.Token)).ConfigureAwait(false);
    }

    /// <summary>Waits until the server's window has room and takes up to <paramref name="wanted"/> bytes of it.</summary>
    private async Task<int> ReserveAsync(ChannelEnd channel, int wanted, CancellationToken cancellation)
    {
        while (true)
        {
            Task opened;
            lock (sync)
            {
                if (channel.Failure is not null) throw new SshException(channel.Failure);
                if (channel.TheirClose || channel.OurClose) throw new SshException(ClosedReason(channel));
                if (channel.TheirWindow > 0)
                {
                    var count = (int)Math.Min(Math.Min((uint)wanted, channel.TheirWindow), channel.TheirPacket);
                    channel.TheirWindow -= (uint)count;
                    return count;
                }
                channel.WindowOpened ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                opened = channel.WindowOpened.Task;
            }
            await opened.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    private void Widen(ChannelEnd channel, uint bytes)
    {
        lock (sync)
        {
            channel.TheirWindow = (uint)Math.Min((ulong)channel.TheirWindow + bytes, uint.MaxValue);
            Wake(channel);
        }
    }

    /// <summary>Lets a channel's waiting writer look again; the lock is held.</summary>
    private static void Wake(ChannelEnd channel)
    {
        channel.WindowOpened?.TrySetResult();
        channel.WindowOpened = null;
    }

    private string ClosedReason(ChannelEnd channel) => channel == session ? "session closed" : "channel closed";

    /// <summary>Ends the session: every channel's readers and writers get <paramref name="reason"/>, waiting requests
    /// fail, and an open file channel is told closed.</summary>
    private void Fail(string reason)
    {
        (TaskCompletionSource<byte[]?> Reply, bool Global)[] waiting;
        ChannelEnd[] open;
        lock (sync)
        {
            failure ??= reason;
            waiting = [.. replies];
            replies.Clear();
            open = [.. channels.Values];
        }
        foreach (var (reply, _) in waiting) reply.TrySetException(new SshException(reason));
        foreach (var channel in open) Abandon(channel, reason);
    }

    /// <summary>A send whose failure the read loop reports anyway.</summary>
    private static async Task QuietlyAsync(Func<Task> send)
    {
        try { await send().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static byte[] CancelForward(int port) =>
        new SshWriter(SshMessage.GlobalRequest).String("cancel-tcpip-forward").Boolean(false).String(ForwardAddress).UInt32((uint)port).ToArray();

    private static SshException Disconnected(byte[] message)
    {
        var reader = new SshReader(message, 1);
        reader.UInt32();
        return new SshException("disconnected: " + reader.Text());
    }

    /// <summary>Banner text as the terminal may show it: CR LF line ends, no other control characters but tab.</summary>
    private static string Clean(string text)
    {
        var clean = new StringBuilder();
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
            if (c == '\n') clean.Append("\r\n");
            else if (c == '\t' || !char.IsControl(c)) clean.Append(c);
        return clean.ToString();
    }

    /// <summary>Our end of one channel: the ids each side calls it by, the window each side has left, the server's bytes
    /// waiting to be read, and how it ended. The client's lock guards it, except what only its one reader touches
    /// (<see cref="Pending"/>, <see cref="Unacknowledged"/>) and <see cref="OurWindow"/>, which is interlocked.</summary>
    private sealed class ChannelEnd(uint ours)
    {
        public readonly uint Ours = ours;
        public readonly Channel<byte[]> Incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public uint Theirs, TheirWindow, TheirPacket;
        public int OurWindow = (int)Window, Unacknowledged, PendingOffset;
        public byte[] Pending = [];
        public TaskCompletionSource? WindowOpened;
        /// <summary>The server sent CLOSE; we did (nothing of ours may follow it).</summary>
        public bool TheirClose, OurClose;
        /// <summary>Why the session dropped it.</summary>
        public string? Failure;
        /// <summary>The share's caller was told it opened; was told it closed.</summary>
        public bool Announced, Ended;
    }

    /// <summary>A file channel as a stream: reads end when the channel or the session does, writes wait for the server's
    /// window, and disposing it closes the channel.</summary>
    private sealed class ChannelStream(SshClient client, ChannelEnd channel) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            client.ReadAsync(channel, buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            client.WriteAsync(channel, buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) client.CloseChannel(channel, "closed");
            base.Dispose(disposing);
        }
    }
}

/// <summary>Makes the X6 runtime's SSH connector (PARAM kind 1): it logs in with the slot's user and password or the
/// key the slot names (field 6), opens a shell at the slot's terminal size, and fails a handshake that stalls.</summary>
public static class SshConnector
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <param name="trustHostKey">Decides about each server's host key: the caller's policy (trust on first use, a
    /// known-hosts file, a question to the owner).</param>
    /// <param name="keys">Finds the private key a slot names; null (or no answer) fails a slot that names one.</param>
    /// <param name="timeout">How long the handshake may take; <see cref="HandshakeTimeout"/> when null.</param>
    /// <param name="forward">The drive share a session asks for (hc2000-remote-drive-mount): its forward options when
    /// the session names shared drives, else null. The share's stop action cancels the forward and keeps the terminal.</param>
    public static Func<X6SessionParameters, IX6Channel?, CancellationToken, Task<IX6Channel>> Create(
        Func<SshHostKey, CancellationToken, ValueTask<bool>> trustHostKey, Func<string, SshPrivateKey?>? keys = null,
        TimeSpan? timeout = null, Func<X6SessionParameters, SshForwardOptions?>? forward = null)
    {
        ArgumentNullException.ThrowIfNull(trustHostKey);
        return async (parameters, tcp, cancellation) =>
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(tcp);
            var key = parameters.KeyName.Length == 0 ? null
                : keys?.Invoke(parameters.KeyName) ?? throw new SshException("no key named " + parameters.KeyName);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(timeout ?? HandshakeTimeout);
            try
            {
                var client = await SshClient.ConnectAsync(tcp, new SshClientOptions
                {
                    Host = parameters.Host,
                    Port = parameters.Port,
                    User = parameters.User,
                    Password = parameters.Password.Length > 0 ? parameters.Password : null,
                    Key = key,
                    Columns = parameters.Columns,
                    Rows = parameters.Rows,
                    TrustHostKey = trustHostKey,
                    Forward = forward?.Invoke(parameters),
                }, limit.Token).ConfigureAwait(false);
                if (parameters.ShareState is { } share)
                    share.Stop = async () =>
                    {
                        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await client.CancelForwardAsync(stopping.Token).ConfigureAwait(false);
                    };
                return client;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new SshException("timed out"); }
        };
    }
}
