using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hc2000.App.Network;

// An explicitly started run. Its owner retains the certificate and external-state lease
// until CloseAsync succeeds; this object never starts itself or opens an export source.
internal sealed class CpNetListener : ICpNetInboundListener
{
    private const int Capacity = 8;
    private readonly object sync = new();
    private readonly TcpListener listener;
    private readonly CpNetFrozenExport export;
    private readonly X509Certificate2 certificate;
    private readonly byte[] password;
    private readonly byte serverId;
    private readonly int resourceDisk;
    private readonly CancellationTokenSource stopped = new();
    private readonly SemaphoreSlim slots = new(Capacity, Capacity);
    private readonly HashSet<TcpClient> sockets = [];
    private readonly HashSet<Task> workers = [];
    private readonly Task acceptLoop;
    private Task? closeTask;
    private Exception? cleanupError;
    private double tokens = Capacity;
    private long tokenTime = Stopwatch.GetTimestamp();
    private int loggedIn;

    private CpNetListener(TcpListener listener, CpNetFrozenExport export, X509Certificate2 certificate,
        byte[] password, byte serverId, int resourceDisk)
    {
        this.listener = listener;
        this.export = export;
        this.certificate = certificate;
        this.password = password;
        this.serverId = serverId;
        this.resourceDisk = resourceDisk;
        EndPoint = (IPEndPoint)listener.LocalEndpoint;
        acceptLoop = AcceptAsync();
    }

    public IPEndPoint EndPoint { get; }
    public int LoggedIn => Volatile.Read(ref loggedIn);
    public int Connections { get { lock (sync) return sockets.Count; } }
    public Task Completion => acceptLoop;

    public static CpNetListener Start(IPEndPoint bind, CpNetFrozenExport export, X509Certificate2 certificate,
        ReadOnlySpan<byte> password, byte serverId, int resourceDisk = 0)
    {
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(certificate);
        if (!CpNetTlsConnection.IsUnicast(bind.Address)) throw new ArgumentException("Bind requires a unicast address.", nameof(bind));
        if (password.Length != 8 || !password.ContainsAnyExcept((byte)0, (byte)' '))
            throw new ArgumentException("An explicit nonempty eight-byte CP/NET password is required.", nameof(password));
        if (serverId == 0xff) throw new ArgumentOutOfRangeException(nameof(serverId));
        if (resourceDisk is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(resourceDisk));
        ValidateCertificate(certificate);
        var listener = new TcpListener(bind);
        if (bind.Address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = false;
        var ownedPassword = password.ToArray();
        try
        {
            listener.Start(Capacity);
            return new(listener, export, certificate, ownedPassword, serverId, resourceDisk);
        }
        catch (Exception error)
        {
            CryptographicOperations.ZeroMemory(ownedPassword);
            try { listener.Stop(); }
            catch (Exception cleanup) { throw new CpNetStartCleanupException(new AggregateException(error, cleanup), listener); }
            throw;
        }
    }

    private static void ValidateCertificate(X509Certificate2 certificate)
    {
        var now = DateTime.UtcNow;
        if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            throw new ArgumentException("A currently valid certificate with a private key is required.", nameof(certificate));
        foreach (var extension in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            if (!extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == "1.3.6.1.5.5.7.3.1"))
                throw new ArgumentException("The certificate is not valid for server authentication.", nameof(certificate));
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopped.Token).ConfigureAwait(false);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                if (!TryAdmit()) { client.Dispose(); slots.Release(); continue; }
                lock (sync)
                {
                    sockets.Add(client);
                    var worker = ServeAsync(client);
                    workers.Add(worker);
                    _ = worker.ContinueWith(completed =>
                    {
                        lock (sync) workers.Remove(completed);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stopped.IsCancellationRequested) { }
        catch (SocketException) when (stopped.IsCancellationRequested) { }
        catch (Exception error) { RecordCleanupError(error); throw; }
        Exception? failure;
        lock (sync) failure = cleanupError;
        if (failure is not null) throw new InvalidOperationException("The CP/NET listener failed.", failure);
    }

    private bool TryAdmit()
    {
        lock (sync)
        {
            var now = Stopwatch.GetTimestamp();
            tokens = Math.Min(Capacity, tokens + (now - tokenTime) * (8.0 / Stopwatch.Frequency));
            tokenTime = now;
            if (tokens < 1) return false;
            tokens -= 1;
            return true;
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        SslStream? tls = null;
        var authenticated = false;
        try
        {
            tls = new SslStream(client.GetStream());
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, handshake.Token).ConfigureAwait(false);
            }
            stopped.Token.ThrowIfCancellationRequested();
            var resource = export.OpenDetached();
            stopped.Token.ThrowIfCancellationRequested();
            using var server = new CpNetStreamServer(tls, resource, password, serverId, resourceDisk, transition =>
            {
                if (transition == CpNetAuthenticationTransition.LoginAccepted && !authenticated)
                { authenticated = true; Interlocked.Increment(ref loggedIn); }
                else if (transition == CpNetAuthenticationTransition.LoggedOff && authenticated)
                { authenticated = false; Interlocked.Decrement(ref loggedIn); }
            }, TryAdmit);
            await server.RunAsync(stopped.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or AuthenticationException or
                                      SocketException or InvalidDataException or ObjectDisposedException) { }
        catch (Exception error) { RecordCleanupError(error); }
        finally
        {
            if (authenticated) Interlocked.Decrement(ref loggedIn);
            try { tls?.Dispose(); }
            catch (Exception error) { RecordCleanupError(error); }
            try { client.Dispose(); }
            catch (Exception error) { RecordCleanupError(error); }
            lock (sync) sockets.Remove(client);
            slots.Release();
        }
    }

    private void RecordCleanupError(Exception error)
    {
        lock (sync) cleanupError ??= error;
        stopped.Cancel();
        listener.Stop();
    }

    public Task CloseAsync()
    {
        lock (sync) return closeTask ??= CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        stopped.Cancel();
        listener.Stop();
        TcpClient[] active;
        lock (sync) active = sockets.ToArray();
        foreach (var client in active)
        {
            try { client.Dispose(); }
            catch (Exception error) { RecordCleanupError(error); }
        }
        try
        {
            try { await acceptLoop.ConfigureAwait(false); }
            catch (Exception error) { lock (sync) cleanupError ??= error; }
            Task[] remaining;
            lock (sync) remaining = workers.ToArray();
            try { await Task.WhenAll(remaining).ConfigureAwait(false); }
            catch (Exception error) { lock (sync) cleanupError ??= error; }
            Exception? failure;
            lock (sync) failure = cleanupError;
            if (failure is not null) throw new InvalidOperationException("A CP/NET run failed to close safely.", failure);
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }
}
