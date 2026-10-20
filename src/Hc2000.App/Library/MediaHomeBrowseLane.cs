using System.IO;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Library;

/// <summary>
/// The kind of one scanned media home row (contract hc2000-media-home-scan-admission-v1, sections
/// 2 and 3). The kind comes from the existing readers and inspectors at scan time - never from a
/// file suffix or a size guess.
/// </summary>
public enum MediaHomeRowKind
{
    Snapshot,
    FloppyDsk,
    HddRaw,
    HddContainer,
    Tape,
    Unknown,
}

/// <summary>The outcome of a double-click browse on a scanned media home row.</summary>
public enum MediaHomeBrowseOutcome
{
    Browsable,
    SnapshotRefused,
    UnknownRefused,
    ReaderRefused,
}

/// <summary>
/// One scanned media home row opened read-only by double-click, through the existing per-kind
/// reader: a floppy row through the current explorer (RawFloppyAdapter.Load +
/// CpmExplorer.Open - the same load and list the explorer pane composes for DskContainer rows),
/// an hdd-raw row through CpmExplorer.OpenHddVolume (the same HddVolume DPB and directory
/// validation CpmFs.OpenHddVolume enforces; the file-view surface is the CpmExplorer side), a
/// tape row through Tap.List over the bounded load, and an hdd-container row through the existing
/// footer/index container read (InspectHddFooter + DecodeHdd - the existing reader, no new
/// parser, never arena or payload bytes, never the whole image). A snapshot row is not a CP/M
/// browse and says so; an unknown row has no browse action. Every refusal is closed: nothing is
/// opened and the scanned row is untouched. Browsing writes nothing.
/// </summary>
public sealed record MediaHomeBrowseResult(
    MediaHomeBrowseOutcome Outcome, string Message,
    IReadOnlyList<CpmEntry>? Files = null,
    IReadOnlyList<TapeFile>? Tape = null,
    HddContainerDescriptor? Container = null);

public sealed class MediaHomeBrowseLane
{
    // Fixed caller admission for the existing container read: the closed limits the existing
    // readers use (the HddColdVolume codec limits; hc hdd inspect). No knobs.
    private static readonly HddLimits ContainerLimits = new(uint.MaxValue, 511, 12_276, 114_640);

