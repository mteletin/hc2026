using Hc2000.Machine.Network;

namespace Hc2000.App.Network;

// This is only the framed stream operation, not a connection or an authenticated gateway.
// The session owns the stream and must disconnect it after any uncertain operation.
internal sealed class CpNetStreamExchange
{
    private readonly Stream stream;
    private int state; // 0 available, 1 in flight, 2 failed

    public CpNetStreamExchange(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
    }

    public async Task<CpNetFrame> ExchangeAsync(CpNetFrame request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Format != 0) throw new ArgumentException("Expected a CP/NET request.", nameof(request));
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            throw new InvalidOperationException("The stream is busy or failed.");
        try
        {
            await stream.WriteAsync(CpNetStreamFraming.Encode(request), cancellationToken).ConfigureAwait(false);
            var prefix = new byte[2];
            await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            var length = prefix[0] | prefix[1] << 8;
            if (length < CpNetFrame.HeaderBytes + 1 || length > CpNetFrame.MaxFrameBytes)
                throw new InvalidDataException("Invalid CP/NET record length.");
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (!CpNetFrame.TryParse(bytes, out var response, out _) || response!.Format != 1 ||
                response.Destination != request.Source || response.Source != request.Destination)
                throw new InvalidDataException("Invalid CP/NET response.");
            Volatile.Write(ref state, 0);
            return response;
        }
        catch
        {
            // A write or read can fail after the remote side has acted; never reuse this stream.
            Volatile.Write(ref state, 2);
            throw;
        }
    }
}
