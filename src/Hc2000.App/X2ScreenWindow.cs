using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Hc2000.App.Configuration;
using Hc2000.App.Runtime;

namespace Hc2000.App;

/// <summary>The X2 VGA card's 320x240 surface as a second screen beside the unchanged main display
/// (hc2000-x2-second-screen). Presentation only: it owns no runtime. The main window feeds it the
/// selected machine's X2 frame each tick and decides when it shows; closing it only hides it.</summary>
public sealed class X2ScreenWindow : Window
{
    // The zoom picker's rows, in order: fit to the window, or integer zoom where 0 is the largest
    // integer that fits and 1–4 cap it (the main screen's own semantics).
    internal static readonly (string Label, bool FitToWindow, int Scale)[] Zooms =
    [
        ("Fit window", true, 0), ("Largest integer", false, 0),
        ("1×", false, 1), ("2×", false, 2), ("3×", false, 3), ("4×", false, 4),
    ];

    private readonly ComboBox zoom;
    private bool binding;

    public X2ScreenWindow()
    {
        Title = "X2 VGA";
        // 2x the 320x240 surface plus the zoom bar and the surround margin.
        Width = 656;
        Height = 536;
        MinWidth = 336;
        MinHeight = 296;
        Surface = new VideoSurface { Name = "X2Screen", BorderMode = ScreenBorderMode.Full, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(Surface, "X2 VGA screen");
        Viewport = new AspectRatioViewport { Name = "X2Viewport", FitToWindow = false, Surface = Surface };
        zoom = new ComboBox { Name = "X2ZoomBox", MinWidth = 150 };
        AutomationProperties.SetName(zoom, "VGA screen zoom");
        foreach (var row in Zooms) zoom.Items.Add(new ComboBoxItem { Content = row.Label });
        zoom.SelectionChanged += (_, _) =>
        {
            if (binding || zoom.SelectedIndex < 0) return;
            var row = Zooms[zoom.SelectedIndex];
            Apply(row.FitToWindow, row.Scale);
            ZoomChanged?.Invoke(row.FitToWindow, row.Scale);
        };
        zoom.DropDownClosed += (_, _) => Surface.Focus();

        var bar = new Border { Classes = { "shellHeader" }, Padding = new Thickness(8, 4) };
        bar.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Classes = { "caption" }, Text = "Zoom", VerticalAlignment = VerticalAlignment.Center }, zoom },
        };
        var surround = new Grid { Name = "X2ScreenPane", Children = { Viewport } };
        surround.Bind(Panel.BackgroundProperty, surround.GetResourceObservable("AppScreenSurroundBrush"));
        var layout = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Add(bar);
        layout.Children.Add(surround);
        Content = layout;
    }

    public VideoSurface Surface { get; }
    public AspectRatioViewport Viewport { get; }

    /// <summary>The person picked a zoom: (fit to window, integer cap).</summary>
    public event Action<bool, int>? ZoomChanged;

    /// <summary>Show the named machine's VGA or ULA screen with its remembered zoom; the previous
    /// machine's picture is dropped so it never shows under another machine's name.</summary>
    internal void Bind(string machine, X2ScreenSettings settings, string screenName = "X2 VGA")
    {
        Title = machine + " · " + screenName;
        Surface.ClearPresentation();
        SetZoom(settings.FitToWindow, settings.Scale);
    }

    internal void UpdateTitle(string machine, string screenName = "X2 VGA")
    {
        Title = machine + " · " + screenName;
    }

    internal void SetZoom(bool fitToWindow, int scale)
    {
        Apply(fitToWindow, scale);
        binding = true;
        try { zoom.SelectedIndex = Array.FindIndex(Zooms, z => z.FitToWindow == fitToWindow && z.Scale == (fitToWindow ? 0 : scale)); }
        finally { binding = false; }
    }

    private void Apply(bool fitToWindow, int scale)
    {
        Viewport.FitToWindow = fitToWindow;
        Viewport.RequestedScale = fitToWindow ? 0 : scale;
    }
}
