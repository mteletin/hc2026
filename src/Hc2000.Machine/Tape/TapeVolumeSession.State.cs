using System.Text.Json.Serialization;
using Hc2000.Machine.Storage;

namespace Hc2000.Machine.Tape;

/// <summary>Metadata only. Image bytes already belong to the snapshot's one paged-RAM asset.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeVolumeState(ulong DeckEpoch, TapeMountedVolumeState? Mounted, bool WindowEngaged = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeMountedVolumeState(TapeSide Side, ulong BoundEpoch, TapeCatalogIdentity Catalog,
    TapeCatalogEntry Entry, char Letter, bool Dirty, int FirstPage, bool BoundWriteProtected = false);

public sealed partial class TapeVolumeSession
{
    public TapeVolumeState CaptureState() => new(tape.MediaEpoch, Mounted
        ? new(side, epoch, catalogIdentity!, entry!, Letter, Dirty, FirstPage, boundWriteProtected) : null, memory.Latches.PagedEnabled);

    public void ValidateState(TapeVolumeState state)
    {
        if (Mounted) throw new InvalidOperationException("Restore requires an unmounted session.");
        if (state.WindowEngaged && (state.Mounted is null || state.Mounted.BoundEpoch != state.DeckEpoch))
            throw new ArgumentException("Engaged window requires its current bound session.");
        if (state.Mounted is not { } mounted) return;
        ValidateMetadata(mounted);
        if (mounted.Letter <= 'F' && slotPresent(mounted.Letter)) throw new ArgumentException("Restored letter is occupied.");
        if (memory.ClaimedPagedRanges.Any(r => mounted.FirstPage < r.FirstPage + r.PageCount && r.FirstPage < mounted.FirstPage + (mounted.Entry.ImageBytes + 0x3fff) / 0x4000))
            throw new ArgumentException("Restored range overlaps an owner.");
    }
    public static void ValidateMetadata(TapeMountedVolumeState mounted)
    {
        if (mounted.Side is not (TapeSide.A or TapeSide.B) || mounted.Catalog is null || mounted.Catalog.SourceBlock < 0
            || mounted.Entry is null || mounted.Letter is < 'C' or > 'P' or 'M'
            || mounted.FirstPage < 256 || mounted.Entry.ImageBytes is < 16384 or > 8388608 || mounted.Entry.ImageBytes % 4096 != 0
            || mounted.FirstPage > 65536 - (mounted.Entry.ImageBytes + 0x3fff) / 0x4000)
            throw new ArgumentException("Invalid mounted metadata or paged range.");
        var encoded = new TapeCatalogCodec().EncodeRecord(mounted.Side, [mounted.Entry], new());
        if (!encoded.Success) throw new ArgumentException(encoded.Failure!.Detail);
    }
    /// <summary>Adopt metadata after the deck and existing paged RAM have been restored.</summary>
    public void RestoreState(TapeVolumeState state)
    {
        ValidateState(state);
        if (state.DeckEpoch != tape.MediaEpoch) throw new ArgumentException("Deck epoch was not restored.");
        InvalidateDescriptor();
        memory.SetSessionPagingEngaged(false);
        if (state.Mounted is not { } mounted) return;
        range = memory.ClaimPagedPages(this, checked((int)((mounted.Entry.ImageBytes + 0x3fff) / 0x4000)), Write, mounted.FirstPage);
        side = mounted.Side; epoch = mounted.BoundEpoch; catalogIdentity = mounted.Catalog;
        entry = mounted.Entry; Letter = mounted.Letter; Dirty = mounted.Dirty;
        boundWriteProtected = mounted.BoundWriteProtected;
        cachedSide = null; cachedIndex = null; cachedCatalog = null;
        memory.SetSessionPagingEngaged(state.WindowEngaged, range);
        if (state.WindowEngaged) PublishDescriptor();
    }
}
