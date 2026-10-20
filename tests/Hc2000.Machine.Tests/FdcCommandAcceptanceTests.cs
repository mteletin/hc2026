using Hc2000.Machine.Floppy;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Test-only acceptance of the faithful polled command/data/result handshake.
/// No BIOS boot, CPU IRQ servicing, instant profile, traps or snapshot claims.</summary>
public sealed class FdcCommandAcceptanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FaithfulReadWriteCommandHandshakeAndMissedFirstByteReturnExactOverrun(bool write, bool mfm)
    {
        var (scheduler, board) = FdcTests.Create();
        var fdc = board.Fdc;
        var media = board.DriveA.Image!.Save();
        byte[] command = [(byte)((write ? 5 : 6) | (mfm ? 0x40 : 0)), 0, 0, 0, 1, 2, 9, 0x2a, 0xff];
        Assert.Equal(FdcPhase.Command, fdc.Phase);
        Assert.Equal(0x80, fdc.ReadStatus()); // RQM, CPU -> FDC, not busy
        for (var i = 0; i < command.Length - 1; i++)
        {
            fdc.WriteData(command[i]);
            Assert.Equal(FdcPhase.Command, fdc.Phase);
            Assert.Equal(0x90, fdc.ReadStatus()); // RQM + command busy, DIO/EXM clear
            Assert.Equal(0xff, fdc.ReadData()); // wrong-direction reads never consume parameters
            Assert.Equal(command[..(i + 1)], fdc.Inspect().Command.ToArray());
            Assert.Equal(0, scheduler.Now); // command collection/polling does not run device time
        }
        fdc.WriteData(command[^1]);
        Assert.Equal(FdcPhase.Execution, fdc.Phase);
        Assert.Equal(write ? 0x30 : 0x70, fdc.ReadStatus()); // busy/execution, RQM not yet ready
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(write ? 0x30 : 0x70, fdc.ReadStatus());
            Assert.Equal(0xff, fdc.ReadData());
            fdc.WriteData(0x8); // while RQM is low: not SENSE INTERRUPT, not a data byte
        }
        Assert.Equal(0, scheduler.Now);
        FdcTests.AdvanceUntilReady(scheduler, fdc);
        Assert.Equal(write ? 0xb0 : 0xf0, fdc.ReadStatus());
        Assert.Equal(FdcPhase.Execution, fdc.Phase);
        var offeredAt = scheduler.Now;
        var deadline = Assert.Single(scheduler.PendingEvents, e => e.DeviceId == 10).DueTick;
        var rate = mfm ? 250_000L : 125_000L;
        Assert.Equal(offeredAt + (FrameTiming.MasterHz * 8 + rate - 1) / rate, deadline);
        scheduler.AdvanceTo(deadline - 1);
        // Keep the first byte unserviced, even after a wrong-direction bus access.
        if (write) Assert.Equal(0xff, fdc.ReadData()); else fdc.WriteData(0x8);
        Assert.Equal(write ? 0xb0 : 0xf0, fdc.ReadStatus());
        Assert.Equal(FdcPhase.Execution, fdc.Phase);
        scheduler.AdvanceTo(deadline);
        Assert.Equal(FdcPhase.Result, fdc.Phase);
        Assert.Equal(0xd0, fdc.ReadStatus()); // RQM + DIO + busy, EXM clear
        fdc.WriteData(0x8); // result is FDC -> CPU; command injection must not consume/replace it
        byte[] result = [0x40, 0x10, 0, 0, 0, 1, 2]; // abnormal termination, ST1.OR, exact CHRN
        foreach (var value in result)
        {
            Assert.Equal(0xd0, fdc.ReadStatus());
            Assert.Equal(value, fdc.ReadData());
        }
        Assert.Equal(FdcPhase.Command, fdc.Phase);
        Assert.Equal(0x80, fdc.ReadStatus());
        Assert.Equal(0xff, fdc.ReadData()); // no accidental eighth result or successful completion
        Assert.Equal(media, board.DriveA.Image.Save()); // overrun cannot fabricate a successful write
        FdcTests.Send(fdc, 4, 0);
        Assert.Equal(new byte[] { 0x38 }, FdcTests.Result(fdc)); // a fresh command works after result drain
        Assert.Equal(0x80, fdc.ReadStatus());
    }
}
