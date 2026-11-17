using Hc2000.App.Media;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Network;

// A run owns one immutable image; each admitted connection reconstructs its own filesystem.
internal sealed class CpNetFrozenExport
{
    private readonly byte[] image;
    private readonly MediaImageDescriptor descriptor;
    private readonly Dpb parameters;

    private CpNetFrozenExport(byte[] image, MediaImageDescriptor descriptor, Dpb parameters)
        => (this.image, this.descriptor, this.parameters) = (image, descriptor, parameters);

    public static CpNetFrozenExport Capture(ReadOnlySpan<byte> bytes, MediaImageDescriptor descriptor, Dpb parameters)
    {
        ValidateSelection(descriptor, parameters);
        descriptor.ValidateLength(bytes.Length);
        return Validate(new(bytes.ToArray(), descriptor, parameters));
    }

    public static async Task<CpNetFrozenExport> CaptureAsync(Stream source, MediaImageDescriptor descriptor,
        Dpb parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSelection(descriptor, parameters);
        if (!source.CanRead || !source.CanSeek || source.Position != 0)
            throw new ArgumentException("A seekable source positioned at its beginning is required.", nameof(source));
        var length = source.Length;
        descriptor.ValidateLength(length);
        var bytes = new byte[checked((int)length)];
        await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await source.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0 || source.Length != length)
            throw new InvalidDataException("Media changed while capturing the export.");
        return Validate(new(bytes, descriptor, parameters));
    }

    // The listener must reserve its bounded connection slot before calling this method.
    public CpmFs OpenDetached()
    {
        if (descriptor.Kind == MediaImageKind.HddRaw8MiB)
            return parameters == Dpb.HddVolume() ? CpmFs.OpenHddVolume(image) : CpmFs.OpenMVolumeV2(image);
        var disk = Dsk.Load(image, descriptor.Geometry);
        if ((descriptor.Kind == MediaImageKind.RawFloppy) != (disk.Format == DiskFormat.Raw))
            throw new InvalidDataException("Image does not match the explicit disk descriptor.");
        return new CpmFs(disk, parameters);
    }

    private static CpNetFrozenExport Validate(CpNetFrozenExport export)
    {
        _ = export.OpenDetached();
        return export;
    }

    private static void ValidateSelection(MediaImageDescriptor descriptor, Dpb parameters)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(parameters);
        if (descriptor.Kind == MediaImageKind.HddRaw8MiB)
        {
            if (parameters != Dpb.HddVolume() && parameters != Dpb.MVolumeV2())
                throw new InvalidDataException("Select an existing HDD or M: volume format.");
            return;
        }
        if (!Dpb.Presets().Contains(parameters) ||
            (descriptor.Kind == MediaImageKind.RawFloppy && descriptor.Geometry != parameters.Geometry))
            throw new InvalidDataException("Select a supported CP/M floppy format and matching geometry.");
    }
}
