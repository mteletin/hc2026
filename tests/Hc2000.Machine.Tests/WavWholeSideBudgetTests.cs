using System.Buffers.Binary;
using System.Security.Cryptography;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class WavWholeSideBudgetTests
{
    [Theory]
    [InlineData(1800, 79_380_000L, 158_760_044L)]
    [InlineData(2700, 119_070_000L, 238_140_044L)]
    public void IndependentCanonicalMonoPcm16ArithmeticFitsOnlyWavInputDefault(int seconds, long frames, long bytes)
    {
        Assert.Equal(frames, 44100L * seconds);
        Assert.Equal(bytes, 44L + frames * 2);
        Assert.True(bytes > 64L * 1024 * 1024);
        Assert.True(bytes < 256L * 1024 * 1024);
        Assert.Equal(67_108_864, new TapeLimits().InputBytes); // Generic contract must not change.
    }

    [Fact]
    public void ActualDefaultWholeC60ExportReimportsWithoutCroppingSourceDuration()
    {
        var codec = new WavTapeCodec();
        var sideDuration = new TapeTime(1800, 1);
        // Original extent deliberately short: the output is still a complete physical side.
        var side = new TapeSideImage(sideDuration, new(1, 1), [new(1, [new(false, sideDuration)])], [], [], [],
            new(TapeFormat.Blank, null, true, [], null));
        var written = codec.Write(side, new(TapeFormat.Wav, TapeSide.A));
        Assert.True(written.Success, written.Failure?.Detail);
        var bytes = written.Value.Bytes.AsSpan();
        Assert.Equal(158_760_044, bytes.Length);
        Assert.Equal(44100U, BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes[22..]));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(bytes[34..]));
        Assert.Equal(158_760_000U, BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]));
        var options = new TapeImportOptions(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60)
            { Limits = new() { InputBytes = 268_435_456 } };
        var refused = codec.Read(bytes, options with { Limits = options.Limits with { InputBytes = 67_108_864 } });
        Assert.False(refused.Success); Assert.Equal(TapeError.BoundsExceeded, refused.Failure!.Code);
        var read = codec.Read(bytes, options);
        Assert.True(read.Success, read.Failure?.Detail);
        Assert.Equal(sideDuration, read.Value.Image.Duration);
        Assert.Equal(sideDuration, read.Value.Image.SourceDuration);
        Assert.Equal(sideDuration, Assert.Single(read.Value.Image.Annotations.Where(a => a.Kind == TapeAnnotationKind.SourceEof)).Position);
        Assert.Equal(158_760_044, Assert.Single(read.Value.SourceAssets).Bytes.Length);
        Assert.False(Assert.Single(Assert.Single(read.Value.Image.Chunks).Runs).Level);
        Assert.Equal(sideDuration, read.Value.Image.Chunks[0].Runs[0].Duration);

        var machine = TapeHcs2Tests.Compose();
        var blank = new TapeSideImage(sideDuration, default, [new(1, [new(false, sideDuration)])], [], [], [],
            new(TapeFormat.Blank, null, true, [], null));
        var inserted = machine.Tape!.Deck.Insert(new(TapeCapacity.C60, read.Value.Image, blank, read.Value.SourceAssets), false);
        Assert.True(inserted.Success, inserted.Failure?.Detail);
        var expectedSourceHash = SHA256.HashData(bytes);
        var file = machine.Save();
        Assert.True(file.Length < 268_435_456);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() =>
            HcsCodec.Read(new MemoryStream(file), new MachineSchema(MachineProfile.Default))).Error);
        var restored = TapeHcs2Tests.Compose(); restored.Load(file);
        Assert.Equal(machine.Tape.Deck.Observe(), restored.Tape!.Deck.Observe());
        var restoredCassette = restored.Tape.Capture().Value.Cassette!;
        Assert.Equal(sideDuration, restoredCassette.A.SourceDuration);
        Assert.Equal(expectedSourceHash, SHA256.HashData(Assert.Single(restoredCassette.SourceAssets).Bytes.AsSpan()));
        // Two such originals alone exceed the entire envelope, before any clone/output.
        using var refusedOutput = new MemoryStream();
        using var manifest = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => HcsCodec.Write(refusedOutput,
            manifest.RootElement, [file, file], new MachineSchema(MachineProfile.Default, HcsWavPolicy.Default))).Error);
        Assert.Equal(0, refusedOutput.Length);
    }
}
