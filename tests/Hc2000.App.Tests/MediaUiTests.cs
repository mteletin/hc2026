using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class MediaUiTests
{
    [AvaloniaFact]
    public async Task ConnectedPanelUsesBoundaryRepliesAndNeverEnablesMissingTapeDevice()
    {
        var queue = new InputQueue(); var bridge = new MediaBridge(queue);
        var session = new MediaSession(new(new()), new(new()));
        var controller = new InputController(queue, media: (command, _) => bridge.Publish(session.Apply(command)));
        var panel = new MediaPanel(); var window = new Window { Content = panel }; window.Show();
        try
        {
            panel.Attach(bridge);
            var inspect = panel.InspectAsync(); Assert.False(inspect.IsCompleted);
            controller.ApplyPending(0, new KeyboardMatrix()); await inspect;
            var buttons = panel.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.All(buttons.Where(b => Equals(b.Content, "Insert DSK…")), b => Assert.True(b.IsEnabled));
            Assert.All(buttons.Where(b => Equals(b.Content, "Eject")), b => Assert.False(b.IsEnabled));
            var protection = panel.GetLogicalDescendants().OfType<CheckBox>().First(c => Equals(c.Content, "Write protect"));
            protection.IsChecked = true;
            controller.ApplyPending(0, new KeyboardMatrix());
            await Task.Delay(10);
            Assert.True(protection.IsChecked);
            Assert.False(Assert.Single(buttons, b => Equals(b.Content, "Play")).IsEnabled);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".DSK");
            try
            {
                await File.WriteAllBytesAsync(path, MediaSessionTests.Disk().ToArray());
                var insertion = panel.InsertDiskAsync(path, DiskSlot.B);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!insertion.IsCompleted)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    controller.ApplyPending(0, new KeyboardMatrix());
                    await Task.Delay(1, deadline.Token);
                }
                await insertion;
                Assert.True(buttons.Where(b => Equals(b.Content, "Save changes…")).Last().IsEnabled);
                Assert.False(buttons.Where(b => Equals(b.Content, "Save changes…")).First().IsEnabled);
                Assert.Contains(Path.GetFileName(path), panel.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("B:") == true).Text);
                var eject = buttons.Where(b => Equals(b.Content, "Eject")).Last();
                eject.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                controller.ApplyPending(0, new KeyboardMatrix()); await Task.Delay(10);
                Assert.False(eject.IsEnabled);
            }
            finally { File.Delete(path); }
            panel.Disconnect();
            Assert.All(buttons.Where(b => Equals(b.Content, "Insert DSK…")), b => Assert.False(b.IsEnabled));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TapValidationIsParsedOnlyAndMalformedTapIsVisible()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tap");
        var panel = new MediaPanel(); var window = new Window { Content = panel }; window.Show();
        try
        {
            await File.WriteAllBytesAsync(path, [3, 0, 0xff, 0x42, 0xbd]); // raw checksum-valid TAP data block, not guest media
            await panel.ValidateTapeAsync(path);
            Assert.Contains("Not loaded into a machine", panel.Notice);
            await File.WriteAllBytesAsync(path, [3, 0, 0xff, 0x42, 0]);
            await panel.ValidateTapeAsync(path);
            Assert.Contains("Invalid TAP checksum", panel.Notice);
            Assert.False(panel.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Play")).IsEnabled);
        }
        finally { window.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public void DisconnectedShellShowsDiskAndTapeControlsWithoutPretendingToPlay()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var buttons = window.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.Equal(2, buttons.Count(b => Equals(b.Content, "Insert DSK…")));
            Assert.Equal(2, buttons.Count(b => Equals(b.Content, "Save changes…")));
            Assert.False(Assert.Single(buttons, b => Equals(b.Content, "Play")).IsEnabled);
            Assert.False(Assert.Single(buttons, b => Equals(b.Content, "Stop")).IsEnabled);
            Assert.False(Assert.Single(window.GetLogicalDescendants().OfType<CheckBox>(), c => Equals(c.Content, "Instant load")).IsEnabled);
        }
        finally { window.Close(); }
    }
}
