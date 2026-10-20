using Hc2000.App.Debugging;
using Hc2000.Disasm;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class DebuggerSessionTests
{
    private static HcMemory Memory() => new(new MemoryProfile(), new byte[16384], new byte[16384], new byte[16384]);

    [Fact]
    public void BusStopsLatchUntilBoundaryAndReportTriggerSeparateFromPc()
    {
        var session = new DebuggerSession("synthetic test-fed fixture");
        session.AddBreakpoint(new(BreakpointKind.MemoryWrite, 0, 0));
        var memory = Memory();
        memory.Write(0, 42); // ignored ROM write attempt remains an observation
        session.ObserveMemory(memory, 0, 42, MemoryAccess.Write, 4);
        Assert.Null(session.Snapshot);
        var stop = session.Boundary(new RegisterSnapshot(0x1234, 0xffff), memory, null, 11);
        Assert.True(stop.ShouldStop);
        Assert.Equal((ushort)0x1234, stop.Registers.Pc);
        Assert.Equal(4, stop.Trigger!.CpuTStates);
        Assert.Equal((ushort)0, stop.Trigger.Address);
        Assert.Equal((byte)42, stop.Trigger.Value);
        Assert.Equal((byte)0, stop.Memory[0]);
        session.Resume();
        Assert.False(session.Boundary(new(0x1234, 0xffff), memory, null, 15).ShouldStop);
    }

    [Fact]
    public void PcPortsPagingAndM1RemainExplicitAndDirectionSpecific()
    {
        var session = new DebuggerSession("fixture"); var memory = Memory();
        session.AddBreakpoint(new(BreakpointKind.Pc, 0x1234, 0x1234));
        Assert.True(session.Boundary(new(0x1234, 0), memory, null, 0).ShouldStop);
        session.Resume();
        Assert.False(session.Boundary(new(0x1234, 0), memory, null, 0).ShouldStop);
        Assert.True(session.Boundary(new(0x1234, 0), memory, null, 3).ShouldStop);
        session.ClearBreakpoints(); session.Resume();
        session.AddBreakpoint(new(BreakpointKind.PortOut, 0xab7e, 0xab7e));
        session.ObservePort(0xab7e, 0x81, false, 4);
        Assert.False(session.Boundary(new(1, 0), memory, null, 5).ShouldStop);
        session.ObservePort(0xab7e, 0x81, true, 8);
        var stop = session.Boundary(new(2, 0), memory, null, 10);
        Assert.True(stop.ShouldStop); Assert.Equal((byte)0x81, stop.Trigger!.Value);
        session.ClearBreakpoints(); session.Resume();
        session.AddBreakpoint(new(BreakpointKind.Paging));
        session.ObservePaging(default, default, 11);
        Assert.False(session.Boundary(new(3, 0), memory, null, 12).ShouldStop);
        memory.SetCpm(true); session.ObservePaging(default, memory.Latches, 13);
        Assert.True(session.Boundary(new(4, 0), memory, null, 14).ShouldStop);
        session.ClearBreakpoints(); session.Resume();
        session.AddBreakpoint(new(BreakpointKind.MemoryRead, 0, 10));
        session.ObserveM1(memory, 0, true, 16, 0);
        session.ObserveM1(memory, 1, false, 20, 0);
        Assert.False(session.Boundary(new(2, 0), memory, null, 24).ShouldStop);
        session.ObserveMemory(memory, 2, 0, MemoryAccess.Read, 26);
        Assert.True(session.Boundary(new(3, 0), memory, null, 28).ShouldStop);
    }

    [Fact]
    public void CopiedMemoryAndPageMapStayIsolatedAndDisasmUsesControlLabels()
    {
        var session = new DebuggerSession("fixture"); var memory = Memory();
        memory.Ram.Span[0x8000] = 0;
        var view = session.Boundary(new(0x8000, 0), memory, null, 0);
        var result = view.Disassemble(0x8000, 1, ControlFile.Parse("entry 8000\nlabel 8000 fixture_entry"));
        Assert.True(result.Success); Assert.Contains("fixture_entry:", result.Source); Assert.Contains("NOP", result.Source);
        memory.Ram.Span[0x8000] = 42; memory.SetCpm(true);
        Assert.Equal((byte)0, view.Memory[0x8000]);
        Assert.False(view.Latches.Cpm);
        Assert.Equal(0xc000, view.Locations[0xc000].Offset);
        Assert.Null(view.Fdc);
        Assert.Contains("fixture", view.Source);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Boundary(new(0, 0), memory, null, -1));
    }

    [Fact]
    public void MutationsFromUiOrWorkerThreadAreRejected()
    {
        var session = new DebuggerSession("fixture");
        var recorder = new ExecutionRecorder(new byte[1], MemoryStore.Ram, 0, "fixture", new string('d', 64));
        Exception? sessionError = null, recorderError = null;
        var thread = new Thread(() =>
        {
            sessionError = Record.Exception(session.Resume);
            recorderError = Record.Exception(recorder.Start);
        });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(sessionError);
        Assert.IsType<InvalidOperationException>(recorderError);
    }

    [Fact]
    public void ExplicitDdcbFixtureRecordsOnlyRealM1AndStartNotFinalOpcodeOrPorts()
    {
        var memory = Memory();
        byte[] image = [0xdd, 0xcb, 0, 0x46];
        image.CopyTo(memory.Ram.Span[0x8000..]);
        var session = new DebuggerSession("synthetic DDCB producer fixture")
        {
            Recorder = new ExecutionRecorder(image, MemoryStore.Ram, 0x8000, "fixture", new string('d', 64))
        };
        session.Recorder.Start();
        session.ObserveM1(memory, 0x8000, true, 3, 0xdd);
        session.ObserveM1(memory, 0x8001, false, 7, 0xcb);
        session.ObserveMemory(memory, 0x8002, 0, MemoryAccess.Read, 10);
        session.ObserveMemory(memory, 0x8003, 0x46, MemoryAccess.Read, 13);
        session.ObservePort(0x8000, 42, false, 16);
        var map = ExecutionMap.Parse(session.Recorder.ExportBinary());
        var config = Assert.Single(map.Configurations);
        Assert.Equal(ExecutionFlags.M1 | ExecutionFlags.Start, config.FlagsAt(0x8000));
        Assert.Equal(ExecutionFlags.M1, config.FlagsAt(0x8001));
        Assert.Equal(ExecutionFlags.Read, config.FlagsAt(0x8003));
        var code = new Disassembler().Disassemble(image, 0x8000, map: map);
        Assert.True(code.Success); Assert.Equal(4, code.Statistics.CodeBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ObservePort(0, 0, true, 15));
    }

    [Fact]
    public void FdcPanelCopiesWithoutConsumingResultOrAdvancingScheduler()
    {
        var scheduler = new DeviceScheduler();
        var controller = new FloppyController(scheduler, 1, new FloppyDrive(new()), new FloppyDrive(new()));
        controller.WriteLatch(0x10); // release reset
        controller.Fdc.WriteData(8);
        var phase = controller.Fdc.Phase; var time = scheduler.Now;
        var session = new DebuggerSession("fixture");
        var view = session.Boundary(new(0, 0), Memory(), controller.Fdc, 0);
        Assert.Equal(phase, controller.Fdc.Phase); Assert.Equal(time, scheduler.Now);
        Assert.Equal((byte)0xc0, view.Fdc!.LastSt0);
        Assert.Equal(new byte[] { 8 }, view.Fdc.LastCommand);
        Assert.Null(view.Fdc.LastSt3);
        Assert.Equal((byte)0xc0, controller.Fdc.ReadData());
        Assert.Equal((byte)0, controller.Fdc.ReadData());
        Assert.Equal(FdcPhase.Result, view.Fdc.Phase);
    }
}
