namespace Hc2000.Machine.Timing;

/// <summary>
/// The shared bus/observer seam. Call before Step, before every externally visible bus
/// or observer effect at its reported CPU timestamp, and after Step's idle tail.
/// No IBus or CPU is duplicated here; faithful Contend remains a no-op.
/// </summary>
public sealed class CpuDeviceTimeAdapter(DeviceScheduler scheduler, CpuTimeMapper mapper)
{
    public void SyncCpuTime(long absoluteCpuTStates) => scheduler.AdvanceTo(mapper.Map(absoluteCpuTStates));
}
