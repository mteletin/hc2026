using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Debugging;
using Hc2000.Machine.Memory;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Hc2000.App.Tests.DebuggerTestApplication))]

namespace Hc2000.App.Tests;

public static class DebuggerTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class DebuggerWindowTests
{
    [AvaloniaFact]
    public void DisconnectedWindowDoesNotPretendCpuIsRunningAndDisablesRuntimeCommands()
    {
        var window = new DebuggerWindow(); window.Show();
        try
        {
            var controls = window.GetLogicalDescendants().ToArray();
            Assert.Contains(controls.OfType<TextBlock>(), t => t.Text?.Contains("Selected machine awaiting snapshot.") == true);
            var resume = Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Resume"));
            Assert.False(resume.IsEnabled);
            Assert.False(Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Pause machine")).IsEnabled);
            Assert.False(Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Record")).IsEnabled);
            Assert.Contains(controls.OfType<TextBox>(), t => t.Text?.Contains("Unavailable: no boundary snapshot") == true);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CopiedStateRendersAndControlsOnlyEnqueueRequests()
    {
        var commands = new List<DebuggerCommand>();
        var window = new DebuggerWindow(commands.Add); window.Show();
        try
        {
            var memory = new HcMemory(new MemoryProfile(), new byte[16384], new byte[16384], new byte[16384]);
            var view = new DebuggerSession("synthetic window fixture").Boundary(new(0x1234, 0xffff), memory, null, 42);
            window.Publish(view, generation: 1, sequence: 1, paused: false);
            var controls = window.GetLogicalDescendants().ToArray();
            Assert.Contains(controls.OfType<TextBlock>(), t => t.Text?.Contains("last captured boundary T=42") == true);
            var tabs = Assert.Single(controls.OfType<TabControl>());
            tabs.SelectedIndex = 2;
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBox>(), t => t.Text?.Contains("PC=1234 SP=FFFF") == true);
            tabs.SelectedIndex = 3;
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBox>(), t => t.Text?.Contains("FDC unavailable") == true);
            var resume = Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Resume"));
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(DebuggerCommandKind.Resume, Assert.Single(commands).Kind);
        }
        finally { window.Close(); }
    }
}
