using System.Collections.Immutable;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Explorer;

// Host view state only: immutable captured image, never a device or mount authority.
internal sealed class ExplorerPaneState
{
    public long Generation { get; set; } = -1;
    public long Request { get; set; }
    public string MachineName { get; set; } = "HC2000";
    public string Notice { get; set; } = "Choose Browse files on a drive or an existing disk-library row.";
    public ExplorerImage? Image { get; set; }
    // Read-only listing for a scanned row that is not a disk file view: a tape block list
    // (Tap.List) or an hdd-container volume catalog (the existing footer/index read). Set with
    // Image null; the pane renders it without the disk copy-in/out controls.
    public string? ListingTitle { get; set; }
    public IReadOnlyList<string>? Listing { get; set; }
    public Func<Task>? Refresh { get; set; }
    public string Name { get; set; } = "";
    public int User { get; set; }
    public bool Replace { get; set; }
}

internal sealed record ExplorerImage(string Label, string? Path, MediaImageDescriptor Descriptor,
    ImmutableArray<byte> Bytes, IReadOnlyList<CpmEntry> Entries)
{
    private static readonly DriveProfile Floppy = new(80, 2);
    public bool CanImport => Path is not null && Descriptor.Kind != MediaImageKind.HddRaw8MiB;
    /// <summary>The flat 8 MiB volume lane: an explicit one-file copy-in onto the file the user
    /// opened, written volume-shaped (CpmFs.OpenHddVolume + volume bytes export), published only
    /// by WriteExplorerImage. CheckExplorerOwnership and CanImport are unchanged (amendment 1).</summary>
    public bool CanImportVolume => Path is not null && Descriptor.Kind == MediaImageKind.HddRaw8MiB;
    public static ExplorerImage Parse(string label, string? path, MediaImageDescriptor descriptor, ImmutableArray<byte> bytes)
    {
        descriptor.ValidateLength(bytes.Length);
        var entries = descriptor.Kind == MediaImageKind.HddRaw8MiB
            ? CpmExplorer.OpenHddVolume(bytes.AsSpan()).List()
            : CpmExplorer.Open(RawFloppyAdapter.Load(bytes.AsSpan(), descriptor, Floppy)).List();
        return new(label, path, descriptor, bytes, entries);
    }

    public CpmFs Open() => Descriptor.Kind == MediaImageKind.HddRaw8MiB
        ? CpmFs.OpenHddVolume(Bytes.AsSpan())
        : CpmFs.Open(RawFloppyAdapter.Load(Bytes.AsSpan(), Descriptor, Floppy));
}
