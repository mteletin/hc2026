using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class DrivePowerCycleTests
{
    [Fact]
    public void ResetMechanicsPreservesDirtyMediaOverlaysStatusAndProtection()
    {
        var (scheduler, board) = FdcTests.Create();
        var drive = board.DriveA;
        FdcTests.Send(board.Fdc, 0x4d, 0, 1, 2, 0x2a, 0x67);
        FdcTests.Transfer(scheduler, board, 8, [4, 0, 9, 1, 4, 0, 5, 1], tc: false);
        FdcTests.Result(board.Fdc);
        drive.Step(1);
        drive.WriteProtected = true;
        Assert.True(drive.MotorOn);
        Assert.True(drive.HeadLoaded);
        var image = drive.Image;
        var exported = drive.ExportMedia();
        var dirty = drive.DirtySectors.ToArray();
        var formatted = drive.FormattedTracks.ToArray();

        drive.PowerCycle();

        Assert.Same(image, drive.Image);
        Assert.Equal(exported, drive.ExportMedia());
        Assert.Equal(dirty, drive.DirtySectors);
        Assert.Equal(formatted, drive.FormattedTracks);
        Assert.True(drive.WriteProtected);
        Assert.Equal(0, drive.HeadPosition);
        Assert.Equal(0, drive.RotationOrigin);
        Assert.False(drive.MotorOn);
        Assert.False(drive.HeadLoaded);
        Assert.True(drive.DiskChanged);
        Assert.NotEmpty(drive.DirtySectors);
        Assert.NotEmpty(drive.FormattedTracks);
    }
}
