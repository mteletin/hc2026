using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network;

// Single-use run settings. The owner transfers this object to the start task or disposes it.
internal sealed class CpNetServerActivation : IDisposable
{
    private const int MaxIdentityBytes = 1024 * 1024;
    private readonly string imagePath;
    private readonly MediaImageDescriptor descriptor;
    private readonly Dpb parameters;
    private readonly string identityPath;
    private readonly IPEndPoint endpoint;
    private readonly byte serverId;
    private readonly int resourceDisk;
    private readonly byte[] loginPassword;
    private readonly char[] identityPassword;
    private int used;

    public CpNetServerActivation(string imagePath, MediaImageDescriptor descriptor, Dpb parameters,
        string identityPath, ReadOnlySpan<char> identityPassword, ReadOnlySpan<byte> loginPassword,
        IPEndPoint endpoint, byte serverId, int resourceDisk = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityPath);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!CpNetTlsConnection.IsUnicast(endpoint.Address)) throw new ArgumentException("A unicast address is required.", nameof(endpoint));
        if (endpoint.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint));
        if (serverId == 0xff) throw new ArgumentOutOfRangeException(nameof(serverId));
        if (resourceDisk is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(resourceDisk));
        if (loginPassword.Length != 8 || !loginPassword.ContainsAnyExcept((byte)0, (byte)' '))
            throw new ArgumentException("An explicit nonempty eight-byte CP/NET password is required.", nameof(loginPassword));
        this.imagePath = Path.GetFullPath(imagePath);
        this.descriptor = descriptor;
        this.parameters = parameters;
        this.identityPath = Path.GetFullPath(identityPath);
        this.endpoint = new(endpoint.Address, endpoint.Port);
        this.serverId = serverId;
        this.resourceDisk = resourceDisk;
        this.loginPassword = loginPassword.ToArray();
        this.identityPassword = identityPassword.ToArray();
    }

    public async Task<ICpNetInboundListener> StartAsync(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("Server settings were already used.");
        X509Certificate2? identity = null;
        CpNetListener? listener = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var export = await CaptureFileAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            identity = await LoadIdentityAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            listener = CpNetListener.Start(endpoint, export, identity, loginPassword, serverId, resourceDisk);
            var run = new CpNetServerRun(listener, identity);
            listener = null;
            identity = null;
            return run;
        }
        catch (Exception error)
        {
            if (listener is not null)
            {
                try { await listener.CloseAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    var retained = (listener, identity);
                    identity = null;
                    throw new CpNetStartCleanupException(new AggregateException(error, cleanup), retained);
                }
            }
            if (error is CpNetStartCleanupException)
            {
                var retained = (error, identity);
                identity = null;
                throw new CpNetStartCleanupException(error, retained);
            }
            throw;
        }
        finally
        {
            identity?.Dispose();
            ClearSecrets();
        }
    }

    private async Task<CpNetFrozenExport> CaptureFileAsync(CancellationToken cancellation)
    {
        if (NativeImageAdmission.PlatformMode == ImageAdmissionPlatformMode.LegacyOnly)
        {
            await using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous);
            return await CpNetFrozenExport.CaptureAsync(source, descriptor, parameters, cancellation).ConfigureAwait(false);
        }
        var result = NativeImageAdmission.TryAcquireColdRead(cancellation);
        using var admission = result.RequireAdmission();
        using var input = admission.OpenColdSource(imagePath);
        var facts = admission.ValidateOpenedSource(input, imagePath);
        var captured = await CpNetFrozenExport.CaptureAsync(input, descriptor, parameters, cancellation).ConfigureAwait(false);
        admission.ValidateSourceStillCurrent(input, facts);
        return captured;
    }

    private async Task<X509Certificate2> LoadIdentityAsync(CancellationToken cancellation)
    {
        if (NativeImageAdmission.PlatformMode == ImageAdmissionPlatformMode.LegacyOnly)
        {
            await using var source = new FileStream(identityPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous);
            return await LoadIdentityStreamAsync(source, cancellation).ConfigureAwait(false);
        }
        var result = NativeImageAdmission.TryAcquireColdRead(cancellation);
        using var admission = result.RequireAdmission();
        using var input = admission.OpenColdSource(identityPath);
        var facts = admission.ValidateOpenedSource(input, identityPath);
        var identity = await LoadIdentityStreamAsync(input, cancellation).ConfigureAwait(false);
        try { admission.ValidateSourceStillCurrent(input, facts); }
        catch { identity.Dispose(); throw; }
        return identity;
    }

    private async Task<X509Certificate2> LoadIdentityStreamAsync(Stream source, CancellationToken cancellation)
    {
        var length = source.Length;
        if (length is < 1 or > MaxIdentityBytes) throw new InvalidDataException("Invalid TLS identity size.");
        var bytes = new byte[checked((int)length)];
        try
        {
            await source.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
            if (source.Length != length || await source.ReadAsync(new byte[1], cancellation).ConfigureAwait(false) != 0)
                throw new InvalidDataException("TLS identity changed while loading.");
            return X509CertificateLoader.LoadPkcs12(bytes, identityPassword, X509KeyStorageFlags.EphemeralKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref used, 1) == 0) ClearSecrets();
    }

    private void ClearSecrets()
    {
        CryptographicOperations.ZeroMemory(loginPassword);
        Array.Clear(identityPassword);
    }
}

// The identity remains leased if close fails: the role and snapshot guard remain latched too.
internal sealed class CpNetServerRun(CpNetListener listener, X509Certificate2 identity) : ICpNetInboundListener
{
    private readonly object sync = new();
    private Task? closing;
    public CpNetPublicIdentity PublicIdentity { get; } = new(
        Display(identity.Subject), Display(identity.Extensions["2.5.29.17"]?.Format(false) ?? "none"),
        identity.NotAfter.ToUniversalTime(), identity.GetCertHashString(HashAlgorithmName.SHA256));
    public Task Completion => listener.Completion;
    public IPEndPoint EndPoint => listener.EndPoint;
    public int LoggedIn => listener.LoggedIn;

    public Task CloseAsync()
    {
        lock (sync) return closing ??= CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        await listener.CloseAsync().ConfigureAwait(false);
        identity.Dispose();
    }

    private static string Display(string value)
    {
        var trimmed = value.Length > 512 ? value[..512] + "…" : value;
        return string.Concat(trimmed.Select(character => char.IsControl(character) ? ' ' : character));
    }
}

internal sealed record CpNetPublicIdentity(string Subject, string SubjectAlternativeNames, DateTime ExpiresUtc, string Sha256);
