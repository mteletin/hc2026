using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Hc2000.App.Runtime;

/// <summary>
/// Presentation-only host for the existing full-frame surface. Fit fills the available
/// rectangle uniformly; integer zoom is capped by the request and the finite slot,
/// with a 1x minimum. Does not own or dispose the surface.
/// </summary>
public sealed class AspectRatioViewport : Decorator
{
    public static readonly StyledProperty<VideoSurface?> SurfaceProperty =
        AvaloniaProperty.Register<AspectRatioViewport, VideoSurface?>(nameof(Surface));
    public static readonly StyledProperty<int> RequestedScaleProperty =
        AvaloniaProperty.Register<AspectRatioViewport, int>(nameof(RequestedScale), 2,
            validate: value => value is >= 1 and <= 8);
    public static readonly StyledProperty<bool> FitToWindowProperty =
        AvaloniaProperty.Register<AspectRatioViewport, bool>(nameof(FitToWindow), true);

    private readonly Viewbox viewbox = new() { Stretch = Stretch.Uniform };

    static AspectRatioViewport()
    {
        AffectsMeasure<AspectRatioViewport>(SurfaceProperty, RequestedScaleProperty, FitToWindowProperty);
        AffectsArrange<AspectRatioViewport>(SurfaceProperty, RequestedScaleProperty, FitToWindowProperty);
    }

    public AspectRatioViewport()
    {
        Child = viewbox;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public VideoSurface? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    public int RequestedScale
    {
        get => GetValue(RequestedScaleProperty);
        set => SetValue(RequestedScaleProperty, value);
    }

    public bool FitToWindow
    {
        get => GetValue(FitToWindowProperty);
        set => SetValue(FitToWindowProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SurfaceProperty) viewbox.Child = Surface;
    }

    // The frame the viewport fits to is the machine frame, not the display bitmap's logical
    // size: the X1 overlay presents a 2W x 2H bitmap at 96 dpi, so Source.Size reads 2x. The
    // control itself is kept W x H machine pixels at the set Scale (VideoSurface.SetScale),
    // and it is the Viewbox that upscales it, so the viewport measures W/Scale x H/Scale.
    private Size FrameSize => Surface is not { } surface
        ? default
        : new(surface.Width / surface.Scale, surface.Height / surface.Scale);

    private Size DisplaySize(Size available)
    {
        var frame = FrameSize;
        if (frame.Width <= 0 || frame.Height <= 0) return default;
        double fit = Math.Min(available.Width / frame.Width, available.Height / frame.Height);
        // An unbounded parent still gets a finite preferred size.
        double scale;
        if (FitToWindow)
            scale = double.IsFinite(fit) ? fit : RequestedScale;
        else if (double.IsFinite(fit))
            // Integer zoom only; keep the request even when a smaller scale must be displayed.
            scale = Math.Max(1, Math.Min(RequestedScale, Math.Floor(fit)));
        else
            scale = RequestedScale;
        return new(frame.Width * scale, frame.Height * scale);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        viewbox.Measure(Size.Infinity);
        return DisplaySize(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = DisplaySize(finalSize);
        viewbox.Arrange(new Rect((finalSize.Width - size.Width) / 2,
            (finalSize.Height - size.Height) / 2, size.Width, size.Height));
        return finalSize;
    }
}
