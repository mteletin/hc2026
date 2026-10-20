using Hc2000.Machine.Video;

namespace Hc2000.App.Audio;

/// <summary>Single-emulation-worker audio owner. True from ProcessFrame selects audio
/// backpressure exclusively; false requires the runtime's monotonic fallback.</summary>
public sealed class BeeperAudioOutput : IDisposable
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly IAudioWait wait;
    private readonly Action<string>? diagnostic;
    private readonly BeeperResampler resampler;
    private readonly BeeperResampler? earResampler;
    public bool TapeMonitoringEnabled { get; set; } = true;
    private IQueuedAudioSink? sink;
    private bool playing, disposed, reported;
    private long cursor;
    private BeeperEdge pins;
    public int QueueHighWaterSamples { get; private set; }
    public int SoftwareStarvations { get; private set; }
    public bool Available => sink is not null && !disposed;
    public int SampleRate => resampler.SampleRate;

    public BeeperAudioOutput(long masterHz, IQueuedAudioSink? sink,
        IAudioWait? wait = null, Action<string>? diagnostic = null, bool physicalChannels = false)
    {
        if (sink is not null && sink.SampleRate is not (44100 or 48000))
            throw new ArgumentException("Unsupported PCM rate.", nameof(sink));
        this.sink = sink;
        this.wait = wait ?? new AudioWait();
        this.diagnostic = diagnostic;
        resampler = new(masterHz, sink?.SampleRate ?? 48000, channel: physicalChannels ? BeeperChannel.Bel : BeeperChannel.LegacyMix);
        if (physicalChannels) earResampler = new(masterHz, resampler.SampleRate, channel: BeeperChannel.EarMonitor);
        if (sink is not null) TrySink(() => sink.Pause(true));
    }

    /// <summary>Construct on the worker. Disabled mode does not load any native library. When
    /// <paramref name="capturePath"/> names the sound-dump route (PO ruling board 634859), the live
    /// sink is wrapped so the machine's audio stream is written to the WAV while it runs; with no
    /// live sink the file is still written - silent, and saying why it is. A capture failure keeps
    /// the machine's sound; only the dump is reported.</summary>
    public static BeeperAudioOutput Open(long masterHz, bool enabled, Action<string>? diagnostic = null,
        string? capturePath = null)
    {
        IQueuedAudioSink? sink = null;
        string? unavailable = null;
        if (enabled)
        {
            try { sink = SdlQueuedAudioSink.Open(); }
            catch (Exception ex) when (IsSinkError(ex))
            {
                unavailable = ex.Message;
                diagnostic?.Invoke($"Audio unavailable: {unavailable}");
            }
        }
        if (capturePath is not null)
        {
            try
            {
                if (sink is not null)
                    sink = new CapturingAudioSink(sink, capturePath);
                else
                    SoundDumpSilentFile.Write(capturePath,
                        enabled ? "Audio unavailable: " + unavailable : "Audio is disabled.");
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke($"Sound dump unavailable: {ex.Message}");
            }
        }
        return new(masterHz, sink, diagnostic: diagnostic, physicalChannels: true);
    }

    public bool ProcessFrame(long startTick, long endTick, ReadOnlySpan<BeeperEdge> edges,
        int speedPercent, CancellationToken cancellationToken, ReadOnlySpan<BeeperEdge> earEdges = default)
    {
        RequireOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (startTick != cursor || endTick < startTick) throw new ArgumentException("Audio intervals must be contiguous; reset on epoch changes.");
        var last = startTick;
        foreach (var edge in edges)
        {
            if (edge.MasterTick < last || edge.MasterTick > endTick) throw new ArgumentException("Unordered/out-of-interval edge.", nameof(edges));
            last = edge.MasterTick;
        }
        if (earResampler is not null)
        {
            last = startTick;
            foreach (var edge in earEdges)
            {
                if (edge.MasterTick < last || edge.MasterTick > endTick) throw new ArgumentException("Unordered/out-of-interval EAR edge.", nameof(earEdges));
                last = edge.MasterTick;
            }
        }
        var nextPins = edges.Length > 0 ? edges[^1] : pins;
        if (speedPercent != 100 || sink is null)
        {
            Flush();
            resampler.Reset(endTick, nextPins.Speaker, nextPins.Microphone);
            var earPins = earEdges.Length > 0 ? earEdges[^1] : earResampler?.Pins ?? default;
            earResampler?.Reset(endTick, earPins.Speaker, earPins.Microphone);
            pins = nextPins;
            cursor = endTick;
            return false;
        }
        // Render validation must succeed before committing the frame cursor/pins.
        var pcm = resampler.Render(endTick, edges);
        if (earResampler is not null)
        {
            var ear = earResampler.Render(endTick, earEdges, TapeMonitoringEnabled ? 1 : 0);
            for (var i = 0; i < pcm.Length; i++) pcm[i] = Math.Clamp(pcm[i] + ear[i], -1, 1);
        }
        pins = nextPins;
        cursor = endTick;
        try
        {
            var rate = sink.SampleRate;
            var target = rate * 40 / 1000;
            var high = rate * 60 / 1000;
            var cap = rate * 100 / 1000;
            if (playing && sink.QueuedSamples == 0) SoftwareStarvations++;
            var offset = 0;
            while (offset < pcm.Length)
            {
                WaitBelow(cap - 1, cancellationToken);
                var queued = CheckedDepth();
                var length = Math.Min(pcm.Length - offset, cap - queued);
                sink.Queue(pcm.AsSpan(offset, length));
                QueueHighWaterSamples = Math.Max(QueueHighWaterSamples, checked(queued + length));
                offset += length;
                if (!playing && CheckedDepth() >= target)
                {
                    sink.Pause(false);
                    playing = true;
                }
                if (playing && CheckedDepth() > high) WaitBelow(target, cancellationToken);
            }
            return true;
        }
        catch (Exception ex) when (IsSinkError(ex))
        {
            Disable(ex);
            return false;
        }
    }

    public void Pause()
    {
        RequireOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        Flush();
        resampler.Reset(cursor, pins.Speaker, pins.Microphone);
        if (earResampler is not null) earResampler.Reset(cursor, earResampler.Pins.Speaker, earResampler.Pins.Microphone);
    }

    public void Reset(long epochTick = 0, bool speaker = false, bool microphone = false)
    {
        RequireOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (epochTick < 0) throw new ArgumentOutOfRangeException(nameof(epochTick));
        Flush();
        cursor = epochTick;
        pins = new(epochTick, speaker, microphone);
        resampler.Reset(epochTick, speaker, microphone);
        earResampler?.Reset(epochTick);
    }

    private int CheckedDepth()
    {
        var depth = sink!.QueuedSamples;
        if (depth < 0 || depth > SampleRate / 10) throw new IOException("Invalid/excessive audio queue depth.");
        return depth;
    }

    private void WaitBelow(int threshold, CancellationToken cancellationToken)
    {
        var previous = CheckedDepth();
        var progress = wait.Elapsed;
        while (previous > threshold)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (wait.Elapsed - progress >= TimeSpan.FromMilliseconds(500)) throw new IOException("Audio queue did not drain for 500ms.");
            wait.Wait(TimeSpan.FromMilliseconds(2), cancellationToken);
            var current = CheckedDepth();
            if (current < previous) progress = wait.Elapsed;
            previous = current;
        }
    }

    private void Flush()
    {
        playing = false;
        if (sink is not null) TrySink(() => { sink.Pause(true); sink.Clear(); });
    }
    private void TrySink(Action operation)
    {
        try { operation(); }
        catch (Exception ex) when (IsSinkError(ex)) { Disable(ex); }
    }
    private void Disable(Exception error)
    {
        var old = sink;
        sink = null;
        playing = false;
        try { old?.Dispose(); } catch (Exception ex) when (IsSinkError(ex)) { }
        if (!reported) { reported = true; diagnostic?.Invoke($"Audio disabled; monotonic pacing required: {error.Message}"); }
    }
    private static bool IsSinkError(Exception ex) => ex is IOException or InvalidOperationException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or UnauthorizedAccessException;
    private void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Audio belongs to the emulation worker.");
    }
    public void Dispose()
    {
        RequireOwner();
        if (disposed) return;
        Flush();
        TrySink(() => sink?.Dispose());
        sink = null;
        disposed = true;
    }
}
