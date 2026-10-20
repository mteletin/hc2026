using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Uses only pre-existing APIs so this exact test can run on the pre-HDD App baseline.</summary>
public sealed class HddOptInRegressionTests
{
    [Fact]
    public void ExplicitHddOptInIsRecognizedWithoutRomOrGuestInjectionOptions()
    {
        var parsed = LaunchOptions.Parse(["--hdd"]);
        Assert.True(parsed.Ok, parsed.StatusLine); // Baseline rejects --hdd as unknown; actual failing-first run waits GO.
        Assert.NotNull(parsed.Options);
        Assert.False(parsed.Options.Help);
        Assert.False(LaunchOptions.Parse(["--rom-set", "anything"]).Ok);
        Assert.False(LaunchOptions.Parse(["--asset-root", "anything"]).Ok);
    }
}
