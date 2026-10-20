using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class CpuTurboSelectionTests
{
    [Fact]
    public void OrdinaryLaunchIsNotATurboSelection()
    {
        Assert.False(CpuTurboSelection.IsRequest([]));
        Assert.False(CpuTurboSelection.IsRequest(["--scale", "2"]));
    }

    [Theory]
    [InlineData("hc2000-v2", "1")]
    [InlineData("hc2000-v2", "2")]
    [InlineData("hc2000-v2", "4")]
    [InlineData("hc2000-v2", "8")]
    public void CliSelectsOwnedMultiplierAndReportsScaledWorkWithFixedFrameCadence(string profile, string multiplier)
    {
        var catalog = ProfileRomSelection.Describe(profile);
        Assert.Equal(1, catalog.CpuTurboMultiplier);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CpuTurboSelection.Run(["--profile", profile, "--cpu-turbo", multiplier], stdout, stderr);
        Assert.Equal(0, code);
        Assert.Equal("", stderr.ToString());
        var report = Parse(stdout.ToString());
        var chosen = int.Parse(multiplier);
        Assert.Equal("cpu.turbo", report["selection"]);
        Assert.Equal(profile, report["profile"]);
        Assert.Equal(multiplier, report["multiplier"]);
        Assert.Equal(CpuTurboSelection.Workload, report["workload"]);
        Assert.Equal("3", report["frames"]);
        Assert.Equal((3 * 17_784L * chosen).ToString(), report["work"]);
        Assert.Equal((3 * 71_136L * chosen).ToString(), report["states"]);
        Assert.Equal((3 * FrameTiming.FrameTicks).ToString(), report["masterTick"]);
        Assert.Equal(FrameTiming.FrameTicks.ToString(), report["frameTicks"]);
        Assert.Equal("180", report["intAssertOffset"]);
        Assert.Equal("380", report["intClearOffset"]);
        Assert.Equal("6", report["intEdges"]);
        Assert.Equal("1", report["catalogMultiplier"]);
        Assert.Equal("5", report["mapperNumerator"]);
        Assert.Equal(multiplier, report["mapperDenominator"]);
        Assert.Equal(1, catalog.CpuTurboMultiplier);
    }

    [Theory]
    [InlineData("--cpu-turbo", "4", "--profile", "hc2000-v1")]
    [InlineData("--cpu-turbo", "3")]
    [InlineData("--cpu-turbo", "0")]
    [InlineData("--cpu-turbo")]
    [InlineData("--cpu-turbo", "4", "--cpu-turbo", "2")]
    [InlineData("--cpu-turbo", "4", "--profile", "HC2000-V2")]
    [InlineData("--cpu-turbo", "4", "--speed", "0")]
    public void UnsupportedSelectionDoesNotReportARun(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.Equal(2, CpuTurboSelection.Run(args, stdout, stderr));
        Assert.Equal("", stdout.ToString());
        Assert.Contains("usage: hc2000 --cpu-turbo", stderr.ToString());
    }

    private static Dictionary<string, string> Parse(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
}
