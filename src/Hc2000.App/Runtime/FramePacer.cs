using System.Diagnostics;
using Hc2000.Machine.Timing;

namespace Hc2000.App.Runtime;

public enum SpeedMode { Normal = 100, Double = 200, Quadruple = 400, Turbo = 0 }

public interface IHostClock
{
    long Frequency { get; }
    long Now { get; }
    void WaitUntil(long deadline, CancellationToken cancellation);
}

public sealed class MonotonicHostClock : IHostClock
{
    public long Frequency => Stopwatch.Frequency;
    public long Now => Stopwatch.GetTimestamp();
    public void WaitUntil(long deadline, CancellationToken cancellation)
    {
        while (Now < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            var ms = Math.Max(1, (int)Math.Min(50, (deadline - Now) * 1000 / Frequency));
            if (cancellation.WaitHandle.WaitOne(ms)) cancellation.ThrowIfCancellationRequested();
        }
    }
}

/// <summary>Wall deadlines only. Neither host clock nor audio depth enters machine time.</summary>
public sealed class FramePacer(IHostClock clock)
{
    private long deadline = clock.Now;
    private long remainder;
    private SpeedMode? lastMode;
    public void Rebase() { deadline = clock.Now; remainder = 0; lastMode = null; }
    public void Pace(SpeedMode mode, bool audioPaced, CancellationToken cancellation)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == SpeedMode.Turbo || (mode == SpeedMode.Normal && audioPaced))
        {
            Rebase(); return;
        }
        if (lastMode != mode) { deadline = clock.Now; remainder = 0; lastMode = mode; }
        long denominator = checked(FrameTiming.MasterHz * (int)mode);
        var numerator = (Int128)FrameTiming.FrameTicks * clock.Frequency * 100 + remainder;
        long period = (long)(numerator / denominator);
        remainder = (long)(numerator % denominator);
        deadline = checked(deadline + period);
        // Avoid an unbounded catch-up burst after host suspension or expensive loading.
        if (clock.Now - deadline > period) { deadline = clock.Now; remainder = 0; }
        clock.WaitUntil(deadline, cancellation);
    }
}
