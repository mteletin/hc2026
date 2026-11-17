using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Workspace;
using Hc2000.Machine.Video;

namespace Hc2000.App;

// hc2000-x2-second-screen: the X2 VGA card's surface in its own window beside the unchanged main
// screen. One window follows the selected machine, as the main screen does; whether it shows, its
// zoom and its placement are that machine's (MachineConfiguration.X2Screen, saved by machine id).
public partial class MainWindow
{
    private X2ScreenWindow? x2Screen;
    // The session the window presents, and its tab (null for the implicit window session).
    private MachineSession? x2ScreenSession;
    private MachineTabContext? x2ScreenTab;
    private X2ScreenSettings implicitX2Screen = new();
    private bool syncingX2Screen;

    internal X2ScreenWindow? X2Screen => x2Screen;
    internal VideoFrame? LatestPresentedX2Frame { get; private set; }
    /// <summary>The ULA and X2 frames presented in the same tick: both screens showing one emulated frame.</summary>
    internal (VideoFrame Ula, VideoFrame X2)? LatestPresentedPair { get; private set; }

    private X2ScreenSettings SelectedX2Screen
        => workspace.Selected is { } tab ? X2ScreenOf(tab.Configuration) : implicitX2Screen;

    private X2ScreenSettings BoundX2Screen
        => x2ScreenTab is { } tab ? X2ScreenOf(tab.Configuration) : implicitX2Screen;

    /// <summary>A machine's second-screen settings, or its defaults until the person changes them: the full
    /// workstation's console is on its X2 VGA (system v2, hc2000-v2-system), so its VGA is the main screen.</summary>
    internal static X2ScreenSettings X2ScreenOf(MachineConfiguration configuration)
        => configuration.X2Screen ?? new(VgaIsPrimary: configuration.Profile == "hc2000-v2-full-workstation-v1");

    // A machine without the card, or one that is not running, offers no second screen.
    private bool X2ScreenOffered(MachineSession session)
        => !closing && session.Worker is { Completion.IsCompleted: false } && session.Video.HasX2;

    private void WireX2Screen()
    {
        this.FindControl<Button>("X2ScreenToggle")!.Click += (_, _) => ToggleX2Screen();
        if (this.FindControl<CheckBox>("VgaMainScreenToggle") is { } vgaToggle)
        {
            vgaToggle.IsCheckedChanged += (_, _) =>
            {
                var session = CurrentSession;
                if (!X2ScreenOffered(session)) return;
                var wantPrimary = vgaToggle.IsChecked == true;
                SetVgaPrimary(wantPrimary);
            };
        }
    }

    internal void SetVgaPrimary(bool vgaPrimary)
    {
        var session = CurrentSession;
        if (!X2ScreenOffered(session)) return;
        var settings = SelectedX2Screen;
        if (settings.VgaIsPrimary != vgaPrimary)
        {
            StoreX2Screen(workspace.Selected, settings with { VgaIsPrimary = vgaPrimary });
            session.Video.Invalidate();
            SyncX2Screen();
        }
    }

    /// <summary>Show, hide or rebind the window for the selected machine. Idempotent: runs every tick.</summary>
    private void SyncX2Screen()
    {
        // A layout save rebinds the Machine panel, which syncs again: the outer call finishes the job.
        if (syncingX2Screen) return;
        syncingX2Screen = true;
        try { SyncX2ScreenCore(); }
        finally { syncingX2Screen = false; }
    }

    private void SyncX2ScreenCore()
    {
        var session = CurrentSession;
        var offered = X2ScreenOffered(session);
        var settings = SelectedX2Screen;
        this.FindControl<StackPanel>("X2ScreenChoice")!.IsVisible = offered;
        var toggle = this.FindControl<Button>("X2ScreenToggle")!;
        if (settings.VgaIsPrimary)
        {
            toggle.Content = settings.Shown ? "Hide ULA screen" : "Show ULA screen";
        }
        else
        {
            toggle.Content = settings.Shown ? "Hide VGA screen" : "Show VGA screen";
        }
        if (this.FindControl<CheckBox>("VgaMainScreenToggle") is { } vgaToggle)
        {
            vgaToggle.IsChecked = settings.VgaIsPrimary;
        }
        if (x2ScreenSession is not null && (!ReferenceEquals(x2ScreenSession, session) || !offered)) LeaveX2Screen();
        if (!offered || !settings.Shown)
        {
            x2Screen?.Hide();
            return;
        }
        var window = x2Screen ??= CreateX2Screen();
        var screenName = settings.VgaIsPrimary ? "ULA" : "X2 VGA";
        if (x2ScreenSession is null)
        {
            x2ScreenSession = session;
            x2ScreenTab = workspace.Selected;
            window.Bind(x2ScreenTab?.Configuration.Name ?? "HC2000", settings, screenName);
            LatestPresentedX2Frame = null;
            session.Video.Invalidate();
        }
        else
        {
            window.UpdateTitle(x2ScreenTab?.Configuration.Name ?? "HC2000", screenName);
        }
        if (!window.IsVisible)
        {
            Place(window, settings);
            window.Show(this);
        }
    }

