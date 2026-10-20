using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Hc2000.App.Runtime;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Leaf B (design-app-consumer-634443): VideoSurface.Present composes the shipped
/// 0.1.122 TextOverlayVideoFrame mask; off default stays the base-only byte-identical path.</summary>
public sealed class X1VideoSurfaceOverlayTests
{
    private static int Pixel(VideoSurface surface, int x, int y)
    {
        using var buffer = ((WriteableBitmap)surface.Source!).Lock();
        return Marshal.ReadInt32(buffer.Address + y * buffer.RowBytes + x * 4);
    }

    /// <summary>Mask built with the producer's real semantics: 2x2-replicated blocks,
    /// paper ARGB 0, opaque ink ARGB (same values Ula.PublishOverlay publishes).</summary>
    private static (VideoFrame frame, uint[] mask) BuildOverlayFrame(
        int w, int h, long tick, Func<int, int, uint> basePixel, Func<int, int, uint> ink)
    {
        var basePixels = new uint[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                basePixels[y * w + x] = basePixel(x, y);
        var ow = w * 2;
        var oh = h * 2;
        var mask = new uint[ow * oh];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var value = ink(x, y);
                foreach (var dy in new[] { 0, 1 })
                    foreach (var dx in new[] { 0, 1 })
                        mask[(y * 2 + dy) * ow + x * 2 + dx] = value;
            }
        return (new TextOverlayVideoFrame(tick, w, h, basePixels, new VideoFrame(tick, ow, oh, mask)), mask);
    }

