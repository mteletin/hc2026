using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Hc2000.App.Runtime;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

// hc2000-app-blank-screen-after-cold-start-150: two defects blanked the overlay-ON screen at 0.1.150.
// (1) SetScale sized the surface from the 2W x 2H mask's physical pixel count, inflating the
// overlay-ON surface to 1408 x 1184 at scale 2: a 704 x 592 window then shows only a clipped
// slice and the composed ink rows are off-screen. (2) The composed mask bitmap was presented at
// 192 dpi, and the render path drops the content of non-96-dpi bitmaps (reproduced headless: a
// 192 dpi 704 x 592 bitmap renders blank at any control size, while a 96 dpi bitmap renders 1:1).
// The fix sizes the surface from the machine-logical W x H and presents the composed bitmap at
// 96 dpi, like the field-proven overlay-OFF path, so overlay ON occupies exactly the same window
// space as overlay OFF (704 x 592 at scale 2) and the composed ink actually renders (demo-run-150-20261019).
public sealed class VideoSurfaceOverlayRenderTests
{
    private const uint Paper = 0xff804020u;
    private const uint Ink = 0xffff0000u;

    private static uint[] BasePixels()
    {
        const int w = 352, h = 296;
        var px = new uint[w * h];
        for (var i = 0; i < px.Length; i++) px[i] = Paper;
        for (var y = 180; y <= 184; y++)
            for (var x = 0; x < w; x++)
                px[y * w + x] = Ink;
        return px;
    }

    /// <summary>Shipped X1 overlay shape: straight-alpha 2x2 mask of the base frame (0.1.122 Ula.PublishOverlay).</summary>
    private static VideoFrame OverlayFrame(long tick)
    {
        const int w = 352, h = 296;
        var basePixels = BasePixels();
        var ow = w * 2;
        var oh = h * 2;
        var mask = new uint[ow * oh];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var value = basePixels[y * w + x] == Ink ? Ink : 0u;
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                        mask[(y * 2 + dy) * ow + (x * 2 + dx)] = value;
            }
        return new TextOverlayVideoFrame(tick, w, h, (uint[])basePixels.Clone(), new VideoFrame(tick, ow, oh, mask));
    }

    // The headless render capture is Argb8888: memory bytes are [R,G,B,A], so the red ink
    // 0xFFFF0000 reads back as the little-endian int 0xFF0000FF (confirmed by the 0.1.150 probe
    // histogram, doc note-20261019-115543 / /tmp/blank150-probe-out.txt).
    private const int CapturedInk = unchecked((int)0xFF0000FF);

    private static int CountInk(Avalonia.Media.Imaging.WriteableBitmap capture)
    {
        using var ctx = capture.Lock();
        var ink = 0;
        for (var y = 0; y < ctx.Size.Height; y++)
            for (var x = 0; x < ctx.Size.Width; x++)
                if (Marshal.ReadInt32(ctx.Address + y * ctx.RowBytes + x * 4) == CapturedInk)
                    ink++;
        return ink;
    }

    [AvaloniaFact]
    public void OverlayOnSurfaceKeepsTheOffSurfaceSize()
    {
        using var surface = new VideoSurface();
        surface.Present(new VideoFrame(1, 352, 296, (uint[])BasePixels().Clone()));
        Assert.Equal(704, surface.Width);
        Assert.Equal(592, surface.Height);
        surface.Present(OverlayFrame(2));
        Assert.Equal(704, surface.Width);
        Assert.Equal(592, surface.Height);
    }

    [AvaloniaFact]
    public void OverlayOnWindowRenderShowsComposedInk()
    {
        using var surface = new VideoSurface();
        var window = new Window { Width = 704, Height = 592, Content = surface };
        window.Show();
        try
        {
            // Control first: overlay OFF renders the composed ink.
            surface.Present(new VideoFrame(1, 352, 296, (uint[])BasePixels().Clone()));
            window.UpdateLayout();
            var offCapture = window.CaptureRenderedFrame();
            Assert.NotNull(offCapture);
            Assert.True(CountInk(offCapture) > 0, "overlay-OFF control lost its composed ink");
            // Cold-start shape: the same surface then presents the overlay-ON frame.
            surface.Present(OverlayFrame(2));
            window.UpdateLayout();
            var onCapture = window.CaptureRenderedFrame();
            Assert.NotNull(onCapture);
            Assert.True(CountInk(onCapture) > 0, "overlay-ON render dropped the composed ink (blank screen)");
        }
        finally
        {
            window.Close();
        }
    }
}
