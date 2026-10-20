using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Hc2000.App.Runtime;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class VideoSurfaceTests
{
    [AvaloniaFact]
    public void SurfaceCopiesArgbWithIntegerNearestNeighbourScale()
    {
        using var surface = new VideoSurface();
        var pixels = Enumerable.Repeat(0xff123456u, 352 * 296).ToArray();
        surface.Present(new(42, 352, 296, pixels));
        surface.SetScale(3);
        Assert.Equal(1056, surface.Width); Assert.Equal(888, surface.Height);
        Assert.Equal(42, surface.PresentedMasterTick);
        pixels[0] = 0;
        using var buffer = ((WriteableBitmap)surface.Source!).Lock();
        Assert.Equal(unchecked((int)0xff123456), Marshal.ReadInt32(buffer.Address));
        Assert.Equal(unchecked((int)0xff123456), Marshal.ReadInt32(buffer.Address + buffer.RowBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.SetScale(0));
        Assert.Throws<ArgumentException>(() => surface.Present(new(0, 2, 2, new uint[1])));
    }
    [Fact]
    public void Stage1FixtureUsesOnlyItsCellInkAndPaperColors()
    {
        var frame = Stage1SpectrumDemo.CreateFrame();
        Assert.Equal((256,192), (frame.Width,frame.Height));
        var palette = new VideoProfile().Palette;
        for (int cy=0; cy<24; cy++) for (int cx=0; cx<32; cx++)
        {
            var colors = new HashSet<uint>();
            for (int y=cy*8; y<cy*8+8; y++) for (int x=cx*8; x<cx*8+8; x++) colors.Add(frame.Pixels.Span[y*256+x]);
            Assert.InRange(colors.Count,1,2);
            Assert.All(colors, c => Assert.Contains(c,palette));
        }
    }

    [AvaloniaFact]
    public void MainWindowHasSurfaceAndDoesNotClaimUnconnectedCpuBoot()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            Assert.Single(window.GetLogicalDescendants().OfType<VideoSurface>());
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), x => x.Text!.StartsWith("Draft:"));
        }
        finally { window.Close(); }
    }
}
