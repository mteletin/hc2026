using Hc2000.App;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class LaunchOptionsTests
{
    // ---------------------------------------------------------- acceptance: the full combo

    [Fact]
    public void FullAcceptanceComboParses()
    {
        var result = LaunchOptions.Parse(["--disk", "A:CPM22-HC.DSK", "--start"]);
        Assert.True(result.Ok);
        Assert.Null(result.StatusLine);
        Assert.Equal(new LaunchOptions("A:CPM22-HC.DSK", null, true, false), result.Options);
    }

    // ---------------------------------------------------------- acceptance: each option alone

    [Fact]
    public void DiskAAloneParses()
    {
        var result = LaunchOptions.Parse(["--disk", "A:games/tape.tap"]);
        Assert.True(result.Ok);
        Assert.Equal("A:games/tape.tap", result.Options!.DiskA);
        Assert.Null(result.Options.DiskB);
    }

    [Fact]
    public void DiskBAloneParses()
    {
        var result = LaunchOptions.Parse(["--disk", "B:data.img"]);
        Assert.True(result.Ok);
        Assert.Equal("B:data.img", result.Options!.DiskB);
        Assert.Null(result.Options.DiskA);
    }

    [Fact]
    public void StartAloneParses()
    {
        var result = LaunchOptions.Parse(["--start"]);
        Assert.True(result.Ok);
        Assert.True(result.Options!.Start);
        Assert.Null(result.Options.DiskA);
    }

    [Fact]
    public void HelpAloneReportsHelp()
    {
        var result = LaunchOptions.Parse(["--help"]);
        Assert.True(result.Ok);
        Assert.True(result.Options!.Help);
        Assert.Null(result.StatusLine);
    }

    [Fact]
    public void NoArgumentsParseToDefaults()
    {
        var result = LaunchOptions.Parse([]);
        Assert.True(result.Ok);
        Assert.Equal(new LaunchOptions(null, null, false, false), result.Options);
    }

    // ---------------------------------------------------------- acceptance: rejections (status line, never thrown)

    [Theory]
    [InlineData("C:x.dsk")]   // unsupported drive
    [InlineData("A:")]        // drive with no path
    [InlineData("x.dsk")]     // no drive prefix
    [InlineData("A")]         // colon missing
    [InlineData("1:disk")]    // not a drive letter
    public void MalformedDiskIsRejectedWithAStatusLine(string value)
    {
        var result = LaunchOptions.Parse(["--disk", value]);
        Assert.False(result.Ok);
        Assert.Null(result.Options);
        Assert.StartsWith("Malformed --disk:", result.StatusLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--turbo")]
    [InlineData("--disk=A:x.dsk")] // '=' form is not the accepted surface
    [InlineData("-v")]
    public void UnknownOptionIsRejectedWithAStatusLine(string flag)
    {
        var result = LaunchOptions.Parse([flag]);
        Assert.False(result.Ok);
        Assert.Null(result.Options);
        Assert.Equal($"Unknown option: {flag}.", result.StatusLine);
    }

    [Fact]
    public void PositionalArgumentIsRejectedWithAStatusLine()
    {
        var result = LaunchOptions.Parse(["spectrum"]);
        Assert.False(result.Ok);
        Assert.Equal("Unexpected argument: spectrum.", result.StatusLine);
    }

    [Fact]
    public void MissingValueIsRejectedWithAStatusLine()
    {
        var result = LaunchOptions.Parse(["--disk"]);
        Assert.False(result.Ok);
        Assert.Equal("Missing value for --disk.", result.StatusLine);
    }

    [Fact]
    public void ValueLookingLikeAFlagIsMissingValueNotAValue()
    {
        var result = LaunchOptions.Parse(["--disk", "--start"]);
        Assert.False(result.Ok);
        Assert.Equal("Missing value for --disk.", result.StatusLine);
    }

    [Fact]
    public void DuplicateDiskIsRejectedWithAStatusLine()
    {
        Assert.Equal("Duplicate --disk A:.",
            LaunchOptions.Parse(["--disk", "A:one.dsk", "--disk", "A:two.dsk"]).StatusLine);
        Assert.Equal("Duplicate --disk B:.",
            LaunchOptions.Parse(["--disk", "B:one.dsk", "--disk", "B:two.dsk"]).StatusLine);
    }

    // ------------------------------------ acceptance: removed options are unknown (PO re-scope 626755)

    [Theory]
    [InlineData("--rom-set")]
    [InlineData("--rom-set", "hc2000-v1")]
    [InlineData("--asset-root")]
    [InlineData("--asset-root", "/opt/hc2000")]
    public void RemovedOptionsAreReportedAsUnknownFlags(params string[] argv)
    {
        var result = LaunchOptions.Parse(argv);
        Assert.False(result.Ok);
        Assert.Null(result.Options);
        Assert.StartsWith("Unknown option:", result.StatusLine, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- acceptance: help wins

    [Fact]
    public void HelpAnywhereWinsOverEverythingElse()
    {
        var result = LaunchOptions.Parse(["--turbo", "--help", "spectrum", "--disk"]);
        Assert.True(result.Ok);
        Assert.True(result.Options!.Help);
        Assert.Null(result.StatusLine);
    }

    // ---------------------------------------------------------- acceptance: pure and deterministic

    [Fact]
    public void EqualInputYieldsEqualResults()
    {
        string[] argv = ["--disk", "A:CPM22-HC.DSK", "--disk", "B:work.img", "--start"];
        Assert.Equal(LaunchOptions.Parse(argv), LaunchOptions.Parse(argv));
        var failed = LaunchOptions.Parse(["--bogus"]);
        Assert.Equal(failed, LaunchOptions.Parse(["--bogus"]));
    }

    [Fact]
    public void SyntacticallyValidButAbsentDiskPathStillParses_ExistenceIsARuntimeReportNotAnException()
    {
        // Pure parser: no I/O. A missing/unreadable path is reported by the consumer on the RuntimeStatus
        // line, never by throwing here (item spec).
        var result = LaunchOptions.Parse(["--disk", "A:/definitely/not/here.dsk"]);
        Assert.True(result.Ok);
        Assert.Equal("A:/definitely/not/here.dsk", result.Options!.DiskA);
    }
}

