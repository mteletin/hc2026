using System.Collections.Immutable;
using Hc2000.Machine.Storage;

namespace Hc2000.Machine.Tape;

public sealed partial class TapeTransport
{
    public ulong MediaEpoch => Core.MediaEpoch;
    internal TapeResult<TapeCatalogSide> ReadCatalogSide()
    {
        VerifyOwner();
        var view = Core.Observe();
        if (!view.Present) return TapeResult<TapeCatalogSide>.Fail(TapeError.Interlock, "No cassette.");
        var wire = new TapCodec().Write(Core.ActiveIndex!.Image, new(TapeFormat.Tap, view.ActiveSide));
        if (!wire.Success) return TapeResult<TapeCatalogSide>.Fail(wire.Failure!.Code, wire.Failure.Detail);
        var parsed = new TapeCatalogCodec().FromTap(wire.Value.Bytes.AsSpan(), view.ActiveSide, new());
        if (!parsed.Success) return parsed;
        return TapeResult<TapeCatalogSide>.Ok(parsed.Value with { WriteProtected = !(view.ActiveSide == TapeSide.A ? view.A : view.B).TabPresent });
    }
    internal TapeResult<bool> PublishCatalog(TapeCatalogAppendPlan plan, ulong epoch, TapeSide side) => Command(() => Core.PublishCatalog(plan, epoch, side));
}

internal sealed partial class TapeDeckCore
{
    internal TapeResult<bool> PublishCatalog(TapeCatalogAppendPlan plan, ulong epoch, TapeSide side)
    {
        Sync();
        TapeResult<bool> Refuse(TapeError code, string detail) => TapeResult<bool>.Fail(code, detail);
        if (!Idle || cassette is null || MediaEpoch != epoch || active != side)
            return Refuse(TapeError.Interlock, "Stopped bound cassette and side required.");
        if (!sides[(int)active].TabPresent) return Refuse(TapeError.WriteProtected, "Active tab is absent.");
        using var wire = new MemoryStream();
        foreach (var block in plan.AppendedBlocks)
        {
            wire.WriteByte((byte)block.Length); wire.WriteByte((byte)(block.Length >> 8)); wire.Write(block.AsSpan());
        }
        var codec = new TapCodec();
        var decoded = codec.Read(wire.ToArray(), new(TapeFormat.Tap, active, cassette.Capacity));
        if (!decoded.Success) return Refuse(decoded.Failure!.Code, decoded.Failure.Detail);
        var old = indexes[(int)active];
        var spans = TapCodec.Blocks(old);
        var start = old.Image.SourceDuration;
        if (!spans.IsEmpty && start < spans[^1].End + new TapeTime(1, 1)) start = spans[^1].End + new TapeTime(1, 1);
        var end = start + decoded.Value.Image.SourceDuration;
        if (end > Duration) return Refuse(TapeError.CapacityExceeded, "Appended signal does not fit the cassette.");
        var appendIndex = new TapeSignalIndexFactory().Build(decoded.Value.Image, limits).Value;
        var slice = appendIndex.Slice(default, decoded.Value.Image.SourceDuration, limits);
        if (!slice.Success) return Refuse(slice.Failure!.Code, slice.Failure.Detail);
        // These are newly synthesized signal bytes, not references into another source asset.
        var replacement = new TapeSideImage(slice.Value.Duration, slice.Value.Duration, slice.Value.Chunks, [], [], [], old.Image.ImportOptions);
        var edited = old.Replace(start, end, replacement, limits);
        if (!edited.Success) return Refuse(edited.Failure!.Code, edited.Failure.Detail);
        var image = new TapeSideImage(edited.Value.Duration, end, edited.Value.Chunks, edited.Value.Markers,
            edited.Value.Annotations.Where(a => a.Kind != TapeAnnotationKind.SourceEof)
                .Append(new(end, 0, TapeAnnotationKind.SourceEof, "Catalog append EOF", null, null)),
            edited.Value.SourceRoute, edited.Value.ImportOptions);
        var indexed = new TapeSignalIndexFactory().Build(image, limits);
        if (!indexed.Success) return Refuse(indexed.Failure!.Code, indexed.Failure.Detail);
        var exported = codec.Write(image, new(TapeFormat.Tap, active));
        if (!exported.Success) return Refuse(exported.Failure!.Code, exported.Failure.Detail);
        var catalogCodec = new TapeCatalogCodec();
        var catalog = catalogCodec.FromTap(exported.Value.Bytes.AsSpan(), active, limits);
        if (!catalog.Success) return Refuse(catalog.Failure!.Code, catalog.Failure.Detail);
        var publication = catalogCodec.CheckPublication(catalog.Value, plan, limits);
        if (!publication.Success) return publication;
        var candidate = active == TapeSide.A ? cassette with { A = image } : cassette with { B = image };
        if (persistenceCheck?.Invoke(candidate) is { } failure) return Refuse(failure.Code, failure.Detail);
        var revision = checked(sides[(int)active].EditRevision + 1);
        // One owner-bound commit after physical, codec, publication and persistence admission.
        indexes[(int)active] = indexed.Value; cassette = candidate;
        sides[(int)active] = sides[(int)active] with { Dirty = true, EditRevision = revision };
        NotifyStateChanged();
        return TapeResult<bool>.Ok(true);
    }
}
