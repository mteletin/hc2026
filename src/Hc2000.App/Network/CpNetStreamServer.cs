using Hc2000.Machine.Network;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network;

// One connection owns one logical session. The caller supplies an already authenticated
// stream and exclusive access to an explicitly selected resource; this class never opens a
// listener or a disk. The caller closes the stream after this operation ends or fails.
internal sealed class CpNetStreamServer : IDisposable
{
    private readonly Stream stream;
    private readonly CpNetLogicalServer server;
    private readonly byte serverId;
    private readonly Action<CpNetAuthenticationTransition>? authenticationChanged;
    private readonly Func<bool>? admitAfterLogoff;
    private readonly TimeSpan deadline;
    private int started;

    public CpNetStreamServer(Stream stream, CpmFs resource, ReadOnlySpan<byte> password,
        byte serverId, int resourceDisk = 0, Action<CpNetAuthenticationTransition>? authenticationChanged = null,
        Func<bool>? admitAfterLogoff = null, TimeSpan? deadline = null)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.deadline = deadline ?? TimeSpan.FromSeconds(10);
        if (this.deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        server = new(resource, password, resourceDisk);
        this.serverId = serverId;
        this.authenticationChanged = authenticationChanged;
        this.admitAfterLogoff = admitAfterLogoff;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("The CP/NET server connection is single-use.");
        CancellationTokenSource? loginDeadline = null;
        try
        {
            loginDeadline = StartDeadline(cancellationToken);
            var prefix = new byte[2];
            while (true)
            {
                var phaseToken = loginDeadline?.Token ?? cancellationToken;
                var count = await stream.ReadAsync(prefix.AsMemory(0, 1), phaseToken).ConfigureAwait(false);
                if (count == 0) return;
                using var frameDeadline = StartDeadline(phaseToken);
                await stream.ReadExactlyAsync(prefix.AsMemory(1, 1), frameDeadline.Token).ConfigureAwait(false);
                var length = prefix[0] | prefix[1] << 8;
                if (length < CpNetFrame.HeaderBytes + 1 || length > CpNetFrame.MaxFrameBytes)
                    throw new InvalidDataException("Invalid CP/NET record length.");
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, frameDeadline.Token).ConfigureAwait(false);
                if (!CpNetFrame.TryParse(bytes, out var request, out _) || request!.Format != 0 ||
                    request.Destination != serverId)
                    throw new InvalidDataException("Invalid CP/NET request.");
                var response = server.Handle(request) ?? throw new InvalidDataException("Unanswerable CP/NET request.");
                await stream.WriteAsync(CpNetStreamFraming.Encode(response), frameDeadline.Token).ConfigureAwait(false);
                var transition = server.AuthenticationTransition;
                if (transition == CpNetAuthenticationTransition.None) continue;
                authenticationChanged?.Invoke(transition);
                if (transition == CpNetAuthenticationTransition.LoginRejected) return;
                if (transition == CpNetAuthenticationTransition.LoginAccepted)
                {
                    loginDeadline?.Dispose();
                    loginDeadline = null;
                }
                else if (transition == CpNetAuthenticationTransition.LoggedOff)
                {
                    if (admitAfterLogoff is not null && !admitAfterLogoff()) return;
                    loginDeadline = StartDeadline(cancellationToken);
                }
            }
        }
        finally { loginDeadline?.Dispose(); server.Dispose(); }
    }

    private CancellationTokenSource StartDeadline(CancellationToken cancellationToken)
    {
        var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(deadline);
        return timer;
    }

    public void Dispose() => server.Dispose();
}
