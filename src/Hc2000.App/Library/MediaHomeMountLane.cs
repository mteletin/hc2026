using Hc2000.App.Media;

namespace Hc2000.App.Library;

/// <summary>
/// The media home mount lane (contract hc2000-media-home-scan-admission-v1, section 3): one action
/// takes a scanned media home row - its scanned path is the original - and mounts it as drive A: or
/// B: through the existing disk library lane only: DiskLibraryCatalog.RegisterAsync (the existing
/// closed admission), the existing working copy lane, and DiskLibraryMount.MountAsync (the existing
/// one-action mount through the existing App media path). A refusal at any step leaves nothing
/// mounted and the scanned row untouched: the scanned path is the original and is never the writable
/// target. Eject dispatches the existing MediaAction.EjectDisk through the same media path seam.
/// This class is not a mount engine, a second mount API or a third mechanism.
/// </summary>
public sealed class MediaHomeMountLane(DiskLibraryCatalog catalog, string libraryRoot, DiskLibraryMount mount)
{
    /// <summary>Dispatches the eject of one slot through the existing App media path (the media panel
    /// eject command). Null: the existing App media path is not wired and eject refuses fail-closed.</summary>
    public Func<DiskSlot, CancellationToken, Task<MediaReply>>? DispatchEject { get; set; }

    /// <summary>Mounts one scanned media home row as drive A: or B: through the existing library lane:
    /// the existing closed admission (RegisterAsync, the scanned path as the original), the existing
    /// working copy lane (the writable target), and the existing one-action mount (MountAsync).
    /// A refusal at any step is closed: nothing is mounted and the scanned row stays.</summary>
    public async Task<MediaHomeMountResult> MountFromScannedRowAsync(string scannedPath, DiskSlot slot, CancellationToken cancellation = default)
    {
        // Step 1: the existing closed admission decides the row. If it refuses, nothing mounts and
        // the row stays exactly as it was - no kind-guessing here.
        var registered = await catalog.RegisterAsync(scannedPath, "media-home-scan");
        if (registered.Reason != DiskLibraryRefusal.Accepted || registered.Entry is null)
            return new(MediaHomeMountOutcome.RegisterRefused,
                $"registration refused at the existing admission ({registered.Reason}): {registered.Message}",
                RegisterRefusal: registered.Reason);

        // Step 2: the existing working copy lane: the writable runtime target is the copy; the
        // scanned path is the original and is never a writable runtime target.
        var copyResult = await catalog.CreateWorkingCopyAsync(registered.Entry.Id, libraryRoot, cancellation: cancellation);
        if (copyResult.Status != DiskLibraryCopyStatus.Created || copyResult.Copy is null)
            return new(MediaHomeMountOutcome.CopyRefused,
                $"working copy refused ({copyResult.Status}): {copyResult.Message}", registered.Entry);

        // Step 3: the existing one-action mount through the existing App media path.
        var mountResult = await mount.MountAsync(copyResult.Copy.Id, slot, cancellation);
        if (mountResult.Outcome != DiskLibraryMountOutcome.Mounted)
            return new(MediaHomeMountOutcome.MountRefused,
                $"mount refused ({mountResult.Outcome}): {mountResult.Message}", registered.Entry, copyResult.Copy, Mount: mountResult);

        return new(MediaHomeMountOutcome.Mounted, mountResult.Message, registered.Entry, copyResult.Copy, Mount: mountResult);
    }

    /// <summary>Ejects drive A: or B: through the existing App media path; a refusal is closed.</summary>
    public Task<MediaHomeEjectResult> EjectAsync(DiskSlot slot, CancellationToken cancellation = default)
    {
        var eject = DispatchEject;
        if (eject is null)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(new MediaHomeEjectResult(false,
                $"eject refused fail-closed: the existing App media path is not wired (slot {slot})"));
        }
        return EjectSlotAsync(eject, slot, cancellation);
    }

    private static async Task<MediaHomeEjectResult> EjectSlotAsync(Func<DiskSlot, CancellationToken, Task<MediaReply>> eject, DiskSlot slot, CancellationToken cancellation)
    {
        MediaReply reply;
        try
        {
            reply = await eject(slot, cancellation);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new MediaHomeEjectResult(false, $"eject cancelled (slot {slot})");
        }
        if (reply is null)
            return new MediaHomeEjectResult(false, $"eject refused: the existing App media path returned no reply (slot {slot})");
        return new MediaHomeEjectResult(reply.Error is null, reply.Error ?? $"drive {slot} ejected", reply);
    }
}

/// <summary>Outcome of a media home mount lane action: Mounted only when the existing one-action
/// mount mounted; every other value is a closed refusal from the existing admission surface that
/// decided.</summary>
public enum MediaHomeMountOutcome
{
    Mounted,
    RegisterRefused,
    CopyRefused,
    MountRefused
}

/// <summary>The closed result of mounting one scanned media home row: the message names the refusing
/// step and the underlying refusal; the underlying results carry the full detail.</summary>
public sealed record MediaHomeMountResult(MediaHomeMountOutcome Outcome, string Message,
    DiskLibraryEntry? Entry = null, DiskLibraryCopy? Copy = null,
    DiskLibraryRefusal? RegisterRefusal = null, DiskLibraryMountResult? Mount = null);

/// <summary>The closed result of ejecting one slot through the existing App media path.</summary>
public sealed record MediaHomeEjectResult(bool Ejected, string Message, MediaReply? Reply = null);
