using System.Collections.Immutable;
using Hc2000.App.Media;

namespace Hc2000.App.Network;

/// <summary>The media a remote mount works on (hc2000-remote-drive-mount, decision hc2000-drive-sharing-sftp-v1 §1):
/// one owner of a drive's bytes at a time. Taking a read-write share puts the disk out of the drive (the writeback
/// eject when the drive is bound to its file, otherwise a plain eject of the bytes just exported), so the machine
/// sees not-ready and never writes it; giving it back puts the edited bytes in again the way a person does, which
/// raises the media change. A read-only share is a snapshot of the machine's current bytes and is never ejected.
/// The media operations are the App's own, handed in, so this owns only the order and the exactly-once give-back.</summary>
public sealed class DriveShareMedia(DriveShareMedia.Operations media)
{
    /// <summary>The App's media paths for one machine.</summary>
    public sealed record Operations(
        Func<DiskSlot, Task<MediaReply>> Inspect,
        Func<DiskSlot, Task<ImmutableArray<byte>>> Export,
        Func<DiskSlot, Task<bool>> WritebackEject,
        Func<DiskSlot, Task> Eject,
        Func<DiskSlot, byte[], string?, Task> InsertBytes,
        Func<DiskSlot, string, byte[], Task> SaveAndInsertFromFile,
        Func<DiskSlot, string?> BoundPath,
        Func<DiskSlot, bool> IsProtectedOriginal,
        Func<DiskSlot, bool, Task> Hold);

    /// <summary>One drive while it is shared: its bytes, where they came from, and how they go back.</summary>
    public sealed record Taken(char Drive, bool ReadWrite, ImmutableArray<int> Users, byte[] Bytes, string? Name, string? BoundPath);

    private readonly object sync = new();
    private ImmutableArray<Taken> taken = [];
    private bool returned = true;

    public ImmutableArray<Taken> Current { get { lock (sync) return taken; } }

    /// <summary>Takes the shares' media; refuses (with the reason, taking nothing) a drive that is empty, a read-write
    /// share of a protected original, or one whose eject did not leave the drive empty.</summary>
    public async Task<ImmutableArray<Taken>> TakeAsync(ImmutableArray<X6DriveShare> shares)
    {
        lock (sync)
            if (!returned) throw new InvalidOperationException("The drives are already shared.");
        var result = ImmutableArray.CreateBuilder<Taken>();
        foreach (var share in shares)
        {
            var slot = share.Drive == 'A' ? DiskSlot.A : DiskSlot.B;
            var view = Pick(await media.Inspect(slot), slot);
            if (!view.Present) { await GiveBackAsync(result.ToImmutable(), new Dictionary<char, byte[]>()); throw new InvalidOperationException($"{share.Drive}: no disk in the drive"); }
            if (share.ReadWrite && media.IsProtectedOriginal(slot))
            {
                await GiveBackAsync(result.ToImmutable(), new Dictionary<char, byte[]>());
                throw new InvalidOperationException($"{share.Drive}: a protected original is shared read-only only");
            }
            // A read-write share holds the slot first (contract v3 §1d), so nothing else can put a disk in or take it
            // out between the export and the eject, nor while the mount is up.
            if (share.ReadWrite) await media.Hold(slot, true);
            byte[] bytes;
            string? bound;
            try
            {
                bytes = (await media.Export(slot)).ToArray();
                bound = media.BoundPath(slot);
                if (share.ReadWrite)
                {
                    if (!(bound is not null && await media.WritebackEject(slot))) await media.Eject(slot);
                    if (Pick(await media.Inspect(slot), slot).Present)
                        throw new InvalidOperationException($"{share.Drive}: the disk did not leave the drive; nothing is shared");
                }
            }
            catch
            {
                if (share.ReadWrite) await media.Hold(slot, false);
                await GiveBackAsync(result.ToImmutable(), new Dictionary<char, byte[]>());
                throw;
            }
            result.Add(new(share.Drive, share.ReadWrite, share.Users, bytes, view.Name, share.ReadWrite ? bound : null));
        }
        lock (sync) { taken = result.ToImmutable(); returned = false; }
        return taken;
    }

    /// <summary>Puts every read-write share back, with its edited bytes when the mount changed them; called on unmount,
    /// on a dropped session and on the stop action, and does its work exactly once.</summary>
    public async Task ReturnAsync(IReadOnlyDictionary<char, byte[]> changed)
    {
        ImmutableArray<Taken> giving;
        lock (sync)
        {
            if (returned) return;
            returned = true;
            giving = taken;
            taken = [];
        }
        await GiveBackAsync(giving, changed);
    }

    private async Task GiveBackAsync(ImmutableArray<Taken> giving, IReadOnlyDictionary<char, byte[]> changed)
    {
        foreach (var drive in giving.Where(drive => drive.ReadWrite))
        {
            var slot = drive.Drive == 'A' ? DiskSlot.A : DiskSlot.B;
            var bytes = changed.TryGetValue(drive.Drive, out var edited) ? edited : drive.Bytes;
            // Bound to its file: write the file through the App's publication path, then insert it from the file, which
            // binds writeback again. Otherwise the edited bytes go back into the drive, as an inserted disk would.
            try
            {
                if (drive.BoundPath is { } path) await media.SaveAndInsertFromFile(slot, path, bytes);
                else await media.InsertBytes(slot, bytes, drive.Name);
            }
            finally { await media.Hold(slot, false); }
        }
    }

    private static DriveView Pick(MediaReply reply, DiskSlot slot) => slot == DiskSlot.A ? reply.A : reply.B;
}
