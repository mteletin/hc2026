using Hc2000.App.Audio;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Capture decorator backing the virtual-sink named output check (PO ruling, wi-116b).
/// A small explicit capacity is always passed: the default allocates a one-hour file.</summary>
public sealed class CapturingAudioSinkTests : IDisposable
{
    private readonly string directory;
    private readonly string path;

    public CapturingAudioSinkTests()
    {
        directory = Path.Combine(Path.GetTempPath(), $"hc2000-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "capture.wav");
    }

    public void Dispose() => Directory.Delete(directory, true);

    private sealed class InnerSink(int rate, bool fail = false) : IQueuedAudioSink
    {
        public int SampleRate => rate;
        public int QueuedSamples { get; private set; }
        public List<float> Received { get; } = [];
        public bool Paused { get; private set; }
        public bool Disposed { get; private set; }
        public void Queue(ReadOnlySpan<float> samples)
        {
            if (fail) throw new IOException("Injected queue error.");
            Received.AddRange(samples);
            QueuedSamples += samples.Length;
        }
        public void Pause(bool paused) => Paused = paused;
        public void Clear() => QueuedSamples = 0;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void PersistsExactPcm8WaveAtSinkRate()
    {
        var inner = new InnerSink(44100);
        using var capture = new CapturingAudioSink(inner, path, 10000);
        capture.Queue([0f, 1f, -1f, .5f]);
        capture.Dispose();
        var data = File.ReadAllBytes(path);
        Assert.Equal(44 + 10000, data.Length); // pre-allocated capacity; the data field carries the true length
        Assert.Equal(new byte[] { 0x52, 0x49, 0x46, 0x46 }, data.AsSpan(0, 4).ToArray());            // RIFF
        Assert.Equal(new byte[] { 0x57, 0x41, 0x56, 0x45 }, data.AsSpan(8, 4).ToArray());            // WAVE
        Assert.Equal(40, BitConverter.ToInt32(data.AsSpan(4, 4)));       // riff size = 36 + 4
        Assert.Equal(16, BitConverter.ToInt32(data.AsSpan(16, 4)));      // fmt chunk size
        Assert.Equal(1, BitConverter.ToInt16(data.AsSpan(20, 2)));       // PCM
        Assert.Equal(1, BitConverter.ToInt16(data.AsSpan(22, 2)));       // mono
        Assert.Equal(44100, BitConverter.ToInt32(data.AsSpan(24, 4)));   // sink rate
        Assert.Equal(44100, BitConverter.ToInt32(data.AsSpan(28, 4)));   // byte rate == rate (8 bit)
        Assert.Equal(1, BitConverter.ToInt16(data.AsSpan(32, 2)));       // block align
        Assert.Equal(8, BitConverter.ToInt16(data.AsSpan(34, 2)));       // 8 bit
        Assert.Equal(4, BitConverter.ToInt32(data.AsSpan(40, 4)));       // data length
        Assert.Equal(128, data[44]);  // 0.0 -> midpoint
        Assert.Equal(255, data[45]);  // +1.0
        Assert.Equal(1, data[46]);    // -1.0
        Assert.Equal(191, data[47]);  // 0.5 -> 128 + (int)(0.5f*127f) = 128 + 63
        Assert.Equal(4, inner.Received.Count); // forwarded to the virtual sink
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void RollingBufferIsBoundedAndKeepsNewest()
    {
        var inner = new InnerSink(44100);
        using var capture = new CapturingAudioSink(inner, path, 8);
        for (var i = 0; i < 20; i++) capture.Queue([i / 20f]);
        capture.Dispose();
        var data = File.ReadAllBytes(path);
        Assert.Equal(44 + 8, data.Length); // bounded at capacity, not at everything captured
        Assert.Equal(20, capture.CapturedSamples);
        for (var i = 0; i < 8; i++)
            Assert.Equal(CapturingAudioSink.Pcm8((i + 12) / 20f), data[44 + i]); // the last 8 samples, oldest -> newest
    }

    [Fact]
    public void FullLapBatchThenPartialKeepsTheFinalWindow()
    {
        var inner = new InnerSink(44100);
        using var capture = new CapturingAudioSink(inner, path, 8);
        for (var i = 0; i < 8; i++) capture.Queue([i / 12f]);    // exactly one lap
        for (var i = 8; i < 12; i++) capture.Queue([i / 12f]);  // partial second lap
        capture.Dispose();
        var data = File.ReadAllBytes(path);
        for (var i = 0; i < 8; i++)
            Assert.Equal(CapturingAudioSink.Pcm8((i + 4) / 12f), data[44 + i]);
    }

    [Fact]
    public void BatchExceedingCapacityIsRejected()
    {
        var inner = new InnerSink(44100);
        using var capture = new CapturingAudioSink(inner, path, 8);
        Assert.Throws<ArgumentException>(() => capture.Queue(Enumerable.Repeat(0f, 9).ToArray()));
        Assert.Equal(0, capture.CapturedSamples);
        Assert.Empty(inner.Received);
    }

    [Fact]
    public void InvalidCapacityIsRejected()
    {
        var inner = new InnerSink(44100);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturingAudioSink(inner, path, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturingAudioSink(inner, path, -1));
        Assert.False(File.Exists(path)); // the capacity check runs before the file is created
    }

    [Fact]
    public void InnerQueueErrorPropagatesAndIsNotCounted()
    {
        var inner = new InnerSink(44100, fail: true);
        using var capture = new CapturingAudioSink(inner, path, 8);
        Assert.Throws<IOException>(() => capture.Queue([.25f, -.25f]));
        Assert.Equal(0, capture.CapturedSamples);
    }

    [Fact]
    public void PauseAndClearForwardToTheVirtualSink()
    {
        var inner = new InnerSink(44100);
        using var capture = new CapturingAudioSink(inner, path, 8);
        capture.Pause(true);
        capture.Clear();
        Assert.True(inner.Paused);
        Assert.Equal(0, inner.QueuedSamples);
    }
}
