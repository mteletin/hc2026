using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Hc2000.App;
using Hc2000.App.Media;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.InstallerVerify;

/// <summary>Drive the normal v2 whole-chip window-open path against the install copy.
/// Run: HC2000_VERIFY_ASSET_ROOT=&lt;root&gt; dotnet test scripts/installer-verify -c Release
/// This checks composition/ROM identity, not the release-bound D1 screen proof.</summary>
public sealed class InstallerVerifyTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public async Task InstalledCopyPassesRomCheckAndStartsBasic()
    {
        var assetRoot = Environment.GetEnvironmentVariable("HC2000_VERIFY_ASSET_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(assetRoot), "HC2000_VERIFY_ASSET_ROOT is required");
        Assert.Equal("hc2000-v2", Environment.GetEnvironmentVariable("HC2000_VERIFY_PROFILE") ?? "hc2000-v2");
        var window = new MainWindow(null, assetRoot);
        window.Show();
        try
        {
            var status = window.FindControl<TextBlock>("RuntimeStatus")!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (status.Text?.Contains("whole-chip machine running", StringComparison.Ordinal) != true
                && status.Text?.Contains("failed", StringComparison.OrdinalIgnoreCase) != true)
                await Task.Delay(100, deadline.Token);
            Assert.Equal("Real hc2000-v2 whole-chip machine running", status.Text);
            Assert.False(window.FindControl<ProfileRomPanel>("ProfileRomControls")!.IsVisible);
            Assert.True(window.FindControl<Button>("MachineReset")!.IsEnabled);
            output.WriteLine("INSTALLER-VERIFY: whole-chip ROM identity checked; " + status.Text);
        }
        finally { window.Close(); }
    }
}
