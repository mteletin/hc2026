using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Hc2000.App.Runtime;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class AspectRatioViewportTests
{
    [AvaloniaTheory]
    [InlineData(176, 400, 176, 148)]
    [InlineData(1000, 296, 352, 296)]
    [InlineData(1408, 1184, 1408, 1184)]
    [InlineData(0, 0, 0, 0)]
    public void FitPreservesFullFrameAndCenters(double width, double height, double expectedWidth, double expectedHeight)
    {
        using var surface = new VideoSurface();
        var viewport = new AspectRatioViewport { Surface = surface };
        Layout(viewport, width, height);
        var box = Assert.IsType<Viewbox>(viewport.Child);
        Assert.Equal(new Rect((width - expectedWidth) / 2, (height - expectedHeight) / 2,
            expectedWidth, expectedHeight), box.Bounds);
        Assert.Equal(Stretch.Uniform, box.Stretch);
        Assert.Equal(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(surface));
        Assert.Equal(2, surface.Scale);
        Assert.Equal(704, surface.Width);
        Assert.Equal(592, surface.Height);
    }

    [AvaloniaFact]
    public void RequestedIntegerZoomIsRetainedAcrossFitAndSmallWindow()
    {
        using var surface = new VideoSurface();
        var viewport = new AspectRatioViewport { Surface = surface, RequestedScale = 3, FitToWindow = false };
        Layout(viewport, 1408, 1184);
        Assert.Equal(new Rect(176, 148, 1056, 888), viewport.Child!.Bounds);
        // A slot smaller than the 1x frame keeps the full frame at 1x (letterboxed),
        // never a fractional shrink below 1x.
        Layout(viewport, 176, 148);
        Assert.Equal(new Size(352, 296), viewport.Child.Bounds.Size);
        Assert.Equal(3, viewport.RequestedScale);
        viewport.FitToWindow = true;
        Layout(viewport, 1408, 1184);
        Assert.Equal(new Size(1408, 1184), viewport.Child.Bounds.Size);
        viewport.FitToWindow = false;
        Layout(viewport, 1408, 1184);
        Assert.Equal(new Size(1056, 888), viewport.Child.Bounds.Size);
    }

    [AvaloniaTheory]
    [InlineData(600, 400, 352, 296)]   // fit 1.35 floors to 1: letterbox, not a fractional zoom
    [InlineData(704, 592, 704, 592)]   // exact 2x slot keeps the requested zoom
    [InlineData(1000, 800, 704, 592)]  // larger than 704x592 keeps the requested zoom
    public void FiniteSlotCapsIntegerScaleAtRequestedZoom(double slotWidth, double slotHeight, double expectedWidth, double expectedHeight)
    {
        using var surface = new VideoSurface();
        var viewport = new AspectRatioViewport { Surface = surface, RequestedScale = 2, FitToWindow = false };
        Layout(viewport, slotWidth, slotHeight);
        var box = Assert.IsType<Viewbox>(viewport.Child);
        Assert.Equal(new Size(expectedWidth, expectedHeight), box.Bounds.Size);
        Assert.True(box.Bounds.Width <= slotWidth + .001 && box.Bounds.Height <= slotHeight + .001,
            $"slot {slotWidth}x{slotHeight} got {box.Bounds.Size}");
        Assert.Equal(352.0 / 296, box.Bounds.Width / box.Bounds.Height, 3);
        Assert.Equal(2, viewport.RequestedScale); // the ceiling is never rewritten by fitting
    }

    [AvaloniaFact]
    public void RequestedScaleIsACeilingNotAFloorOnAFiniteSlot()
    {
        using var surface = new VideoSurface();
        var viewport = new AspectRatioViewport { Surface = surface, RequestedScale = 4, FitToWindow = false };
        Layout(viewport, 1000, 800); // fit 2.70 floors to 2; min(4, 2) stays 2
        Assert.Equal(new Size(704, 592), Assert.IsType<Viewbox>(viewport.Child).Bounds.Size);
    }

    [AvaloniaFact]
    public void OverlayOnSurfaceMeasuresFromTheMachineLogicalFrame()
    {
        // X1 overlay-ON: the display bitmap is 2W x 2H at 96 dpi, but the viewport still
        // sizes from the machine frame (352x296), so compact 2x is 704x592, not 1408x1184
        // (the clipped-slice blank screen of hc2000-app-blank-screen-after-cold-start-150).
        const int w = 352, h = 296;
        using var surface = new VideoSurface();
        surface.Present(new TextOverlayVideoFrame(1, w, h, new uint[w * h],
            new VideoFrame(1, w * 2, h * 2, new uint[w * 2 * h * 2])));
        var viewport = new AspectRatioViewport { Surface = surface, RequestedScale = 2, FitToWindow = false };
        Layout(viewport, 1408, 1184);
        Assert.Equal(new Size(704, 592), viewport.Child!.Bounds.Size);
        // Fit mode fills the slot at the frame aspect either way.
        viewport.FitToWindow = true;
        Layout(viewport, 1408, 1184);
        Assert.Equal(new Size(1408, 1184), viewport.Child.Bounds.Size);
    }

    [AvaloniaFact]
    public void UnboundedMeasureUsesFiniteRequestedZoomAndEmptyHostIsSafe()
    {
        var viewport = new AspectRatioViewport();
        viewport.Measure(Size.Infinity);
        Assert.Equal(default, viewport.DesiredSize);
        Layout(viewport, 100, 100);
        using var surface = new VideoSurface();
        viewport.Surface = surface;
        viewport.RequestedScale = 4;
        viewport.Measure(Size.Infinity);
        Assert.Equal(new Size(1408, 1184), viewport.DesiredSize);
        Assert.Throws<ArgumentException>(() => viewport.RequestedScale = 0);
        Assert.Throws<ArgumentException>(() => viewport.RequestedScale = 9);
    }

    [AvaloniaFact]
    public void ResizingUsesPublishedFrameDimensionsWithoutChangingPixelsOrTick()
    {
        using var surface = new VideoSurface();
        var viewport = new AspectRatioViewport { Surface = surface };
        var pixels = Enumerable.Repeat(0xff123456u, 352 * 296).ToArray();
        pixels[0] = 0xffabcdef;
        pixels[^1] = 0xff654321;
        surface.Present(new(42, 352, 296, pixels));
        var bitmap = Assert.IsType<WriteableBitmap>(surface.Source);
        Layout(viewport, 176, 148);
        Layout(viewport, 1408, 1184);
        Assert.Same(bitmap, surface.Source);
        using (var buffer = bitmap.Lock())
        {
            Assert.Equal(unchecked((int)pixels[0]), Marshal.ReadInt32(buffer.Address));
            Assert.Equal(unchecked((int)pixels[^1]), Marshal.ReadInt32(buffer.Address + 295 * buffer.RowBytes + 351 * 4));
        }
        Assert.Equal(42, surface.PresentedMasterTick);
        var window = new Window { Content = viewport, Width = 512, Height = 500 };
        window.Show();
        surface.Present(new(43, 256, 192, new uint[256 * 192]));
        window.UpdateLayout();
        window.Close();
        Layout(viewport, 512, 500);
        Assert.Equal(new Rect(0, 58, 512, 384), viewport.Child!.Bounds);
        var transform = surface.TransformToVisual(viewport)!.Value;
        var origin = transform.Transform(new Point(0, 0));
        var end = transform.Transform(new Point(surface.Bounds.Width, surface.Bounds.Height));
        Assert.Equal(new Point(0, 58), origin);
        Assert.Equal(new Point(512, 442), end);
    }

    [AvaloniaFact]
    public void ReplacementAndRemovalDoNotDisposeOrRetainOldSurface()
    {
        using var first = new VideoSurface();
        using var second = new VideoSurface();
        var viewport = new AspectRatioViewport { Surface = first };
        viewport.Surface = second;
        Assert.Null(first.GetVisualParent());
        Assert.Same(second, Assert.IsType<Viewbox>(viewport.Child).Child);
        viewport.Surface = null;
        Assert.Null(second.GetVisualParent());
        first.Present(new(1, 1, 1, new uint[1]));
        second.Present(new(2, 1, 1, new uint[1]));
        Assert.NotNull(first.Source);
        Assert.NotNull(second.Source);
    }

    private static void Layout(AspectRatioViewport viewport, double width, double height)
    {
        viewport.Measure(new Size(width, height));
        viewport.Arrange(new Rect(0, 0, width, height));
    }
}