    /// <summary>The double-click entry point for a scanned media home row (the section 2 row
    /// shape, MediaHomeRow): the kind is the scan's decision and this dispatches on it; a value
    /// the scan does not emit closes as Unknown. Read-only: this lane writes nothing.</summary>
    public Task<MediaHomeBrowseResult> OpenRowAsync(MediaHomeRow row, CancellationToken cancellation = default)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        return OpenAsync(ParseKind(row.Kind), row.Path, cancellation);
    }

    /// <summary>Maps the scan row kind string (MediaHomeRow.Kind, section 2) to the browse
    /// dispatch. No inference beyond the scan's own words: anything unrecognized closes as
    /// Unknown, which has no browse action.</summary>
    public static MediaHomeRowKind ParseKind(string kind) => kind switch
    {
        "floppy-dsk" => MediaHomeRowKind.FloppyDsk,
        "hdd-raw" => MediaHomeRowKind.HddRaw,
        "hdd-container" => MediaHomeRowKind.HddContainer,
        "tape" => MediaHomeRowKind.Tape,
        "snapshot" => MediaHomeRowKind.Snapshot,
        _ => MediaHomeRowKind.Unknown,
    };

    /// <summary>Opens one scanned media home row read-only for browse; a refusal is closed and the
    /// row stays. Read-only: this lane writes nothing. The reads are synchronous on the caller
    /// thread, the pattern the existing explorer reads use (ReadExplorerImage): the browse chain
    /// then updates the pane on the thread that opened the row, never a continuation hop off it.
    /// </summary>
    public Task<MediaHomeBrowseResult> OpenAsync(MediaHomeRowKind kind, string scannedPath, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        // The kind is decided at scan time by the existing readers (section 2); the browse only
        // dispatches on it - no kind-guessing, no suffix or length policy here.
        switch (kind)
        {
            case MediaHomeRowKind.Snapshot:
                // A snapshot row is not a CP/M browse: the scan does not decode it and the browse
                // does not either. No file is opened.
                return Task.FromResult(new MediaHomeBrowseResult(
                    MediaHomeBrowseOutcome.SnapshotRefused,
                    "a snapshot row is not a CP/M browse; the scan does not decode it"));
            case MediaHomeRowKind.Unknown:
                // An unknown row has no mount, select, browse or copy action. No file is opened.
                return Task.FromResult(new MediaHomeBrowseResult(
                    MediaHomeBrowseOutcome.UnknownRefused,
                    "an unknown row has no browse action"));
            case MediaHomeRowKind.FloppyDsk:
                return Task.FromResult(BrowseFloppy(scannedPath));
            case MediaHomeRowKind.HddRaw:
                return Task.FromResult(BrowseHddRaw(scannedPath));
            case MediaHomeRowKind.Tape:
                return Task.FromResult(BrowseTape(scannedPath));
            case MediaHomeRowKind.HddContainer:
                return Task.FromResult(BrowseHddContainer(scannedPath));
        }
        throw new InvalidOperationException($"row kind {kind} has no browse dispatch");
    }

    private static MediaHomeBrowseResult BrowseFloppy(string scannedPath)
    {
        try
        {
            var bytes = ReadWhole(scannedPath, MediaFiles.MaxDiskBytes);
            // The current explorer over the scanned bytes: the same load + list the explorer pane
            // composes for DskContainer rows (ExplorerImage.Parse).
            var disk = RawFloppyAdapter.Load(bytes, MediaImageDescriptor.DskContainer, new DriveProfile(80, 2));
            var entries = CpmExplorer.Open(disk).List();
            return new MediaHomeBrowseResult(MediaHomeBrowseOutcome.Browsable,
                $"floppy row browsed through the current explorer: {entries.Count} files", Files: entries);
        }
        catch (Exception ex) when (IsReaderRefusal(ex))
        { return Refused($"floppy row refused by the current explorer: {ex.Message}"); }
    }

    private static MediaHomeBrowseResult BrowseHddRaw(string scannedPath)
    {
        try
        {
            // The existing volume reader decides the shape: the exact 8 MiB length and the
            // directory validation. The read is capped at the volume length; a wrong length is a
            // refusal, not a partial read.
            var bytes = ReadWhole(scannedPath, HddMediaAdapter.ImageLength);
            var entries = CpmExplorer.OpenHddVolume(bytes).List();
            return new MediaHomeBrowseResult(MediaHomeBrowseOutcome.Browsable,
                $"hdd-raw row browsed through the existing volume reader: {entries.Count} files", Files: entries);
        }
        catch (Exception ex) when (IsReaderRefusal(ex))
        { return Refused($"hdd-raw row refused by the existing volume reader: {ex.Message}"); }
    }

    private static MediaHomeBrowseResult BrowseTape(string scannedPath)
    {
        try
        {
            var bytes = ReadWhole(scannedPath, MediaFiles.MaxTapeBytes);
            // The existing bounded tape load (one native parser, finite caps); the byte cap
            // governs, the block bound is the byte cap (worst-case 2-byte blocks). No new policy.
            var loaded = Tap.LoadBounded(bytes, MediaFiles.MaxTapeBytes, MediaFiles.MaxTapeBytes);
            if (!loaded.Success) throw new InvalidDataException(loaded.Failure!.Detail);
            var files = loaded.Value.List();
            return new MediaHomeBrowseResult(MediaHomeBrowseOutcome.Browsable,
                $"tape row browsed through Tap.List: {files.Count} blocks", Tape: files);
        }
        catch (Exception ex) when (IsReaderRefusal(ex))
        { return Refused($"tape row refused by the existing tape reader: {ex.Message}"); }
    }

    private static MediaHomeBrowseResult BrowseHddContainer(string scannedPath)
    {
        try
        {
            // The existing container read (hc hdd inspect / HddColdVolume admission): the
            // 64-byte trailing footer and then the validated index only - never arena or payload
            // bytes, never the whole image. No new parser.
            using var image = new FileStream(scannedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: false);
            var length = image.Length;
            if (length < 76)
                throw new InvalidDataException($"HDD container image must be at least 76 bytes; found {length}.");
            var footer = new byte[64];
            image.Position = length - 64;
            image.ReadExactly(footer);
            var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, ContainerLimits);
            if (!inspected.Success) throw new InvalidDataException(inspected.Failure!.Detail);
            var view = inspected.Value;
            var index = new byte[checked((int)view.IndexBytes)];
            image.Position = (long)view.IndexStart;
            image.ReadExactly(index);
            var decoded = HddCatalogCodec.DecodeHdd(footer, index, (ulong)length, ContainerLimits);
            if (!decoded.Success) throw new InvalidDataException(decoded.Failure!.Detail);
            if (image.Length != length)
                throw new InvalidDataException("Container length changed during inspection.");
            return new MediaHomeBrowseResult(MediaHomeBrowseOutcome.Browsable,
                $"hdd-container row browsed through the existing container read: {decoded.Value.Entries.Length} volumes",
                Container: decoded.Value);
        }
        catch (Exception ex) when (IsReaderRefusal(ex))
        { return Refused($"hdd-container row refused by the existing container reader: {ex.Message}"); }
    }

    private static byte[] ReadWhole(string path, int maxBytes)
    {
        using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: false);
        if (image.Length > maxBytes)
            throw new InvalidDataException($"image exceeds the {maxBytes}-byte bound: {image.Length} bytes");
        var bytes = new byte[checked((int)image.Length)];
        image.ReadExactly(bytes);
        if (image.Length != bytes.Length)
            throw new InvalidDataException("image length changed during the browse read");
        return bytes;
    }

    private static MediaHomeBrowseResult Refused(string message) =>
        new(MediaHomeBrowseOutcome.ReaderRefused, message);

    private static bool IsReaderRefusal(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or OverflowException;
}
