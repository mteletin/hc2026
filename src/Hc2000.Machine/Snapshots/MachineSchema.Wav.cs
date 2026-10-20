using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Hc2000.Machine.Tape.Snapshots;

namespace Hc2000.Machine.Snapshots;

public sealed partial class MachineSchema
{
    protected internal override bool HasLargeBlobPolicy => wavPolicy is not null || profile.Memory.RamPaged is not null;
    protected internal override void ReserveCopies(long wireBytes) => wavPolicy?.Reserve(wireBytes);

    protected internal override bool AdmitLargeBlob(JsonElement manifest, uint blobId, int length,
        HcsReadWindow read, IReadOnlyList<byte[]> blobs, HcsLimits limits)
    {
        if (AdmitPagedRamBlob(manifest, blobId, length)) return true;
        if (wavPolicy is null || length > wavPolicy.MaxInputBytes || length > TapeInputPolicy.WavInputBytes) return false;
        try
        {
            var assets = manifest.GetProperty("assets").EnumerateArray().ToArray();
            if (!ValidOriginalRoster(manifest, assets)) return false;
            var references = assets.Where(a => UInt(a.GetProperty("blobId")) == blobId).ToArray();
            if (references.Length == 0 || references.Any(a => !IsWavOriginal(a) || Counter(a.GetProperty("byteLength")) != length)) return false;
            var ids = references.Select(a => Text(a.GetProperty("id"))).ToHashSet(StringComparer.Ordinal);
            if (manifest.GetProperty("stores").EnumerateArray().Any(s => ids.Contains(Text(s.GetProperty("assetId"))))) return false;
            var tape = manifest.GetProperty("components").EnumerateArray().SingleOrDefault(c => Text(c.GetProperty("id")) == "tape");
            if (tape.ValueKind != JsonValueKind.Object || UInt(tape.GetProperty("modelVersion")) != 2
                || TapeComponentCodec.Read(tape.GetProperty("state")).CassetteAssetId != TapeSnapshotContract.CassetteAssetId) return false;
            var native = assets.SingleOrDefault(a => Text(a.GetProperty("id")) == TapeSnapshotContract.CassetteAssetId);
            if (native.ValueKind != JsonValueKind.Object || Text(native.GetProperty("kind")) != TapeSnapshotContract.CassetteAssetKind
                || Text(native.GetProperty("format")) != TapeSnapshotContract.CassetteAssetFormat) return false;
            var nativeId = UInt(native.GetProperty("blobId"));
            if (nativeId == blobId || nativeId == 0 || nativeId > blobs.Count || blobs[(int)nativeId - 1] is not { } nativeBytes
                || nativeBytes.Length > limits.BlobBytes || Counter(native.GetProperty("byteLength")) != nativeBytes.Length
                || Text(native.GetProperty("sha256")) != Convert.ToHexStringLower(SHA256.HashData(nativeBytes))) return false;
            // Canonical asset order places this bounded, hash-checked native cassette before its sources.
            var cassette = TapeNativeCodec.Parse<TapeCassetteDto>(nativeBytes);
            var duration = TapeNativeCodec.Rational(cassette.SideDuration);
            if (cassette.Version != 1 || cassette.Sides.Length != 2 || duration != new TapeTime(1800, 1) && duration != new TapeTime(2700, 1)) return false;
            var roster = assets.Where(a => Text(a.GetProperty("id")).StartsWith("tape-source-", StringComparison.Ordinal))
                .Select(a => Text(a.GetProperty("id"))).ToHashSet(StringComparer.Ordinal);
            if (cassette.SourceAssets.Length != roster.Count || !roster.SetEquals(cassette.SourceAssets) || !ids.IsSubsetOf(roster)) return false;
            var used = cassette.Sides.SelectMany(s => s.SourceRoute.Select(r => r.SourceId)
                .Concat(s.Markers.Select(m => m.SourceId)).Concat(s.Annotations.Select(a => a.SourceId)))
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (!roster.SetEquals(used)) return false;
            var options = new TapeImportOptions(TapeFormat.Wav, TapeSide.A, duration.Numerator == 1800 ? TapeCapacity.C60 : TapeCapacity.C90)
                { Limits = wavPolicy.TapeLimits };
            var probe = new WavPcmPreflight(length, options);
            Span<byte> window = stackalloc byte[18];
            while (probe.ReadLength != 0)
            {
                var part = window[..probe.ReadLength]; read(probe.ReadOffset, part); probe.Feed(part);
            }
            return probe.Complete;
        }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or NullReferenceException)
        { return false; }
    }

    private bool ValidOriginalRoster(JsonElement manifest, JsonElement[] assets)
    {
        var required = new Dictionary<string, (string Kind, string Format)>(StringComparer.Ordinal)
        {
            ["disk-a-current"] = ("disk-current", "raw1"), ["disk-b-current"] = ("disk-current", "raw1"),
            ["ram"] = ("ram", "raw1"), ["rom"] = ("rom", "raw1"),
            ["shadow-ram"] = ("ram", "raw1"), ["shadow-rom"] = ("rom", "raw1"),
            [TapeSnapshotContract.CassetteAssetId] = (TapeSnapshotContract.CassetteAssetKind, TapeSnapshotContract.CassetteAssetFormat)
        };
        foreach (var asset in assets)
        {
            var id = Text(asset.GetProperty("id"));
            var kind = Text(asset.GetProperty("kind")); var format = Text(asset.GetProperty("format"));
            if (required.Remove(id, out var expected))
            { if (kind != expected.Kind || format != expected.Format) return false; }
            else if (id is "disk-a-base" or "disk-b-base")
            { if (kind != "disk-base" || format != "raw1") return false; }
            else if (id.StartsWith("tape-source-", StringComparison.Ordinal))
            {
                _ = TapeNativeCodec.Unsigned(id[12..]);
                if (kind != "buffer" || format is not ("tap1" or "tzx1" or "pzx1" or "csw1" or "wav1")) return false;
            }
            else return false;
        }
        if (required.Count != 0) return false;
        var components = manifest.GetProperty("components").EnumerateArray().Select(c => Text(c.GetProperty("id"))).ToHashSet(StringComparer.Ordinal);
        if (UsesCpuFrontiers && !components.Remove(ExpansionSchema.ComponentId)) return false;
        if (!new[] { "frame-clock", "keyboard", "memory", "tape", "ula" }.All(components.Remove)) return false;
        if (components.Count != 0 && !components.SetEquals(new[] { "fdc", "drive-a", "drive-b" })) return false;
        var stores = manifest.GetProperty("stores").EnumerateArray().ToArray();
        return stores.Length == 2 && stores.All(s => Text(s.GetProperty("id")) is "ram" or "shadow-ram"
            && Text(s.GetProperty("assetId")) == Text(s.GetProperty("id")));
    }

    private static bool IsWavOriginal(JsonElement asset)
    {
        var id = Text(asset.GetProperty("id"));
        if (!id.StartsWith("tape-source-", StringComparison.Ordinal) || Text(asset.GetProperty("kind")) != "buffer"
            || Text(asset.GetProperty("format")) != "wav1") return false;
        _ = TapeNativeCodec.Unsigned(id[12..]);
        return true;
    }

    protected override void ValidateBlobModels(JsonElement manifest, byte[][] blobs, HcsLimits limits)
    {
        try { ValidatePagedBlobModel(manifest, blobs); ValidateWavBlobModels(manifest, blobs); }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException)
        { Error(HcsError.InvalidFormat, "native WAV source model"); }
    }

    private void ValidateWavBlobModels(JsonElement manifest, byte[][] blobs)
    {
        if (wavPolicy is null) return;
        var assets = manifest.GetProperty("assets").EnumerateArray().ToArray();
        var sources = assets.Where(a => Text(a.GetProperty("id")).StartsWith("tape-source-", StringComparison.Ordinal)).ToArray();
        if (!sources.Any(a => Text(a.GetProperty("format")) == "wav1")) return;
        if (!ValidOriginalRoster(manifest, assets)) Error(HcsError.AssetMismatch, "original WAV manifest roster");
        var tape = manifest.GetProperty("components").EnumerateArray().SingleOrDefault(c => Text(c.GetProperty("id")) == "tape");
        if (tape.ValueKind != JsonValueKind.Object || UInt(tape.GetProperty("modelVersion")) != 2
            || TapeComponentCodec.Read(tape.GetProperty("state")).CassetteAssetId != TapeSnapshotContract.CassetteAssetId)
            Error(HcsError.AssetMismatch, "original WAV cassette relationship");
        var native = assets.SingleOrDefault(a => Text(a.GetProperty("id")) == TapeSnapshotContract.CassetteAssetId);
        if (native.ValueKind != JsonValueKind.Object || Text(native.GetProperty("kind")) != TapeSnapshotContract.CassetteAssetKind
            || Text(native.GetProperty("format")) != TapeSnapshotContract.CassetteAssetFormat)
            Error(HcsError.AssetMismatch, "original WAV native cassette");
        // Charge declared originals plus the existing native DTO's actual storage before immutable copies.
        var nativeBytes = blobs[UInt(native.GetProperty("blobId")) - 1];
        var dto = TapeNativeCodec.Parse<TapeCassetteDto>(nativeBytes);
        long retained = sources.Sum(a => Counter(a.GetProperty("byteLength")));
        foreach (var side in dto.Sides)
        {
            retained = checked(retained + 512L + side.SourceRoute.Length * 64L + side.ImportOptions.Selections.Length * 24L);
            foreach (var chunk in side.Chunks) retained = checked(retained + 64L + chunk.Runs.Length * 64L);
            foreach (var marker in side.Markers.Concat(side.Annotations)) retained = checked(retained + 128L + marker.Label.Length * 2L);
        }
        if (retained > new TapeLimits().DecodedBytes) Error(HcsError.LimitExceeded, "joint native/original storage");
        var originals = sources.Select(a => new TapeSourceAsset(Text(a.GetProperty("id")),
            TapeNativeCodec.FormatValue(Text(a.GetProperty("format"))[..^1]), blobs[UInt(a.GetProperty("blobId")) - 1])).ToArray();
        var result = TapeNativeCodec.Read(blobs[UInt(native.GetProperty("blobId")) - 1], originals, new(), wavPolicy.TapeLimits);
        if (!result.Success) Error(HcsError.AssetMismatch, "native WAV source validation: " + result.Failure!.Detail);
    }
}
