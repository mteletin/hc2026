using Hc2000.Machine.Snapshots;
using Hc2000.Z80;
using Xunit;
using Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Tests;

public sealed class ProductionZ80SnapshotTests
{
    [Fact]
    public void AdapterUsesTheExistingCpuWithoutSteppingOrBootInjection()
    {
        var cpu = new Cpu(new Bus());
        cpu.R.AF = 0x1234; cpu.R.AFShadow = 0x5678; cpu.R.PC = 0x9123;
        cpu.R.WZ = 0xabcd; cpu.R.IFF1 = true; cpu.R.IM = 2; cpu.TStates = 12345;
        cpu.Nmi();
        var type = typeof(ICpuSnapshotSeam).Assembly.GetType("Hc2000.Machine.Snapshots.Z80SnapshotSeam");
        Assert.NotNull(type);
        var seam = Assert.IsAssignableFrom<ICpuSnapshotSeam>(Activator.CreateInstance(type, cpu));
        var captured = seam.Capture();
        Assert.True(captured.NmiPending); Assert.Equal((ushort)0x9123, captured.Pc);
        Assert.Equal(12345, captured.TStates);
        cpu.Reset(); seam.Restore(captured);
        Assert.Equal(captured, seam.Capture());
        Assert.Equal((ushort)0x5678, cpu.R.AFShadow);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void CaptureRefusesUnrepresentableEiDelayWithoutWrapping(int value)
    {
        var cpu = new Cpu(new Bus());
        var property = typeof(Cpu).GetProperty("EiDelayRaw");
        Assert.NotNull(property);
        property.SetValue(cpu, value);
        var type = typeof(ICpuSnapshotSeam).Assembly.GetType("Hc2000.Machine.Snapshots.Z80SnapshotSeam");
        Assert.NotNull(type);
        var seam = (ICpuSnapshotSeam)Activator.CreateInstance(type, cpu)!;
        Assert.Throws<InvalidOperationException>(() => seam.Capture());
        Assert.Equal(value, property.GetValue(cpu));
    }

    private sealed class Bus : IBus
    {
        public byte Read(ushort address) => 0;
        public void Write(ushort address, byte value) { }
        public byte In(ushort port) => 0;
        public void Out(ushort port, byte value) { }
        public byte IntVector() => 0xff;
        public void Contend(ushort address, int tstates) { }
    }
}
