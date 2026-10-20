using System.Collections.Immutable;

namespace Hc2000.Machine.Tape;

public enum TapeSide { A, B }
public enum TapeCapacity { C60, C90 }
public enum TapeFormat { Blank, Tap, Tzx, Pzx, Csw, Wav }
public enum TapeChannel { Left, Right }
public enum TapeAnnotationKind { Text, Metadata, Overwrite, UnsupportedAction, SourceEof }
public sealed record TapeSelection(uint Occurrence, uint Option);
public sealed record TapeRouteVisit(string SourceId, uint BlockIndex, uint Visit);
public sealed record TapeStopMarker(TapeTime Position, uint Ordinal, string Label,
    string? SourceId, uint? SourceBlock);
public sealed record TapeAnnotation(TapeTime Position, uint Ordinal, TapeAnnotationKind Kind,
    string Label, string? SourceId, uint? SourceBlock);
public sealed record TapeImportProvenance(TapeFormat Format, TapeChannel? Channel, bool Target48K,
    ImmutableArray<TapeSelection> Selections, string? ComparatorModel);

/// <summary>Compact explicit-level sequence. Repetition never implies toggling at its boundary.</summary>
public sealed record TapeSignalChunk
{
    public ulong Repeat { get; }
    public ImmutableArray<TapeRun> Runs { get; }
    public TapeSignalChunk(ulong repeat, IEnumerable<TapeRun> runs)
    {
        if (repeat == 0) throw new ArgumentOutOfRangeException(nameof(repeat));
        Runs = runs.ToImmutableArray();
        if (Runs.IsEmpty) throw new ArgumentException("A chunk needs runs.", nameof(runs));
        Repeat = repeat;
    }
}

/// <summary>The single detached signal representation shared by codecs and the existing core.
/// Core validates coverage, budgets, route and ordering and builds an ITapeSignalIndex before use.
/// Construction copies collections; it does not claim that an unvalidated image is playable.</summary>
public sealed record TapeSideImage
{
    public TapeTime Duration { get; }
    public TapeTime SourceDuration { get; }
    public ImmutableArray<TapeSignalChunk> Chunks { get; }
    public ImmutableArray<TapeStopMarker> Markers { get; }
    public ImmutableArray<TapeAnnotation> Annotations { get; }
    public ImmutableArray<TapeRouteVisit> SourceRoute { get; }
    public TapeImportProvenance ImportOptions { get; }

    public TapeSideImage(TapeTime duration, TapeTime sourceDuration, IEnumerable<TapeSignalChunk> chunks,
        IEnumerable<TapeStopMarker> markers, IEnumerable<TapeAnnotation> annotations,
        IEnumerable<TapeRouteVisit> sourceRoute, TapeImportProvenance importOptions)
    {
        Duration = duration; SourceDuration = sourceDuration;
        Chunks = chunks.ToImmutableArray(); Markers = markers.ToImmutableArray();
        Annotations = annotations.ToImmutableArray(); SourceRoute = sourceRoute.ToImmutableArray();
        ImportOptions = importOptions;
    }
}

/// <summary>Index/edit operations over the SAME TapeSideImage, supplied by core, not a second
/// timeline or device. No implementation is enabled by the declarations-only foundation.</summary>
public interface ITapeSignalIndex
{
    TapeSideImage Image { get; }
    bool Sample(TapeTime position);
    TapeTime? NextEdge(TapeTime position);
    TapeResult<TapeSideImage> Slice(TapeTime start, TapeTime end, TapeLimits limits);
    TapeResult<TapeSideImage> Replace(TapeTime start, TapeTime end, TapeSideImage replacement, TapeLimits limits);
}

/// <summary>Core validates/indexes a detached image under the supplied bounds, without insertion.</summary>
public interface ITapeSignalIndexFactory
{
    TapeResult<ITapeSignalIndex> Build(TapeSideImage image, TapeLimits limits);
}

public sealed record TapeSourceAsset
{
    public string Id { get; }
    public TapeFormat Format { get; }
    public ImmutableArray<byte> Bytes { get; }
    public TapeSourceAsset(string id, TapeFormat format, ReadOnlySpan<byte> bytes)
    {
        Id = id; Format = format; Bytes = ImmutableArray.Create<byte>(bytes);
    }
}

public sealed record TapeCassetteImage(TapeCapacity Capacity, TapeSideImage A, TapeSideImage B,
    ImmutableArray<TapeSourceAsset> SourceAssets);
