using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Media;

public enum MediaImageKind { DskContainer, RawFloppy, HddRaw8MiB }

/// <summary>Explicit App format choice, not file identity, mount authority or writer eligibility.
/// No filename or byte length selects a format or supplies missing geometry.</summary>
public sealed record MediaImageDescriptor
{
    private MediaImageDescriptor(MediaImageKind kind, DiskGeometry? geometry = null)
        => (Kind, Geometry) = (kind, geometry);

    public MediaImageKind Kind { get; }
    public DiskGeometry? Geometry { get; }
    public static MediaImageDescriptor DskContainer { get; } = new(MediaImageKind.DskContainer);
    public static MediaImageDescriptor HddRaw8MiB { get; } = new(MediaImageKind.HddRaw8MiB);

    public static MediaImageDescriptor RawFloppy(DiskGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        geometry.Validate();
        // Validate before multiplication; even hostile int inputs cannot wrap into an accepted size.
        var length = checked(geometry.Cylinders * geometry.Sides * geometry.Sectors * geometry.SectorSize);
        if (length > MediaFiles.MaxDiskBytes)
            throw new InvalidDataException("Raw floppy exceeds the App disk size limit.");
        return new(MediaImageKind.RawFloppy, geometry);
    }

    /// <summary>Validate the observed byte count before decoding; host admission must separately
    /// reject nonregular/link/device inputs and revalidate canonical identity before opening.</summary>
    public void ValidateLength(long length)
    {
        var valid = Kind switch
        {
            MediaImageKind.DskContainer => length is > 0 and <= MediaFiles.MaxDiskBytes,
            MediaImageKind.RawFloppy => length == Geometry!.Length,
            MediaImageKind.HddRaw8MiB => length == HddMediaAdapter.ImageLength,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Image length does not match its explicit descriptor or App bound.");
    }

    public void ValidateForFloppy(DriveProfile device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Cylinders is not (40 or 80) || device.Sides is not (1 or 2))
            throw new InvalidDataException("Unsupported floppy device profile.");
        if (Kind == MediaImageKind.HddRaw8MiB)
            throw new InvalidDataException("An HDD descriptor cannot be used for a floppy device.");
        if (Geometry is { } geometry && (geometry.Cylinders > device.Cylinders || geometry.Sides > device.Sides))
            throw new InvalidDataException("Raw geometry exceeds the selected floppy device.");
    }
}
