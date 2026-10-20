using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class VideoShellRegressionTests
{
    [AvaloniaFact]
    public void DisconnectedShellStillOffersThe352By296IntegerScaledVideoArea()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var image = Assert.Single(window.GetLogicalDescendants().OfType<Image>());
            Assert.Equal(352 * 2, image.Width);
            Assert.Equal(296 * 2, image.Height);
        }
        finally { window.Close(); }
    }
}
