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
        AvaloniaProperty.Register<AspectRatioViewport, int>(nameof(RequestedScale), 0,
            validate: value => value is >= 0 and <= 8);
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

    /// <summary>Size the machine frame was last arranged at inside this viewport (the Viewbox's
    /// arranged rect). Presentation evidence for the zoom chords and the frame layout acceptance;
    /// zero until the first arrange. The effective zoom is DisplayedSize / FrameSize.</summary>
    public Size DisplayedSize { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SurfaceProperty)
        {
            if (change.OldValue is VideoSurface oldSurface)
                oldSurface.PropertyChanged -= OnSurfacePropertyChanged;
            var newSurface = change.NewValue as VideoSurface;
            viewbox.Child = newSurface;
            if (newSurface is not null)
                newSurface.PropertyChanged += OnSurfacePropertyChanged;
        }
    }

    private void OnSurfacePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == WidthProperty || change.Property == HeightProperty)
        {
            InvalidateMeasure();
            InvalidateArrange();
        }
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
        var preferred = RequestedScale == 0 ? 1 : RequestedScale;
        if (FitToWindow)
            scale = double.IsFinite(fit) ? fit : preferred;
        else if (double.IsFinite(fit))
            // Integer zoom only; keep the request even when a smaller scale must be displayed.
            scale = Math.Max(1, Math.Min(RequestedScale == 0 ? double.PositiveInfinity : RequestedScale, Math.Floor(fit)));
        else
            scale = preferred;
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
        DisplayedSize = size;
        viewbox.Arrange(new Rect((finalSize.Width - size.Width) / 2,
            (finalSize.Height - size.Height) / 2, size.Width, size.Height));
        return finalSize;
    }
}
