using System.Collections.Immutable;
using Hc2000.App.Network.Sftp;
using Hc2000.App.Network.Ssh;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network;

/// <summary>One session's drive share, end to end (hc2000-remote-drive-mount): the SSH forward reports its port, and
/// each file channel the remote opens (its <c>sshfs -o directport=</c>) takes the shared drives off the machine,
/// serves them over SFTP v3 and, when the channel ends for any reason, gives the edited disks back exactly once
/// (decision hc2000-drive-sharing-sftp-v1 §1; the SSH side in hc2000-ssh-client-bcl-v1 amendment 2). Every step goes
/// to the gateway's log: the forward, each mount open and close, each file the remote wrote.</summary>
public sealed class DriveShareRun(DriveShareState state, ImmutableArray<X6DriveShare> shares, DriveShareMedia media,
    Action<string> log)
{
    private int mounts;

    /// <summary>The forward options the SSH connector hands to its client.</summary>
    public SshForwardOptions Options => new()
    {
        Bound = port =>
        {
            state.Set(DriveShareStage.Ready, (ushort)port);
            log($"share {state.Shares} ready on the remote's 127.0.0.1:{port}");
        },
        Unavailable = why =>
        {
            state.Set(DriveShareStage.Unavailable, why: why);
            log(why);
        },
        Opened = stream => _ = Task.Run(() => MountAsync(stream)),
        Closed = why => log($"share channel closed: {why}"),
    };

    /// <summary>Completes when the current mount (if any) has given its drives back; for tests and shutdown.</summary>
    public Task Settled { get; private set; } = Task.CompletedTask;

    private async Task MountAsync(Stream stream)
    {
        var done = new TaskCompletionSource();
        Settled = done.Task;
        Interlocked.Increment(ref mounts);
        var (_, port, _) = state.Snapshot();
        ImmutableArray<DriveShareMedia.Taken> taken;
        try { taken = await media.TakeAsync(shares).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            log($"share refused: {error.Message}");
            await stream.DisposeAsync().ConfigureAwait(false);
            done.SetResult();
            return;
        }
        var changed = new Dictionary<char, byte[]>();
        try
        {
            var set = new SftpShareSet(taken.Select(drive => new SftpShare(drive.Drive, CpmFs.Open(Dsk.Load(drive.Bytes)),
                drive.ReadWrite ? SftpShareMode.ReadWrite : SftpShareMode.ReadOnly, drive.Users)));
            state.Set(DriveShareStage.Mounted, port);
            log($"mount open: {string.Join(", ", taken.Select(drive => $"{drive.Drive}: {(drive.ReadWrite ? "read-write, off the machine" : "read-only")}"))}");
            var server = new SftpServer(set, log);
            try { await server.RunAsync(stream, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { log($"mount ended: {error.Message}"); }
            finally
            {
                foreach (var drive in set.Changed) changed[drive.Letter] = drive.Bytes();
                if (server.EndReason is { } reason) log($"mount ended: {reason}");
            }
        }
        finally
        {
            await media.ReturnAsync(changed).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            log($"mount closed: {(changed.Count == 0 ? "no drive changed" : "changed " + string.Join(", ", changed.Keys.Order().Select(d => d + ":")))}; the drives are back in the machine");
            if (state.Snapshot().Stage == DriveShareStage.Mounted) state.Set(DriveShareStage.Ready, port);
            done.SetResult();
        }
    }
}
