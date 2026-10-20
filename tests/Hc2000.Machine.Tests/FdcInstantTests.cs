using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class FdcInstantTests
{
    private static (DeviceScheduler, FloppyController) Create(bool instant)
    {
        var s = new DeviceScheduler();
        var b = new FloppyController(s, 10, new(new(), Dsk.Create(new(80, 2, 9, 512))), new(new()), instant: instant);
        b.WriteLatch(0x1a);
        FdcTests.Send(b.Fdc, 3, 0xdf, 0xff);
        for (var i = 0; i < 4; i++) { FdcTests.Send(b.Fdc, 8); FdcTests.Result(b.Fdc); }
        return (s, b);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SeekRecalibrateAndReadIdPreserveResultsButInstantConsumesNoTicks(bool instant)
    {
        var (s, b) = Create(instant);
        FdcTests.Send(b.Fdc, 15, 0, 3);
        Assert.Equal(instant ? 0 : 1, b.Fdc.ReadStatus() & 1);
        if (!instant)
            while ((b.Fdc.ReadStatus() & 1) != 0) s.AdvanceTo(s.PendingEvents.Min(e => e.DueTick));
        Assert.Equal(3, b.DriveA.HeadPosition);
        FdcTests.Send(b.Fdc, 8);
        Assert.Equal(new byte[] { 0x20, 3 }, FdcTests.Result(b.Fdc));
        FdcTests.Send(b.Fdc, 7, 0);
        if (!instant)
            while ((b.Fdc.ReadStatus() & 1) != 0) s.AdvanceTo(s.PendingEvents.Min(e => e.DueTick));
        FdcTests.Send(b.Fdc, 8);
        Assert.Equal(new byte[] { 0x20, 0 }, FdcTests.Result(b.Fdc));
        FdcTests.Send(b.Fdc, 0x4a, 0);
        FdcTests.AdvanceUntilReady(s, b.Fdc);
        var result = FdcTests.Result(b.Fdc);
        Assert.Equal(7, result.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0 }, result[..5]);
        Assert.InRange(result[5], (byte)1, (byte)9);
        Assert.Equal(2, result[6]);
        Assert.Equal(instant, s.Now == 0);
    }

    [Fact]
    public void InstantReadWriteAndTcUsePollingWithoutByteDeadlines()
    {
        var (s, b) = Create(true);
        var source = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        FdcTests.Send(b.Fdc, 0x45, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        FdcTests.Transfer(s, b, 512, source);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 2, 2 }, FdcTests.Result(b.Fdc));
        FdcTests.Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        Assert.Equal(source, FdcTests.Transfer(s, b, 512));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 2, 2 }, FdcTests.Result(b.Fdc));
        Assert.Equal(0, s.Now);
        Assert.DoesNotContain(s.PendingEvents, e => e.DeviceId == 10);
        Assert.Single(b.DriveA.DirtySectors);
    }

    [Fact]
    public void InstantSlowPollDoesNotOverrunAndInspectDoesNotOfferNextByte()
    {
        var (s, b) = Create(true);
        FdcTests.Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 1, 0x2a, 0xff);
        Assert.Equal(0xf0, b.Fdc.ReadStatus());
        Assert.Equal(0xe5, b.Fdc.ReadData());
        s.AdvanceTo(100_000);
        Assert.Equal(0x70, b.Fdc.Inspect().MainStatus);
        Assert.Equal(0xf0, b.Fdc.ReadStatus());
        Assert.Equal(0xf0, b.Fdc.ReadStatus());
        Assert.Equal(0xe5, b.Fdc.ReadData());
        b.WriteLatch(0x1b);
        Assert.Equal(0, FdcTests.Result(b.Fdc)[1]);
    }

    [Fact]
    public void InstantFormatScanAndPartialWriteTcKeepProgrammedSemantics()
    {
        var (s, b) = Create(true);
        FdcTests.Send(b.Fdc, 0x4d, 0, 1, 2, 0x2a, 0x67);
        FdcTests.Transfer(s, b, 8, new byte[] { 4, 0, 9, 1, 4, 0, 5, 1 }, tc: false);
        Assert.Equal(new byte[] { 0, 0, 0, 4, 0, 6, 1 }, FdcTests.Result(b.Fdc));
        Assert.Equal(new byte[] { 9, 5 }, b.DriveA.Track(0)!.Sectors.Select(x => x.R));
        FdcTests.Send(b.Fdc, 0x51, 0, 4, 0, 5, 1, 5, 0x2a, 1);
        FdcTests.Transfer(s, b, 256, Enumerable.Repeat((byte)0x67, 256).ToArray(), tc: false);
        Assert.Equal(8, FdcTests.Result(b.Fdc)[2]);
        FdcTests.Send(b.Fdc, 0x45, 0, 4, 0, 5, 1, 9, 0x2a, 0xff);
        FdcTests.Transfer(s, b, 3, new byte[] { 1, 2, 3 });
        Assert.Equal(0, FdcTests.Result(b.Fdc)[1]);
        var data = b.DriveA.Track(0)!.Sectors.Single(x => x.R == 5).Data;
        Assert.Equal(new byte[] { 1, 2, 3 }, data[..3]);
        Assert.All(data[3..], x => Assert.Equal(0, x));
        Assert.All(b.DriveA.Track(0)!.Sectors.Single(x => x.R == 9).Data, x => Assert.Equal(0x67, x));
        Assert.Equal(0, s.Now);
    }

    [Fact]
    public void InstantResetDuringDataDiscardsTransferButKeepsMotorAndTime()
    {
        var (s, b) = Create(true);
        FdcTests.Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        Assert.Equal(0xe5, b.Fdc.ReadData());
        b.WriteLatch(0x0a);
        Assert.Equal(FdcPhase.Reset, b.Fdc.Phase);
        Assert.Equal(0, b.Fdc.ReadStatus());
        Assert.False(b.DriveA.HeadLoaded);
        Assert.True(b.DriveA.MotorOn);
        b.WriteLatch(0x1a);
        FdcTests.Send(b.Fdc, 8);
        Assert.Equal(new byte[] { 0xc0, 0 }, FdcTests.Result(b.Fdc));
        Assert.Equal(0, s.Now);
        Assert.DoesNotContain(s.PendingEvents, e => e.DeviceId == 10);
    }

    [Fact]
    public void InstantNoMediaWrongIdAndWriteProtectStillReportErrors()
    {
        var (_, b) = Create(true);
        b.DriveA.WriteProtected = true;
        FdcTests.Send(b.Fdc, 0x45, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        Assert.Equal(2, FdcTests.Result(b.Fdc)[1]);
        FdcTests.Send(b.Fdc, 0x46, 0, 0, 0, 42, 2, 9, 0x2a, 0xff);
        Assert.Equal(4, FdcTests.Result(b.Fdc)[1]);
        b.WriteLatch(0x10); // deselected, no motor
        FdcTests.Send(b.Fdc, 0x46, 0, 0, 0, 1, 2, 9, 0x2a, 0xff);
        Assert.Equal(4, FdcTests.Result(b.Fdc)[1]);
    }
}
