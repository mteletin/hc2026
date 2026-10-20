using Hc2000.Machine.Tape.Codecs;

namespace Hc2000.Machine.Tape;

/// <summary>Explicit host caller policy, not an implicit codec retry or a generic cap increase.
/// Callers select once before reading; an explicitly supplied TapeLimits must be used unchanged.</summary>
public static class TapeInputPolicy
{
    public const int WavInputBytes = 256 * 1024 * 1024;
    public static TapeLimits DefaultLimits(TapeFormat format) => format == TapeFormat.Wav
        ? new() { InputBytes = WavInputBytes } : new();
    public static TapeImportOptions Select(TapeImportOptions options) => options.HasExplicitLimits
        ? options : options with { Limits = DefaultLimits(options.Format) };

    internal static TapeFailure? ValidateSources(TapeCassetteImage cassette, TapeLimits limits, TapeLimits? wavLimits)
    {
        long retained = 0;
        foreach (var source in cassette.SourceAssets)
        {
            var chosen = source.Format == TapeFormat.Wav && wavLimits is not null ? wavLimits : limits;
            if (source.Bytes.Length > chosen.InputBytes || !Enum.IsDefined(source.Format) || source.Format == TapeFormat.Blank)
                return new(TapeError.BoundsExceeded, "Original source input budget/format.");
            retained = checked(retained + source.Bytes.Length);
            if (source.Format == TapeFormat.Wav)
            {
                var probe = new WavPcmPreflight(source.Bytes.Length,
                    new(TapeFormat.Wav, TapeSide.A, cassette.Capacity) { Limits = chosen });
                while (probe.ReadLength != 0) probe.Feed(source.Bytes.AsSpan((int)probe.ReadOffset, probe.ReadLength));
                if (probe.Failure is not null) return probe.Failure;
            }
        }
        // Do not change non-WAV policy semantics in this qualification.
        if (!cassette.SourceAssets.Any(s => s.Format == TapeFormat.Wav))
            return retained > limits.DecodedBytes ? new(TapeError.BoundsExceeded, "Original storage budget.") : null;
        foreach (var side in new[] { cassette.A, cassette.B })
        {
            retained = checked(retained + 512L + side.SourceRoute.Length * 64L + side.ImportOptions.Selections.Length * 24L);
            foreach (var chunk in side.Chunks) retained = checked(retained + 64L + chunk.Runs.Length * 64L);
            foreach (var marker in side.Markers) retained = checked(retained + 128L + marker.Label.Length * 2L);
            foreach (var annotation in side.Annotations) retained = checked(retained + 128L + annotation.Label.Length * 2L);
        }
        return retained > limits.DecodedBytes ? new(TapeError.BoundsExceeded, "Joint original/signal/metadata budget.") : null;
    }
}
