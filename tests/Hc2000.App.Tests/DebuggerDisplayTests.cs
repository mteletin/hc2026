using Hc2000.App.Debugging;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class DebuggerDisplayTests
{
    [Fact]
    public void CopiedPanelsShowUnavailableFieldsAndPhysicalAliasesExplicitly()
    {
        var memory = new HcMemory(new MemoryProfile(), new byte[16384], new byte[16384], new byte[16384]);
        memory.SetCpm(true);
        var view = new DebuggerSession("synthetic fixture").Boundary(new(0x1234, 0xffff, Af: 0x5678), memory, null, 20);
        var registers = DebuggerDisplay.Registers(view);
        Assert.Contains("PC=1234 SP=FFFF", registers);
        Assert.Contains("AF=5678 BC=unavailable", registers);
        Assert.Contains("IFF1=unavailable", registers);
        Assert.Contains("FDC unavailable", DebuggerDisplay.Fdc(view));
        Assert.Contains("C000  00  Ram+00E000 RW", DebuggerDisplay.Memory(view, 0xc000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DebuggerDisplay.Memory(view, 65535, 2));
    }

    [Fact]
    public void FdcStatusPanelsRetainReportedValuesAndPartialCommandsWithoutConsumingThem()
    {
        var controller = new FloppyController(new DeviceScheduler(), 1, new FloppyDrive(new()), new FloppyDrive(new()));
        var fdc = controller.Fdc; controller.WriteLatch(0x12);
        fdc.WriteData(4); fdc.WriteData(0); // Sense Drive: ST3
        var state = fdc.Inspect();
        Assert.Equal((byte)0x38, state.LastSt3); Assert.Null(state.LastSt0);
        Assert.Equal((byte)0x38, fdc.ReadData());
        fdc.WriteData(8); var sense = fdc.Inspect();
        Assert.Equal((byte)0xc0, sense.LastSt0); Assert.Equal((byte)0x38, sense.LastSt3);
        fdc.ReadData(); fdc.ReadData();
        fdc.WriteData(3); // incomplete SPECIFY
        Assert.Equal(new byte[] { 3 }, fdc.Inspect().Command);
        Assert.Equal(new byte[] { 8 }, sense.LastCommand);
        controller.WriteLatch(0);
        Assert.Null(fdc.Inspect().LastSt0); Assert.Null(fdc.Inspect().LastSt3);
        Assert.Equal(FdcPhase.Result, sense.Phase);
    }
}