    /// <summary>The second screen's part of a presentation tick: the selected machine's second screen frame.
    /// When VGA is primary, the secondary window presents ULA. When ULA is primary, it presents VGA.</summary>
    private void PresentX2Screen(MachineSession session, VideoFrame? mainFrame)
    {
        SyncX2Screen();
        if (x2Screen is not { IsVisible: true } window || !ReferenceEquals(x2ScreenSession, session)) return;
        var settings = SelectedX2Screen;
        bool vgaPrimary = X2ScreenOffered(session) && settings.VgaIsPrimary;
        var secondFrame = vgaPrimary ? session.Video.TakeUla() : session.Video.TakeX2();
        if (secondFrame is null) return;
        var replaced = window.Surface.Present(secondFrame);
        session.Worker?.Recycle(replaced);
        if (vgaPrimary)
        {
            LatestPresentedX2Frame = mainFrame;
            if (mainFrame is not null) LatestPresentedPair = (secondFrame, mainFrame);
        }
        else
        {
            LatestPresentedX2Frame = secondFrame;
            if (mainFrame is not null) LatestPresentedPair = (mainFrame, secondFrame);
        }
    }

    /// <summary>A new runtime generation dropped the old frames: drop the old VGA picture too.</summary>
    private void ClearX2Presentation(MachineSession session)
    {
        if (!ReferenceEquals(x2ScreenSession, session)) return;
        var replaced = x2Screen?.Surface.ClearPresentation();
        session.Worker?.Recycle(replaced);
        LatestPresentedX2Frame = null;
    }

    private void ToggleX2Screen()
    {
        var session = CurrentSession;
        if (!X2ScreenOffered(session)) return;
        var settings = ReferenceEquals(x2ScreenSession, session) ? CapturePlacement(BoundX2Screen) : SelectedX2Screen;
        StoreX2Screen(workspace.Selected, settings with { Shown = !settings.Shown });
        SyncX2Screen();
        if (!SelectedX2Screen.Shown)
        {
            Activate();
            this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
        }
    }

    // The window leaves the machine it showed (another tab selected, the tab closed, the runtime
    // stopped): its placement stays with that machine.
    private void LeaveX2Screen()
    {
        var tab = x2ScreenTab;
        var settings = CapturePlacement(BoundX2Screen);
        var replaced = x2Screen?.Surface.ClearPresentation();
        x2ScreenSession?.Worker?.Recycle(replaced);
        x2ScreenSession = null;
        x2ScreenTab = null;
        x2Screen?.Hide();
        StoreX2Screen(tab, settings);
    }

    private X2ScreenWindow CreateX2Screen()
    {
        var window = new X2ScreenWindow();
        window.ZoomChanged += (fit, scale) =>
        {
            if (x2ScreenSession is not null)
                StoreX2Screen(x2ScreenTab, CapturePlacement(BoundX2Screen) with { FitToWindow = fit, Scale = scale });
        };
        // Guest input typed while the VGA screen has focus goes to the machine it shows.
        window.AddHandler(KeyDownEvent, X2ScreenKeyDown, RoutingStrategies.Tunnel);
        window.AddHandler(KeyUpEvent, (_, e) => KeyboardInput.KeyUp(e.Key), RoutingStrategies.Tunnel);
        window.AddHandler(TextInputEvent, (_, e) =>
        {
            if (!IsX2ZoomInput(e.Source)) e.Handled |= KeyboardInput.TextInput(e.Text);
        });
        window.Deactivated += (_, _) => KeyboardInput.ReleaseFocus(InputReleaseOrigin.Deactivated);
        window.Closing += (_, e) =>
        {
            // The person closing it hides it for this machine; it reopens from the Machine panel.
            if (!closing && e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
                if (x2ScreenSession is not null) StoreX2Screen(x2ScreenTab, CapturePlacement(BoundX2Screen) with { Shown = false });
                SyncX2Screen();
                return;
            }
            // The app is closing: keep where it was for next time.
            if (x2ScreenSession is not null) StoreX2Screen(x2ScreenTab, CapturePlacement(BoundX2Screen));
        };
        window.Closed += (_, _) =>
        {
            x2Screen = null;
            x2ScreenSession = null;
            x2ScreenTab = null;
        };
        return window;
    }

