using System.Collections.Immutable;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;

namespace Hc2000.App.Explorer;

internal static class NestedMediaExplorer
{
    internal static async Task<ExplorerVolumeListing> ListAsync(MediaHomeRow row)
    {
        try
        {
            if (row.Kind == "hdd-container")
            {
                var result = await Task.Run(() => new MediaHomeBrowseLane().OpenRowAsync(row));
                if (result.Container is null) return new([], result.Message);
                var volumes = result.Container.Entries.Select(entry =>
                    new ExplorerVolumeRow(entry.Name, entry.ImageBytes, entry.ReadOnly)).ToArray();
                return new(volumes, volumes.Length == 0 ? "HDD container has no volumes." : "Select a volume to inspect its files; no guest is attached.");
            }
            if (row.Kind == "tape")
            {
                var (_, catalog) = await ReadTapeCatalogAsync(row.Path);
                if (catalog is null)
                {
                    var ordinary = await Task.Run(() => new MediaHomeBrowseLane().OpenRowAsync(row));
                    return ordinary.Tape is { } files
                        ? new([], "Standard TAP files (not a CP/M directory); select a file to preview.", files)
                        : new([], "No CP/M catalog on tape side A; standard TAP files unavailable: " + ordinary.Message);
                }
                var volumes = catalog.Entries.Select(entry =>
                    new ExplorerVolumeRow(entry.Name, entry.ImageBytes, entry.WriteProtected)).ToArray();
                return new(volumes, volumes.Length == 0 ? "Tape side A has no catalog volumes." : "Tape side A: select a volume to inspect its files.");
            }
            return new([], "This media kind has no nested volume listing.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new([], "Volume listing refused: " + exception.Message);
        }
    }

    internal static async Task<ImmutableArray<byte>> ReadVolumeAsync(MediaHomeRow row, ExplorerVolumeRow volume)
    {
        if (row.Kind == "hdd-container")
        {
            return await Task.Run(() =>
            {
                using var selected = HddColdVolume.Open(row.Path, volume.Name);
                return selected.Media(2).Bytes;
            });
        }
        if (row.Kind == "tape")
        {
            var (side, catalog) = await ReadTapeCatalogAsync(row.Path);
            var entry = catalog?.Entries.SingleOrDefault(item => string.Equals(item.Name, volume.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The selected tape volume no longer exists in the newest catalog.");
            var image = new TapeCatalogCodec().ReadImage(side, entry, new TapeLimits());
            if (!image.Success) throw new InvalidDataException(image.Failure!.Detail);
            if (image.Value.Length != HddMediaAdapter.ImageLength)
                throw new InvalidDataException("This tape volume size is not supported by the current CP/M file reader.");
            return image.Value;
        }
        throw new InvalidOperationException("This media kind has no nested volumes.");
    }

    private static async Task<(TapeCatalogSide Side, TapeCatalogRecord? Catalog)> ReadTapeCatalogAsync(string path)
    {
        var bytes = await MediaFiles.ReadAsync(path, MediaFiles.MaxTapeBytes);
        var codec = new TapeCatalogCodec();
        var limits = new TapeLimits();
        var side = codec.FromTap(bytes.AsSpan(), TapeSide.A, limits);
        if (!side.Success) throw new InvalidDataException(side.Failure!.Detail);
        var selection = codec.FindNewest(side.Value, limits);
        if (!selection.Success) throw new InvalidDataException(selection.Failure!.Detail);
        return (side.Value, selection.Value.Newest);
    }
}
