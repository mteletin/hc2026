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
        Width = (bitmap?.PixelSize.Width ?? 352) * scale;
        Height = (bitmap?.PixelSize.Height ?? 296) * scale;
        Stretch = Stretch.Fill;
    }
    public void Present(VideoFrame frame)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length != checked(frame.Width * frame.Height))
            throw new ArgumentException("Frame dimensions/pixel count mismatch.", nameof(frame));
        if (bitmap is null || bitmap.PixelSize != new PixelSize(frame.Width, frame.Height))
        {
            Source = null; bitmap?.Dispose();
            bitmap = new(new(frame.Width, frame.Height), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Source = bitmap; SetScale(Scale);
        }
        // ARGB uint -> BGRA bytes on little-endian hosts; explicit conversion also supports big endian.
        var bytes = new byte[checked(frame.Pixels.Length * 4)];
        for (var i = 0; i < frame.Pixels.Length; i++)
        {
            uint p = frame.Pixels.Span[i]; int j = i * 4;
            bytes[j] = (byte)p; bytes[j + 1] = (byte)(p >> 8);
            bytes[j + 2] = (byte)(p >> 16); bytes[j + 3] = (byte)(p >> 24);
        }
        using (var target = bitmap.Lock())
            for (var y = 0; y < frame.Height; y++)
                Marshal.Copy(bytes, y * frame.Width * 4, target.Address + y * target.RowBytes, frame.Width * 4);
        PresentedMasterTick = frame.MasterTick; InvalidateVisual();
    }
    /// <summary>Discard the prior session's presentation; this does not synthesize a frame.</summary>
    public void ClearPresentation()
    {
        Dispatcher.UIThread.VerifyAccess();
        Source = null; bitmap?.Dispose(); bitmap = null; PresentedMasterTick = null;
    }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess(); ClearPresentation();
    }
}
