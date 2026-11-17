using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Hc2000.Machine.Network;

namespace Hc2000.App.Network;

// An explicit host-session connection; constructing a machine or selecting a profile never
// calls this. The operator supplies the target, and normal platform TLS validation applies.
internal sealed class CpNetTlsConnection : IDisposable, ICpNetRemoteConnection
{
    private readonly TcpClient client;
    private readonly SslStream stream;

    private CpNetTlsConnection(TcpClient client, SslStream stream)
    {
        this.client = client;
        this.stream = stream;
        Exchange = new CpNetStreamExchange(stream);
    }

    public CpNetStreamExchange Exchange { get; }

    public Task<CpNetFrame> ExchangeAsync(CpNetFrame request, CancellationToken cancellation) =>
        Exchange.ExchangeAsync(request, cancellation);

    public Task CloseAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public static async Task<CpNetTlsConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (!IsValidHost(host)) throw new ArgumentException("Invalid operator-selected host.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        // Resolve once and connect to one unicast address only: no discovery or scan.
        var address = addresses.FirstOrDefault(IsUnicast) ?? throw new IOException("No unicast address for the selected host.");
        var client = new TcpClient(address.AddressFamily);
        SslStream? stream = null;
        try
        {
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            stream = new SslStream(client.GetStream());
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken)
                .ConfigureAwait(false);
            return new(client, stream);
        }
        catch
        {
            try
            {
                try { stream?.Dispose(); }
                finally { client.Dispose(); }
            }
            catch (Exception cleanupFailure) { throw new CpNetConnectCleanupException(cleanupFailure); }
            throw;
        }
    }

    internal static bool IsValidHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Length <= 253 && host == host.Trim() &&
        Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    internal static bool IsUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast) return false;
        if (address.AddressFamily != AddressFamily.InterNetwork) return address.AddressFamily == AddressFamily.InterNetworkV6;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224;
    }

    public void Dispose()
    {
        try { stream.Dispose(); }
        finally { client.Dispose(); }
    }
}
