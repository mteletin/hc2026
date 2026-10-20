using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Hc2000.Machine.Video;

namespace Hc2000.App.Runtime;

/// <summary>UI-owned bitmap. Publication copies ARGB values; no worker-owned storage is retained.</summary>
public sealed class VideoSurface : Image, IDisposable
{
    private WriteableBitmap? bitmap;
    // The machine frame's logical size (W x H machine pixels) the surface occupies. The display
    // bitmap is 2W x 2H for the X1 overlay, so its size is not what the control sizes from
    // (hc2000-app-blank-screen-after-cold-start-150).
    private Size _machineLogical = new(352, 296);
    public int Scale { get; private set; } = 2;
    public long? PresentedMasterTick { get; private set; }
    public VideoSurface()
    {
        Width = 352 * Scale; Height = 296 * Scale;
        Stretch = Stretch.None;
        Focusable = true;
        PointerPressed += (_, _) => Focus();
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }
    // Image has no peer of its own, so the default one hides it from accessibility; expose the screen as an image.
    protected override AutomationPeer OnCreateAutomationPeer() => new SurfaceAutomationPeer(this);
    private sealed class SurfaceAutomationPeer(VideoSurface owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
    public void SetScale(int scale)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (scale < 1 || scale > 8) throw new ArgumentOutOfRangeException(nameof(scale));
        Scale = scale;
        // Size the control from the machine frame's logical size (W x H machine pixels), not from
        // the display bitmap's size: at 0.1.150 the overlay path used the 2W x 2H mask's physical
        // pixel count, inflating the overlay-ON surface to 1408 x 1184 at scale 2, so a 704 x 592
        // window showed only a clipped slice and the composed ink rows fell off-screen (blank
        // screen, hc2000-app-blank-screen-after-cold-start-150).
        Width = _machineLogical.Width * scale;
        Height = _machineLogical.Height * scale;
        Stretch = Stretch.Fill;
    }
    public void Present(VideoFrame frame)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length != checked(frame.Width * frame.Height))
            throw new ArgumentException("Frame dimensions/pixel count mismatch.", nameof(frame));
        // The shipped X1 overlay (0.1.122 Ula.PublishOverlay) is a straight-alpha 2x2 mask of the base frame;
        // the renderer composes it, never decodes guest text. Validated before any mutation: a malformed mask
        // fails closed and the prior presentation stays untouched (ADR hc2000-x1-app-adoption-v1).
        var mask = frame as TextOverlayVideoFrame is { } t ? t.TextOverlay : null;
        if (mask is not null && (mask.Width != frame.Width * 2 || mask.Height != frame.Height * 2
            || mask.Pixels.Length != checked(mask.Width * mask.Height) || mask.MasterTick != frame.MasterTick))
            throw new ArgumentException("TextOverlay mask dimensions, pixel count or MasterTick mismatch.", nameof(frame));
        var width = mask is null ? frame.Width : mask.Width;
        var height = mask is null ? frame.Height : mask.Height;
        // The display bitmap is 96 dpi on both paths: the render path drops the content of
        // non-96-dpi bitmaps (the overlay-ON blank screen at 0.1.150; headless repro in
        // hc2000-app-blank-screen-after-cold-start-150), so the machine-logical W x H the surface
        // occupies rides _machineLogical instead of a 192 dpi display trick. The composed mask
        // bitmap stays 2W x 2H pixels; at scale s it is stretched s/2 onto the s x (W x H) control.
        _machineLogical = new Size(frame.Width, frame.Height);
        if (bitmap is null || bitmap.PixelSize != new PixelSize(width, height))
        {
            Source = null; bitmap?.Dispose();
            bitmap = new(new(width, height), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Source = bitmap; SetScale(Scale);
        }
        // ARGB uint -> BGRA bytes on little-endian hosts; explicit conversion also supports big endian.
        var bytes = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = mask is null
                    ? frame.Pixels.Span[y * frame.Width + x]
                    : Compose(frame.Pixels.Span[(y / 2) * frame.Width + (x / 2)], mask.Pixels.Span[y * width + x]);
                int j = (y * width + x) * 4;
                bytes[j] = (byte)p; bytes[j + 1] = (byte)(p >> 8);
                bytes[j + 2] = (byte)(p >> 16); bytes[j + 3] = (byte)(p >> 24);
            }
        using (var target = bitmap.Lock())
            for (var y = 0; y < height; y++)
                Marshal.Copy(bytes, y * width * 4, target.Address + y * target.RowBytes, width * 4);
        PresentedMasterTick = frame.MasterTick; InvalidateVisual();
    }
    /// <summary>Straight (non-premultiplied) alpha blend of the mask pixel over the base pixel.
    /// The shipped producer emits only alpha 0 (transparent paper) or 0xFF (opaque ink); partial
    /// alpha is blended per channel as a defensive contract, and output alpha is forced opaque
    /// because the surface bitmap is AlphaFormat.Opaque.</summary>
    private static uint Compose(uint basePixel, uint maskPixel)
    {
        var a = maskPixel >> 24;
        if (a == 0) return basePixel;
        if (a == 0xFF) return maskPixel;
        uint Blend(byte m, byte b) => (uint)((m * a + b * (byte)(255 - a) + 127) / 255);
        return 0xFF000000u | (Blend((byte)(maskPixel >> 16), (byte)(basePixel >> 16)) << 16)
                   | (Blend((byte)(maskPixel >> 8), (byte)(basePixel >> 8)) << 8)
                   | Blend((byte)maskPixel, (byte)basePixel);
    }
    /// <summary>Discard the prior session's presentation; this does not synthesize a frame.</summary>
    public void ClearPresentation()
    {
        Dispatcher.UIThread.VerifyAccess();
        Source = null; bitmap?.Dispose(); bitmap = null; PresentedMasterTick = null;
        _machineLogical = new Size(352, 296); // back to the default frame, as before 0.1.150
    }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess(); ClearPresentation();
    }
}
