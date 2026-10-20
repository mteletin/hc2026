using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Input;
using Hc2000.App.Snapshots;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>The .hcs slice of the 06d panel: save/load of full machine snapshots through the
/// public Machine.Save/Load API, driven over the shared input queue at ordered boundaries.
/// Load is all-or-nothing onto the current machine state; failures surface as the panel's
/// notice and leave the machine untouched.</summary>
public sealed class HcsUiTests
{
    [AvaloniaFact]
    public void DisconnectedShellShowsSnapshotControlsDisabled()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var panel = window.FindControl<HcsPanel>("SnapshotControls")!;
            Assert.NotNull(panel);
            Assert.StartsWith("Snapshot disconnected", panel.Notice);
            var buttons = panel.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.Contains(buttons, b => Equals(b.Content, "Save snapshot…"));
            Assert.Contains(buttons, b => Equals(b.Content, "Load snapshot…"));
            Assert.All(buttons, b => Assert.False(b.IsEnabled));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConnectedPanelSavesAndLoadsHcsThroughBoundaryReplies()
    {
        using var nativeDomain = new HddColdAdmissionTests.PrivateScopedDomain();
        var queue = new InputQueue();
        var bridge = new HcsBridge(queue);
        var wiring = HcsMachineFixture.Compose();
        var session = new HcsSession(wiring.Machine);
        var controller = new InputController(queue, snapshot: (command, _) => bridge.Publish(session.Apply(command)));

        var panel = new HcsPanel();
        var window = new Window { Content = panel };
        window.Show();
        var path = Path.Combine(Path.GetTempPath(), "hc2000-" + Guid.NewGuid() + ".hcs");
        var badPath = Path.Combine(Path.GetTempPath(), "hc2000-" + Guid.NewGuid() + ".hcs");
        try
        {
            panel.Attach(bridge);
            Assert.Equal("Snapshot connected.", panel.Notice);
            var buttons = panel.GetLogicalDescendants().OfType<Button>().ToArray();
            var save = Assert.Single(buttons, b => Equals(b.Content, "Save snapshot…"));
            var load = Assert.Single(buttons, b => Equals(b.Content, "Load snapshot…"));
            var discard = Assert.Single(panel.GetLogicalDescendants().OfType<CheckBox>());
            Assert.True(save.IsEnabled);
            Assert.True(load.IsEnabled);
            Assert.True(discard.IsEnabled);

            // Save captures the machine at the next ordered boundary into the file.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var saving = panel.SaveSnapshotAsync(path);
            Assert.False(saving.IsCompleted); // Owner has not processed the queued capture.
            Assert.False(save.IsEnabled);
            Assert.False(load.IsEnabled);
            Assert.False(discard.IsEnabled);
            while (!saving.IsCompleted)
            {
                deadline.Token.ThrowIfCancellationRequested();
                controller.ApplyPending(0, new KeyboardMatrix());
                await Task.Delay(1, deadline.Token);
            }
            await saving;
            Assert.True(discard.IsEnabled);
            Assert.Contains(Path.GetFileName(path), panel.Notice);
            Assert.True(File.Exists(path));
            Assert.Equal(wiring.Machine.Save(), await File.ReadAllBytesAsync(path));

            // Load re-adopts the same file through the same boundary path.
            var loading = panel.LoadSnapshotAsync(path);
            while (!loading.IsCompleted)
            {
                deadline.Token.ThrowIfCancellationRequested();
                controller.ApplyPending(0, new KeyboardMatrix());
                await Task.Delay(1, deadline.Token);
            }
            await loading;
            Assert.Equal("Snapshot loaded at an emulation boundary.", panel.Notice);
            Assert.Equal(wiring.Machine.Save(), await File.ReadAllBytesAsync(path));

            // A malformed .hcs fails visibly and leaves the machine untouched.
            await File.WriteAllBytesAsync(badPath, System.Text.Encoding.UTF8.GetBytes("not a snapshot"));
            var failing = panel.LoadSnapshotAsync(badPath);
            while (!failing.IsCompleted)
            {
                deadline.Token.ThrowIfCancellationRequested();
                controller.ApplyPending(0, new KeyboardMatrix());
                await Task.Delay(1, deadline.Token);
            }
            await failing;
            Assert.StartsWith("Snapshot action failed:", panel.Notice);
            Assert.Equal(wiring.Machine.Save(), await File.ReadAllBytesAsync(path));
            Assert.True(save.IsEnabled);
            Assert.True(load.IsEnabled);
            Assert.True(discard.IsEnabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(badPath)) File.Delete(badPath);
            panel.Disconnect();
            window.Close();
        }
        Assert.All(panel.GetLogicalDescendants().OfType<Button>(), b => Assert.False(b.IsEnabled));
        Assert.Equal("Snapshot runtime disconnected.", panel.Notice);
    }
}
