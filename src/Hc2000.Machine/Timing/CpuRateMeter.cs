using System;
using System.Collections.Generic;

namespace Hc2000.Machine.Timing;

/// <summary>
/// An immutable achieved-rate sample published for the UI (hc2000-accelerated-clock-domains,
/// honest capacity: report achieved throughput, never promise a rate). <see cref="AchievedTStatesPerSecond"/>
/// and <see cref="FrameHz"/> are meaningful only while <see cref="WarmingUp"/> is false; while
/// warming up they are 0. <see cref="Overloaded"/> is true only once the window is full and the
/// achieved rate is at least <see cref="CpuRateMeter.OverloadTolerance"/> below the requested rate
/// (the host cannot keep up). Guest work is never skipped to improve these numbers: the meter is
/// observation-only.
/// </summary>
public sealed record CpuRateSample(
    long RequestedTStatesPerSecond,
    double AchievedTStatesPerSecond,
    double FrameHz,
    bool WarmingUp,
    bool Overloaded,
    int WindowMs);

/// <summary>
/// The honest main-CPU rate meter: achieved T-states per second from the committed CPU counter's
/// delta and frame cadence from the rendered-frame count, both measured against monotonic
/// <em>active</em> wall time over a trailing <see cref="WindowMs"/> window.
/// <para>
/// It never reads a host clock itself: the emulation owner feeds it <see cref="Sample"/> once per
/// rendered frame with the monotonic host tick (the same clock the pacer paces from), the machine
/// identity and the committed CPU counter. A changed machine identity (reset/restore adopts a
/// fresh machine whose counter restarts at zero) and a wall gap longer than the suspend gap
/// (<see cref="SuspendGapMs"/>: a pause, host suspend, loading) both start a fresh window, so pause
/// time is excluded and the window rebases at reset/restore. A shorter gap is a slow host, not idle
/// time: the samples stay, the window anchors at the newest sample at or before (now -
/// <see cref="WindowMs"/>) and the stated window is that actual span, so a delayed host displays
/// its actual rate and the overload. <see cref="SetRequested"/> rebases the window when the
/// requested rate changes (a runtime speed switch). The window is stated: rates are measured over
/// the trailing <see cref="WindowMs"/> of active wall time (or longer on a slow host) and are
/// <em>unavailable</em> (warming up) until that window is full.
/// </para>
/// <para>
/// <see cref="Sample"/> and <see cref="SetRequested"/> run on the emulation owner only;
/// <see cref="Latest"/> is a volatile read of an immutable sample safe for the UI thread.
/// </para>
/// </summary>
public sealed class CpuRateMeter
{
    /// <summary>The stated trailing window of active wall time the rates are measured over.</summary>
    public const int WindowMs = 500;

    /// <summary>Overload threshold: achieved at least this fraction below the requested rate (5%).</summary>
    public const double OverloadTolerance = 0.05;

    /// <summary>Wall gaps longer than this are a pause or a host suspend and prune the window; a
    /// shorter gap is a slow host still delivering frames, so its samples stay and the meter keeps
    /// reporting the actual (low) rate and the overload. The emulation owner rebases on resume for
    /// exact pause exclusion.</summary>
    public const int SuspendGapMs = 5000;

    private readonly long frequency;
    private readonly List<(long Ticks, long TStates, long Frames)> window = new(32);
    // Frames is a global monotonic frame counter, NOT the list index: once the suspend-gap prune
    // starts sliding the list, the count at add-time stays near-constant and a list index would
    // report 0 Hz in steady state (demo #190, capture 2683). It restarts with a cleared window;
    // deltas are only ever taken inside one window.
    private long frameCount;
    private long requested;
    private object? machine;
    private CpuRateSample current;

