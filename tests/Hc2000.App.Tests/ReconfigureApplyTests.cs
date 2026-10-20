using System.IO;
using Hc2000.App.Configuration;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Apply-running-configuration policy: a valid draft keeps the mounted A/B media and writeback
/// choices byte-for-byte; any media, identity, profile or validation difference is refused with a clear
/// reason. Pure policy — no runtime, no persistence, no snapshot or RAM writes.</summary>
public sealed class ReconfigureApplyTests
{
    private static MachineConfiguration Machine(string name = "M") => new() { Name = name };
    private static MachineConfiguration WithMedia() => Machine("M") with
    {
        DiskA = new FloppyConfiguration(Path.GetFullPath("a.dsk"), Writeback: true),
        DiskB = new FloppyConfiguration(Path.GetFullPath("b.dsk")),
        LastSnapshot = Path.GetFullPath("snap.hcs")
    };

    [Fact]
    public void LiveControlChangesPlanAndKeepTheMountedMedia()
    {
        var current = WithMedia();
        var draft = current with { SpeedPercent = 200, Scale = 4, KeyboardMode = "Symbolic" };
        Assert.True(ReconfigureApply.TryPlan(current, draft, out var error));
        Assert.Null(error);
        Assert.Equal(current.DiskA, draft.DiskA);
        Assert.Equal(current.DiskB, draft.DiskB);
        Assert.Equal(current.LastSnapshot, draft.LastSnapshot);
    }

    [Fact]
    public void IdentityMismatchIsRefused()
    {
        var current = Machine("M");
        Assert.False(ReconfigureApply.TryPlan(current, Machine("other"), out var error));
        Assert.Contains("different machine", error!);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(300)]
    public void InvalidSpeedIsRefused(int speed)
    {
        var current = Machine("M");
        Assert.False(ReconfigureApply.TryPlan(current, current with { SpeedPercent = speed }, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ScaleAboveEightIsRefused()
    {
        var current = Machine("M");
        Assert.False(ReconfigureApply.TryPlan(current, current with { Scale = 9 }, out var error));
        Assert.Contains("1–8", error!);
    }

    [Theory]
    [InlineData("diskA")]
    [InlineData("diskB")]
    [InlineData("snapshot")]
    public void AnyMediaChangeIsRefused(string which)
    {
        var current = WithMedia();
        var draft = which switch
        {
            "diskA" => current with { DiskA = new FloppyConfiguration(Path.GetFullPath("other-a.dsk")) },
            "diskB" => current with { DiskB = new FloppyConfiguration(Path.GetFullPath("other-b.dsk")) },
            _ => current with { LastSnapshot = Path.GetFullPath("other.hcs") }
        };
        Assert.False(ReconfigureApply.TryPlan(current, draft, out var error));
        Assert.Contains("new machine draft", error!);
    }

    [Fact]
    public void WritebackChoiceChangeIsRefused()
    {
        var current = Machine("M") with { DiskA = new FloppyConfiguration(Path.GetFullPath("a.dsk")) };
        var draft = current with { DiskA = new FloppyConfiguration(Path.GetFullPath("a.dsk"), Writeback: true) };
        Assert.False(ReconfigureApply.TryPlan(current, draft, out var error));
        Assert.Contains("new machine draft", error!);
    }

    [Fact]
    public void NonEmptyHddAndForeignProfileAreRefused()
    {
        var current = Machine("M");
        Assert.False(ReconfigureApply.TryPlan(current, current with
        {
            Hdd = [new HddConfiguration("C", Path.GetFullPath("c.img"))]
        }, out var hdd));
        Assert.NotNull(hdd);
        Assert.False(ReconfigureApply.TryPlan(current, current with { Profile = "hc2000-v1" }, out var profile));
        Assert.Contains("hc2000-v2", profile!);
    }
}
