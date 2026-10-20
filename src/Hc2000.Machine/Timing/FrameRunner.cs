namespace Hc2000.Machine.Timing;

/// <summary>CPU-independent instruction-boundary runner; carries all instruction overshoot.</summary>
public sealed class FrameRunner(FrameClock clock, CpuDeviceTimeAdapter adapter)
{
    public void RunFrame(Action step, Func<long> cpuTStates)
        => TryRunFrame(step, cpuTStates, () => true);

    public bool TryRunFrame(Action step, Func<long> cpuTStates, Func<bool> boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(cpuTStates);
        var requested = checked(clock.CompletedFrames + 1);
        adapter.SyncCpuTime(cpuTStates());
        while (clock.CompletedFrames < requested)
        {
            var before = cpuTStates();
            adapter.SyncCpuTime(before); // INT is current before Step samples it.
            if (!boundary()) return false;
            step();
            var after = cpuTStates();
            if (after <= before) throw new InvalidOperationException("CPU Step must advance its absolute counter, including HALT.");
            adapter.SyncCpuTime(after); // Include tail/idle cycles; do not truncate at frame end.
        }
        return true;
    }
}
