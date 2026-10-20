using System.Buffers.Binary;
using Hc2000.App.Media;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class TapeWavBudgetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-app-wav-" + Guid.NewGuid().ToString("N"));
    public TapeWavBudgetTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string SparseWave(uint frames, uint rate = 44100)
    {
        var path = Path.Combine(directory, "original.wav");
        var header = new byte[44];
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 36 + frames * 2);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), rate); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), frames * 2);
        using var stream = File.Create(path); stream.Write(header); stream.SetLength(44L + frames * 2L);
        return path;
    }

    [Fact]
    public async Task FullC60UsesCallerWavDefaultAndRetainsFullDuration()
    {
        var image = await TapeFiles.ReadAsync(SparseWave(79_380_000), TapeCodecRegistry.Installed(), new(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60));
        Assert.Equal(new TapeTime(1800, 1), image.A.Duration); Assert.Equal(image.A.Duration, image.A.SourceDuration);
        Assert.Equal(158_760_044, Assert.Single(image.SourceAssets).Bytes.Length);
    }

    [Fact]
    public async Task Explicit64MiBRefusalNeverAdoptsOrClearsDirtyState()
    {
        var fixture = new RuntimeTests.Fixture(); var tape = fixture.Machine.EnableTape(3);
        Assert.True(tape.Deck.Blank(TapeCapacity.C60, false).Success);
        var saved = tape.Capture().Value;
        Assert.True(tape.Restore(saved with { Component = saved.Component with { Dirty = [true, false], EditRevision = ["1", "0"] } }).Success);
        var before = tape.Deck.Observe();
        await Assert.ThrowsAsync<InvalidDataException>(() => TapeFiles.ReadAsync(SparseWave(79_380_000), TapeCodecRegistry.Installed(),
            new(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60) { Limits = new() }));
        Assert.Equal(before, tape.Deck.Observe()); Assert.True(tape.Deck.Observe().A.Dirty);
    }

    [Fact]
    public async Task FormatAndCapacityAndTransientCapsRefuseBeforePayloadRead()
    {
        var path = SparseWave(79_380_001);
        await Assert.ThrowsAsync<InvalidDataException>(() => TapeFiles.ReadAsync(path, TapeCodecRegistry.Installed(), new(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60)));
        path = SparseWave(1, 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => TapeFiles.ReadAsync(path, TapeCodecRegistry.Installed(), new(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60)));
        path = SparseWave(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => TapeFiles.ReadAsync(path, TapeCodecRegistry.Installed(),
            new(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60), transientBytes: 1));
    }
}
