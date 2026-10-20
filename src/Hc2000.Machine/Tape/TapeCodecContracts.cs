using System.Collections.Immutable;

namespace Hc2000.Machine.Tape;

public enum TapeError
{
    InvalidFormat, UnsupportedFormat, UnsupportedVersion, UnsupportedCompression,
    BoundsExceeded, CapacityExceeded, RouteSelectionRequired, InvalidRoute, Interlock,
    WriteProtected, DirtyConfirmationRequired, ExactEncodingUnavailable, RuntimeUnavailable,
    SnapshotUnsupported
}
public sealed record TapeFailure(TapeError Code, string Detail);
public sealed record TapeWarning(string Code, string Detail);

/// <summary>Failures cannot carry partial-success data. Accessing Value on a failure is an error.</summary>
public sealed class TapeResult<T> where T : notnull
{
    private readonly T? value;
    public TapeFailure? Failure { get; }
    public bool Success => Failure is null;
    public T Value => Success ? value! : throw new InvalidOperationException("Tape operation failed.");
    private TapeResult(T value) { this.value = value; }
    private TapeResult(TapeFailure failure) { Failure = failure; }
    public static TapeResult<T> Ok(T value) => new(value ?? throw new ArgumentNullException(nameof(value)));
    public static TapeResult<T> Fail(TapeError code, string detail) => new(new TapeFailure(code, detail));
}

/// <summary>Callers may lower these policy bounds; core/codecs must validate them before work.</summary>
public sealed record TapeLimits
{
    public int InputBytes { get; init; } = 64 * 1024 * 1024;
    public int DecodedBytes { get; init; } = 256 * 1024 * 1024;
    public int SourceBlocks { get; init; } = 100_000;
    public int RouteSteps { get; init; } = 1_000_000;
    public int VirtualTransitions { get; init; } = 16_000_000;
    public TapeTime MaxDuration { get; init; } = new(2700, 1);
}

public sealed record TapeImportOptions(TapeFormat Format, TapeSide Side, TapeCapacity Capacity)
{
    private TapeLimits limits = new();
    internal bool HasExplicitLimits { get; private init; }
    public TapeChannel Channel { get; init; } = TapeChannel.Left;
    public bool Target48K { get; init; } = true;
    public ImmutableArray<TapeSelection> Selections { get; init; } = [];
    public TapeLimits Limits { get => limits; init { limits = value; HasExplicitLimits = true; } }
}
public sealed record TapeExportOptions(TapeFormat Target, TapeSide Side)
{
    public uint? Rate { get; init; }
    public TapeChannel? Channel { get; init; }
    public bool AllowQuantize { get; init; }
    public bool UseZrle { get; init; }
    public uint WavBitsPerSample { get; init; } = 16;
    public TapeLimits Limits { get; init; } = new();
}
public enum TapeSignalFidelity { ExactSignal, Quantized, DataOnly }
public enum TapeByteFidelity { OriginalBytes, Transformed }
public enum TapeAmplitudeFidelity { OriginalAmplitude, Synthesized, NotPreserved, NotApplicable }
public sealed record TapeWriteReport(TapeFormat Target, TapeSignalFidelity Signal,
    TapeByteFidelity Bytes, TapeAmplitudeFidelity Amplitude, uint? Rate,
    TapeTime MaxEdgeDisplacement, ulong EliminatedTransitions, ImmutableArray<TapeAnnotation> ChangedAnnotations);
public sealed record TapeWriteResult
{
    public ImmutableArray<byte> Bytes { get; }
    public TapeWriteReport Report { get; }
    public TapeWriteResult(ReadOnlySpan<byte> bytes, TapeWriteReport report)
    {
        Bytes = ImmutableArray.Create(bytes.ToArray()); Report = report;
    }
}
public sealed record TapeReadResult(TapeSideImage Image, ImmutableArray<TapeSourceAsset> SourceAssets,
    ImmutableArray<TapeWarning> Warnings);
public sealed record TapeCodecCapabilities(bool CanRead, bool CanWrite, string? UnavailableReason);

/// <summary>Detached bounded conversion. Implementations do not mutate a running deck or do I/O.</summary>
public interface ITapeCodec
{
    TapeFormat Format { get; }
    TapeCodecCapabilities Capabilities { get; }
    TapeResult<TapeReadResult> Read(ReadOnlySpan<byte> bytes, TapeImportOptions options);
    TapeResult<TapeWriteResult> Write(TapeSideImage image, TapeExportOptions options);
}
public interface ITapeCodecRegistry
{
    ImmutableArray<TapeFormat> Formats { get; }
    TapeResult<ITapeCodec> Find(TapeFormat format);
}

/// <summary>No codec implementation is shipped/enabled by the foundation.</summary>
public sealed class UnavailableTapeCodecRegistry : ITapeCodecRegistry
{
    public ImmutableArray<TapeFormat> Formats => [];
    public TapeResult<ITapeCodec> Find(TapeFormat format) =>
        TapeResult<ITapeCodec>.Fail(TapeError.RuntimeUnavailable, $"{format} codec is not installed.");
}
