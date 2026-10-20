using System.Text.Json;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Hc2000.Machine.Tape.Encoding;
using Xunit;

namespace Hc2000.Tools.Tests;

/// <summary>Consumes the already pushed TZX/helper implementation. No independent second decoder.</summary>
public sealed class TapeCliTzxIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-tape-cli-tzx-" + Guid.NewGuid().ToString("N"));
    private readonly ITapeCodecRegistry registry;
    private readonly NativeImageAdmissionTests.Domain nativeDomain = new();
    private readonly IDisposable nativeScope;
    public TapeCliTzxIntegrationTests()
    {
        Directory.CreateDirectory(directory);
        nativeScope = nativeDomain.Enter();
        ITapeRunEncoding encoding = new TapeRunEncoding();
        registry = new TapeCodecRegistry([new TapCodec(), new TzxTapeCodec(encoding)]);
    }
    public void Dispose() { nativeScope.Dispose(); nativeDomain.Dispose(); Directory.Delete(directory, true); }
    private string Input()
    {
        var path = Path.Combine(directory, "tone.tzx");
        // Independently authored TZX1.20 pure-tone: two 3-T-state pulses, post-inverter false then true.
        File.WriteAllBytes(path, [0x5a, 0x58, 0x54, 0x61, 0x70, 0x65, 0x21, 0x1a, 1, 20, 0x12, 3, 0, 2, 0]);
        return path;
    }
    private (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter(); var error = new StringWriter();
        int code = TapeCommand.Run(args, output, error, registry);
        return (code, output.ToString(), error.ToString());
    }
    [Fact]
    public void ExactConversionKeepsHandComputedPhysicalEdgesAndReportsExactSignalNotOriginalBytes()
    {
        var input = Input(); var before = File.ReadAllBytes(input); var target = Path.Combine(directory, "exact.tzx");
        var result = Run("convert", input, "-o", target, "--rate", "3500000", "--json");
        Assert.Equal(0, result.Code); Assert.Equal(before, File.ReadAllBytes(input));
        using var report = JsonDocument.Parse(result.Output);
        Assert.Equal("ExactSignal", report.RootElement.GetProperty("Signal").GetString());
        Assert.Equal("Transformed", report.RootElement.GetProperty("Bytes").GetString());
        Assert.Equal(0ul, report.RootElement.GetProperty("EliminatedTransitions").GetUInt64());
        var parsed = registry.Find(TapeFormat.Tzx).Value.Read(File.ReadAllBytes(target), new(TapeFormat.Tzx, TapeSide.A, TapeCapacity.C60));
        Assert.True(parsed.Success, parsed.Failure?.Detail);
        var indexed = new TapeSignalIndexFactory().Build(parsed.Value.Image, new TapeLimits());
        Assert.True(indexed.Success, indexed.Failure?.Detail);
        Assert.False(indexed.Value.Sample(default));
        Assert.False(indexed.Value.Sample(new(1, 3500000)));
        Assert.True(indexed.Value.Sample(new(3, 3500000)));
        Assert.False(indexed.Value.Sample(new(6, 3500000)));
        Assert.Equal(new TapeTime(3, 3500000), indexed.Value.NextEdge(default));
        Assert.Equal(new TapeTime(6, 3500000), indexed.Value.NextEdge(new(3, 3500000)));
        Assert.Null(indexed.Value.NextEdge(new(6, 3500000)));
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }
    [Fact]
    public void UnrepresentableRequestedRateFailsThenExplicitQuantizedCopyReportsTwoMissingEdges()
    {
        var input = Input(); var before = File.ReadAllBytes(input); var target = Path.Combine(directory, "copy.tzx"); File.WriteAllBytes(target, [9]);
        var exact = Run("convert", input, "-o", target, "--rate", "1", "--force");
        Assert.Equal(1, exact.Code); Assert.Contains("ExactEncodingUnavailable", exact.Error);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(target)); Assert.Equal(before, File.ReadAllBytes(input));
        var quantized = Run("convert", input, "-o", target, "--rate", "1", "--force", "--quantize", "--json");
        Assert.Equal(0, quantized.Code);
        using var report = JsonDocument.Parse(quantized.Output);
        Assert.Equal("Quantized", report.RootElement.GetProperty("Signal").GetString());
        Assert.Equal(2ul, report.RootElement.GetProperty("EliminatedTransitions").GetUInt64());
        Assert.Equal(1u, report.RootElement.GetProperty("Rate").GetUInt32());
        Assert.Equal(before, File.ReadAllBytes(input)); Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }
}
