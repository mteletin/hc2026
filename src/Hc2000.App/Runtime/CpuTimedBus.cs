using Hc2000.Machine;
using Hc2000.Machine.Timing;
using Hc2000.Z80;
using CpuZ80 = Hc2000.Z80.Z80;

namespace Hc2000.App.Runtime;

/// <summary>Shared CPU-to-device time adapter used by both the visual fixture and real machines.</summary>
internal sealed class CpuTimedBus(IM1FetchBus inner, CpuDeviceTimeAdapter time) : IBus, IM1FetchBus
{
    private CpuZ80? cpu;
    public void Attach(CpuZ80 value) => cpu = value;
    private void Sync() => time.SyncCpuTime(cpu?.TStates ?? 0);
    public byte Read(ushort addr) { Sync(); return inner.Read(addr); }
    public void Write(ushort addr, byte value) { Sync(); inner.Write(addr, value); }
    public byte In(ushort port) { Sync(); return inner.In(port); }
    public void Out(ushort port, byte value) { Sync(); inner.Out(port, value); }
    public void Contend(ushort addr, int tstates) => inner.Contend(addr, tstates);
    public byte IntVector() => inner.IntVector();
    public byte FetchM1(ushort address, bool instructionStart, long cpuTStates)
    {
        time.SyncCpuTime(cpuTStates);
        return inner.FetchM1(address, instructionStart, cpuTStates);
    }
}
