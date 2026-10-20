using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Runtime;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Demo seats drive the app over AT-SPI: the controls a demo needs must reach the accessibility tree by name.</summary>
public sealed class AccessibilityTests
{
    [AvaloniaFact]
    public void DemoControlsExposeAccessibleNames()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var screen = ControlAutomationPeer.CreatePeerForElement(window.FindControl<VideoSurface>("Screen")!);
            Assert.Equal("HC2000 screen", screen.GetName());
            Assert.Equal(AutomationControlType.Image, screen.GetAutomationControlType());
            Assert.True(screen.IsControlElement());
            var named = window.GetLogicalDescendants().OfType<Control>()
                .Select(c => ControlAutomationPeer.CreatePeerForElement(c).GetName()).ToHashSet();
            Assert.Superset(new HashSet<string> { "ROM profile", "Start BASIC", "Reset BASIC", "Keyboard mapping" }, named);
        }
        finally { window.Close(); }
    }
}