    private void X2ScreenKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            if (HandleCtrlShiftAccelerator(e)) return;
        if (IsX2ZoomInput(e.Source)) return;
        // Ctrl+0..Ctrl+4 zoom the VGA screen while it has focus, as they do the main screen.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is >= Key.D0 and <= Key.D4 && x2Screen is { } window)
        {
            e.Handled = true;
            window.SetZoom(false, e.Key - Key.D0);
            if (x2ScreenSession is not null)
                StoreX2Screen(x2ScreenTab, CapturePlacement(BoundX2Screen) with { FitToWindow = false, Scale = e.Key - Key.D0 });
            return;
        }
        e.Handled |= KeyboardInput.KeyDown(e.Key, e.KeyModifiers);
    }

    private static bool IsX2ZoomInput(object? source) => source is Visual visual
        && visual.GetSelfAndVisualAncestors().Any(v => v is ComboBox or ComboBoxItem);

    private X2ScreenSettings CapturePlacement(X2ScreenSettings settings) => x2Screen is { IsVisible: true } window
        ? settings with
        {
            X = window.Position.X, Y = window.Position.Y,
            Width = Math.Clamp(window.ClientSize.Width, 160, 16384), Height = Math.Clamp(window.ClientSize.Height, 120, 16384),
        }
        : settings;

    // The remembered size and position, when they still land on a screen; on a first show,
    // beside the main window when the screen has room for it, else centred over it.
    private void Place(X2ScreenWindow window, X2ScreenSettings settings)
    {
        if (settings.Width is { } width && settings.Height is { } height)
        {
            window.Width = width;
            window.Height = height;
        }
        var screens = Screens;
        if (settings.X is { } x && settings.Y is { } y
            && (screens is null || screens.All.Count == 0 || screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(x, y)))))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(x, y);
            return;
        }
        var scaling = DesktopScaling;
        var beside = new PixelPoint(Position.X + (int)Math.Ceiling((FrameSize?.Width ?? Bounds.Width) * scaling) + 8, Position.Y);
        var area = screens?.ScreenFromWindow(this)?.WorkingArea;
        if (area is { } a && a.Contains(new PixelRect(beside, PixelSize.FromSize(new Size(window.Width, window.Height), scaling))))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = beside;
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    /// <summary>Remember the second screen's layout for one machine: its tab's draft, and its own
    /// record in the saved workspace (by machine id, nothing else in the file changes).</summary>
    private void StoreX2Screen(MachineTabContext? tab, X2ScreenSettings value)
    {
        if (tab is null)
        {
            implicitX2Screen = value;
            return;
        }
        if (X2ScreenOf(tab.Configuration) == value) return;
        workspace.TryUpdateDraft(new MachineDraftTarget(tab.TabId, tab.DraftRevision), tab.Configuration with { X2Screen = value });
        if (!closing && ReferenceEquals(workspace.Selected, tab)) BindReconfigureView();
        var loaded = configurationStore.Load();
        // A malformed file is never silently replaced by a layout change.
        if (loaded.Warning is not null)
        {
            if (!closing) this.FindControl<TextBlock>("StatusLine")!.Text = "VGA screen layout applied, not saved: " + loaded.Warning;
            return;
        }
        // A draft that was never started has no saved record yet; its Start saves the draft with it.
        if (loaded.Settings.Workspace is not { } stored) return;
        var index = -1;
        for (var i = 0; i < stored.Machines.Length && index < 0; i++)
            if (stored.Machines[i].Id == tab.Configuration.Id) index = i;
        if (index < 0 || stored.Machines[index].X2Screen == value) return;
        try
        {
            configurationStore.Save(loaded.Settings with
            {
                Workspace = stored with { Machines = stored.Machines.SetItem(index, stored.Machines[index] with { X2Screen = value }) },
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!closing) this.FindControl<TextBlock>("StatusLine")!.Text = "VGA screen layout applied, not saved: " + ex.Message;
        }
    }
}
