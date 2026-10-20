using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Media;

/// <summary>Bounded in-memory native codec adapter. Does not open files, mount media,
/// convert formats or grant writable bindings; descriptor propagation remains a host responsibility.</summary>
public static class RawFloppyAdapter
{
    public static Dsk Load(ReadOnlySpan<byte> bytes, MediaImageDescriptor descriptor, DriveProfile device)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.ValidateForFloppy(device);
        descriptor.ValidateLength(bytes.Length);
        // Native Dsk.Load prefers a container header even when given geometry: refuse that
        // ambiguity explicitly instead of silently interpreting an explicit raw image as DSK.
        if (descriptor.Kind == MediaImageKind.RawFloppy &&
            (bytes.StartsWith("EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8) ||
             bytes.StartsWith("MV - CPCEMU Disk-File\r\nDisk-Info\r\n"u8)))
            throw new InvalidDataException("Container header conflicts with explicit raw floppy descriptor.");
        var disk = Dsk.Load(bytes, descriptor.Geometry);
        ValidateDisk(disk, descriptor, device);
        return disk;
    }

    public static byte[] Save(Dsk disk, MediaImageDescriptor descriptor, DriveProfile device)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.ValidateForFloppy(device);
        ValidateDisk(disk, descriptor, device);
        var bytes = disk.Save(); // Preserve native flat/container representation; never ToContainer/ToRaw.
        // Revalidate serialized bytes too: mutable sector data must not introduce header ambiguity.
        Load(bytes, descriptor, device);
        return bytes;
    }

    private static void ValidateDisk(Dsk disk, MediaImageDescriptor descriptor, DriveProfile device)
    {
        if (disk.Cylinders > device.Cylinders || disk.Sides > device.Sides)
            throw new InvalidDataException("Image exceeds the selected floppy device.");
        if (descriptor.Kind == MediaImageKind.RawFloppy)
        {
            if (disk.Format != DiskFormat.Raw || disk.UniformGeometry() != descriptor.Geometry)
                throw new InvalidDataException("Disk format/geometry does not match the raw descriptor.");
        }
        else if (disk.Format == DiskFormat.Raw)
            throw new InvalidDataException("A raw disk cannot be saved as a container without explicit conversion.");
    }
}
