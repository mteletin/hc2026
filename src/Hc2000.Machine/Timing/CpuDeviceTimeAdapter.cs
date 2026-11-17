namespace Hc2000.Machine.Timing;

/// <summary>
/// The shared bus/observer seam. Call before Step, before every externally visible bus
/// or observer effect at its reported CPU timestamp, and after Step's idle tail.
/// No IBus or CPU is duplicated here; faithful Contend remains a no-op.
/// </summary>
public sealed class CpuDeviceTimeAdapter
{
    private readonly DeviceScheduler scheduler;
    private CpuTimeMapper mapper;

    public CpuDeviceTimeAdapter(DeviceScheduler scheduler, CpuTimeMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(mapper);
        this.scheduler = scheduler;
        this.mapper = mapper;
    }

    /// <summary>The mapper in force. A runtime speed switch replaces it through <see cref="InstallMapper"/>.</summary>
    public CpuTimeMapper Mapper => mapper;

    /// <summary>
    /// Advance the scheduler to the exact logical time of this CPU counter, not its ceiling.
    /// At faithful 5/1 the exact time is the integer ceiling, so the trace is unchanged.
    /// </summary>
    public void SyncCpuTime(long absoluteCpuTStates)
    {
        mapper.Map(absoluteCpuTStates);
        // Integer-exact rates (faithful 5/1 and every integer main-CPU target) land on whole ticks;
        // the long form skips the fraction bookkeeping and keeps the trace byte-identical.
        if (mapper.Remainder == 0)
            scheduler.AdvanceTo(mapper.WholeTicks);
        else
            scheduler.AdvanceTo(mapper.ExactTime());
    }

    /// <summary>
    /// Installs the mapper a runtime speed switch built (anchored at the committed boundary).
    /// The caller (the machine owner) has already verified the safe boundary and the anchor.
    /// </summary>
    internal void InstallMapper(CpuTimeMapper next)
    {
        ArgumentNullException.ThrowIfNull(next);
        mapper = next;
    }
}
