using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class V2OnlyWindowTests(ITestOutputHelper output) : IDisposable
{
    private readonly HddColdAdmissionTests.PrivateScopedDomain nativeDomain = new();
    public void Dispose() => nativeDomain.Dispose();
    [AvaloniaFact]
    public async Task MissingWholeChipShowsErrorAndNeverOffersLegacyBoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc2000-missing-chip-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var window = new MainWindow(new LaunchOptions(null, null, true, false), root);
        window.Show();
        try
        {
            var status = window.FindControl<TextBlock>("RuntimeStatus")!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (status.Text?.Contains("failed", StringComparison.OrdinalIgnoreCase) != true)
                await Task.Delay(20, deadline.Token);
            output.WriteLine(status.Text);
            Assert.Contains("M27C512.bin", status.Text);
            Assert.DoesNotContain("hc2000-v1", status.Text);
            Assert.False(window.FindControl<ProfileRomPanel>("ProfileRomControls")!.IsVisible);
            Assert.Null(window.CurrentRuntime);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task VisibleResetPowerCyclesV2WithIf1AndKeepsMountedDisks()
    {
        var root = RomSelectionFixture.RepositoryRoot();
        var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(root));
        var copyRoot = Path.Combine(Path.GetTempPath(), "hc2000-reset-chip-" + Guid.NewGuid());
        var chip = Path.Combine(copyRoot, ProfileRomSelection.WholeChipSourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(chip)!);
        File.Copy(Path.Combine(root, ProfileRomSelection.WholeChipSourcePath), chip);
        var window = new MainWindow(new LaunchOptions(null, null, true, false), copyRoot);
        window.Show();
        try
        {
            var reset = window.FindControl<Button>("MachineReset");
            Assert.NotNull(reset);
            Assert.True(reset!.IsVisible);
            await WaitForBasic(window, roms.Basic);
            Assert.True(reset.IsEnabled);
            File.Delete(chip); // Reset must reuse the verified boot bytes, not resolve/reload a file.
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            var diskPath = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
            await media.InsertDiskAsync(diskPath, DiskSlot.A);
            await media.InsertDiskAsync(diskPath, DiskSlot.B);
            var before = window.LatestPresentedFrame!.MasterTick;
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (window.LatestPresentedFrame is not { } fresh || fresh.MasterTick >= before)
                await Task.Delay(10, deadline.Token);
            await WaitForBasic(window, roms.Basic);
            await media.InspectAsync();
            foreach (var slot in new[] { "A:", "B:" })
            {
                var label = media.GetLogicalDescendants().OfType<TextBlock>()
                    .Single(t => t.Text?.StartsWith(slot) == true);
                Assert.Contains("CPM22-HC.DSK", label.Text);
                output.WriteLine(label.Text!);
            }
            Assert.Equal(16384, window.BootedIf1Length);
            Assert.Contains("Real hc2000-v2 whole-chip machine running", window.FindControl<TextBlock>("RuntimeStatus")!.Text);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(copyRoot, true);
        }
    }

    private async Task WaitForBasic(MainWindow window, byte[] basic)
    {
        var text = "(no frame)";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (window.LatestPresentedFrame is { } frame)
            {
                text = BasicMachineCompositionTests.DecodeBasicScreen(frame, basic);
                if (text.Contains("HC2000") && text.Contains("I.C.E.") && text.Contains("FELIX")) break;
            }
            await Task.Delay(20, deadline.Token);
        }
        output.WriteLine("BASIC text: " + text);
    }
}
