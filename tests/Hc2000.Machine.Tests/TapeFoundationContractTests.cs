using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Encoding;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeFoundationContractTests
{
    [Theory]
    [InlineData(0, 7, 0, 1)]
    [InlineData(6, 8, 3, 4)]
    [InlineData(2168, 3500000, 271, 437500)]
    [InlineData(5, 17734475, 1, 3546895)]
    public void TimeIsReducedSeconds(long num, long den, long reducedNum, long reducedDen)
    {
        var time = new TapeTime(num, den);
        Assert.Equal((Int128)reducedNum, time.Numerator);
        Assert.Equal((Int128)reducedDen, time.Denominator);
        Assert.Equal(new TapeTime(reducedNum, reducedDen), time);
    }

    [Fact]
    public void DefaultZeroAndInt128BoundaryAreCanonical()
    {
        Assert.Equal(new TapeTime(0, Int128.MaxValue), default);
        Assert.Equal((Int128)1, default(TapeTime).Denominator);
        Assert.Equal(Int128.MaxValue, new TapeTime(Int128.MaxValue, 1).Numerator);
        Assert.Equal(new TapeTime(1, 1), new TapeTime(Int128.MaxValue, Int128.MaxValue));
        Assert.Equal(Int128.MaxValue, new TapeTime(1, Int128.MaxValue).Denominator);
        Assert.Equal(new TapeTime(0, 1).GetHashCode(), default(TapeTime).GetHashCode());
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    public void InvalidRationalFailsExplicitly(long num, long den) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TapeTime(num, den));

    [Fact]
    public void RunsRequirePositiveDurationAndRetainD6Polarity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TapeRun(true, default));
        var run = new TapeRun(true, new TapeTime(1, 3_500_000));
        Assert.True(run.Level);
        Assert.Equal(new TapeTime(1, 3_500_000), run.Duration);
    }

    private static TapeSideImage Image(IEnumerable<TapeSignalChunk> chunks) =>
        new(new(1800, 1), new(1, 1), chunks, [], [], [],
            new(TapeFormat.Blank, null, true, [], null));

    [Fact]
    public void CompactMediaCopiesCollectionsWithoutExpandingRepeat()
    {
        var runs = new[] { new TapeRun(false, new(1, 7)), new TapeRun(true, new(1, 7)) };
        var chunk = new TapeSignalChunk(1_000_000, runs);
        var chunks = new[] { chunk };
        var image = Image(chunks);
        runs[0] = new(true, new(99, 1));
        chunks[0] = new(1, [new(false, new(1, 1))]);
        Assert.Single(image.Chunks);
        Assert.Equal(1_000_000UL, image.Chunks[0].Repeat);
        Assert.Equal(2, image.Chunks[0].Runs.Length);
        Assert.False(image.Chunks[0].Runs[0].Level);
        Assert.Equal(new TapeTime(1, 7), image.Chunks[0].Runs[0].Duration);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TapeSignalChunk(0, runs));
        Assert.Throws<ArgumentException>(() => new TapeSignalChunk(1, []));
    }

    [Fact]
    public void SourceAndOutputBytesAreDetachedCopies()
    {
        byte[] bytes = [1, 2, 3];
        var source = new TapeSourceAsset("tape-source-0", TapeFormat.Csw, bytes);
        var report = new TapeWriteReport(TapeFormat.Wav, TapeSignalFidelity.Quantized,
            TapeByteFidelity.Transformed, TapeAmplitudeFidelity.Synthesized, 44100,
            new(1, 44100), 2, []);
        var output = new TapeWriteResult(bytes, report);
        bytes[0] = 99;
        Assert.Equal((byte)1, source.Bytes[0]);
        Assert.Equal((byte)1, output.Bytes[0]);
        Assert.Equal(TapeSignalFidelity.Quantized, output.Report.Signal);
        Assert.Equal(TapeAmplitudeFidelity.Synthesized, output.Report.Amplitude);
    }

    [Fact]
    public void OptionDefaultsAreFaithfulBoundedAndNeverImplicitlyQuantized()
    {
        var import = new TapeImportOptions(TapeFormat.Tzx, TapeSide.B, TapeCapacity.C60);
        Assert.True(import.Target48K);
        Assert.Equal(TapeChannel.Left, import.Channel);
        Assert.Empty(import.Selections);
        var export = new TapeExportOptions(TapeFormat.Tzx, TapeSide.B);
        Assert.False(export.AllowQuantize);
        Assert.Null(export.Rate);
        Assert.False(export.UseZrle);
        Assert.Equal(16U, export.WavBitsPerSample);
        var limits = import.Limits;
        Assert.Equal(64 * 1024 * 1024, limits.InputBytes);
        Assert.Equal(256 * 1024 * 1024, limits.DecodedBytes);
        Assert.Equal(100_000, limits.SourceBlocks);
        Assert.Equal(1_000_000, limits.RouteSteps);
        Assert.Equal(16_000_000, limits.VirtualTransitions);
        Assert.Equal(new TapeTime(2700, 1), limits.MaxDuration);
        Assert.Equal(64, (limits with { InputBytes = 64 }).InputBytes);
    }

    [Fact]
    public void UninstalledRegistryCannotPretendToHaveCodecs()
    {
        ITapeCodecRegistry registry = new UnavailableTapeCodecRegistry();
        Assert.Empty(registry.Formats);
        foreach (var format in Enum.GetValues<TapeFormat>())
        {
            var result = registry.Find(format);
            Assert.False(result.Success);
            Assert.Equal(TapeError.RuntimeUnavailable, result.Failure!.Code);
            Assert.Throws<InvalidOperationException>(() => result.Value);
        }
    }

    [Fact]
    public void SharedEncodingPlaceholderRefusesAllFourOperations()
    {
        ITapeRunEncoding service = new UnavailableTapeRunEncoding();
        var rle = service.DecodeRle([1, 2, 3], 3, new());
        var zrle = service.DecodeZrle([0x78, 1], null, new());
        var encoded = service.EncodeRle([1, 2, 3]);
        var compressed = service.EncodeZrle([1, 2, 3]);
        Assert.False(rle.Success); Assert.False(zrle.Success);
        Assert.False(encoded.Success); Assert.False(compressed.Success);
        Assert.Equal(TapeError.RuntimeUnavailable, rle.Failure!.Code);
        Assert.Equal(TapeError.RuntimeUnavailable, zrle.Failure!.Code);
        Assert.Equal(TapeError.RuntimeUnavailable, encoded.Failure!.Code);
        Assert.Equal(TapeError.RuntimeUnavailable, compressed.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => encoded.Value);
    }

    [Fact]
    public void FailuresHaveNoUsablePartialPayload()
    {
        var result = TapeResult<TapeWriteResult>.Fail(TapeError.ExactEncodingUnavailable, "mixed grid");
        Assert.False(result.Success);
        Assert.Equal("mixed grid", result.Failure!.Detail);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Throws<ArgumentNullException>(() => TapeResult<TapeWriteResult>.Ok(null!));
    }

    private static string[] WireNames<T>() => typeof(T).GetProperties()
        .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name).Order().ToArray();
    private static void Roster<T>(string names)
    {
        Assert.Equal(names.Split(' ').Order(), WireNames<T>());
        Assert.All(typeof(T).GetProperties(), p => Assert.NotNull(p.GetCustomAttribute<JsonRequiredAttribute>()));
        Assert.Equal(JsonUnmappedMemberHandling.Disallow,
            typeof(T).GetCustomAttribute<JsonUnmappedMemberHandlingAttribute>()!.UnmappedMemberHandling);
    }

    [Fact]
    public void Tape2DtoHasExactlyPinnedComponentRoster()
    {
        Roster<TapeComponent2Dto>("cassetteAssetId activeSide position mode paused holdReason consumedStop " +
            "counterZeroTurns tabs dirty editRevision savedRevision fastWind mediaIdentity anchorTick " +
            "anchorPosition waveformLevel micLevel recordStart recordHead recordLevel nextPosition idealDue " +
            "pendingKind pendingToken");
        Assert.Equal(2U, TapeSnapshotContract.ModelVersion);
        Assert.Equal("tape", TapeSnapshotContract.ComponentId);
        Assert.Equal((100, 101, 102), (TapeSnapshotContract.SignalEdgeKind,
            TapeSnapshotContract.FormatStopKind, TapeSnapshotContract.MechanicalEndKind));
        Assert.Equal(EventPhase.SignalEdge, TapeSnapshotContract.Phase);
    }

    [Fact]
    public void NativeBlobDtosHaveExactlyPinnedRosters()
    {
        Roster<TapeCassetteDto>("version sideDuration sides sourceAssets");
        Roster<TapeSideDto>("chunks markers annotations sourceRoute importOptions");
        Roster<TapeChunkDto>("repeat runs");
        Roster<TapeRunDto>("level duration");
        Roster<TapeMarkerDto>("position ordinal kind label sourceId sourceBlock");
        Roster<TapeRouteVisitDto>("sourceId blockIndex visit");
        Roster<TapeImportProvenanceDto>("format channel target48k selections comparatorModel");
        Roster<TapeSelectionDto>("occurrence option");
        Roster<TapeRationalDto>("num den");
        Assert.Equal(1U, TapeSnapshotContract.NativeVersion);
        Assert.Equal("tape-cassette", TapeSnapshotContract.CassetteAssetId);
        Assert.Equal("tape", TapeSnapshotContract.CassetteAssetKind);
        Assert.Equal("hc-tape-signal-v1", TapeSnapshotContract.CassetteAssetFormat);
    }

    private static TapeComponent2Dto EmptyState() => new(null, 0, new("0", "1"), "stopped", false,
        "none", null, ["0", "0"], [true, true], [false, false], ["0", "0"], ["0", "0"],
        false, "0", "0", new("0", "1"), false, false, null, null, false, null, null, null, "0");

    [Fact]
    public void EmptyComponentSerializationKeepsExplicitNullsAndDecimalStrings()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(EmptyState()));
        var state = json.RootElement;
        Assert.Equal(WireNames<TapeComponent2Dto>(), state.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("cassetteAssetId").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("idealDue").ValueKind);
        Assert.Equal("0", state.GetProperty("anchorTick").GetString());
        Assert.Equal("0", state.GetProperty("position").GetProperty("num").GetString());
        Assert.Equal("1", state.GetProperty("position").GetProperty("den").GetString());
        Assert.Equal(2, state.GetProperty("tabs").GetArrayLength());
    }

    [Fact]
    public void NativeSerializationUsesStringsForRepeatAndExplicitLevels()
    {
        var run = new TapeRunDto(true, new("271", "437500"));
        var side = new TapeSideDto([new("8063", [run])], [], [], [], new("blank", null, true, [], null));
        var blob = new TapeCassetteDto(1, new("1800", "1"), [side, side], []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(blob));
        var root = json.RootElement;
        Assert.Equal(1U, root.GetProperty("version").GetUInt32());
        Assert.Equal(2, root.GetProperty("sides").GetArrayLength());
        var chunk = root.GetProperty("sides")[0].GetProperty("chunks")[0];
        Assert.Equal("8063", chunk.GetProperty("repeat").GetString());
        Assert.True(chunk.GetProperty("runs")[0].GetProperty("level").GetBoolean());
        // This deliberately checks DTO shape, not coverage, playable media or restore validation.
    }

    [Fact]
    public void DtoDeserializationRejectsMissingAndUnknownFields()
    {
        var state = JsonNode.Parse(JsonSerializer.Serialize(EmptyState()))!.AsObject();
        state.Remove("paused");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TapeComponent2Dto>(state.ToJsonString()));
        state["paused"] = false;
        state["hostPath"] = "/not-a-supported-field";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TapeComponent2Dto>(state.ToJsonString()));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TapeRationalDto>("{\"num\":\"0\"}"));
    }

    [Fact]
    public void MigrationInputReusesExistingCursorAndEventTypes()
    {
        var cursor = new TapeCursor(TapeState.Stopped, 0, TapeStage.Pilot, 0, 0, 0, false, 0, 3, 1, 2);
        var pending = new DeviceEvent(10, EventPhase.SignalEdge, 3, 100, 2, 0, 1);
        var input = new Tape1MigrationInput(cursor, [], 9, 3, 2, pending);
        Assert.Same(cursor, input.Cursor);
        Assert.Same(pending, input.PendingEvent);
        Assert.Equal((Int128)3, input.Cursor.RemainingNumerator);
        Assert.True(typeof(ITapeDeck).IsInterface);
        Assert.True(typeof(ITapeDeckStateTransfer).IsInterface);
        Assert.True(typeof(ITapeSignalIndex).IsInterface);
        // Core extends the SAME transport; this type assertion is not runtime/installed proof.
        Assert.True(typeof(ITapeDeck).IsAssignableFrom(typeof(TapeTransport)));
        Assert.True(typeof(ITapeDeckStateTransfer).IsAssignableFrom(typeof(TapeTransport)));
    }
}
