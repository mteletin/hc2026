using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Tape.Snapshots;

/// <summary>Version pins for the existing HCS container. Core accepts tape1 via explicit
/// validated migration and emits tape2 with the exact native cassette state.</summary>
public static class TapeSnapshotContract
{
    public const uint ModelVersion = 2;
    public const uint NativeVersion = 1;
    public const string ComponentId = "tape";
    public const string CassetteAssetId = "tape-cassette";
    public const string CassetteAssetKind = "tape";
    public const string CassetteAssetFormat = "hc-tape-signal-v1";
    public const int SignalEdgeKind = 100;
    public const int FormatStopKind = 101;
    public const int MechanicalEndKind = 102;
    public const EventPhase Phase = EventPhase.SignalEdge;
}

/// <summary>Wire rational: canonical unsigned decimal strings, reduced and bounded to Int128.MaxValue.
/// Unit is dictated by the containing field (seconds except idealDue, which is master ticks).
/// DTO construction/serialization is NOT validation or permission to restore.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeRationalDto(
    [property: JsonPropertyName("num"), JsonRequired] string Num,
    [property: JsonPropertyName("den"), JsonRequired] string Den);

/// <summary>Exact tape2 roster, spec-10-tape-state-proof v4 §3. String counters are decimal64;
/// modes/holds have the specified kebab spellings. Core must validate cross-state/event invariants.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeComponent2Dto(
    [property: JsonPropertyName("cassetteAssetId"), JsonRequired] string? CassetteAssetId,
    [property: JsonPropertyName("activeSide"), JsonRequired] uint ActiveSide,
    [property: JsonPropertyName("position"), JsonRequired] TapeRationalDto Position,
    [property: JsonPropertyName("mode"), JsonRequired] string Mode,
    [property: JsonPropertyName("paused"), JsonRequired] bool Paused,
    [property: JsonPropertyName("holdReason"), JsonRequired] string HoldReason,
    [property: JsonPropertyName("consumedStop"), JsonRequired] uint? ConsumedStop,
    [property: JsonPropertyName("counterZeroTurns"), JsonRequired] ImmutableArray<string> CounterZeroTurns,
    [property: JsonPropertyName("tabs"), JsonRequired] ImmutableArray<bool> Tabs,
    [property: JsonPropertyName("dirty"), JsonRequired] ImmutableArray<bool> Dirty,
    [property: JsonPropertyName("editRevision"), JsonRequired] ImmutableArray<string> EditRevision,
    [property: JsonPropertyName("savedRevision"), JsonRequired] ImmutableArray<string> SavedRevision,
    [property: JsonPropertyName("fastWind"), JsonRequired] bool FastWind,
    [property: JsonPropertyName("mediaIdentity"), JsonRequired] string MediaIdentity,
    [property: JsonPropertyName("anchorTick"), JsonRequired] string AnchorTick,
    [property: JsonPropertyName("anchorPosition"), JsonRequired] TapeRationalDto AnchorPosition,
    [property: JsonPropertyName("waveformLevel"), JsonRequired] bool WaveformLevel,
    [property: JsonPropertyName("micLevel"), JsonRequired] bool MicLevel,
    [property: JsonPropertyName("recordStart"), JsonRequired] TapeRationalDto? RecordStart,
    [property: JsonPropertyName("recordHead"), JsonRequired] TapeRationalDto? RecordHead,
    [property: JsonPropertyName("recordLevel"), JsonRequired] bool RecordLevel,
    [property: JsonPropertyName("nextPosition"), JsonRequired] TapeRationalDto? NextPosition,
    [property: JsonPropertyName("idealDue"), JsonRequired] TapeRationalDto? IdealDue,
    [property: JsonPropertyName("pendingKind"), JsonRequired] uint? PendingKind,
    [property: JsonPropertyName("pendingToken"), JsonRequired] string PendingToken);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeCassetteDto(
    [property: JsonPropertyName("version"), JsonRequired] uint Version,
    [property: JsonPropertyName("sideDuration"), JsonRequired] TapeRationalDto SideDuration,
    [property: JsonPropertyName("sides"), JsonRequired] ImmutableArray<TapeSideDto> Sides,
    [property: JsonPropertyName("sourceAssets"), JsonRequired] ImmutableArray<string> SourceAssets);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeSideDto(
    [property: JsonPropertyName("chunks"), JsonRequired] ImmutableArray<TapeChunkDto> Chunks,
    [property: JsonPropertyName("markers"), JsonRequired] ImmutableArray<TapeMarkerDto> Markers,
    [property: JsonPropertyName("annotations"), JsonRequired] ImmutableArray<TapeMarkerDto> Annotations,
    [property: JsonPropertyName("sourceRoute"), JsonRequired] ImmutableArray<TapeRouteVisitDto> SourceRoute,
    [property: JsonPropertyName("importOptions"), JsonRequired] TapeImportProvenanceDto ImportOptions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeChunkDto(
    [property: JsonPropertyName("repeat"), JsonRequired] string Repeat,
    [property: JsonPropertyName("runs"), JsonRequired] ImmutableArray<TapeRunDto> Runs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeRunDto(
    [property: JsonPropertyName("level"), JsonRequired] bool Level,
    [property: JsonPropertyName("duration"), JsonRequired] TapeRationalDto Duration);

/// <summary>Markers allow only format-stop; annotations allow the five specified data-only kinds.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeMarkerDto(
    [property: JsonPropertyName("position"), JsonRequired] TapeRationalDto Position,
    [property: JsonPropertyName("ordinal"), JsonRequired] uint Ordinal,
    [property: JsonPropertyName("kind"), JsonRequired] string Kind,
    [property: JsonPropertyName("label"), JsonRequired] string Label,
    [property: JsonPropertyName("sourceId"), JsonRequired] string? SourceId,
    [property: JsonPropertyName("sourceBlock"), JsonRequired] uint? SourceBlock);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeRouteVisitDto(
    [property: JsonPropertyName("sourceId"), JsonRequired] string SourceId,
    [property: JsonPropertyName("blockIndex"), JsonRequired] uint BlockIndex,
    [property: JsonPropertyName("visit"), JsonRequired] uint Visit);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeImportProvenanceDto(
    [property: JsonPropertyName("format"), JsonRequired] string Format,
    [property: JsonPropertyName("channel"), JsonRequired] string? Channel,
    [property: JsonPropertyName("target48k"), JsonRequired] bool Target48K,
    [property: JsonPropertyName("selections"), JsonRequired] ImmutableArray<TapeSelectionDto> Selections,
    [property: JsonPropertyName("comparatorModel"), JsonRequired] string? ComparatorModel);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TapeSelectionDto(
    [property: JsonPropertyName("occurrence"), JsonRequired] uint Occurrence,
    [property: JsonPropertyName("option"), JsonRequired] uint Option);

/// <summary>Detached logical capture/adoption; transport validates all fields before live mutation.</summary>
public sealed record TapeDeckState(TapeComponent2Dto Component, TapeCassetteImage? Cassette, ulong MediaEpoch = 0);

/// <summary>Migration must first validate the old cursor, raw TAP, profile/asset and scheduler pair.
/// The SAME transport supplies its existing TapeCursor and DeviceEvent; no second clock/facade.</summary>
public sealed record Tape1MigrationInput(TapeCursor Cursor, ImmutableArray<TapeSourceAsset> RawTapAssets,
    long Now, int DeviceId, long Generation, DeviceEvent? PendingEvent);

public interface ITapeDeckStateTransfer
{
    TapeResult<TapeDeckState> Capture();
    TapeResult<TapeDeckView> Restore(TapeDeckState state);
    TapeResult<TapeDeckState> Migrate(Tape1MigrationInput input, TapeLimits limits);
}