    /// <summary><paramref name="requestedTStatesPerSecond"/>: the exact requested main-CPU rate
    /// (profile CpuTStatesPerSecond); <paramref name="clockFrequency"/>: the monotonic host
    /// clock's frequency (IHostClock.Frequency).</summary>
    public CpuRateMeter(long requestedTStatesPerSecond, long clockFrequency)
    {
        if (requestedTStatesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedTStatesPerSecond), requestedTStatesPerSecond, "The requested rate must be positive.");
        if (clockFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(clockFrequency), clockFrequency, "The host clock frequency must be positive.");
        requested = requestedTStatesPerSecond;
        frequency = clockFrequency;
        current = new CpuRateSample(requested, 0, 0, true, false, WindowMs);
    }

    /// <summary>The latest immutable sample; 0 achieved/frame values while warming up.</summary>
    public CpuRateSample Latest => Volatile.Read(ref current);

    /// <summary>The current requested T-states per second (updated by <see cref="SetRequested"/>).</summary>
    public long RequestedTStatesPerSecond => Volatile.Read(ref requested);

    /// <summary>The requested rate changed (a runtime speed switch, or a rebase after load):
    /// refresh the sample and start a fresh window so two rates never mix in one window.</summary>
    public void SetRequested(long requestedTStatesPerSecond)
    {
        if (requestedTStatesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedTStatesPerSecond), requestedTStatesPerSecond, "The requested rate must be positive.");
        Volatile.Write(ref requested, requestedTStatesPerSecond);
        ClearWindow();
    }

    /// <summary>Emulation-owner feed, once per rendered frame.
    /// <paramref name="hostTicks"/>: the monotonic host tick (IHostClock.Now);
    /// <paramref name="machineToken"/>: the live machine (identity change = a fresh machine, its
    /// counter restarts); <paramref name="committedTStates"/>: Machine.CommittedCpuTStates.</summary>
    public void Sample(long hostTicks, object machineToken, long committedTStates)
    {
        ArgumentNullException.ThrowIfNull(machineToken);
        if (committedTStates < 0)
            throw new ArgumentOutOfRangeException(nameof(committedTStates), committedTStates, "The committed counter is not negative.");
        if (!ReferenceEquals(machineToken, machine))
        {
            machine = machineToken;
            window.Clear();
        }
        // Defensive: the counter never runs backwards; a regression means an adopted machine
        // without an identity change, and a negative window would lie, so rebase.
        if (window.Count > 0 && committedTStates < window[^1].TStates)
            window.Clear();
        window.Add((hostTicks, committedTStates, frameCount++));
        // Only a true pause/suspend (a gap beyond SuspendGapMs) prunes the window. A shorter gap is a
        // slow host: its samples stay, so the meter keeps anchoring and reporting the actual rate
        // instead of restarting warm forever.
        var suspendTicks = SuspendTicks();
        while (window.Count > 1 && hostTicks - window[0].Ticks > suspendTicks)
            window.RemoveAt(0);
        Publish();
    }

    /// <summary>Drop all evidence (an explicit rebase, e.g. before the owner adopts a load).</summary>
    public void Rebase() => ClearWindow();

    private void ClearWindow()
    {
        window.Clear();
        frameCount = 0;
        Publish();
    }

    private long WindowTicks() => (long)WindowMs * checked(frequency) / 1000;

    private long SuspendTicks() => (long)SuspendGapMs * checked(frequency) / 1000;

    private void Publish()
    {
        if (window.Count == 0)
        {
            Volatile.Write(ref current, new CpuRateSample(requested, 0, 0, true, false, WindowMs));
            return;
        }
        var latest = window[^1];
        // The anchor is the newest sample at or before (now - WindowMs): with it the trailing window
        // is full. Without it, every sample is newer than that, the window is not full yet and the
        // rate is unavailable (warming up), not estimated. Because only a suspend-gap gap prunes the
        // samples, a host rendering sparsely still reaches an anchor and reports its actual rate.
        var cutoff = latest.Ticks - WindowTicks();
        var anchorIndex = -1;
        for (var i = window.Count - 1; i >= 0; i--)
        {
            if (window[i].Ticks <= cutoff) { anchorIndex = i; break; }
        }
        if (anchorIndex < 0)
        {
            Volatile.Write(ref current, new CpuRateSample(requested, 0, 0, true, false, WindowMs));
            return;
        }
        var (anchorTicks, anchorTStates, anchorFrames) = window[anchorIndex];
        var elapsed = latest.Ticks - anchorTicks;
        if (elapsed <= 0)
        {
            Volatile.Write(ref current, new CpuRateSample(requested, 0, 0, true, false, WindowMs));
            return;
        }
        var achieved = (double)(latest.TStates - anchorTStates) * (double)frequency / (double)elapsed;
        var frameHz = (double)(latest.Frames - anchorFrames) * (double)frequency / (double)elapsed;
        var overloaded = achieved < (double)requested * (1.0 - OverloadTolerance);
        // The stated window is the actual span measured (>= WindowMs; longer on a slow host).
        var windowMs = (int)(elapsed * 1000 / (long)frequency);
        Volatile.Write(ref current, new CpuRateSample(requested, achieved, frameHz, false, overloaded, windowMs));
    }
}
