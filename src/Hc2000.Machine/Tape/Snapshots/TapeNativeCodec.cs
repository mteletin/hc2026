using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Hc2000.Machine.Tape.Snapshots;

/// <summary>Strict, bounded native cassette wire; host asset hashes are verified by HCS.
/// This codec has no path access, scheduler or live-deck mutation.</summary>
public static class TapeNativeCodec
{
    internal static string Decimal(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    internal static ulong Unsigned(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 20 || text.Length > 1 && text[0] == '0' || text.Any(c => c is < '0' or > '9')
            || !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw new ArgumentException("Noncanonical decimal64.");
        return value;
    }
    public static TapeRationalDto Rational(TapeTime value) => new(value.Numerator.ToString(CultureInfo.InvariantCulture), value.Denominator.ToString(CultureInfo.InvariantCulture));
    public static TapeTime Rational(TapeRationalDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        BigInteger Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 39 || text.Length > 1 && text[0] == '0' || text.Any(c => c is < '0' or > '9'))
                throw new ArgumentException("Noncanonical rational decimal.");
            var number = BigInteger.Parse(text, CultureInfo.InvariantCulture);
            if (number > (BigInteger)Int128.MaxValue) throw new ArgumentException("Rational width.");
            return number;
        }
        var num = Parse(value.Num); var den = Parse(value.Den);
        if (den == 0 || BigInteger.GreatestCommonDivisor(num, den) != 1) throw new ArgumentException("Rational must be reduced with positive denominator.");
        return new((Int128)num, (Int128)den);
    }
    internal static T Parse<T>(ReadOnlySpan<byte> bytes) where T : notnull
    {
        using var doc = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 32 });
        CheckDuplicates(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { MaxDepth = 32 }) ?? throw new JsonException("Missing native state.");
    }
    internal static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate field."); CheckDuplicates(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var value in node.EnumerateArray()) CheckDuplicates(value);
    }
    public static TapeResult<byte[]> Write(TapeCassetteImage cassette, TapeLimits limits, TapeLimits? wavLimits = null)
    {
        try
        {
            TapePolicy.Validate(limits);
            if (!Enum.IsDefined(cassette.Capacity)) throw new ArgumentException("Capacity.");
            var duration = new TapeTime(cassette.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1);
            if (cassette.A.Duration != duration || cassette.B.Duration != duration) throw new ArgumentException("Capacity coverage.");
            foreach (var image in new[] { cassette.A, cassette.B })
            {
                var index = new TapeSignalIndexFactory().Build(image, limits);
                if (!index.Success) return TapeResult<byte[]>.Fail(index.Failure!.Code, index.Failure.Detail);
            }
            ValidateAssets(cassette, limits, wavLimits);
            var wire = new TapeCassetteDto(1, Rational(duration), [Side(cassette.A), Side(cassette.B)], cassette.SourceAssets.Select(s => s.Id).Order(StringComparer.Ordinal).ToImmutableArray());
            var result = JsonSerializer.SerializeToUtf8Bytes(wire);
            if (result.Length + cassette.SourceAssets.Sum(s => (long)s.Bytes.Length) > limits.DecodedBytes)
                return TapeResult<byte[]>.Fail(TapeError.BoundsExceeded, "Native/provenance byte limit.");
            return TapeResult<byte[]>.Ok(result);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or JsonException or InvalidOperationException or NullReferenceException)
        { return TapeResult<byte[]>.Fail(TapeError.InvalidFormat, e.Message); }
    }
    private static TapeSideDto Side(TapeSideImage image) => new(
        image.Chunks.Select(c => new TapeChunkDto(Decimal(c.Repeat), c.Runs.Select(r => new TapeRunDto(r.Level, Rational(r.Duration))).ToImmutableArray())).ToImmutableArray(),
        image.Markers.Select(m => new TapeMarkerDto(Rational(m.Position), m.Ordinal, "format-stop", m.Label, m.SourceId, m.SourceBlock)).ToImmutableArray(),
        WithSourceExtent(image).Select(m => new TapeMarkerDto(Rational(m.Position), m.Ordinal, AnnotationName(m.Kind), m.Label, m.SourceId, m.SourceBlock)).ToImmutableArray(),
        image.SourceRoute.Select(r => new TapeRouteVisitDto(r.SourceId, r.BlockIndex, r.Visit)).ToImmutableArray(),
        new(FormatName(image.ImportOptions.Format), image.ImportOptions.Channel?.ToString().ToLowerInvariant(), image.ImportOptions.Target48K,
            image.ImportOptions.Selections.Select(s => new TapeSelectionDto(s.Occurrence, s.Option)).ToImmutableArray(), image.ImportOptions.ComparatorModel));
    private static IEnumerable<TapeAnnotation> WithSourceExtent(TapeSideImage image)
    {
        var entries = image.Annotations.Where(a => a.Kind == TapeAnnotationKind.SourceEof).ToArray();
        if (entries.Length > 1 || entries.Length == 1 && entries[0].Position != image.SourceDuration)
            throw new ArgumentException("Conflicting source extent.");
        if (entries.Length == 1) return image.Annotations;
        var samePosition = image.Annotations.Where(a => a.Position == image.SourceDuration).ToArray();
        uint ordinal = samePosition.Length == 0 ? 0 : checked(samePosition.Max(a => a.Ordinal) + 1);
        return image.Annotations.Append(new(image.SourceDuration, ordinal, TapeAnnotationKind.SourceEof, "source-eof", null, null))
            .OrderBy(a => a.Position).ThenBy(a => a.Ordinal);
    }
    public static TapeResult<TapeCassetteImage> Read(ReadOnlySpan<byte> bytes, IEnumerable<TapeSourceAsset> sources, TapeLimits limits, TapeLimits? wavLimits = null)
    {
        try
        {
            TapePolicy.Validate(limits);
            if (bytes.Length > limits.DecodedBytes) return TapeResult<TapeCassetteImage>.Fail(TapeError.BoundsExceeded, "Native byte limit.");
            var dto = Parse<TapeCassetteDto>(bytes);
            if (dto.Version != 1) return TapeResult<TapeCassetteImage>.Fail(TapeError.UnsupportedVersion, "Native cassette version.");
            var duration = Rational(dto.SideDuration);
            if (duration != new TapeTime(1800, 1) && duration != new TapeTime(2700, 1) || dto.Sides.Length != 2) throw new ArgumentException("Native capacity/sides.");
            var assets = sources.OrderBy(s => s.Id, StringComparer.Ordinal).ToImmutableArray();
            if (dto.SourceAssets.Distinct(StringComparer.Ordinal).Count() != dto.SourceAssets.Length || !dto.SourceAssets.Order().SequenceEqual(assets.Select(s => s.Id).Order()))
                throw new ArgumentException("Native source asset roster.");
            var a = Image(dto.Sides[0], duration, limits); var b = Image(dto.Sides[1], duration, limits);
            var cassette = new TapeCassetteImage(duration == new TapeTime(1800, 1) ? TapeCapacity.C60 : TapeCapacity.C90, a, b, assets);
            ValidateAssets(cassette, limits, wavLimits);
            if (bytes.Length + assets.Sum(s => (long)s.Bytes.Length) > limits.DecodedBytes) throw new ArgumentException("Native/provenance byte limit.");
            return TapeResult<TapeCassetteImage>.Ok(cassette);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or JsonException or InvalidOperationException or NullReferenceException)
        { return TapeResult<TapeCassetteImage>.Fail(TapeError.InvalidFormat, e.Message); }
    }
    private static TapeSideImage Image(TapeSideDto dto, TapeTime duration, TapeLimits limits)
    {
        if (dto.Chunks.Length > limits.VirtualTransitions || dto.Markers.Length > limits.RouteSteps || dto.Annotations.Length > limits.RouteSteps
            || dto.SourceRoute.Length > limits.RouteSteps) throw new ArgumentException("Native collection limit.");
        var options = dto.ImportOptions;
        var format = FormatValue(options.Format);
        TapeChannel? channel = options.Channel switch { null => null, "left" => TapeChannel.Left, "right" => TapeChannel.Right, _ => throw new ArgumentException("Channel.") };
        if (options.ComparatorModel is not (null or "hc-ear-rc-q32-v1" or Codecs.WavTapeCodec.ComparatorModel)) throw new ArgumentException("Comparator model.");
        var annotations = dto.Annotations.Select(m => new TapeAnnotation(Rational(m.Position), m.Ordinal, AnnotationValue(m.Kind), m.Label, m.SourceId, m.SourceBlock)).ToImmutableArray();
        var eof = annotations.Where(a => a.Kind == TapeAnnotationKind.SourceEof).ToArray();
        if (eof.Length > 1 || eof.Length == 0 && format != TapeFormat.Blank) throw new ArgumentException("Missing/duplicate native source extent.");
        var sourceDuration = eof.Length == 0 ? default : eof[0].Position;
        var image = new TapeSideImage(duration, sourceDuration,
            dto.Chunks.Select(c => new TapeSignalChunk(Unsigned(c.Repeat), c.Runs.Select(r => new TapeRun(r.Level, Rational(r.Duration))))),
            dto.Markers.Select(m => m.Kind == "format-stop" ? new TapeStopMarker(Rational(m.Position), m.Ordinal, m.Label, m.SourceId, m.SourceBlock) : throw new ArgumentException("Marker kind.")),
            annotations, dto.SourceRoute.Select(r => new TapeRouteVisit(r.SourceId, r.BlockIndex, r.Visit)),
            new(format, channel, options.Target48K, options.Selections.Select(s => new TapeSelection(s.Occurrence, s.Option)).ToImmutableArray(), options.ComparatorModel));
        var result = new TapeSignalIndexFactory().Build(image, limits);
        if (!result.Success) throw new ArgumentException(result.Failure!.Detail);
        return image;
    }
    internal static void ValidateAssets(TapeCassetteImage cassette, TapeLimits limits, TapeLimits? wavLimits = null)
    {
        if (TapeInputPolicy.ValidateSources(cassette, limits, wavLimits) is { } failure) throw new ArgumentException(failure.Detail);
        var ids = new HashSet<string>(StringComparer.Ordinal); long bytes = 0;
        foreach (var asset in cassette.SourceAssets)
        {
            if (!asset.Id.StartsWith("tape-source-", StringComparison.Ordinal) || !ids.Add(asset.Id) || !Enum.IsDefined(asset.Format) || asset.Format == TapeFormat.Blank)
                throw new ArgumentException("Source asset id/format.");
            Unsigned(asset.Id[12..]);
            bytes += asset.Bytes.Length;
            if (bytes > limits.DecodedBytes) throw new ArgumentException("Source storage limit.");
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var side in new[] { cassette.A, cassette.B })
        {
            foreach (var id in side.SourceRoute.Select(r => r.SourceId).Concat(side.Markers.Select(m => m.SourceId)).Concat(side.Annotations.Select(m => m.SourceId)))
                if (id is not null) used.Add(id);
        }
        if (!used.SetEquals(ids)) throw new ArgumentException("Source assets must exactly match provenance references.");
    }
    internal static string FormatName(TapeFormat format) => format.ToString().ToLowerInvariant();
    internal static TapeFormat FormatValue(string format) => format switch
    { "blank" => TapeFormat.Blank, "tap" => TapeFormat.Tap, "tzx" => TapeFormat.Tzx, "pzx" => TapeFormat.Pzx, "csw" => TapeFormat.Csw, "wav" => TapeFormat.Wav, _ => throw new ArgumentException("Format.") };
    private static string AnnotationName(TapeAnnotationKind kind) => kind switch
    { TapeAnnotationKind.Text => "text", TapeAnnotationKind.Metadata => "metadata", TapeAnnotationKind.Overwrite => "overwrite", TapeAnnotationKind.UnsupportedAction => "unsupported-action", TapeAnnotationKind.SourceEof => "source-eof", _ => throw new ArgumentException("Annotation kind.") };
    private static TapeAnnotationKind AnnotationValue(string kind) => kind switch
    { "text" => TapeAnnotationKind.Text, "metadata" => TapeAnnotationKind.Metadata, "overwrite" => TapeAnnotationKind.Overwrite, "unsupported-action" => TapeAnnotationKind.UnsupportedAction, "source-eof" => TapeAnnotationKind.SourceEof, _ => throw new ArgumentException("Annotation kind.") };
}
