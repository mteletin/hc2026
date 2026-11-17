using System.Diagnostics;

namespace Hc2000.App;

// A desktop under software rendering (llvmpipe) froze for 40+ minutes when the app window was
// resized under load. The app's own
// resize path is lightweight; the continuous GL work is the 50Hz video present on top of it.
// This policy keeps that present from running while a resize is in flight and caps the frame
// rate while the UI thread is saturated, so the resize settles instead of the render queue
// growing without bound. The machine is unaffected (the present mailbox is single-slot - a
// skipped frame simply stays queued for the next tick).
internal static class VideoPresentPolicy
{
    // Pause the video present for this long after the last SizeChanged (an active drag keeps
    // firing SizeChanged, so the pause follows the drag).
    public const long ResizePauseMs = 750;

    // Tick gap above which the dispatcher is considered saturated (nominal is the 20 ms timer).
    public const double SaturationGapMs = 100;

    // Consecutive back-pressure skips before one present is forced through, so the display is
    // never starved for good: under sustained saturation the frame rate settles at ~1/6 of the
    // nominal (~8 Hz instead of 50 Hz).
    public const int MaxBacklogSkips = 5;

    /// <summary>
    /// Decide, from pure clock inputs, whether this presentation tick presents a frame.
    /// <paramref name="nowUtcTicks"/> / <paramref name="lastResizeUtcTicks"/> are
    /// DateTime.UtcNow.Ticks; <paramref name="nowStamp"/> / <paramref name="lastPresentStamp"/>
    /// are Stopwatch.GetTimestamp() of this and the previous tick;
    /// <paramref name="backlogSkips"/> is the running count of consecutive back-pressure skips.
    /// </summary>
    public static bool ShouldPresent(
        long nowUtcTicks,
        long lastResizeUtcTicks,
        long nowStamp,
        long lastPresentStamp,
        ref int backlogSkips)
    {
        if (nowUtcTicks - lastResizeUtcTicks < ResizePauseMs * TimeSpan.TicksPerMillisecond)
            return false;
        var gapMs = (nowStamp - lastPresentStamp) * 1000.0 / Stopwatch.Frequency;
        if (gapMs <= SaturationGapMs)
        {
            backlogSkips = 0;
            return true;
        }
        if (backlogSkips < MaxBacklogSkips)
        {
            backlogSkips++;
            return false;
        }
        backlogSkips = 0;
        return true;
    }
}
