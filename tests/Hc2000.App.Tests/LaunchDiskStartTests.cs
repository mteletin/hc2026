using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Launch-option consumption: --disk via the MediaBridge after compose, --start without a press, --help.</summary>
public sealed class LaunchDiskStartTests
{
    [Fact]
    public void HelpExitsZeroWithUsage()
    {
        var (code, stdout, stderr) = RunMain("--help");
        Assert.Equal(0, code);
        Assert.Contains(LaunchStartup.Usage, stdout, StringComparison.Ordinal);
        Assert.Equal("", stderr);
    }

    [Fact]
    public void UnknownOptionExitsNonZeroWithUsage()
    {
        var (code, stdout, stderr) = RunMain("--not-a-flag");
        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.Contains("Unknown option: --not-a-flag.", stderr, StringComparison.Ordinal);
        Assert.Contains(LaunchStartup.Usage, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDiskAppliesNothingAndIsDeterministic()
    {
        var bridge = new MediaBridge(new InputQueue());
        var calls = 0;
        Task Insert(DiskSlot slot, string path) { calls++; return Task.CompletedTask; }
        var once = await LaunchStartup.ApplyDisksAsync(new LaunchOptions(null, null, true, false), Insert);
        var twice = await LaunchStartup.ApplyDisksAsync(null, Insert);
        Assert.Null(once);
        Assert.Null(twice);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MissingDiskIsAStatusLineAndIsNotEnqueued()
    {
        var calls = 0;
        Task Insert(DiskSlot slot, string path) { calls++; return Task.CompletedTask; }
        var status = await LaunchStartup.ApplyDisksAsync(
            new LaunchOptions("A:/definitely/not/here.dsk", null, false, false), Insert);
        Assert.NotNull(status);
        Assert.StartsWith("Disk A:", status, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [AvaloniaFact]
    public async Task StartOmitsThePressAndInsertsDiskBeforeAnyTypedInput()
    {
        var missing = RomSelectionFixture.MissingAssets();
        Assert.Null(missing);
        var diskPath = Path.Combine(RomSelectionFixture.RepositoryRoot(), "disks", "cpm", "CPM22-HC.DSK");
        Assert.True(File.Exists(diskPath) && new FileInfo(diskPath).Length > 256, "CPM22-HC.DSK is not materialized.");
        var options = LaunchOptions.Parse(["--disk", "A:" + diskPath, "--start"]).Options;
        Assert.NotNull(options);
        var window = new MainWindow(options);
        window.Show();
        try
        {
            // Window open composes the machine and applies --disk. No panel select, no Start press.
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            string label = "";
            while (DateTime.UtcNow < deadline)
            {
                label = string.Join("\n", media.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Select(b => b.Text));
                if (label.Contains("CPM22-HC.DSK", StringComparison.Ordinal)) break;
                await Task.Delay(20);
            }
            Assert.Contains("CPM22-HC.DSK", label, StringComparison.Ordinal);
            Assert.Equal(0, window.InputQueue.BoundaryCount);
            window.InputQueue.Enqueue(new KeyTransition("launch-after-reply", new KeyChord(HcKey.P), true));
            Assert.Equal(1, window.InputQueue.BoundaryCount);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [AvaloniaFact]
    public async Task StartWithoutDiskLeavesTheDriveEmpty()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var window = new MainWindow(new LaunchOptions(null, null, true, false));
        window.Show();
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            var status = "";
            while (DateTime.UtcNow < deadline)
            {
                status = window.FindControl<Avalonia.Controls.TextBlock>("RuntimeStatus")!.Text ?? "";
                if (status.Contains("Real hc2000-v2 whole-chip machine running", StringComparison.Ordinal)) break;
                await Task.Delay(20);
            }
            Assert.Contains("Real hc2000-v2 whole-chip machine running", status, StringComparison.Ordinal);
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            var labels = "";
            while (DateTime.UtcNow < deadline)
            {
                labels = string.Join("\n", media.GetLogicalDescendants().OfType<TextBlock>().Select(b => b.Text));
                if (labels.Contains("empty", StringComparison.Ordinal)) break;
                await Task.Delay(20);
            }
            Assert.Contains("empty", labels, StringComparison.Ordinal);
            Assert.DoesNotContain("CPM22-HC.DSK", labels, StringComparison.Ordinal);
            Assert.Equal(0, window.InputQueue.BoundaryCount);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static (int Code, string Stdout, string Stderr) RunMain(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            return (Program.Main(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }
}
