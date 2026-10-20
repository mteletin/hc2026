using System.Collections.Immutable;
using System.Text.Json;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.Tools.Tests;

/// <summary>Host adapter contract vectors; codec/guest fidelity is deliberately not claimed here.</summary>
public sealed class TapeCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-tape-cli-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureCodec codec = new();
    private readonly NativeImageAdmissionTests.Domain nativeDomain = new();
    private readonly IDisposable nativeScope;
    public TapeCommandTests() { Directory.CreateDirectory(directory); nativeScope = nativeDomain.Enter(); }
    public void Dispose() { nativeScope.Dispose(); nativeDomain.Dispose(); Directory.Delete(directory, true); }
    private string PathIn(string name) => Path.Combine(directory, name);
    private string Input(string name = "in.tzx")
    {
        var p = PathIn(name);
        // The WAV caller's admission is real even when the downstream codec is a host-contract fixture.
        // Independent stereo PCM16/rate1/zero-frame RIFF permits the existing explicit right-channel test.
        File.WriteAllBytes(p, Path.GetExtension(name) == ".wav"
            ? Convert.FromHexString("524946462400000057415645666D742010000000010002000100000004000000040010006461746100000000")
            : [0x5a, 0x58, 0x54, 0x61, 0x70, 0x65, 0x21, 0x1a, 1, 20, 0x12, 1, 0, 2, 0]);
        return p;
    }
    private (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter(); var error = new StringWriter();
        int code = TapeCommand.Run(args, output, error, new FixtureRegistry(codec));
        return (code, output.ToString(), error.ToString());
    }
    [Fact]
    public void InfoUsesCodecRationalsAndComparatorMetadataWithoutMutatingSource()
    {
        var input = Input(); var before = File.ReadAllBytes(input);
        var result = Run("info", input, "--json", "--side", "B", "--capacity", "C90");
        Assert.Equal(0, result.Code); Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(TapeSide.B, codec.Import!.Side); Assert.Equal(TapeCapacity.C90, codec.Import.Capacity);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("1", json.RootElement.GetProperty("SourceDuration").GetProperty("Num").GetString());
        Assert.Equal("7", json.RootElement.GetProperty("SourceDuration").GetProperty("Den").GetString());
        Assert.Equal("fixture-comparator-v1", json.RootElement.GetProperty("Comparator").GetString());
        Assert.Contains("warning fixture-warning", result.Error);
        Assert.Equal(1, codec.Reads); Assert.Equal(0, codec.Writes);
    }
    [Fact]
    public void BlocksUsesRouteVisitsMarkersAndAnnotationsIncludingRepeatedVisits()
    {
        var result = Run("blocks", Input(), "--json");
        Assert.Equal(0, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        var route = json.RootElement.GetProperty("SourceRoute");
        Assert.Equal(2, route.GetArrayLength()); Assert.Equal(2u, route[0].GetProperty("BlockIndex").GetUInt32());
        Assert.Equal(1u, route[1].GetProperty("Visit").GetUInt32());
        Assert.Equal("pause", json.RootElement.GetProperty("Markers")[0].GetProperty("Label").GetString());
        Assert.Equal("authored metadata", json.RootElement.GetProperty("Annotations")[0].GetProperty("Label").GetString());
    }
    [Fact]
    public void ConvertPassesSameImageAndOptionsAndReportsWriterFidelityWithoutInventingLosslessClaim()
    {
        var input = Input("in.wav"); var before = File.ReadAllBytes(input); var target = PathIn("out.tzx");
        var result = Run("convert", input, "-o", target, "--rate", "3546895", "--quantize", "--zrle", "--channel", "right", "--side", "B", "--json");
        Assert.Equal(0, result.Code); Assert.Same(codec.Image, codec.WrittenImage);
        Assert.Equal(TapeChannel.Right, codec.Import!.Channel); Assert.Equal(TapeSide.B, codec.Export!.Side);
        Assert.Equal(3546895u, codec.Export.Rate); Assert.True(codec.Export.AllowQuantize); Assert.True(codec.Export.UseZrle);
        Assert.Equal(new byte[] { 0x21, 0x43, 0x65 }, File.ReadAllBytes(target)); Assert.Equal(before, File.ReadAllBytes(input));
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("Quantized", json.RootElement.GetProperty("Signal").GetString());
        Assert.Equal("Transformed", json.RootElement.GetProperty("Bytes").GetString());
        Assert.Equal("NotPreserved", json.RootElement.GetProperty("Amplitude").GetString());
        Assert.Equal(2ul, json.RootElement.GetProperty("EliminatedTransitions").GetUInt64());
        Assert.Equal("3546895", json.RootElement.GetProperty("MaxEdgeDisplacement").GetProperty("Den").GetString());
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }
    [Fact]
    public void RouteChoicesAndTargetArePassedWithoutInterpretingContainerBytes()
    {
        var result = Run("info", Input(), "--select", "0:1", "--select", "2:0", "--target48k", "false");
        Assert.Equal(0, result.Code); Assert.False(codec.Import!.Target48K);
        Assert.Equal(new[] { new TapeSelection(0, 1), new TapeSelection(2, 0) }, codec.Import.Selections);
        Assert.Equal(new byte[] { 0x5a, 0x58, 0x54, 0x61, 0x70, 0x65, 0x21, 0x1a, 1, 20, 0x12, 1, 0, 2, 0 }, codec.ReadBytes);
    }
    [Theory]
    [InlineData(TapeError.RouteSelectionRequired)] [InlineData(TapeError.InvalidRoute)]
    [InlineData(TapeError.UnsupportedCompression)] [InlineData(TapeError.InvalidFormat)]
    public void ReadFailuresPreserveInputAndExistingOutput(TapeError code)
    {
        codec.ReadFailure = code;
        var input = Input(); var before = File.ReadAllBytes(input); var target = PathIn("out.tzx"); File.WriteAllBytes(target, [9]);
        var result = Run("convert", input, "-o", target, "--force");
        Assert.Equal(1, result.Code); Assert.Contains(code.ToString(), result.Error); Assert.Equal("", result.Output);
        Assert.Equal(before, File.ReadAllBytes(input)); Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target));
        Assert.Equal(0, codec.Writes); Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }
    [Fact]
    public void ExactWriterFailureDoesNotRetryQuantizedOrWriteAnything()
    {
        codec.WriteFailure = TapeError.ExactEncodingUnavailable;
        var target = PathIn("out.tzx"); File.WriteAllBytes(target, [9]);
        var result = Run("convert", Input(), "-o", target, "--force");
        Assert.Equal(1, result.Code); Assert.Contains("ExactEncodingUnavailable", result.Error);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target)); Assert.Equal(1, codec.Writes);
        Assert.False(codec.Export!.AllowQuantize); Assert.Null(codec.Export.Rate);
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }
    [Fact]
    public void ExistingOutputNeedsExplicitForceAndInputCannotBeOverwrittenEvenWithForce()
    {
        var input = Input(); var before = File.ReadAllBytes(input); var target = PathIn("out.tzx"); File.WriteAllBytes(target, [9]);
        Assert.Equal(1, Run("convert", input, "-o", target).Code); Assert.Equal(0, codec.Reads);
        Assert.Equal(1, Run("convert", input, "-o", input, "--force").Code); Assert.Equal(0, codec.Reads);
        Assert.Equal(before, File.ReadAllBytes(input)); Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target));
        Assert.Equal(0, Run("convert", input, "-o", target, "--force").Code);
    }
    [Fact]
    public void BoundsCheckedBeforeReadingOrCallingCodec()
    {
        var result = Run("info", Input(), "--max-input", "14");
        Assert.Equal(1, result.Code); Assert.Contains("BoundsExceeded", result.Error); Assert.Equal(0, codec.Reads);
    }
    [Fact]
    public void HostWriteFailureKeepsSourceAndRemovesTemporaryFile()
    {
        var input = Input(); var before = File.ReadAllBytes(input); var target = PathIn("directory.tzx"); Directory.CreateDirectory(target);
        var result = Run("convert", input, "-o", target, "--force");
        Assert.Equal(1, result.Code); Assert.Equal("", result.Output); Assert.Equal(before, File.ReadAllBytes(input));
        Assert.True(Directory.Exists(target)); Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }
    [Fact]
    public void UnavailableRegistryAndCapabilitiesAreTruthfulWithoutFileAccess()
    {
        var output = new StringWriter(); var error = new StringWriter(); var registry = new UnavailableTapeCodecRegistry();
        Assert.Equal(1, TapeCommand.Run(["info", PathIn("missing.tap")], output, error, registry));
        Assert.Contains("RuntimeUnavailable", error.ToString()); Assert.Equal("", output.ToString());
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(0, TapeCommand.Run(["info", "--capabilities", "--json"], output, error, registry));
        using var json = JsonDocument.Parse(output.ToString()); Assert.Equal(5, json.RootElement.GetArrayLength());
        foreach (var entry in json.RootElement.EnumerateArray()) { Assert.False(entry.GetProperty("CanRead").GetBoolean()); Assert.False(entry.GetProperty("CanWrite").GetBoolean()); }
    }
    [Fact]
    public void MissingWriterCapabilityDoesNotCallEitherCodecOrTouchFiles()
    {
        codec.Capabilities = new(true, false, "writer pending");
        var result = Run("convert", Input(), "-o", PathIn("out.tzx"));
        Assert.Equal(1, result.Code); Assert.Contains("RuntimeUnavailable: writer pending", result.Error);
        Assert.Equal(0, codec.Reads); Assert.Equal(0, codec.Writes); Assert.False(File.Exists(PathIn("out.tzx")));
    }
    [Theory]
    [InlineData("--unknown")] [InlineData("--rate", "0")] [InlineData("--rate", "-1")]
    [InlineData("--bits", "12")] [InlineData("--bits", "24")]
    [InlineData("--channel", "right")] [InlineData("--select", "1")]
    [InlineData("--select", "1:0", "--select", "1:1")]
    [InlineData("--side", "2")] [InlineData("--capacity", "C120")]
    [InlineData("--format", "blank")] [InlineData("--format", "5")]
    [InlineData("--max-input", "67108865")] [InlineData("--max-input", "0")]
    [InlineData("--quantize", "--quantize")] [InlineData("--rate")]
    public void InvalidOptionsNeverCallCodecOrModifyFiles(params string[] options)
    {
        var input = Input(); var before = File.ReadAllBytes(input); var target = PathIn("out.tzx"); File.WriteAllBytes(target, [9]);
        var args = new[] { "convert", input, "-o", target, "--force" }.Concat(options).ToArray();
        Assert.Equal(1, Run(args).Code); Assert.Equal(0, codec.Reads); Assert.Equal(0, codec.Writes);
        Assert.Equal(before, File.ReadAllBytes(input)); Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target));
    }
    private sealed class FixtureRegistry(FixtureCodec codec) : ITapeCodecRegistry
    {
        public ImmutableArray<TapeFormat> Formats => [TapeFormat.Tzx, TapeFormat.Wav];
        public TapeResult<ITapeCodec> Find(TapeFormat format) => format is TapeFormat.Tzx or TapeFormat.Wav
            ? TapeResult<ITapeCodec>.Ok(codec) : TapeResult<ITapeCodec>.Fail(TapeError.RuntimeUnavailable, "fixture codec missing");
    }
    private sealed class FixtureCodec : ITapeCodec
    {
        public TapeFormat Format => TapeFormat.Tzx;
        public TapeCodecCapabilities Capabilities { get; set; } = new(true, true, null);
        public int Reads { get; private set; } public int Writes { get; private set; }
        public byte[]? ReadBytes { get; private set; }
        public TapeImportOptions? Import { get; private set; } public TapeExportOptions? Export { get; private set; }
        public TapeSideImage? WrittenImage { get; private set; }
        public TapeError? ReadFailure { get; set; } public TapeError? WriteFailure { get; set; }
        // Independently specified adapter metadata oracle, not a simulated codec implementation.
        public TapeSideImage Image { get; } = new(new(1800, 1), new(1, 7),
            [new(1, [new(false, new(1, 14)), new(true, new(1, 14)), new(false, new(12599, 7))])],
            [new(new(1, 7), 0, "pause", "fixture", 2)],
            [new(new(1, 14), 1, TapeAnnotationKind.Metadata, "authored metadata", "fixture", 2)],
            [new("fixture", 2, 0), new("fixture", 2, 1)],
            new(TapeFormat.Tzx, TapeChannel.Left, true, [], "fixture-comparator-v1"));
        public TapeResult<TapeReadResult> Read(ReadOnlySpan<byte> bytes, TapeImportOptions options)
        {
            Reads++; Import = options; ReadBytes = bytes.ToArray();
            return ReadFailure is { } failure ? TapeResult<TapeReadResult>.Fail(failure, "authored failure")
                : TapeResult<TapeReadResult>.Ok(new(Image, [], [new("fixture-warning", "authored warning")]));
        }
        public TapeResult<TapeWriteResult> Write(TapeSideImage image, TapeExportOptions options)
        {
            Writes++; Export = options; WrittenImage = image;
            return WriteFailure is { } failure ? TapeResult<TapeWriteResult>.Fail(failure, "authored failure")
                : TapeResult<TapeWriteResult>.Ok(new(new byte[] { 0x21, 0x43, 0x65 },
                    new(options.Target, TapeSignalFidelity.Quantized, TapeByteFidelity.Transformed, TapeAmplitudeFidelity.NotPreserved,
                        options.Rate, new(1, 3546895), 2, [Image.Annotations[0]])));
        }
    }
}
