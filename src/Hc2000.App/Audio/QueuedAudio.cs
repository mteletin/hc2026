using System.Diagnostics;

namespace Hc2000.App.Audio;

/// <summary>Worker-owned copying PCM sink; software queue size is not speaker position.</summary>
public interface IQueuedAudioSink : IDisposable
{
    int SampleRate { get; }
    int QueuedSamples { get; }
    void Queue(ReadOnlySpan<float> samples);
    void Pause(bool paused);
    void Clear();
}

/// <summary>Only host waiting, never machine time. Injectable for deterministic watchdog tests.</summary>
public interface IAudioWait
{
    TimeSpan Elapsed { get; }
    void Wait(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed class AudioWait : IAudioWait
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public TimeSpan Elapsed => clock.Elapsed;
    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationToken.WaitHandle.WaitOne(duration)) cancellationToken.ThrowIfCancellationRequested();
    }
}