    [AvaloniaFact]
    public void OverlayComposesRealMaskInkOverTransparentPaper()
    {
        var (frame, _) = BuildOverlayFrame(4, 3, 7,
            (x, y) => 0xff010101u + (uint)(y * 4 + x),
            (x, y) => (x + y) % 2 == 0 ? 0xff336699u : 0u);
        using var surface = new VideoSurface();
        surface.Present(frame);
        var bitmap = (WriteableBitmap)surface.Source!;
        Assert.Equal(new PixelSize(8, 6), bitmap.PixelSize);
        Assert.Equal(7, surface.PresentedMasterTick);
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 8; x++)
            {
                var ink = (x / 2 + y / 2) % 2 == 0;
                var expected = ink ? 0xff336699u : 0xff010101u + (uint)((y / 2) * 4 + x / 2);
                Assert.Equal(unchecked((int)expected), Pixel(surface, x, y));
            }
    }

    [AvaloniaFact]
    public void OverlayCompositesPartialMaskAlphaStraight()
    {
        // Paper 0xff112233 under a straight (non-premultiplied) 0x80-alpha red ink:
        // r = (255*128 + 17*127 + 127) / 255 = 136, g = (0*128 + 34*127 + 127) / 255 = 17, b = 25.
        var (frame, _) = BuildOverlayFrame(1, 1, 1,
            (_, _) => 0xff112233u, (_, _) => 0x80ff0000u);
        using var surface = new VideoSurface();
        surface.Present(frame);
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++)
                Assert.Equal(unchecked((int)0xff881119u), Pixel(surface, x, y));
    }

    [AvaloniaFact]
    public void MalformedOverlayFailsClosedKeepingPriorPresentation()
    {
        var (good, mask) = BuildOverlayFrame(4, 3, 7,
            (_, _) => 0xff224455u, (_, _) => 0u);
        using var surface = new VideoSurface();
        surface.Present(good);
        var source = surface.Source;
        var tick = surface.PresentedMasterTick;
        var badBase = new uint[4 * 3];
        Array.Fill(badBase, 0xff224455u);
        // mask width 9 != 2*4
        Assert.Throws<ArgumentException>(() => surface.Present(new TextOverlayVideoFrame(7, 4, 3, badBase,
            new VideoFrame(7, 9, 6, new uint[9 * 6]))));
        // mask height 5 != 2*3
        Assert.Throws<ArgumentException>(() => surface.Present(new TextOverlayVideoFrame(7, 4, 3, badBase,
            new VideoFrame(7, 8, 5, new uint[8 * 5]))));
        // master tick disagrees with the base frame
        Assert.Throws<ArgumentException>(() => surface.Present(new TextOverlayVideoFrame(7, 4, 3, badBase,
            new VideoFrame(8, 8, 6, (uint[])mask.Clone()))));
        // mask pixel count 40 != 8*6
        Assert.Throws<ArgumentException>(() => surface.Present(new TextOverlayVideoFrame(7, 4, 3, badBase,
            new VideoFrame(7, 8, 6, new uint[40]))));
        Assert.Same(source, surface.Source);
        Assert.Equal(tick, surface.PresentedMasterTick);
        Assert.Equal(unchecked((int)0xff224455u), Pixel(surface, 0, 0));
        Assert.Equal(unchecked((int)0xff224455u), Pixel(surface, 7, 5));
    }

    [AvaloniaFact]
    public void OffPathPresentsBasePixelsByteIdenticalAt96Dpi()
    {
        const int w = 352, h = 296;
        var pixels = new uint[w * h];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = 0xa5000000u | (uint)i;
        using var surface = new VideoSurface();
        surface.Present(new VideoFrame(99, w, h, pixels));
        var bitmap = (WriteableBitmap)surface.Source!;
        Assert.Equal(new PixelSize(w, h), bitmap.PixelSize);
        Assert.Equal(new Vector(96, 96), bitmap.Dpi);
        Assert.Equal(new Size(w, h), bitmap.Size);
        Assert.Equal(99, surface.PresentedMasterTick);
        using var buffer = bitmap.Lock();
        for (var y = 0; y < h; y++)
        {
            var row = new byte[w * 4];
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, w * 4);
            for (var x = 0; x < w; x++)
            {
                var p = pixels[y * w + x]; int j = x * 4;
                Assert.Equal((byte)p, row[j]);
                Assert.Equal((byte)(p >> 8), row[j + 1]);
                Assert.Equal((byte)(p >> 16), row[j + 2]);
                Assert.Equal((byte)(p >> 24), row[j + 3]);
            }
        }
    }

    [AvaloniaFact]
    public void OverlayPresentationKeepsLogicalSizeAndSetScaleContract()
    {
        var (frame, _) = BuildOverlayFrame(4, 3, 7,
            (_, _) => 0xff010101u, (_, _) => 0xff336699u);
        using var surface = new VideoSurface();
        surface.Present(frame);
        var bitmap = (WriteableBitmap)surface.Source!;
        // 2W x 2H pixels at 96 dpi: the render path drops non-96-dpi bitmap content (the overlay-ON
        // blank screen at 0.1.150; hc2000-app-blank-screen-after-cold-start-150), so the
        // machine-logical W x H the surface occupies is carried by the surface itself, not by a
        // 192 dpi display bitmap.
        Assert.Equal(new Vector(96, 96), bitmap.Dpi);
        Assert.Equal(new Size(8, 6), bitmap.Size);
        surface.SetScale(3);
        Assert.Equal(3, surface.Scale);
        // Machine-logical W x H (4 x 3) at scale 3: the surface occupies 12 x 9, the same space
        // the overlay-OFF path gives the same frame (the old 24 x 18 physical sizing was the
        // 0.1.150 blank-screen bug).
        Assert.Equal(12, surface.Width);
        Assert.Equal(9, surface.Height);
        Assert.Equal(7, surface.PresentedMasterTick);
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.SetScale(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.SetScale(9));
        // A repeated presentation reuses the single UI-owned bitmap.
        Assert.Same(surface.Source, surface.Source);
        var source = surface.Source;
        surface.Present(frame);
        Assert.Same(source, surface.Source);
        Assert.Equal(7, surface.PresentedMasterTick);
    }
}
