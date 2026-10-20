using System.Buffers.Binary;
using System.Text.Json;
using Hc2000.Machine.Tape;
using Hc2000.Tools;
using Xunit;

namespace Hc2000.Tools.Tests;

// Independent RIFF arithmetic and sparse host files, not the WAV writer as its own oracle.
public sealed class TapeWavBudgetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-wav-budget-" + Guid.NewGuid().ToString("N"));
    public TapeWavBudgetTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string PathFor(string name) => Path.Combine(directory, name);

    private string Wave(string name, uint frames, uint rate = 44100, ushort channels = 1, ushort bits = 16)
    {
        var align = checked((ushort)(channels * bits / 8));
        var dataBytes = checked(frames * align);
        var length = 44L + dataBytes + (dataBytes & 1);
        var header = new byte[44];
        "RIFF"u8.CopyTo(header); Put32(header, 4, checked((uint)length - 8));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8)); Put32(header, 16, 16);
        Put16(header, 20, 1); Put16(header, 22, channels); Put32(header, 24, rate);
        Put32(header, 28, checked(rate * align)); Put16(header, 32, align); Put16(header, 34, bits);
        "data"u8.CopyTo(header.AsSpan(36)); Put32(header, 40, dataBytes);
        var path = PathFor(name);
        using var stream = File.Create(path);
        stream.Write(header); stream.SetLength(length); // PCM16 zero silence; no huge test fixture array.
        return path;
    }
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = TapeCommand.Run(args, output, error, TapeCodecRegistry.Installed());
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void DefaultC60WavInputAdmitsIndependentFullSideAndPreservesDuration()
    {
        var path = Wave("whole.wav", 79_380_000);
        Assert.Equal(158_760_044L, new FileInfo(path).Length);
        var result = Run("info", path, "--json");
        Assert.Equal(0, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("1800", json.RootElement.GetProperty("Duration").GetProperty("Num").GetString());
        Assert.Equal("1", json.RootElement.GetProperty("Duration").GetProperty("Den").GetString());
        Assert.Equal("1800", json.RootElement.GetProperty("SourceDuration").GetProperty("Num").GetString());
        Assert.Equal("1", json.RootElement.GetProperty("SourceDuration").GetProperty("Den").GetString());
        Assert.Contains("ComparatorApproximation", result.Error);
    }

    [Theory]
    [InlineData("whole.wav", null)]
    [InlineData("whole.bin", "wav")]
    public void Explicit64MiBRefusalPreservesExistingOutputAndNeverRetries(string name, string? format)
    {
        var input = Wave(name, 79_380_000);
        var output = PathFor("preserved.wav"); File.WriteAllBytes(output, [9, 8, 7]);
        var args = new List<string> { "convert", input, "-o", output, "--force", "--max-input", "67108864" };
        if (format is not null) args.AddRange(["--format", format]);
        var result = Run([.. args]);
        Assert.Equal(1, result.Code); Assert.Contains("BoundsExceeded", result.Error);
        Assert.Empty(result.Output); Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(output));
        Assert.Equal(158_760_044L, new FileInfo(input).Length);
        Assert.Empty(Directory.GetFiles(directory, "*.hc-*"));
    }

    [Fact]
    public void DefaultNonWavInputStillRefusesBeyond64MiB()
    {
        var input = PathFor("large.tap");
        using (var stream = File.Create(input)) stream.SetLength(67_108_865);
        var result = Run("info", input);
        Assert.Equal(1, result.Code); Assert.Contains("BoundsExceeded", result.Error); Assert.Empty(result.Output);
    }

    [Fact]
    public void WavExplicitLimitCannotRaise256MiBPolicy()
    {
        var result = Run("info", Wave("small.wav", 1), "--max-input", "268435457");
        Assert.Equal(1, result.Code); Assert.Contains("cannot raise", result.Error); Assert.Empty(result.Output);
    }
}
