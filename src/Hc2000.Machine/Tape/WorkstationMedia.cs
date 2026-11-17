using Hc2000.Machine.Storage;

namespace Hc2000.Machine.Tape;

/// <summary>
/// The combined-workstation A: system disk, composed from bytes. The base media is the
/// SHIPPED unbound-template disk (its TAPEINIT.COM is the assembled TPV1 template, not yet
/// bound); the composed image is session-specific and is never written to a file. Binding
/// is metadata only (TapeGuestInstaller); nothing here mounts a volume, publishes the
/// descriptor, or invents a guest operation port.
/// </summary>
public static class WorkstationMedia
{
    /// <summary>The shipped base disk name: the m-paged-v2 boot media with the current guest
    /// tools placed and the UNBOUND TAPEINIT template in TAPEINIT.COM.</summary>
    public const string BaseDiskName = "CPM22-HC-ram-paged-v2-workstation.DSK";

    /// <summary>The A: entry that carries the unbound template; the session-bound image the
    /// preparation emits is named this so it stays distinct from the shipped base.</summary>
    public const string PreparedDiskName = "CPM22-HC-ram-paged-v2-workstation-bound.DSK";

    /// <summary>The TAPEINIT.COM entry on the A: system disk.</summary>
    public const string TapeInitEntry = "TAPEINIT.COM";

    /// <summary>
    /// Bind the base disk's unbound TAPEINIT against the mounted live session and return the
    /// composed A: system disk. The base media must be the workstation base image: a readable
    /// disk whose TAPEINIT.COM is the unbound TPV1 template. A missing or already-bound entry,
    /// or a missing/unmounted/unlive session, is refused with the installer's own wording;
    /// the input bytes are never mutated.
    /// </summary>
    public static byte[] ComposeSystemDisk(ReadOnlySpan<byte> baseDisk, TapeVolumeSession session)
    {
        var media = Dsk.Load(baseDisk);
        var files = CpmFs.Open(media);
        byte[] template;
        try
        {
            template = files.Get(TapeInitEntry);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Not the workstation base media: {TapeInitEntry} is not on the disk.");
        }
        var bound = TapeGuestInstaller.Bind(template, session);
        files.Put(TapeInitEntry, bound);
        return media.Save();
    }
}
