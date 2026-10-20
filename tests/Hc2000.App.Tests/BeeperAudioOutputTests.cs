using Hc2000.App.Audio;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class BeeperAudioOutputTests
{
    private const long Hz = FrameTiming.MasterHz;

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void PrefillWatermarksAndHardCapIncludingNextBlock(int rate)
    {
        var sink = new FakeSink(rate);
        var wait = new FakeWait(sink);
        using var output = new BeeperAudioOutput(Hz, sink, wait);
        Assert.True(sink.Paused);
        Assert.True(output.ProcessFrame(0, Hz / 50, [], 100, default));
        Assert.True(sink.Paused); // first ~20ms insufficient for 40ms prefill
        Assert.True(output.ProcessFrame(Hz / 50, Hz / 10, [], 100, default));
        Assert.False(sink.Paused);
        Assert.True(wait.Waits > 0);
        Assert.InRange(output.QueueHighWaterSamples, rate * 40 / 1000, rate / 10);
        Assert.All(sink.Depths, depth => Assert.InRange(depth, 0, rate / 10));
        Assert.Equal((int)((Int128)(Hz / 10) * rate / Hz), sink.TotalQueued);
    }

    [Fact]
    public void NonNormalSpeedPauseAndResetFlushAndSelectFallback()
    {
        var sink = new FakeSink(48000);
        using var output = new BeeperAudioOutput(Hz, sink, new FakeWait(sink));
        Assert.True(output.ProcessFrame(0, Hz / 20, [], 100, default));
        Assert.False(output.ProcessFrame(Hz / 20, Hz / 10, [new(Hz / 10, true, true)], 200, default));
        Assert.True(sink.Paused);
        Assert.Equal(0, sink.Depth);
        Assert.False(output.ProcessFrame(Hz / 10, Hz / 5, [], 400, default));
        Assert.False(output.ProcessFrame(Hz / 5, Hz / 4, [], 0, default));
        output.Reset(42, true, false);
        Assert.True(output.ProcessFrame(42, 42 + Hz / 20, [], 100, default));
        output.Pause();
        Assert.Equal(0, sink.Depth);
        Assert.True(sink.Paused);
        Assert.True(output.ProcessFrame(42 + Hz / 20, 42 + Hz / 10, [], 100, default));
    }

    [Fact]
    public void StalledQueueHas500msWatchdogThenOneDiagnosticAndFallback()
    {
        var sink = new FakeSink(48000);
        var wait = new FakeWait(sink) { Drain = false };
        var errors = new List<string>();
        using var output = new BeeperAudioOutput(Hz, sink, wait, errors.Add);
        Assert.False(output.ProcessFrame(0, Hz / 10, [], 100, default));
        Assert.Equal(TimeSpan.FromMilliseconds(500), wait.Elapsed);
        Assert.True(sink.Disposed);
        Assert.Single(errors);
        Assert.False(output.Available);
        Assert.False(output.ProcessFrame(Hz / 10, Hz / 5, [], 100, default));
        Assert.Single(errors);
    }

    [Fact]
    public void CancellationInterruptsWaitWithoutDisablingSink()
    {
        var sink = new FakeSink(48000);
        using var cts = new CancellationTokenSource();
        var wait = new FakeWait(sink) { OnWait = cts.Cancel };
        using var output = new BeeperAudioOutput(Hz, sink, wait);
        Assert.Throws<OperationCanceledException>(() => output.ProcessFrame(0, Hz / 10, [], 100, cts.Token));
        Assert.True(output.Available);
        Assert.True(wait.Elapsed < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void QueueFailureUnavailableAndSoftwareStarvationRemainHostOnly()
    {
        using var silent = BeeperAudioOutput.Open(Hz, false);
        Assert.False(silent.ProcessFrame(0, Hz / 10, [], 100, default));
        var sink = new FakeSink(48000);
        var errors = new List<string>();
        using var output = new BeeperAudioOutput(Hz, sink, new FakeWait(sink), errors.Add);
        Assert.True(output.ProcessFrame(0, Hz / 20, [], 100, default));
        sink.Depth = 0; // synthetic software starvation, not an actual speaker underrun
        Assert.True(output.ProcessFrame(Hz / 20, Hz / 10, [], 100, default));
        Assert.Equal(1, output.SoftwareStarvations);
        sink.Fail = true;
        Assert.False(output.ProcessFrame(Hz / 10, Hz / 5, [], 100, default));
        Assert.Single(errors);
        Assert.Equal(1, sink.Disposals);
    }

    [Fact]
    public void InvalidIntervalDoesNotMoveCursorAndUnsupportedSinkIsRejected()
    {
        var sink = new FakeSink(48000);
        using var output = new BeeperAudioOutput(Hz, sink, new FakeWait(sink));
        Assert.Throws<ArgumentException>(() => output.ProcessFrame(1, 2, [], 100, default));
        Assert.Throws<ArgumentException>(() => output.ProcessFrame(0, Hz / 20, [new(-1, true, false)], 100, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => output.ProcessFrame(0, Hz * 11, [], 100, default));
        Assert.True(output.ProcessFrame(0, Hz / 20, [], 100, default));
        Assert.Throws<ArgumentException>(() => new BeeperAudioOutput(Hz, new FakeSink(22050)));
    }

    [Fact]
    public void OwnerThreadIsEnforcedAndRepeatedDisposeIsSafe()
    {
        var sink = new FakeSink(48000);
        var output = new BeeperAudioOutput(Hz, sink);
        Exception? failure = null;
        var thread = new Thread(() => { try { output.Pause(); } catch (Exception ex) { failure = ex; } });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
        output.Dispose(); output.Dispose();
        Assert.Equal(1, sink.Disposals);
        Assert.Throws<ObjectDisposedException>(() => output.Pause());
    }

    [Theory]
    [InlineData(44100, 1, 512)]
    [InlineData(48000, 1, 1024)]
    public void ObtainedSupportedFormatAndNativeStructLayout(int rate, byte channels, ushort block)
    {
        SdlQueuedAudioSink.ValidateObtained(rate, SdlQueuedAudioSink.FloatFormat, channels, block);
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, SdlQueuedAudioSink.AudioSpecSize);
        Assert.Equal(16, SdlQueuedAudioSink.CallbackOffset);
    }

    [Theory]
    [InlineData(22050, 0x8120, 1, 512)]
    [InlineData(48000, 0x8010, 1, 512)]
    [InlineData(48000, 0x8120, 2, 512)]
    [InlineData(48000, 0x8120, 1, 0)]
    public void UnsupportedObtainedFormatIsRejected(int rate, ushort format, byte channels, ushort block)
        => Assert.Throws<IOException>(() => SdlQueuedAudioSink.ValidateObtained(rate, format, channels, block));

    private sealed class FakeWait(FakeSink sink) : IAudioWait
    {
        public TimeSpan Elapsed { get; private set; }
        public bool Drain { get; init; } = true;
        public Action? OnWait { get; init; }
        public int Waits { get; private set; }
        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            Waits++;
            Elapsed += duration;
            OnWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Drain && !sink.Paused) sink.Depth = Math.Max(0, sink.Depth - (int)(duration.TotalSeconds * sink.SampleRate));
        }
    }
    private sealed class FakeSink(int rate) : IQueuedAudioSink
    {
        public int SampleRate => rate;
        public int Depth { get; set; }
        public int QueuedSamples => Depth;
        public int TotalQueued { get; private set; }
        public bool Paused { get; private set; }
        public bool Disposed { get; private set; }
        public bool Fail { get; set; }
        public int Disposals { get; private set; }
        public List<int> Depths { get; } = [];
        public void Queue(ReadOnlySpan<float> samples)
        {
            if (Fail) throw new IOException("Injected queue error");
            var owned = samples.ToArray(); // sink contract: consume/copy before returning
            Depth += owned.Length;
            TotalQueued += owned.Length;
            Depths.Add(Depth);
        }
        public void Pause(bool paused) => Paused = paused;
        public void Clear() => Depth = 0;
        public void Dispose() { Disposed = true; Disposals++; }
    }
}
