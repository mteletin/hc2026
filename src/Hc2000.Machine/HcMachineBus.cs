using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Z80;

namespace Hc2000.Machine;

/// <summary>
/// Machine implementation of the Z80 bus boundary. Ordinary reads, writes, ports and
/// <see cref="Contend"/> never infer an opcode fetch; only the optional
/// <see cref="IM1FetchBus.FetchM1"/> capability carries the exact absolute CPU T-state,
/// synchronizes the shared device-time scheduler to that tick, and then resolves the
/// M1 fetch through the memory's IF1 mapping (hc2000-z80-m1-observer-contract).
/// </summary>
public sealed class HcMachineBus(
    HcMemory memory,
    PortDecoder ports,
    CpuDeviceTimeAdapter time,
    DeviceScheduler scheduler,
    CpuPortView? portView = null) : IM1FetchBus
{
    private readonly CpuPortView? boundPortView = portView?.BindTo(ports);

    public byte Read(ushort address) => memory.Read(address);
    public void Write(ushort address, byte value) => memory.Write(address, value);
    public byte In(ushort port) => boundPortView is { } view
        ? view.Read(port, scheduler.Now) : ports.Read(port, scheduler.Now);
    public void Out(ushort port, byte value)
    {
        if (boundPortView is { } view) view.Write(port, value, scheduler.Now);
        else ports.Write(port, value, scheduler.Now);
    }

    /// <summary>Faithful contention model for the single-master HC2000 bus: no-op.</summary>
    public void Contend(ushort address, int count) { }

    /// <summary>INTA floats high; the HC2000 is not an IM2 bus.</summary>
    public byte IntVector() => PortDecoder.IntVector();

    /// <summary>
    /// One true memory-backed opcode fetch: the scheduler drains every event due at the
    /// mapped tick before the page lookup, and the returned byte is executed by the CPU.
    /// </summary>
    public byte FetchM1(ushort address, bool instructionStart, long cpuTStates)
    {
        time.SyncCpuTime(cpuTStates);
        return memory.ReadM1(address);
    }
}
