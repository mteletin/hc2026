namespace Hc2000.Machine.Timing;

/// <summary>Selected M1 phases, not claims of measured hardware precision (INT/pixel ±4 nominal T).</summary>
public sealed record FrameTiming(long IntStartTick = 180, long IntWidthTicks = 200,
    long FirstPixelAfterIntTicks = 64_160)
{
    public const long MasterHz = 17_734_475;
    public const long LineTicks = 1_140;
    public const long FrameTicks = 355_680;
    public const long CharacterTicks = 20;
    public long FirstPixelTick => checked(IntStartTick + FirstPixelAfterIntTicks);
}

public readonly record struct VideoPosition(int Line, int Character, bool Blanked, bool Display, bool Border);

/// <summary>Independent immutable video period; subscribers supply rendering, not CPU pacing.</summary>
public sealed class FrameClock
{
    private const int Sample = 0, IntOn = 1, IntOff = 2, Publish = 3;
    private readonly DeviceScheduler scheduler;
    private readonly int deviceId;
    public FrameTiming Timing { get; }
    internal int DeviceId => deviceId;
    public bool IntAsserted { get; private set; }
    public long CompletedFrames { get; private set; }
    public event Action<long, bool>? InterruptChanged;
    public event Action<long, VideoPosition>? VideoSample;
    public event Action<long>? FrameCompleted;

    public FrameClock(DeviceScheduler scheduler, int stableDeviceId, FrameTiming? timing = null)
    {
        if (scheduler.Now != 0) throw new ArgumentException("Construct frame clock at the machine epoch.", nameof(scheduler));
        this.scheduler = scheduler;
        deviceId = stableDeviceId;
        Timing = timing ?? new();
        // Pixel phase is deliberately pinned to the documented first display line.
        if (Timing.IntStartTick <= 0 || Timing.IntWidthTicks <= 0 ||
            checked(Timing.IntStartTick + Timing.IntWidthTicks) >= FrameTiming.LineTicks ||
            Timing.FirstPixelTick / FrameTiming.LineTicks != 56 ||
            Timing.FirstPixelTick % FrameTiming.CharacterTicks != 0 ||
            Timing.FirstPixelTick % FrameTiming.LineTicks + 640 > FrameTiming.LineTicks)
            throw new ArgumentOutOfRangeException(nameof(timing));
        scheduler.RegisterDevice(deviceId, Dispatch);
        liveGeneration = scheduler.Generation(deviceId);
        // Epoch-zero outputs are synchronous (INT false, blanked first line); no zero-time enqueue. The per-character video
        // samples are not events any more: RenderTo takes each one, in its place in the machine's order, just before the
        // first event that comes after it (hc2000-fast-emulation; 17,784 events a frame saved).
        scheduler.CatchUp = RenderTo;
        Enqueue(Timing.IntStartTick, EventPhase.SignalEdge, IntOn);
        Enqueue(checked(Timing.IntStartTick + Timing.IntWidthTicks), EventPhase.SignalEdge, IntOff);
        Enqueue(FrameTiming.FrameTicks, EventPhase.Frame, Publish);
    }

    /// <summary>Restores the two video-counter fields of a captured snapshot (snapshot restore).
    /// The frame's own pending events come from the scheduler restore, not here.</summary>
    internal void Restore(long completedFrames, bool intAsserted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedFrames);
        CompletedFrames = completedFrames;
        IntAsserted = intAsserted;
        // Every sample at or before the restored time was taken when the snapshot was captured.
        nextSample = checked((scheduler.Now / FrameTiming.CharacterTicks + 1) * FrameTiming.CharacterTicks);
    }

    // The tick of the next video sample not yet taken (samples fall every CharacterTicks from CharacterTicks on).
    private long nextSample = FrameTiming.CharacterTicks;

    // The device generation the clock's events carry. Invalidating the device silences the clock: its events lapse, and
    // so do its samples, as they did when each sample was an event (a test that stops the video and then advances the
    // scheduler to a far time would otherwise render every sample on the way).
    private readonly long liveGeneration;

    /// <summary>Takes every video sample that comes before an event at <paramref name="time"/> in phase
    /// <paramref name="phase"/>: a sample shares its tick with events of later phases (Frame, Cpu), which it precedes, and
    /// follows the earlier ones (input, signal edges, controller). So the raster sees each change exactly when the
    /// per-character event did. Also called before a state change made outside an event (a ULA port write).</summary>
    public void RenderTo(LogicalTime time, EventPhase phase)
    {
        var limit = time.WholeTicks;
        if (time.IsInteger && phase <= EventPhase.Video) limit--;
        if (limit < nextSample || scheduler.Generation(deviceId) != liveGeneration) return;
        if (VideoSample is { } sample)
            for (var tick = nextSample; tick <= limit; tick += FrameTiming.CharacterTicks) sample(tick, PositionAt(tick));
        nextSample = checked(nextSample + ((limit - nextSample) / FrameTiming.CharacterTicks + 1) * FrameTiming.CharacterTicks);
    }

    public VideoPosition PositionAt(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        var phase = tick % FrameTiming.FrameTicks;
        var line = (int)(phase / FrameTiming.LineTicks);
        var horizontal = phase % FrameTiming.LineTicks;
        var start = Timing.FirstPixelTick % FrameTiming.LineTicks;
        var blanked = line < 24;
        var display = line is >= 56 and <= 247 && horizontal >= start && horizontal < start + 640;
        return new(line, (int)(horizontal / FrameTiming.CharacterTicks), blanked, display, !blanked && !display);
    }

    private void Enqueue(long due, EventPhase phase, int kind) => scheduler.Schedule(new(due, phase, deviceId, kind));

    private void Dispatch(DeviceEvent item)
    {
        switch (item.Kind)
        {
            case Sample:
                // An older snapshot's queued sample: RenderTo takes the samples now, so the event only lapses.
                break;
            case IntOn:
            case IntOff:
                Enqueue(checked(item.DueTick + FrameTiming.FrameTicks), EventPhase.SignalEdge, item.Kind);
                IntAsserted = item.Kind == IntOn;
                InterruptChanged?.Invoke(item.DueTick, IntAsserted);
                break;
            case Publish:
                var frames = checked(CompletedFrames + 1);
                Enqueue(checked(item.DueTick + FrameTiming.FrameTicks), EventPhase.Frame, Publish);
                CompletedFrames = frames;
                FrameCompleted?.Invoke(item.DueTick);
                break;
        }
    }
}
