using System.Collections.Immutable;
// Hc2000.App: Avalonia desktop app. UI dependencies live here only.
// Skeleton for slice 00a; the real app lands in its own epic (epic 06, spec 06-app.md).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Styling;
using Hc2000.App.Input;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.App.Workspace;
using Hc2000.Machine.Capabilities;
using Hc2000.Machine.Video;

namespace Hc2000.App;

public static partial class Program
{
    public static bool Z80VisualDemoEnabled { get; private set; }
    public static bool X2BenchmarkLaunchEnabled { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must precede the first Console.Out/Error access (the streams are lazy).
        // WinExe has no new console; terminal launches can still print help/errors.
        WindowsConsole.AttachParent();
        // Numbered owner demo: deterministic fixture via the existing renderer only.
        Stage1SpectrumDemo.Enabled = args.Contains("--demo", StringComparer.Ordinal) &&
            args.Contains("spectrum", StringComparer.Ordinal);
        Z80VisualDemoEnabled = args.Contains("--demo", StringComparer.Ordinal) &&
            args.Contains("z80", StringComparer.Ordinal);
        X2BenchmarkLaunchEnabled = args.Contains("--x2-benchmark", StringComparer.Ordinal);
        if (Stage1SpectrumDemo.Enabled && Z80VisualDemoEnabled)
            throw new ArgumentException("Select only one --demo mode.");
        var exit = DispatchLaunch(args, Console.Out, Console.Error);
        if (exit is int code) return code;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Everything Main decides before the GUI: the benchmark self-test, --help, a catalog refusal
    /// or a refused session file all return their exit code; a launch the window may open returns null.
    /// Entry-point regressions call this instead of a GUI lifetime (architecture-linux-parity-20261026).
    /// The catalog flag --cpu-turbo is NOT intercepted: it takes the ordinary cold launcher route.</summary>
    public static int? DispatchLaunch(string[] args, TextWriter stdout, TextWriter stderr, Hc2000.Features.FeatureSet? features = null)
    {
        // The release's feature flags, from the manifest beside the executable. A bad manifest turns the flagged
        // features off and says so; the launch goes on.
        features ??= Hc2000.Features.FeatureSet.Current;
        if (features.Diagnostic is { } flagDiagnostic) stderr.WriteLine("hc2000: " + flagDiagnostic);
        if (args.Contains(Hc2000.FeatureSample.FeatureSampleGate.AppOption, StringComparer.Ordinal))
            return Hc2000.FeatureSample.FeatureSampleGate.Run(features, "hc2000: " + Hc2000.FeatureSample.FeatureSampleGate.AppOption, stdout, stderr);
        // Numbered owner demo: deterministic fixture via the existing renderer only.
        Stage1SpectrumDemo.Enabled = args.Contains("--demo", StringComparer.Ordinal) &&
            args.Contains("spectrum", StringComparer.Ordinal);
        Z80VisualDemoEnabled = args.Contains("--demo", StringComparer.Ordinal) &&
            args.Contains("z80", StringComparer.Ordinal);
        X2BenchmarkLaunchEnabled = args.Contains("--x2-benchmark", StringComparer.Ordinal);
        if (Stage1SpectrumDemo.Enabled && Z80VisualDemoEnabled)
            throw new ArgumentException("Select only one --demo mode.");
        // Synthetic turbo timing self-test behind its own spelling; it does not start the GUI.
        if (Runtime.CpuTurboSelection.IsRequest(args))
            return Runtime.CpuTurboSelection.Run(args, stdout, stderr);
        // --help wins over a GUI start. Demo args are not launch options and keep their own path.
        if (!Stage1SpectrumDemo.Enabled && !Z80VisualDemoEnabled && !X2BenchmarkLaunchEnabled)
        {
            var parsed = LaunchOptions.Parse(args);
            var early = LaunchStartup.EarlyExit(parsed, stdout, stderr, features);
            if (early is int code) return code;
            var session = LaunchStartup.ApplySession(parsed.Options!);
            if (!session.Ok)
            {
                stderr.WriteLine(session.StatusLine);
                stderr.WriteLine(LaunchStartup.Usage);
                return 1;
            }
            CurrentLaunch = session.Options;
        }
        return null;
    }

    /// <summary>Launch options for the window this process is about to open. Tests pass options to the
    /// window directly so they do not depend on this static.</summary>
    public static LaunchOptions? CurrentLaunch { get; private set; }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(Program.CurrentLaunch);
            desktop.MainWindow = window;
            // hc2000-speed-140-turbo-280: the boot check, once per app start, off the UI thread; the window shows its
            // recommendation. Started here, at the real app's entry, so windows built by tests never run it.
            _ = Task.Run(() =>
            {
                try
                {
                    var result = Runtime.HostSpeedCheck.Run();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => window.ShowHostSpeed(result));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
                {
                    Console.Error.WriteLine("[HostSpeedCheck] " + ex.Message);
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public partial class MainWindow : Window
{
    private readonly MachineWorkspace workspace = new();
    private readonly MachineSession implicitSession = new(KeyMappingMode.Positional);

    private readonly LaunchOptions? launchOptions;
    private bool launchTapeApplied;
    // Launch --disk A:/B: on a named-HDD draft rides the warm insertion lane, deferred until the
    // acknowledged launch start (adr-saved-draft-launch-638412): armed by the --start branch,
    // consumed once at that start's success point (inside the boot call's own session/generation
    // checks), cancelled on every refused, failed or skipped start; never replayed on Reset or a
    // second tab.
    private Workspace.MachineSession? launchDiskDeferral;
    private readonly string? assetRootOverride;
    // Scanned-home mount uses the accepted MediaHomeMountLane only. Null means the catalog
    // could not be loaded; the panel then shows no Mount or Eject action.
    private DiskLibraryMount? mediaHomeMount;
    private MediaHomeMountLane? mediaHomeLane;
    private bool libraryHddPreparing;
    private int coldStartsInFlight;
    private Configuration.PrestartPanel prestart;
    private readonly Configuration.ReconfigurePanel reconfigure;
    private Network.CpNetBridge? displayedNetworkBridge;
    private bool closing;
    private Task shutdownCompletion = Task.CompletedTask;
    private bool closeConfirmed, checkingClose;
    private bool syncingInputEvidenceUi;
    private readonly CheckBox inputEvidenceToggle = new() { Content = "Observe future emulator-window input (volatile, bounded)", Focusable = false };
    private readonly TextBlock inputEvidenceText = new() { Text = FrontendInputEvidence.Coverage, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Avalonia.Threading.DispatcherTimer presentation = new() { Interval = TimeSpan.FromMilliseconds(20) };

    // Resize-aware present gating (see VideoPresentPolicy) - the
    // last SizeChanged stamp, the Stopwatch stamp of the previous presentation tick, and the
    // running back-pressure skip count.
    private long presentGateLastResizeUtcTicks;
    private long presentGateLastStampTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    private int presentGateBacklogSkips;

    // The one machine the shared view answers for: the selected tab's session, or the implicit
    // window session when there are no tabs (every legacy single-machine path and test).
    internal MachineSession CurrentSession
    {
        get
        {
            var selected = workspace.Selected;
            return selected is not null ? selected.Session : implicitSession;
        }
    }
    public InputQueue InputQueue => CurrentSession.InputQueue;
    public KeyboardInputAdapter KeyboardInput => CurrentSession.KeyboardInput;

    internal IEnumerable<MachineSession> AllSessions()
    {
        yield return implicitSession;
        foreach (var tab in workspace.Tabs) yield return tab.Session;
    }

    private void WatchInputRejected(MachineSession session)
        => session.KeyboardInput.Rejected += message => this.FindControl<TextBlock>("StatusLine")!.Text = message;

    // The one shared media/snapshot panel set re-binds to the selected session. A session without
    // a live runtime (draft, or a completed one) leaves the panel disconnected; selection never
    // touches the runtime state of another session.
    private void BindSelectedSessionView()
    {
        var media = this.FindControl<Media.MediaPanel>("MediaControls")!;
        var snapshots = this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!;
        media.Unbind();
        media.Hdd.ConfigureLibraryRemaps([], null);
        snapshots.Unbind();
        var session = CurrentSession;
        session.Video.Invalidate();
        LatestPresentedFrame = null;
        LatestPresentedGeneration = -1;
        var replaced = this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
        session.Worker?.Recycle(replaced);
        SyncX2Screen();
        // Per-machine snapshot scope: this machine's persisted last snapshot selection, from the
        // tab's own configuration (drafts never carry StartingConfiguration). The list is
        // refreshed for the bound machine only; selection dispatches through the bound bridge.
        var configuration = workspace.Selected?.Configuration ?? session.StartingConfiguration;
        this.FindControl<Runtime.AspectRatioViewport>("ScreenViewport")!.RequestedScale = configuration?.Scale ?? configurationStore.Load().Settings.Scale;
        var screenSurface = this.FindControl<Runtime.VideoSurface>("Screen");
        if (screenSurface is not null)
        {
            screenSurface.BorderMode = Runtime.VideoSurface.ParseBorderMode(configurationStore.Load().Settings.Border);
            screenSurface.PresentationMode = session.PresentationMode;
        }
        var modeSelector = this.FindControl<ComboBox>("PresentationModeSelector");
        if (modeSelector is not null) modeSelector.SelectedIndex = (int)session.PresentationMode;
        BindExplorer(session, configuration);
        var lastSnapshot = configuration?.LastSnapshot;
        snapshots.LastSnapshot = lastSnapshot;
        snapshots.SnapshotDirectory = lastSnapshot is null ? null : Path.GetDirectoryName(lastSnapshot);
        BindMediaHomeLane(session);
        BindMediaHomeBrowse(session);
        if (session.Closed || session.Worker?.Completion.IsCompleted != false)
        {
            media.RefreshDiskLibraryList();
            snapshots.RefreshSnapshotList();
            return;
        }
        if (session.MediaBridge is not null && session.WritebackHost is not null)
        {
            media.Attach(session.MediaBridge);
            BindMediaPanel(session, media, session.WritebackHost);
        }
        if (session.SnapshotBridge is not null)
            snapshots.Attach(session.SnapshotBridge);
    }

    /// <summary>Wire the scanned media-home list to the existing catalog and mount lane.
    /// A catalog that cannot be loaded leaves the lane null (no Mount or Eject); the scan still runs.</summary>
    private void WireMediaHomeList()
    {
        var panel = this.FindControl<MediaHomeListPanel>("MediaHomeList");
        if (panel is null) return;
        panel.Scan = cancellation =>
        {
            var settings = configurationStore.Load().Settings;
            var configuration = workspace.Selected?.Configuration ?? CurrentSession.StartingConfiguration;
            var last = configuration?.LastSnapshot;
            return MediaHomeScan.ScanAsync(settings, last is null ? null : Path.GetDirectoryName(last), cancellation);
        };
        try
        {
            var settingsDir = Path.GetDirectoryName(Path.GetFullPath(configurationStore.FilePath))
                ?? throw new InvalidOperationException("settings directory is missing");
            try { Settings.SettingsStore.EnsurePrivateSettingsDirectory(settingsDir); }
            catch (Exception) { }
            var legacyCatalog = Path.Combine(settingsDir, "disk-library.json");
            var catalogPath = legacyCatalog;
            if (!File.Exists(legacyCatalog))
            {
                // Working-copy admission requires its root to be private. Do not chmod the
                // user's settings directory or silently relocate an existing catalog/copies.
                var libraryDirectory = Path.Combine(settingsDir, "disk-library");
                if (OperatingSystem.IsLinux())
                    Directory.CreateDirectory(libraryDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else
                    Directory.CreateDirectory(libraryDirectory);
                catalogPath = Path.Combine(libraryDirectory, "catalog.json");
            }
            var catalog = DiskLibraryCatalog.Load(catalogPath, DiskLibraryHostAdmission.StoppedHost);
            // CreateWorkingCopyAsync compares Path.GetDirectoryName(CatalogPath) to Path.GetFullPath(libraryRoot).
            var root = Path.GetDirectoryName(catalog.CatalogPath)
                ?? throw new InvalidOperationException("catalog directory is missing");
            mediaHomeMount = new DiskLibraryMount(catalog, root);
            mediaHomeLane = new MediaHomeMountLane(catalog, root, mediaHomeMount);
            panel.MountLane = mediaHomeLane;
            // Both UI surfaces bind the real detached editor and the same private HDD catalog.
            panel.HddLibraryAction = (row, slot) => SelectEditorLibraryHddAsync(prestart,
                new("scanned-hdd:" + row.Path, "hdd", row.Name, row.Path, row.Size, row.Detail,
                    row.Selectable ? Media.InlineMediaStatus.Ready : Media.InlineMediaStatus.Refused), slot);
            if (this.FindControl<MediaPanel>("MediaControls") is { } media)
            {
                media.DiskLibrary = catalog;
                media.TwoPane.MountLane = mediaHomeLane;
                media.TwoPane.ShowNotice = msg => panel.ShowNotice(msg);
            }
        }
        catch (Exception ex) when (ex is DiskLibrarySchemaException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            mediaHomeMount = null;
            mediaHomeLane = null;
            panel.MountLane = null;
            panel.HddLibraryAction = null;
            if (this.FindControl<MediaPanel>("MediaControls") is { } media)
            {
                media.DiskLibrary = null;
                media.TwoPane.MountLane = null;
                media.TwoPane.ShowNotice = null;
            }
            panel.ShowNotice("Media home mount is closed: the disk library catalog could not be loaded. " + ex.Message);
        }
        // The HDD cold profile menu (hc2000-hdd-context-mount-menu): the commit resolves the
        // selected machine draft at click time, so it binds once here rather than per session.
        panel.HddAction = (row, slot, select) => MediaHomeHddDraftAction.RunAsync(workspace, row, slot, select);
    }

    /// <summary>Point the lane's existing media-path seams at this session's bridge, or clear them
    /// when the session is not a live worker. The captured bridge is the one passed in, not a later selection.</summary>
    private void BindMediaHomeLane(MachineSession session)
    {
        if (mediaHomeMount is null || mediaHomeLane is null) return;
        var bridge = session.Closed || session.Worker?.Completion.IsCompleted != false ? null : session.MediaBridge;
        if (bridge is null)
        {
            mediaHomeMount.Inspect = null;
            mediaHomeMount.DispatchInsert = null;
            mediaHomeLane.DispatchEject = null;
            return;
        }
        mediaHomeMount.Inspect = () => bridge.RequestAsync(new MediaCommand(MediaAction.Inspect));
        mediaHomeMount.DispatchInsert = (slot, bytes, name) =>
            bridge.RequestAsync(new MediaCommand(MediaAction.InsertDisk, slot, bytes, name));
        mediaHomeLane.DispatchEject = (slot, _) =>
            bridge.RequestAsync(new MediaCommand(MediaAction.EjectDisk, slot));
    }

    /// <summary>Point the scanned list's double-click at this session's existing reader (contract
    /// section 3). Browse reads host bytes only, so a draft session is wired too; the pane refuses
    /// a row that is not the current session.</summary>
    private void BindMediaHomeBrowse(MachineSession session)
    {
        var panel = this.FindControl<MediaHomeListPanel>("MediaHomeList");
        if (panel is null) return;
        panel.BrowseRequested = row => { ShowMediaFullView(); return BrowseMediaHomeRowAsync(session, row); };
    }

    private void RefreshTabsView()
    {
        RefreshKeyboardReference();
        var panel = this.FindControl<StackPanel>("MachineTabs")!;
        panel.Children.Clear();
        panel.IsVisible = workspace.Tabs.Count > 0 || hasDetachedSetup;
        RefreshShellView();
        foreach (var tab in workspace.Tabs)
        {
            var selected = !draftInFront && ReferenceEquals(workspace.Selected, tab);
            var select = new Button
            {
                Content = tab.Configuration.Name,
                Focusable = false,
                Padding = new Thickness(10, 4),
                Classes = { "tabStripItem" },
                FontWeight = selected ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal
            };
            if (selected) select.Classes.Add("selected");
            Avalonia.Automation.AutomationProperties.SetName(select, "Select machine " + tab.Configuration.Name);
            ToolTip.SetTip(select, (selected ? "Selected machine: " : "Select machine: ") + tab.Configuration.Name);
            select.Click += (_, _) => SelectTabView(tab);
            var close = new Button
            {
                Content = "\u00D7",
                Focusable = false,
                Width = 26,
                Padding = new Thickness(0),
                Classes = { "ghostAction" },
                Margin = new Thickness(0)
            };
            Avalonia.Automation.AutomationProperties.SetName(close, "Close machine " + tab.Configuration.Name);
            ToolTip.SetTip(close, "Close only " + tab.Configuration.Name);
            close.Click += async (_, _) => await CloseTabAsync(tab);
            var running = tab.Session.Worker is { Completion.IsCompleted: false };
            var state = new TextBlock
            {
                Text = "\u25CF",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            state.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                running ? "AppStatusRunningBrush" : "AppStatusStoppedBrush"));
            ToolTip.SetTip(state, running ? "Running" : tab.Session.Worker is null ? "Not started" : "Stopped");
            var frame = new Border
            {
                Classes = { "tab" },
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { state, select, close } }
            };
            if (selected) frame.Classes.Add("selected");
            panel.Children.Add(frame);
        }
        AddDetachedSetupTab(panel);
    }

    internal void ApplyTheme(string theme)
    {
        RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    private void SelectTabView(MachineTabContext tab)
    {
        if (!workspace.Select(tab.TabId)) return;
        draftInFront = false;
        filesInFront = false;
        BindSelectedSessionView();
        SyncInputView();
        BindReconfigureView();
        RefreshTabsView();
        this.FindControl<TextBlock>("StatusLine")!.Text = CurrentSession.Worker is null
            ? "Selected machine is not running."
            : CurrentSession.Worker.Completion.IsCompleted ? "Selected machine stopped: " + tab.Configuration.Name + "."
            : "Selected machine runtime; actions below affect only " + tab.Configuration.Name + ".";
    }

    private void CycleTabView(int direction)
    {
        if (workspace.Tabs.Count < 2 || workspace.Selected is null) return;
        var current = workspace.Tabs.IndexOf(workspace.Selected);
        SelectTabView(workspace.Tabs[(current + direction + workspace.Tabs.Count) % workspace.Tabs.Count]);
    }

    private void SyncInputView()
    {
        var box = this.FindControl<ComboBox>("InputModeBox")!;
        // Reflect the selected machine's adapter mode; switching tabs never re-maps the other one.
        box.SelectedIndex = CurrentSession.KeyboardInput.Mode == KeyMappingMode.Positional ? 1 : 0;
    }

    // Tab close: actual close/drain evidence. A live tab must answer its own writeback close
    // gate, so the panel is bound to it first; the session is retired, the worker drained, and
    // the named hold released before the tab is removed.
    internal async Task CloseTabAsync(MachineTabContext tab)
    {
        var session = tab.Session;
        if (session.Closed || closing) return;
        if (session.BasicRuntimeActive && session.Worker is { Completion: { IsCompleted: false } })
        {
            if (workspace.Select(tab.TabId))
            {
                BindSelectedSessionView();
                SyncInputView();
            }
            if (!await this.FindControl<Media.MediaPanel>("MediaControls")!.CanCloseAsync()) return;
        }
        var wasSelected = ReferenceEquals(workspace.Selected, tab);
        session.Retire();
        var old = session.Worker;
        session.DetachWorker();
        old?.Dispose();
        var shutdown = AwaitShutdownAndDiscardPendingAsync(session, old, session.BasicReplacementCompletion);
        // Proven owner drain before the named release: the retired worker and the in-flight
        // replacement/boot (including a configured prestart read under the fence) are quiescent
        // before the fence goes away; the release itself also awaits any started member read.
        try { await shutdown; }
        catch { /* the replacement records its own failure before close */ }
        session.ReleaseHeldRead("tab closed");
        session.BasicRuntimeActive = false;
        adoptedProfiles.Remove(session);
        adoptedOverlays.Remove(session);
        adoptedTurbos.Remove(session);
        displayedStops.Remove(session);
        workspace.RemoveTab(tab.TabId, out _);
        if (wasSelected) BindSelectedSessionView();
        this.FindControl<Button>("MachineReset")!.IsEnabled = CurrentSession.BasicRuntimeActive;
        SyncInputView();
        BindReconfigureView();
        RefreshTabsView();
        if (!presentation.IsEnabled && AllSessions().Any(s => s.Worker is { Completion: { IsCompleted: false } }))
            presentation.Start();
    }

    internal Runtime.EmulationWorker? CurrentRuntime => CurrentSession.Worker;
    internal Task ShutdownCompletion => shutdownCompletion;
    /// <summary>Last frame the window presented. The startup test reads this, not a worker it composed.</summary>
    internal VideoFrame? LatestPresentedFrame { get; private set; }
    internal long LatestPresentedGeneration { get; private set; } = -1;
    // Diagnosis-only (env-gated; no behavior change when unset): trace the presentation tick to
    // a file so a wedged desktop instance can say whether the timer fires, whether a fresh frame
    // is obtained, and whether Present throws (hc2000-signon-wedge-live-0-1-171).
    private static readonly string? PresentTracePath = System.Environment.GetEnvironmentVariable("HC2000_PRESENT_TRACE");
    private long presentTickCount;
    private uint presentTraceLastHash;
    private bool presentTraceHashed;
    private static uint Fnv1aB(System.Collections.ObjectModel.ReadOnlyCollection<byte> mem, int start, int len)
    {
        unchecked
        {
            var h = 2166136261u;
            for (var i = 0; i < len; i++) { h ^= mem[start + i]; h *= 16777619u; }
            return h;
        }
    }
    private static uint Fnv1a(ReadOnlySpan<uint> span)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var p in span) { h ^= p; h *= 16777619u; }
            return h;
        }
    }
    /// <summary>IF1 shadow length composed at window open. 0 until the whole-chip boot attaches.</summary>
    internal int BootedIf1Length { get; private set; }

    /// <summary>Composition root attaches the worker to the selected machine; no synthetic CPU
    /// is used for a fake boot.</summary>
    public void AttachRuntime(Runtime.EmulationWorker runtime)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        var session = CurrentSession;
        if (session.Worker is not null) throw new InvalidOperationException("Runtime already attached.");
        session.AttachWorker(runtime);
        BindExplorer(session, workspace.Selected?.Configuration ?? session.StartingConfiguration);
        RefreshInputEvidenceSession();
        this.FindControl<TextBlock>("StatusLine")!.Text = "Runtime attached";
        RefreshShellView();
        presentation.Start();
    }

    private async Task RestartZ80DemoAsync()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        var session = CurrentSession;
        if (closing || session.ReplacingRuntime || !Program.Z80VisualDemoEnabled) return;
        session.ReplacingRuntime = true;
        var button = this.FindControl<Button>("Z80DemoRerun")!;
        button.IsEnabled = false;
        presentation.Stop();
        this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
        var old = session.Worker;
        session.KeyboardInput.Evidence.EndSession();
        ResetInputEvidenceView();
        session.DetachWorker(); // no old frame or completion can reach the new session
        session.BumpGeneration();
        old?.Dispose();
        try
        {
            if (old is not null) await old.Completion;
            if (closing) return;
            AttachRuntime(Runtime.Z80VisualDemo.CreateWorker());
            this.FindControl<TextBlock>("StatusLine")!.Text = "Fresh Z80 session running; completed image is CPU-painted";
        }
        catch (Exception ex)
        {
            if (!closing) this.FindControl<TextBlock>("StatusLine")!.Text = "Demo runtime failed: " + ex.GetBaseException().Message;
        }
        finally
        {
            session.ReplacingRuntime = false;
            if (!closing) button.IsEnabled = true;
        }
    }

    private void ResetWholeChipMachine()
    {
        var session = CurrentSession;
        if (closing || !session.BasicRuntimeActive || session.Worker?.Completion.IsCompleted != false) return;
        // Binding-preserving reset (machine tabs config v2): an unresolved writeback fence refuses
        // the reset WITHOUT invalidating the mounted media or its bindings; the machine keeps both.
        if (session.WritebackHost is { } host && !host.ResetGuardClear())
        {
            this.FindControl<TextBlock>("StatusLine")!.Text =
                "Reset refused: a writeback fence is unresolved; mounted media and bindings retained.";
            return;
        }
        // The owner's reset consumption is enqueued before the named drain: the reset is the owner's
        // work, and the drain (which also awaits any started member read) must not run before the
        // owner has a chance to consume it.
        session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.Reset));
        session.ReleaseHeldRead("machine reset");
        session.KeyboardInput.ReleaseFocus(InputReleaseOrigin.Reset);
        session.KeyboardInput.Evidence.EndSession();
        ResetInputEvidenceView();
        this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
        LatestPresentedFrame = null;
        if (draftInFront && adoptedProfiles.TryGetValue(session, out var adoptedProfile))
        {
            try
            {
                var draft = prestart.Build();
                var adoptedOverlay = adoptedOverlays.TryGetValue(session, out var o) && o;
                var adoptedTurbo = adoptedTurbos.TryGetValue(session, out var t) && t > 0 ? t : 1;
                if (draft.Profile != adoptedProfile || draft.TextOverlay != adoptedOverlay || draft.CpuTurboMultiplier != adoptedTurbo)
                {
                    this.FindControl<TextBlock>("StatusLine")!.Text = CapabilityCatalog.Cold(adoptedProfile);
                }
            }
            catch { }
        }
    }

    /// <summary>Attach only a command/reply bridge; live drives remain on the worker.</summary>
    public void AttachMedia(Media.MediaBridge bridge)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        this.FindControl<Media.MediaPanel>("MediaControls")!.Attach(bridge);
        _ = this.FindControl<Media.MediaPanel>("MediaControls")!.InspectAsync();
    }

    /// <summary>Attach only a snapshot command/reply bridge; the live machine remains on the worker.</summary>
    public void AttachSnapshots(Snapshots.HcsBridge bridge)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Attach(bridge);
    }

    /// <summary>Record a Start that did not proceed (a silent early return) on the StatusLine and
    /// to start.log, so a Start where nothing happens names its cause. Only the current session's line is touched.</summary>
    private void NoteStartSkipped(MachineSession session, string reason)
    {
        launchDiskDeferral = null; // A skipped start cancels the deferred launch-disk insertion.
        Diagnostics.StartLog.Write(configurationStore.FilePath, "Start not started: " + reason);
        if (!closing && ReferenceEquals(CurrentSession, session))
            this.FindControl<TextBlock>("StatusLine")!.Text = "Start not started: " + reason;
    }

    /// <summary>Whole-chip boot after explicit Start, or explicit CLI automation.
    /// Disks still go through <see cref="LaunchStartup"/> after the worker exists. Skipped when a
    /// runtime is already attached (demo, or a test that composed its own worker before Show).
    /// The chip read runs off the UI thread: LoadWholeChipV2 blocks on ReadAsync, which deadlocks
    /// a UI sync context.</summary>
    private async Task BootWholeChipMachine(ImmutableArray<HddInitialMedia>? initialHdd = null,
        Media.HddColdVolume? coldVolume = null, byte coldDrive = 2, bool useColdMap = false)
    {
        var session = CurrentSession;
        if (closing || libraryHddPreparing || session.Worker is not null || session.ReplacingRuntime || session.Closed || Program.Z80VisualDemoEnabled || Stage1SpectrumDemo.Enabled)
        {
            coldVolume?.Admission.CancelBeforeEnqueue();
            NoteStartSkipped(session, closing ? "the window is closing"
                : libraryHddPreparing ? "a library HDD preparation is in flight"
                : session.Worker is not null ? "a machine is already running on this session"
                : session.ReplacingRuntime ? "a runtime replacement is in flight"
                : session.Closed ? "the session is closing"
                : "a demo mode is active");
            return;
        }
        var appAssembly = typeof(MainWindow).Assembly;
        var appVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(appAssembly)?.InformationalVersion;
        Diagnostics.StartLog.Write(configurationStore.FilePath,
            $"Start attempt: version={appVersion}; executable={Environment.ProcessPath}; OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; admission={Hc2000.HostFiles.NativeImageAdmission.PlatformMode}; profile={session.StartingConfiguration?.Profile}");
        var ownerStarted = false;
        var startingEditor = prestart;
        var generation = session.RuntimeGeneration;
        var panel = this.FindControl<Media.ProfileRomPanel>("ProfileRomControls");
        if (panel is not null) panel.IsVisible = false;
        coldStartsInFlight++;
        try
        {
            var assetRoot = assetRootOverride ?? ProfileRomSelection.DefaultAssetRoot();
            var startProfile = session.StartingConfiguration?.Profile;
            var roms = await Task.Run(() => startProfile switch
            {
                "hc2000-v2-hdd-cpm3-v1" => ProfileRomSelection.LoadCpm3V1(assetRoot),
                "hc2000-v2-hdd-cboot-v1" or "hc2000-v2-full-workstation-v1" => ProfileRomSelection.LoadWholeChipCbootV1(assetRoot),
                _ => ProfileRomSelection.LoadWholeChipV2(assetRoot)
            });
            if (closing || session.Closed || session.Worker is not null || session.ReplacingRuntime || generation != session.RuntimeGeneration) { NoteStartSkipped(session, "the session changed while the ROM map was loading"); return; }
            var disks = session.StartingConfiguration is { } configuration
                ? await Configuration.PrestartMedia.ReadAsync(configuration, session.ClaimHeldConfiguredStart(configuration)) : [];
            if (closing || session.Closed || session.Worker is not null || generation != session.RuntimeGeneration)
            {
                session.ReleaseHeldRead("machine restart");
                NoteStartSkipped(session, "the machine is restarting while its disks were being read");
                return;
            }
            var prepared = session.StartingConfiguration is { } startConfiguration
                ? await Runtime.StartupHardwarePreparation.PrepareAsync(roms, startConfiguration)
                : coldVolume is not null
                ? useColdMap
                    ? Runtime.BasicMachineComposition.PrepareAdmittedHddMapWholeChipV2(roms, coldVolume)
                    : Runtime.BasicMachineComposition.PrepareAdmittedHddWholeChipV2(roms, coldVolume.Media(coldDrive), coldVolume.Admission)
                : initialHdd is { } images
                    ? Runtime.BasicMachineComposition.PrepareHddWholeChipV2(roms, images)
                    : Runtime.BasicMachineComposition.PrepareWholeChipV2(roms);
            if (closing || session.Closed || session.Worker is not null || generation != session.RuntimeGeneration) { NoteStartSkipped(session, "the session changed while hardware was being prepared"); return; }
            var media = this.FindControl<Media.MediaPanel>("MediaControls")!;
            if (ReferenceEquals(CurrentSession, session)) media.Disconnect();
            var bridge = new Media.MediaBridge(session.InputQueue);
            session.MediaBridge = bridge;
            var ledger = new Media.DiskWritebackOwnerLedger();
            var host = new Media.DiskWritebackHost(ledger, bridge, launchOptions?.Writeback == true);
            session.WritebackHost = host;
            if (ReferenceEquals(CurrentSession, session))
            {
                BindMediaPanel(session, media, host);
                if (LaunchStartup.HasDisk(launchOptions) || initialHdd.HasValue || coldVolume is not null || session.StartingConfiguration is not null)
                    media.Attach(bridge);
                else
                    AttachMedia(bridge);
            }
            var snapshots = new Snapshots.HcsBridge(session.InputQueue);
            session.SnapshotBridge = snapshots;
            var network = new Network.CpNetBridge(session.InputQueue);
            session.NetworkBridge = network;
            var networkServer = new Network.CpNetServerBridge(session.InputQueue);
            session.NetworkServerBridge = networkServer;
            // The existing FIFO consumes Pause before its first instruction. Mount receipts are
            // acknowledged while paused; a failed mount must never resume a partially configured guest.
            if (session.StartingConfiguration is { } draft)
            {
                session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.Pause));
                session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: draft.SpeedPercent));
            }
            // The sound-dump route (hc2000-app-sound-dump-route): the CLI flag and the saved setting
            // share the name and the one fence (PO ruling board 634859); the CLI flag wins, and a
            // refusal is reported once on the deck line and, for the CLI, on stderr.
            var soundDump = launchOptions?.SoundDump ?? configurationStore.Load().Settings.SoundDump;
            var soundDumpResolution = Audio.SoundDumpGate.Resolve(soundDump);
            if (soundDumpResolution.Refusal is not null)
            {
                if (launchOptions?.SoundDump is not null) Console.Error.WriteLine(soundDumpResolution.Refusal);
                if (ReferenceEquals(CurrentSession, session))
                    this.FindControl<TextBlock>("StatusLine")!.Text = soundDumpResolution.Refusal;
            }
            KeepX6GatewayLog(session);
            session.OutboundGateway.SetEnabled(session.StartingConfiguration?.NetworkGateway == true || launchOptions?.NetworkGateway == true);
            session.HasX6Net = prepared.X6Net;
            var runtime = Runtime.BasicMachineComposition.CreateWorker(prepared, session.InputQueue, bridge, snapshots,
                audioEnabled: true, writeback: ledger, soundDump: soundDumpResolution.Path, hostGate: session.CpNetHostGate,
                network: network, inboundHostGate: session.CpNetInboundHostGate, networkServer: networkServer,
                outbound: session.OutboundGateway, x6Ssh: X6SshConnector(session), x6Default: X6Default);
            ownerStarted = true; // Only the actual owner may now acknowledge/release admission.
            session.AttachWorker(runtime);
            if (ReferenceEquals(CurrentSession, session))
            {
                this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
                AttachSnapshots(snapshots);
                // The shared scanned-media-home lane binds to this live session: Mount/Eject captures
                // this worker's bridge and the double-click browse lane reads its host bytes. A fresh
                // Start (or --start) never re-runs BindSelectedSessionView after the worker attaches,
                // so without this the mount/eject seam stays null and the scanned-row Mount/Eject
                // refuses fail-closed even while the machine is running (demo #92).
                BindMediaHomeLane(session);
                BindMediaHomeBrowse(session);
            }
            var ownerGeneration = session.RuntimeGeneration;
            if (ReferenceEquals(CurrentSession, session)) BindExplorer(session, session.StartingConfiguration);
            presentation.Start();
            RefreshInputEvidenceSession();
            foreach (var disk in disks)
            {
                // Host-held prestart read: revalidate the file against the read facts before enqueue
                // (a swapped file refuses now, not as an old mount), then acknowledge the actual owner
                // outcome; the cold-read gate stays held by the reservation until that outcome or a drain.
                var reservation = disk.Reservation;
                if (reservation is not null)
                    try { reservation.MarkDispatched(); }
                    catch { reservation.DrainAtStop(); throw; }
                // O5/F7: the owner mount receipt carries the slot's stored descriptor kind+geometry,
                // and the host records it with the observation and the writable-bind decision.
                var descriptor = session.StartingConfiguration is { } configuredStart
                    ? (disk.Slot == Media.DiskSlot.A ? configuredStart.DiskA : configuredStart.DiskB)?.Descriptor.ToDescriptor()
                    : null;
                var receipt = await bridge.RequestAsync(new(MediaAction.InsertDisk, disk.Slot, disk.Bytes, Path.GetFileName(disk.Path), Descriptor: descriptor));
                var identity = reservation is null ? null : reservation.CompleteAfterOwnerOutcome(receipt);
                if (receipt.Error is not null) throw new InvalidDataException(receipt.Error);
                await host.NoteMounted(disk.Slot, disk.Path, disk.Bytes, receipt, identity, descriptor);
            }
            // Every expected member is now accounted (its real receipt, the bind/no-bind outcome and
            // the host accounting): the operation's single fence is released exactly once. A failure
            // above already left through the named "boot aborted" drain.
            session.ReleaseHeldRead("configured start accounted");
            if (session.StartingConfiguration is not null)
            {
                // Even an empty draft requires an actual owner reply, not just a returned
                // thread/worker constructor, before successful Start and any save intent.
                var installed = await bridge.RequestAsync(new(MediaAction.Inspect));
                if (installed.Error is not null) throw new InvalidDataException(installed.Error);
                if (closing || session.Closed || ownerGeneration != session.RuntimeGeneration) { NoteStartSkipped(session, "the session changed before the machine was acknowledged"); return; }
                session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.Resume));
            }
            BootedIf1Length = roms.If1.Length;
            session.BasicRuntimeActive = true;
            adoptedProfiles[session] = prepared.Roms.Profile.Name;
            adoptedOverlays[session] = prepared.Roms.Profile.Video.TextOverlay;
            adoptedTurbos[session] = prepared.Roms.Profile.CpuTurboMultiplier;
            // An automation Start completes the same editor presentation as a UI Start.
            // Never hide a newer detached draft, or one now starting on another session.
            if (session.StartingConfiguration is null && ReferenceEquals(CurrentSession, session)
                && ReferenceEquals(prestart, startingEditor) && !startingConfiguration)
                startingEditor.IsVisible = false;
            RefreshTabsView();
            if (ReferenceEquals(CurrentSession, session))
            {
                // The media-home lane was bound while this session was still a draft (TryAddTabView),
                // where its media-path seams were deliberately left unwired and nothing re-binds them
                // until the next selection change; point them at the now-live session's bridge so the
                // disk library lane can inspect and dispatch (demo #108: mount refused with
                // OwnershipUnknown because the seams were still absent after a successful Start).
                BindMediaHomeLane(session);
                BindMediaHomeBrowse(session);
                BindReconfigureView();
                this.FindControl<TextBlock>("StatusLine")!.Text = "Real " + prepared.Roms.Profile.Name + " whole-chip machine running"
                    + (soundDumpResolution.Refusal is not null ? " | " + soundDumpResolution.Refusal : "");
                if (prepared.Roms.Profile.Name == "hc2000-v2-hdd-v1" || initialHdd.HasValue || coldVolume is not null)
                    await media.Hdd.InspectAsync();
            }
            if (ReferenceEquals(launchDiskDeferral, session))
            {
                // The named-HDD launch draft deferred its launch A/B to this point: the cold-read
                // reservation is released and the owner Inspect reply is in; the warm lane carries
                // the existing receipt and refusal sentences on RuntimeStatus. This is that
                // acknowledged start only: the boot call's own generation checks bound it, and
                // every failure/skip path cancels the deferral before it could fire elsewhere.
                launchDiskDeferral = null;
                _ = ApplyLaunchDisksAsync(media, session);
            }
            else if (session.StartingConfiguration is null) _ = ApplyLaunchDisksAsync(media, session);
            // Catalog argv boots a StartingConfiguration too. Consume tape actions on the first
            // successful start, not only the legacy unconfigured path, and never replay on a new machine.
            if (!launchTapeApplied)
            {
                launchTapeApplied = true;
                await ApplyLaunchTapeAsync(media);
                await ApplyLaunchWorkstationAsync(media);
            }
        }
        catch (Exception ex)
        {
            launchDiskDeferral = null; // A failed start cancels the deferred launch-disk insertion.
            Exception failure = ex;
            if (session.StartingConfiguration is not null && session.Worker is { } failed)
            {
                session.DetachWorker();
                failed.Dispose();
                try { await failed.Completion; }
                catch (Exception ownerFailure) { failure = ownerFailure; } // Report the original owner fault after drain.
                if (!AllSessions().Any(s => s.Worker is { Completion.IsCompleted: false })) presentation.Stop();
                if (ReferenceEquals(CurrentSession, session))
                {
                    this.FindControl<Media.MediaPanel>("MediaControls")!.Disconnect();
                    this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
                }
                // Proven failed-owner drain: the failed owner will never deliver an owner reply, so an
                // operation held under the configured start is discharged here, exactly once, after
                // the worker's completion; the finally's !ownerStarted path then no-ops.
                session.ReleaseHeldRead("configured start failed");
            }
            var failureMessage = (session.StartingConfiguration is { } failedDraft ? "Could not start " + failedDraft.Name + ": " : "BASIC runtime failed: ")
                + failure.GetBaseException().Message;
            Diagnostics.StartLog.Write(configurationStore.FilePath, "Start failed: " + failureMessage);
            Diagnostics.StartLog.Write(configurationStore.FilePath, "Start exception: " + failure.ToString().Replace("\r", "").Replace("\n", " | "));
            if (!closing && ReferenceEquals(CurrentSession, session))
                this.FindControl<TextBlock>("StatusLine")!.Text = failureMessage;
        }
        finally
        {
            coldStartsInFlight--;
            // Proven owner drain, symmetric with the cold-volume admission above: it fires only when
            // the actual owner never started, i.e. the work left in flight can never get an owner
            // reply. On the normal path the owner took over and an in-flight launch-disk operation is
            // the owner's own work, acknowledged by its real owner outcome (the panel mount), never by
            // this drain: draining it mid-read would clobber a live insert.
            if (!ownerStarted)
            {
                coldVolume?.Admission.CancelBeforeEnqueue();
                session.ReleaseHeldRead("boot aborted");
            }
        }
    }

    // The one shared media panel's hooks, bound to a session's host: a member is not the
    // terminal account of the parent operation, and a read never releases a fence.
    private void BindMediaPanel(Workspace.MachineSession session, Media.MediaPanel media, Media.DiskWritebackHost host)
    {
        BindLibraryRemaps(session, media.Hdd);
        media.TryWritebackEject = host.TryEjectAsync;
        media.DiskMounted = async (slot, path, bytes, reply, identity) =>
        {
            // The hook Task is the host's accounting/bind I/O itself (NoteMounted awaits the
            // writeback coordinator's async BindAsync -> InspectAsync), so the parent fence is
            // held across it: capture the same operation identity, await the accounting, and
            // release only once it has completed and every expected member is terminal. A
            // lost-outcome refusal (no hook) waits for a named host drain.
            var operation = session.HeldOperation;
            await host.NoteMounted(slot, path, bytes, reply, identity, media.StoredDescriptorFor?.Invoke(slot));
            if (operation is not null && !operation.Released
                && operation.Member(slot) is { } member
                && member.CurrentPhase == Media.DiskReadReservation.Phase.Terminal
                && operation.AllMembersTerminal)
                await session.ReleaseHeldReadAsync(operation, "all members accounted");
        };
        media.WritebackBeforeClose = host.BeforeCloseAsync;
        media.DiskReadReserver = session.ReserveHeldReadAsync;
        media.DiskReadCompleted = (reservation, _, reply) =>
        {
            Media.DiskReadReservation.HostFileIdentity? identity = null;
            if (reply is not null) identity = reservation.CompleteAfterOwnerOutcome(reply);
            else reservation.CompleteAfterRefusal();
            // A member terminal releases no fence: the parent operation retains the single
            // fence until its own release (last member accounted, or a named host drain).
            return Task.FromResult(identity);
        };
        media.DiskReadHeldFor = slot => session.HeldOperation is { } op && op.IsMember(slot);
        // Cross-tab source protection: the panel is shared, so a save from the selected tab is
        // refused against any other open session's protected mounted source, not only the
        // selected session's own. Closed sessions drop out of the refusal set with their media.
        media.PathIsProtectedOriginal = path => AllSessions().Any(s => !s.Closed && s.PathIsProtectedOriginal(path));
        // O1: the panel admits a raw sector image only under the slot's stored, validated
        // descriptor; a slot without one keeps the legacy .dsk gate.
        media.StoredDescriptorFor = session.StartingConfiguration is { } slotConfiguration
            ? slot => (slot == Media.DiskSlot.A ? slotConfiguration.DiskA : slotConfiguration.DiskB)?.Descriptor.ToDescriptor()
            : null;
    }

    private async Task ApplyLaunchDisksAsync(Media.MediaPanel media, Workspace.MachineSession session)
    {
        try
        {
            var diskStatus = await LaunchStartup.ApplyDisksAsync(launchOptions,
                (slot, path) => media.InsertLaunchDiskAsync(path, slot));
            if (!closing && diskStatus is not null)
                this.FindControl<TextBlock>("StatusLine")!.Text = diskStatus;
            else if (!closing && session.WritebackHost is { ReportsTarget: true } host)
            {
                var status = this.FindControl<TextBlock>("StatusLine")!;
                var report = host.StatusLine();
                if (status.Text?.Contains(report, StringComparison.Ordinal) != true)
                    status.Text = string.IsNullOrEmpty(status.Text) ? report : status.Text + "; " + report;
            }
        }
        catch (Exception ex)
        {
            if (!closing)
                this.FindControl<TextBlock>("StatusLine")!.Text =
                    "BASIC runtime failed: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Tape volume launch actions (hc2000-tape-mount-cli): the parsed --tape/--tape-mount/
    /// --tape-commit/--tape-discard go through the SAME MediaBridge lane as the App tape buttons, so a
    /// headless run mounts, commits or discards and a refusal carries the lane's own wording on the
    /// StatusLine line. The machine keeps booting; a refused tape action is reported, not fatal
    /// (the disk pattern).</summary>
    private async Task ApplyLaunchTapeAsync(Media.MediaPanel media)
    {
        try
        {
            var tapeStatus = await LaunchStartup.ApplyTapeLaunchAsync(launchOptions,
                async command => await media.UIThreadSendAsync(command));
            if (!closing && tapeStatus is not null)
                this.FindControl<TextBlock>("StatusLine")!.Text = tapeStatus;
        }
        catch (Exception ex)
        {
            if (!closing)
                this.FindControl<TextBlock>("StatusLine")!.Text =
                    "BASIC runtime failed: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Workstation launch action (hc2000-combined-cpm-workstation): the parsed --workstation
    /// stages the composed A: system disk through the SAME MediaBridge lane as the App disk insert,
    /// after the tape launch actions mounted the volume; a refusal is reported on the StatusLine
    /// and the machine keeps booting (the disk/tape pattern).</summary>
    private async Task ApplyLaunchWorkstationAsync(Media.MediaPanel media)
    {
        try
        {
            var workstationStatus = await LaunchStartup.PrepareWorkstationAsync(launchOptions,
                command => media.UIThreadSendAsync(command));
            if (!closing && workstationStatus is not null)
                this.FindControl<TextBlock>("StatusLine")!.Text = workstationStatus;
        }
        catch (Exception ex)
        {
            if (!closing)
                this.FindControl<TextBlock>("StatusLine")!.Text =
                    "BASIC runtime failed: " + ex.GetBaseException().Message;
        }
    }

    public MainWindow() : this(null) { }

    /// <summary>The App's own settings store: with no settings file the first machine is a new instance
    /// (hc2000-full-workstation-default).</summary>
    internal static Settings.SettingsStore DefaultSettingsStore() => new() { NewInstallationDefaults = true };

    public MainWindow(LaunchOptions? launch, string? assetRoot = null, Settings.SettingsStore? settingsStore = null)
    {
        launchOptions = launch;
        assetRootOverride = assetRoot;
        InstalledMediaRoots = () => Explorer.InstalledMediaDirectory.Roots(assetRootOverride);
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ContentControl>("MediaNoticeHost")!.Content = this.FindControl<Media.MediaPanel>("MediaControls")!.TakeNotice();
        Title = "HC2000";
        Width = 800;
        Height = 750;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, MediaDragOver);
        AddHandler(DragDrop.DropEvent, MediaDrop);
        WatchInputRejected(implicitSession);
        configurationStore = settingsStore ?? DefaultSettingsStore();
        var initialSettings = configurationStore.Load().Settings;
        ApplyTheme(initialSettings.Theme);
        WireMediaHomeList();
        WireMediaFullView();
        prestart = new Configuration.PrestartPanel(configurationStore, launch, assetRoot: assetRoot);
        this.FindControl<ContentControl>("PrestartConfiguration")!.Content = prestart;
        BindPrestartEvents(prestart);
        this.FindControl<Button>("PoweredOffStart")!.Click += async (_, _) => await prestart.StartAsync();
        // A refused CLI hardware set shows the catalog sentence and stays open in Draft; nothing boots.
        prestart.IsVisible = !Program.Z80VisualDemoEnabled && !Stage1SpectrumDemo.Enabled && !Program.X2BenchmarkLaunchEnabled
            && (prestart.LaunchRefusal is not null || (launch?.Start != true && launch?.HddEnabled != true));
        reconfigure = new Configuration.ReconfigurePanel();
        this.FindControl<ContentControl>("ReconfigureConfiguration")!.Content = reconfigure;
        reconfigure.ApplyRequested = ApplyRunningDraftAsync;
        reconfigure.BorderChanged += SetRequestedBorder;
        ApplyBorder(initialSettings.Border);
        this.FindControl<Button>("NewMachine")!.Click += (_, _) => OpenNewMachineDraft();
        // F4: the side-rail icon buttons select their panel; the toggle button and Ctrl+B open
        // or collapse it. The panel auto-collapses when the window is narrower than 1100 px.
        this.FindControl<Button>("RailToggle")!.Click += (_, _) => ToggleSideRail();
        this.FindControl<Button>("RailMachine")!.Click += (_, _) => SelectRailPanel("Machine");
        this.FindControl<Button>("RailMedia")!.Click += (_, _) => SelectRailPanel("Media");
        this.FindControl<Button>("RailSnapshots")!.Click += (_, _) => SelectRailPanel("Snapshots");
        this.FindControl<Button>("RailInput")!.Click += (_, _) => SelectRailPanel("Input");
        lastWindowWidth = Width;
        if (Width < RailAutoCollapseWidth && !prestart.IsVisible) railOpen = false;
        this.SizeChanged += (_, _) =>
        {
            presentGateLastResizeUtcTicks = DateTime.UtcNow.Ticks;
            HandleWindowWidthChanged(Width);
        };
        UpdateRailPanelWidth();
        BindReconfigureView();
        AddInputEvidenceControls();
        AddHandler(KeyDownEvent, MachineKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, MachineKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var profilePanel = this.FindControl<Media.ProfileRomPanel>("ProfileRomControls")!;
        // The legacy registry panel is never a machine-selection or recovery path.
        profilePanel.IsVisible = false;
        profilePanel.IsEnabled = false;
        this.FindControl<Button>("MachineReset")!.Click += (_, _) => ResetWholeChipMachine();
        if (Program.Z80VisualDemoEnabled)
        {
            Title = "HC2000 — Z80 visual program (nonboot demo)";
            this.FindControl<Media.MediaPanel>("MediaControls")!.IsEnabled = false;
            this.FindControl<Media.ProfileRomPanel>("ProfileRomControls")!.IsEnabled = false;
            this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.IsEnabled = false;
            this.FindControl<StackPanel>("Z80DemoControls")!.IsVisible = true;
            this.FindControl<Button>("Z80DemoRerun")!.Click += async (_, _) => await RestartZ80DemoAsync();
            AttachRuntime(Runtime.Z80VisualDemo.CreateWorker());
            this.FindControl<TextBlock>("StatusLine")!.Text = "Running actual Z80 program; Reset / Run again starts fresh nonboot session";
        }
        if (Stage1SpectrumDemo.Enabled)
        {
            Title = "HC2000 — Stage 1 Spectrum renderer fixture (no CPU/boot)";
            var screen = this.FindControl<Runtime.VideoSurface>("Screen")!;
            screen.Present(Stage1SpectrumDemo.CreateFrame());
            screen.SetScale(2);
            this.FindControl<TextBlock>("StatusLine")!.Text = "Stage 1 fixture: renderer proof only; no CPU or boot";
            var overlay = this.FindControl<CheckBox>("CellOverlayToggle")!;
            var boundaries = this.FindControl<CellBoundaryOverlay>("CellBoundaries")!;
            overlay.IsCheckedChanged += (_, _) => boundaries.IsVisible = overlay.IsChecked == true;
        }
        // Ordinary open stays Draft. Explicit CLI --start and the existing cold HDD path retain their semantics.
        Opened += (_, _) =>
        {
            var session = CurrentSession;
            if (launchOptions?.HddEnabled == true && prestart.LaunchRefusal is null && session.Worker is null && !Program.Z80VisualDemoEnabled && !Stage1SpectrumDemo.Enabled)
            {
                this.FindControl<TextBlock>("StatusLine")!.Text = "HDD opt-in: prepare images, then start a NEW machine. No guest HDD BIOS or live switching.";
                this.FindControl<Media.MediaPanel>("MediaControls")!.Hdd.ConfigureCold(images =>
                {
                    session.BasicReplacementCompletion = BootWholeChipMachine(images);
                    return session.BasicReplacementCompletion;
                }, (volume, drive) =>
                {
                    session.BasicReplacementCompletion = BootWholeChipMachine(coldVolume: volume, coldDrive: drive);
                    return session.BasicReplacementCompletion;
                }, volume =>
                {
                    session.BasicReplacementCompletion = BootWholeChipMachine(coldVolume: volume, useColdMap: true);
                    return session.BasicReplacementCompletion;
                });
            }
            else if (prestart.LaunchRefusal is { } refusal)
                this.FindControl<TextBlock>("StatusLine")!.Text = refusal;
            else if (Program.X2BenchmarkLaunchEnabled)
            {
                _ = StartX2BenchmarkAsync();
            }
            else if (launchOptions?.Start == true || Program.Z80VisualDemoEnabled || Stage1SpectrumDemo.Enabled)
            {
                // --start boots the adopted saved draft (including a recorded named-HDD C:–F:
                // profile) through the same configured-start path the Start button takes
                // (adr-saved-draft-launch-638412), not the plain v2 profile.
                if (launchOptions?.Start == true && session.StartingConfiguration is null)
                {
                    session.StartingConfiguration = prestart.InitialConfiguration;
                    // A named-HDD draft never cold-reads the launch A/B: defer them to the warm
                    // lane for exactly this acknowledged launch start.
                    if (session.StartingConfiguration.Hdd.Any(h => h.Volume is not null)
                        && (launchOptions!.DiskA is not null || launchOptions.DiskB is not null))
                        launchDiskDeferral = session;
                }
                session.BasicReplacementCompletion = BootWholeChipMachine();
            }
            else if (session.Worker is null)
                this.FindControl<TextBlock>("StatusLine")!.Text = "Set up this machine, then start it.";
            if (session.Worker is not null || launchOptions?.Start == true)
                this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
        };

        KeyboardInput.Rejected += message => this.FindControl<TextBlock>("StatusLine")!.Text = message;
        var inputModeBox = this.FindControl<ComboBox>("InputModeBox")!;
        // Reflect the adapter's startup policy instead of maintaining a second XAML default.
        inputModeBox.SelectedIndex = KeyboardInput.Mode == KeyMappingMode.Positional ? 1 : 0;
        inputModeBox.SelectionChanged += (_, _) =>
        {
            CurrentSession.KeyboardInput.Mode = inputModeBox.SelectedIndex == 1
                ? KeyMappingMode.Positional : KeyMappingMode.Symbolic;
            RefreshKeyboardReference();
        };
        inputModeBox.DropDownClosed += (_, _) => this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
        WireX2Screen();
        WireX6Gateway();
        WireTurbo();
        WireSpeedAdvice();
        WireX2Benchmark();
        WireLendingFrame();
        var presentationModeBox = this.FindControl<ComboBox>("PresentationModeSelector");
        if (presentationModeBox is not null)
        {
            presentationModeBox.SelectedIndex = (int)CurrentSession.PresentationMode;
            presentationModeBox.SelectionChanged += (_, _) =>
            {
                var mode = (PresentationMode)Math.Clamp(presentationModeBox.SelectedIndex, 0, 2);
                CurrentSession.PresentationMode = mode;
                var screen = this.FindControl<Runtime.VideoSurface>("Screen");
                if (screen is not null) screen.PresentationMode = mode;
            };
            presentationModeBox.DropDownClosed += (_, _) => this.FindControl<Runtime.VideoSurface>("Screen")?.Focus();
        }
        Deactivated += (_, _) => KeyboardInput.ReleaseFocus(InputReleaseOrigin.Deactivated);
        presentation.Tick += (_, _) =>
        {
            RefreshInputEvidenceSession();
            var session = CurrentSession;
            RefreshRateLine(session);
            RenderTurbo(session);
            RenderSpeedAdvice(session);
            if (PresentTracePath is not null)
            {
                var tick = Interlocked.Increment(ref presentTickCount);
                try
                {
                    var traced = TakeSelectedVideo(session);
                    string hashPart = "";
                    if (traced is not null && tick % 25 == 0)
                    {
                        var hash = Fnv1a(traced.Pixels.Span);
                        hashPart = $" hash={hash:x8} changed={(presentTraceHashed && hash != presentTraceLastHash ? 1 : 0)}";
                        presentTraceLastHash = hash; presentTraceHashed = true;
                    }
                    System.IO.File.AppendAllText(PresentTracePath,
                        $"tick={tick} t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} frame={(traced is null ? "null" : traced.MasterTick.ToString())}{hashPart}\n");
                    if (traced is not null)
                    {
                        LatestPresentedFrame = traced;
                        LatestPresentedGeneration = session.ObservedVideoGeneration;
                        var replaced = this.FindControl<Runtime.VideoSurface>("Screen")!.Present(traced);
                        session.Worker?.Recycle(replaced);
                    }
                    PresentX2Screen(session, traced);
                    // Guest-state probe (diagnosis-only): a request every 150 ticks, read back on
                    // the next tick; the boundary copy lands between them.
                    if (tick % 150 == 0) session.Worker?.RequestDebugSnapshot();
                    else if (tick % 150 == 1)
                    {
                        var snap = session.Worker?.TakeLatestDebugSnapshot();
                        if (snap is not null)
                        {
                            var l = snap.Latches;
                            var mem = snap.Memory;
                            System.IO.File.AppendAllText(PresentTracePath,
                                $"DBG tick={tick} pc={snap.Registers.Pc:x4} t={snap.CpuTStates} " +
                                $"lat=7e:{l.Port7E:x2} cpm:{(l.Cpm ? 1 : 0)} shd:{(l.Shadow ? 1 : 0)} b:{l.BankSelector:x2} " +
                                $"pb:{l.PagedBank:x4} pctl:{l.PagedControl:x2} pen:{(l.PagedEnabled ? 1 : 0)} ptrans:{(l.PagedTransfer ? 1 : 0)} " +
                                $"m4000={Fnv1aB(mem, 0x4000, 0x800):x8} mc000={Fnv1aB(mem, 0xc000, 0x800):x8}\n");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    System.IO.File.AppendAllText(PresentTracePath, $"tick={tick} PRESENT-THREW {ex.GetType().Name}: {ex.Message}\n");
                }
                return;
            }
            var presentStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            var presentingNow = VideoPresentPolicy.ShouldPresent(
                DateTime.UtcNow.Ticks,
                presentGateLastResizeUtcTicks,
                presentStamp,
                presentGateLastStampTicks,
                ref presentGateBacklogSkips);
            presentGateLastStampTicks = presentStamp;
            // Skip both presents while a resize is in flight. The X2 screen is the same GL work;
            // a skipped frame stays in the single-slot mailbox for the next tick.
            if (presentingNow)
                PresentTick();
            if (session.BasicRuntimeActive && !checkingClose && ++session.MediaPollTicks % 10 == 0)
                _ = this.FindControl<Media.MediaPanel>("MediaControls")!.PollAsync();
            if (session.Worker is { Completion: { IsCompleted: true } } runtime
                && (!displayedStops.TryGetValue(session, out var displayed) || !ReferenceEquals(displayed, runtime)))
            {
                displayedStops[session] = runtime;
                if (!AllSessions().Any(s => s.Worker is { Completion.IsCompleted: false })) presentation.Stop();
                session.ReleaseHeldRead("worker completed");
                this.FindControl<Media.MediaPanel>("MediaControls")!.Disconnect();
                this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
                this.FindControl<TextBlock>("StatusLine")!.Text = runtime.Completion.IsFaulted
                    ? "Runtime stopped: " + runtime.Completion.Exception!.GetBaseException().Message : "Runtime stopped";
                this.FindControl<TextBlock>("RateLine")!.IsVisible = false;
                if (session.BasicRuntimeActive)
                {
                    session.BasicRuntimeActive = false;
                    this.FindControl<Button>("MachineReset")!.IsEnabled = false;
                    BindReconfigureView();
                }
                RefreshTabsView();
            }
        };
        // hc2000-accelerated-clock-domains: the honest main-CPU rate line, owned by the presentation
        // tick (20 ms). It shows the requested choice, the achieved T/s and frame cadence over the
        // meter's stated 500 ms active window, and the warm-up and overload states — achieved speed
        // is reported, never promised. It sits in its own status-bar cell next to the (transient)
        // StatusLine, so the two never fight over one text box.
        void RefreshRateLine(Workspace.MachineSession session)
        {
            var rateLine = this.FindControl<TextBlock>("RateLine")!;
            if (session.Worker is not { Completion: { IsCompleted: false } } worker)
            {
                rateLine.IsVisible = false;
                return;
            }
            var sample = worker.RateLatest;
            rateLine.IsVisible = true;
            if (sample is null)
            {
                rateLine.Text = "CPU —";
                return;
            }
            var text = sample.WarmingUp
                ? $"CPU {MainCpuChoiceLabel(sample.RequestedTStatesPerSecond)}: warming up (no rate for {sample.WindowMs / 1000.0:0.#} s)"
                : $"CPU {MainCpuChoiceLabel(sample.RequestedTStatesPerSecond)}: {FormatTStates(sample.AchievedTStatesPerSecond)} achieved · {sample.FrameHz:0.##} Hz frames · {sample.WindowMs / 1000.0:0.#} s window";
            if (!sample.WarmingUp)
            {
                if (sample.Overloaded) text += " · OVERLOADED (host cannot keep up; guest time is never skipped)";
                else text += $" · requested {FormatTStates((double)sample.RequestedTStatesPerSecond)}";
            }
            var switchStatus = worker.MainCpuSwitchStatus;
            rateLine.Text = switchStatus is null ? text : switchStatus + " | " + text;
        }

        // The requested rate as a choice label: an admitted target, the faithful class, or a legacy
        // cpu-turbo multiple of the faithful rate — never a bare number alone.
        static string MainCpuChoiceLabel(long requested)
        {
            foreach (var speed in Hc2000.Machine.MainCpuSpeedInfo.All)
            {
                if (Hc2000.Machine.MainCpuSpeedInfo.TStatesPerSecond(speed) == requested)
                    return speed == Hc2000.Machine.MainCpuSpeed.Faithful
                        ? "faithful (3.5 MHz class)" : $"{Hc2000.Machine.MainCpuSpeedInfo.Setting(speed)} target";
            }
            const long faithful = 3_546_895;
            if (requested > faithful && requested % faithful == 0)
                return $"faithful ×{(requested / faithful).ToString(System.Globalization.CultureInfo.InvariantCulture)} (cpu turbo)";
            return $"{FormatTStates((double)requested)} requested";
        }

        static string FormatTStates(double tStatesPerSecond) => tStatesPerSecond >= 1_000_000
            ? $"{tStatesPerSecond / 1_000_000.0:0.#} M T/s"
            : $"{tStatesPerSecond / 1_000.0:0.#} k T/s";

        Closing += async (_, e) =>
        {
            if (closeConfirmed || !CurrentSession.BasicRuntimeActive || CurrentSession.Worker?.Completion.IsCompleted != false) return;
            e.Cancel = true;
            if (checkingClose) return;
            checkingClose = true;
            try
            {
                if (await this.FindControl<Media.MediaPanel>("MediaControls")!.CanCloseAsync())
                { closeConfirmed = true; Close(); }
            }
            finally { checkingClose = false; }
        };
        Closed += async (_, _) =>
        {
            closing = true;
            presentation.Stop();
            x2Screen?.Close();
            var shutdowns = new List<Task>();
            foreach (var session in AllSessions())
            {
                session.Retire(); // generation bump, focus release, evidence end, shutdown enqueued
                var old = session.Worker;
                session.DetachWorker();
                old?.Dispose();
                shutdowns.Add(AwaitShutdownAndDiscardPendingAsync(session, old, session.BasicReplacementCompletion));
            }
            ResetInputEvidenceView();
            shutdownCompletion = Task.WhenAll(shutdowns);
            // Proven owner drain before the named release: each retired worker and its in-flight
            // replacement/boot (including a configured prestart read under the fence) are quiescent
            // before that session's fence goes away; the release itself also awaits any started member read.
            try { await shutdownCompletion; }
            catch { /* the replacement records its own failure before close */ }
            foreach (var session in AllSessions()) session.ReleaseHeldRead("window closed");
            this.FindControl<Media.MediaPanel>("MediaControls")!.Disconnect();
            this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
            this.FindControl<Runtime.VideoSurface>("Screen")!.Dispose();
        };
        BindExplorer(CurrentSession, workspace.Selected?.Configuration ?? CurrentSession.StartingConfiguration);
        this.FindControl<Media.MediaPanel>("MediaControls")!.RefreshDiskLibraryList();
    }

    // Presentation preference only: no reset, guest input or mounted-media change.
    internal void ApplyBorder(string border)
    {
        var mode = Runtime.VideoSurface.ParseBorderMode(border);
        var normalized = Runtime.VideoSurface.FormatBorderMode(mode);
        var screen = this.FindControl<Runtime.VideoSurface>("Screen");
        if (screen is not null) screen.BorderMode = mode;
        prestart?.SyncBorder(normalized);
        reconfigure?.SyncBorder(normalized);
    }

    internal void SetRequestedBorder(string border)
    {
        var mode = Runtime.VideoSurface.ParseBorderMode(border);
        var normalized = Runtime.VideoSurface.FormatBorderMode(mode);
        ApplyBorder(normalized);
        var loaded = configurationStore.Load();
        if (loaded.Warning is not null)
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = "Border applied, not saved: " + loaded.Warning;
            return;
        }
        try { configurationStore.Save(loaded.Settings with { Border = normalized }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = "Border applied, not saved: " + ex.Message;
        }
    }

    private void SetRequestedZoom(int scale)
    {
        this.FindControl<Runtime.AspectRatioViewport>("ScreenViewport")!.RequestedScale = scale;
        if (workspace.CaptureSelectedDraft() is { } target && workspace.Selected is { } tab)
        {
            workspace.TryUpdateDraft(target, tab.Configuration with { Scale = scale });
            BindReconfigureView();
        }
        var loaded = configurationStore.Load();
        // A malformed file is never silently replaced by an accelerator.
        if (loaded.Warning is not null)
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = "Zoom applied, not saved: " + loaded.Warning;
            return;
        }
        try { configurationStore.Save(loaded.Settings with { Scale = scale }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = "Zoom applied, not saved: " + ex.Message;
        }
    }

    // Adds a machine tab and binds the shared view set to it; no boot here.
    internal bool TryAddTabView(Configuration.MachineConfiguration configuration, out Workspace.MachineTabContext? tab, out string? refusal)
    {
        if (!workspace.TryAddDraft(configuration, out tab, out refusal) || tab is null) return false;
        workspace.Select(tab.TabId);
        WatchInputRejected(tab.Session);
        BindSelectedSessionView();
        SyncInputView();
        RefreshTabsView();
        return true;
    }

    private async Task<bool> StartConfigurationAsync(Configuration.MachineConfiguration configuration)
    {
        if (closing || startingConfiguration) return false;
        configuration.ValidateEffective();
        var previousSelection = workspace.Selected;
        if (!TryAddTabView(configuration, out var tab, out var refusal))
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = refusal ?? "Max 4 machines";
            return false;
        }
        startingConfiguration = true;
        RefreshShellView();
        try
        {
            var session = tab!.Session;
            session.StartingConfiguration = configuration;
            session.KeyboardInput.ReleaseFocus(InputReleaseOrigin.Reset);
            session.KeyboardInput.Mode = configuration.KeyboardMode == "Positional" ? KeyMappingMode.Positional : KeyMappingMode.Symbolic;
            this.FindControl<Runtime.AspectRatioViewport>("ScreenViewport")!.RequestedScale = configuration.Scale;
            session.BasicReplacementCompletion = BootWholeChipMachine();
            await session.BasicReplacementCompletion;
            if (session.Closed || session.Worker is not { Completion.IsCompleted: false } || !session.BasicRuntimeActive || closing)
            {
                var wasSelected = ReferenceEquals(workspace.Selected, tab);
                // Reuse the existing drain/retire path for ONLY this candidate; never retire the
                // previously selected runtime as a way to make room for a failed new Start.
                if (!session.Closed && !closing) await CloseTabAsync(tab);
                if (wasSelected && previousSelection is not null) workspace.Select(previousSelection.TabId);
                BindSelectedSessionView();
                SyncInputView();
                RefreshTabsView();
                return false;
            }
            RefreshTabsView();
            if (ReferenceEquals(workspace.Selected, tab)) this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
            return true;
        }
        finally { startingConfiguration = false; RefreshShellView(); }
    }

    /// <summary>Apply a running draft and reset only the target machine (UI thread, synchronous).
    /// The draft must be the target tab's current revision, keep the mounted A/B media and writeback
    /// choices unchanged (ReconfigureApply policy), and pass the writeback binding-preserving reset
    /// guard. A refusal changes nothing on any machine; no persistence happens here.</summary>
    internal string? ApplyRunningDraft(MachineDraftTarget target, Configuration.MachineConfiguration draft)
    {
        if (closing) return "the window is closing.";
        if (!workspace.TryResolveDraft(target, out var tab) || tab is null)
            return "the machine draft changed while this apply was in flight.";
        var session = tab.Session;
        if (session.Closed || session.ReplacingRuntime || !session.BasicRuntimeActive || session.Worker?.Completion.IsCompleted != false)
            return "that machine is not running.";
        if (!Configuration.ReconfigureApply.TryPlan(tab.Configuration, draft, out var planError))
            return planError;
        var previous = tab.Configuration;
        // Demo #189 (capture 2681): applying a fully identical draft is a no-op. The machine keeps
        // running untouched (no reset, no controls), so the writeback reset guard does not apply.
        // Only the four live fields (main-CPU target, wall pace, keyboard, scale) can differ here:
        // the cold fields and the mounted media are refused above by TryPlan. The draft is already
        // equal to the current configuration, so nothing is committed and the draft revision is NOT
        // bumped: the panel's captured target stays valid for the next apply.
        if (previous.MainCpuTarget == draft.MainCpuTarget && previous.SpeedPercent == draft.SpeedPercent
            && previous.KeyboardMode == draft.KeyboardMode && previous.Scale == draft.Scale)
        {
            if (ReferenceEquals(workspace.Selected, tab))
                this.FindControl<TextBlock>("StatusLine")!.Text = "No changes; this machine kept running. No settings saved.";
            return null;
        }
        // hc2000-accelerated-clock-domains: the live main-CPU switch lane does not reset the machine,
        // so the writeback reset guard (a reset-safety fence over the mounted media) only applies
        // to the reset lanes, exactly as before.
        var speedOnly = previous.MainCpuTarget != draft.MainCpuTarget
            && previous.SpeedPercent == draft.SpeedPercent
            && previous.KeyboardMode == draft.KeyboardMode;
        if (!speedOnly && session.WritebackHost is { } host && !host.ResetGuardClear())
            return "a writeback fence is unresolved; the machine was not reset and its mounted media and bindings were retained.";
        if (!workspace.TryUpdateDraft(target, draft))
            return "the machine draft changed while this apply was in flight.";
        // The reset is the owner's work: the new pacing mode (only when it changed) and the reset
        // are enqueued on the TARGET session's own FIFO only; the other tabs' queues are untouched.
        // hc2000-accelerated-clock-domains: a main-CPU clock change is the ONE diff that does not
        // reset the machine: the owner applies the live switch at the next safe instruction
        // boundary (RAM, media and the guest all continue). If other live fields changed too, the
        // reset goes first and the switch follows it, so the recomposed machine receives it.
        var speedChanged = previous.MainCpuTarget != draft.MainCpuTarget;
        var wallChanged = previous.SpeedPercent != draft.SpeedPercent;
        var keyboardChanged = previous.KeyboardMode != draft.KeyboardMode;
        if (wallChanged)
            session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: draft.SpeedPercent));
        if (!speedChanged)
        {
            // The existing live-field contract: one machine-only reset, everything else kept.
            session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.Reset));
            session.ReleaseHeldRead("running configuration applied; machine reset");
            session.KeyboardInput.ReleaseFocus(InputReleaseOrigin.Reset);
            session.KeyboardInput.Evidence.EndSession();
        }
        else if (wallChanged || keyboardChanged)
        {
            // The other changed field needs a reset; the recomposed machine carries its ORIGINAL
            // target, so the switch is enqueued AFTER the reset, where the owner applies it.
            session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.Reset));
            session.ReleaseHeldRead("running configuration applied; machine reset");
            session.KeyboardInput.ReleaseFocus(InputReleaseOrigin.Reset);
            session.KeyboardInput.Evidence.EndSession();
            session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.SetMainCpuSpeed,
                MainCpuSpeedIndex: (int)Hc2000.Machine.MainCpuSpeedInfo.Resolve(draft.MainCpuTarget, draft.Profile)));
        }
        else
        {
            // Live speed switch only: no reset; the machine keeps running and the status line
            // reports the switch outcome once the owner's boundary has applied or refused it.
            session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.SetMainCpuSpeed,
                MainCpuSpeedIndex: (int)Hc2000.Machine.MainCpuSpeedInfo.Resolve(draft.MainCpuTarget, draft.Profile)));
        }
        if (ReferenceEquals(workspace.Selected, tab))
        {
            if (previous.Scale != draft.Scale)
                this.FindControl<Runtime.AspectRatioViewport>("ScreenViewport")!.RequestedScale = draft.Scale;
            if (!speedChanged || wallChanged || keyboardChanged)
            {
                ResetInputEvidenceView();
                this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
                LatestPresentedFrame = null;
                SyncInputView();
            }
            this.FindControl<TextBlock>("StatusLine")!.Text = speedChanged && !wallChanged && !keyboardChanged
                ? "Applied; the main CPU clock is switching live — no reset, this machine kept running. No settings saved."
                : "Applied; only this machine was reset. No settings saved.";
        }
        return null;
    }

    internal Task<string?> ApplyRunningDraftAsync(MachineDraftTarget target, Configuration.MachineConfiguration draft)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        var error = ApplyRunningDraft(target, draft);
        if (error is null) BindReconfigureView(); // rebind the committed draft so a further apply starts from it
        return Task.FromResult(error);
    }

    // The reconfigure panel binds to the selected running tab's current draft; a draft, closing or
    // completed tab hides it. Selection and lifecycle changes rebind it here.
    private void BindReconfigureView()
    {
        RefreshShellView();
        var tab = workspace.Selected;
        var running = tab is not null && !tab.Session.Closed && !tab.Session.ReplacingRuntime
            && tab.Session.BasicRuntimeActive && tab.Session.Worker is { Completion: { IsCompleted: false } };
        var networkPanel = this.FindControl<Network.CpNetPanel>("CpNetControls")!;
        var networkBridge = running ? tab!.Session.NetworkBridge : null;
        if (!ReferenceEquals(displayedNetworkBridge, networkBridge))
        {
            networkPanel.Unbind();
            displayedNetworkBridge = networkBridge;
            if (networkBridge is not null) networkPanel.Attach(networkBridge, tab!.Session.NetworkServerBridge, tab.Session.NetworkServerSettings);
        }
        networkPanel.IsVisible = networkBridge is not null;
        // The machine with the X6 card: the selected tab's, or the window's own session started with --start.
        var x6Session = running ? tab!.Session
            : tab is null && CurrentSession is { BasicRuntimeActive: true, Closed: false, Worker.Completion.IsCompleted: false } own ? own : null;
        BindX6Gateway(x6Session is { HasX6Net: true } ? x6Session : null, running ? tab : null);
        if (!running) { reconfigure.IsVisible = false; return; }
        if (workspace.CaptureSelectedDraft() is { } captured)
        {
            reconfigure.Bind(captured, tab!.Configuration);
            reconfigure.SyncBorder(configurationStore.Load().Settings.Border);
        }
        reconfigure.IsVisible = true;
    }

    private void AddInputEvidenceControls()
    {
        var read = new Button { Content = "Read bounded input evidence", Focusable = false };
        var clear = new Button { Content = "Clear input evidence", Focusable = false };
        var contents = new StackPanel { Spacing = 8 };
        contents.Children.Add(inputEvidenceToggle); contents.Children.Add(read);
        contents.Children.Add(clear); contents.Children.Add(inputEvidenceText);
        this.FindControl<StackPanel>("InputPanelBody")!.Children.Add(
            new Expander { Header = "Prospective input evidence — no guest-effect proof", Content = contents });
        inputEvidenceToggle.IsCheckedChanged += (_, _) =>
        {
            if (!syncingInputEvidenceUi) SetInputEvidenceEnabled(inputEvidenceToggle.IsChecked == true);
        };
        read.Click += (_, _) =>
        {
            var snapshot = ReadWindowInputEvidence();
            inputEvidenceText.Text = snapshot.Coverage + $"\nSession={snapshot.Session ?? "none"}; enabled={snapshot.Enabled}; dropped={snapshot.Dropped}\n" +
                string.Join("\n", snapshot.Records.Select(r =>
                    $"{r.Order} host={r.HostTimestamp}/{r.HostFrequency}Hz {r.Stage} {r.Origin} {r.Key} {r.Modifiers} {r.Mode} {r.Outcome} chord=[{string.Join(",", r.Chord)}] queue={r.QueueSequence}"));
        };
        clear.Click += (_, _) => { KeyboardInput.Evidence.Clear(); inputEvidenceText.Text = FrontendInputEvidence.Coverage; };
    }

    private void ResetInputEvidenceView()
    {
        syncingInputEvidenceUi = true;
        try { inputEvidenceToggle.IsChecked = false; inputEvidenceText.Text = FrontendInputEvidence.Coverage; }
        finally { syncingInputEvidenceUi = false; }
    }

    internal void RefreshInputEvidenceSession()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        var session = CurrentSession;
        var current = session.Worker;
        if (closing || session.ReplacingRuntime || current is null || current.Completion.IsCompleted || !current.ObservationSessionAvailable)
        {
            KeyboardInput.Evidence.EndSession();
            session.ObservedWorkerSession = -1;
            ResetInputEvidenceView();
            return;
        }
        var generation = current.SessionGeneration;
        if (session.ObservedWorkerSession != generation || session.ObservedRuntimeGeneration != session.RuntimeGeneration)
        {
            KeyboardInput.Evidence.BeginSession($"window-{session.InputEvidenceWindowId}/{session.RuntimeGeneration}/machine-{generation}", current.HostClock,
                () => current.ObservationSessionAvailable && !current.Completion.IsCompleted && current.SessionGeneration == generation);
            session.ObservedWorkerSession = generation; session.ObservedRuntimeGeneration = session.RuntimeGeneration;
            ResetInputEvidenceView();
        }
        if (!KeyboardInput.Evidence.Enabled) ResetInputEvidenceView();
    }

    internal bool SetInputEvidenceEnabled(bool enabled)
    {
        RefreshInputEvidenceSession();
        if (!enabled) KeyboardInput.Evidence.Disable();
        var accepted = enabled && KeyboardInput.Evidence.Enable();
        if (!accepted) ResetInputEvidenceView();
        syncingInputEvidenceUi = true;
        try { inputEvidenceToggle.IsChecked = accepted; }
        finally { syncingInputEvidenceUi = false; }
        return accepted;
    }

    internal FrontendInputSnapshot ReadWindowInputEvidence()
    {
        RefreshInputEvidenceSession();
        return KeyboardInput.Evidence.Read();
    }

    private async Task AwaitShutdownAndDiscardPendingAsync(Workspace.MachineSession session, Runtime.EmulationWorker? old, Task replacement)
    {
        if (old is not null)
        {
            try { await old.Completion; }
            catch { /* Closing retires a failed worker just like a completed one. */ }
        }
        try { await replacement; }
        catch { /* Replacement records the error in the UI before close. */ }
        // With no worker there is no retired consumer whose queue must be drained. Keep the
        // ordered ReleaseAll/Shutdown pair available to an owner/test consumer instead of
        // synchronously discarding the very release that Closing just enqueued.
        if (old is not null) session.InputQueue.DiscardPending();
    }

    private static string? DroppedDisk(DragEventArgs e)
    {
        // O2: the extension gate lives in the panel's descriptor-aware admission; drag offers any
        // single local file and the slot's stored descriptor decides acceptance.
        var files = e.DataTransfer.TryGetFiles();
        if (files?.Length != 1) return null;
        return files[0].TryGetLocalPath();
    }
    private void MediaDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedDisk(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }
    private async void MediaDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DroppedDisk(e) is { } path)
            await this.FindControl<Media.MediaPanel>("MediaControls")!.InsertDiskAsync(path);
    }

    private void MachineKeyDown(object? sender, KeyEventArgs e)
    {
        // App-wide tab navigation precedes editor filtering as well as guest dispatch.
        // Exact Ctrl-only chords leave plain Page keys and extra modifiers untouched.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            CycleTabView(e.Key == Key.PageUp ? -1 : 1);
            return;
        }
        // Named accelerators per the menu/shortcut ownership map own every focus, text editors
        // included: a draft field or the tape deck must never keep the New machine or the close
        // tab chord unreachable. Unmapped Ctrl+Shift chords still fall through to the guest.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            if (HandleCtrlShiftAccelerator(e)) return;
        if (IsTextEditor(e.Source)) return;
        // The focusable media-home Scan control owns its activation keys, not the guest.
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Enter or Key.Space
            && IsMediaHomeButtonInput(e.Source)) return;
        // F4/§3: the side-rail toggle (Ctrl+B) and the integer-zoom chords (Ctrl+0..Ctrl+4) are
        // owned by this shell (slice A), so they are handled before the guest sees the key.
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            if (e.Key == Key.B)
            {
                e.Handled = true;
                ToggleSideRail();
                return;
            }
            // The zoom chords' scope is the screen (design §3): they apply only while the screen
            // surface or its viewport holds focus; otherwise Ctrl+0..4 stays a guest control key.
            if (e.Key is >= Key.D0 and <= Key.D4 &&
                TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Runtime.VideoSurface or Runtime.AspectRatioViewport)
            {
                SetRequestedZoom(e.Key - Key.D0);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            PasteClipboard();
            return;
        }
        e.Handled |= KeyboardInput.KeyDown(e.Key, e.KeyModifiers);
    }

    private bool HandleCtrlShiftAccelerator(KeyEventArgs e)
    {
        if (e.Key == Key.N)
        {
            e.Handled = true;
            OpenNewMachineDraft();
            return true;
        }
        if (e.Key == Key.W)
        {
            e.Handled = true;
            if (workspace.Selected is { } selected) _ = CloseTabAsync(selected);
            return true;
        }
        if (e.Key == Key.R)
        {
            e.Handled = true;
            if (CurrentSession.BasicRuntimeActive && this.FindControl<Button>("MachineReset")?.IsEnabled == true)
                ResetWholeChipMachine();
            return true;
        }
        if (e.Key == Key.D)
        {
            e.Handled = true;
            OpenDebugger(null, null!);
            return true;
        }
        if (e.Key == Key.E)
        {
            e.Handled = true;
            OpenExplorerWorkspace();
            return true;
        }
        if (e.Key == Key.K)
        {
            e.Handled = true;
            OpenKeyboardReference(null, null!);
            return true;
        }
        // hc2000-speed-140-turbo-280: Turbo 280 MHz on or off for the selected running extended machine.
        if (e.Key == Key.T && ToggleTurbo())
        {
            e.Handled = true;
            return true;
        }
        // hc2000-ssh-terminal: the selected machine's X6 network gateway, on or off.
        if (e.Key == Key.O && ToggleX6Gateway())
        {
            e.Handled = true;
            return true;
        }
        if (e.Key is Key.OemComma or Key.OemPeriod)
        {
            e.Handled = true;
            OpenSettings(null, null!);
            return true;
        }
        // Preserve CP/M Ctrl+V (0x16); clipboard paste must use a distinct shortcut.
        if (e.Key == Key.S)
        {
            e.Handled = true;
            if (CurrentSession.WritebackHost is { } host) _ = host.ShortcutAsync().ContinueWith(done =>
            {
                if (done.IsFaulted) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    this.FindControl<TextBlock>("StatusLine")!.Text = host.StatusLine());
            }, TaskScheduler.Default);
            return true;
        }
        if (e.Key == Key.V)
        {
            e.Handled = true;
            PasteClipboard();
            return true;
        }
        if (e.Key == Key.G)
        {
            e.Handled = true;
            ToggleX2Screen();
            return true;
        }
        if (e.Key == Key.B)
        {
            e.Handled = true;
            _ = StartX2BenchmarkAsync();
            return true;
        }
        return false;
    }

    private void MachineKeyUp(object? sender, KeyEventArgs e)
    {
        KeyboardInput.KeyUp(e.Key); // Always release, even if focus moved into a text editor.
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!IsTextEditor(e.Source) && !IsMediaHomeButtonInput(e.Source))
            e.Handled |= KeyboardInput.TextInput(e.Text);
    }

    private static bool IsMediaHomeButtonInput(object? source) => source is Visual visual
        && visual.GetSelfAndVisualAncestors().OfType<Button>()
            .Any(button => button.GetVisualAncestors().Any(parent => parent is MediaHomeListPanel));

    private static bool IsTextEditor(object? source) => source is TextBox or Media.TapeDeckPanel or Configuration.PrestartPanel
        || source is Visual visual && visual.GetVisualAncestors().Any(v => v is TextBox or Media.TapeDeckPanel or Configuration.PrestartPanel);

    private async void PasteClipboard()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null && await clipboard.TryGetTextAsync() is { } text)
                KeyboardInput.Paste(text);
        }
        catch (Exception ex)
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = "Clipboard unavailable: " + ex.Message;
        }
    }

    private void PasteInput(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (KeyboardInput.Paste(this.FindControl<TextBox>("PasteTextBox")!.Text ?? ""))
            this.FindControl<TextBlock>("StatusLine")!.Text = "Paste queued; consumed only at emulation boundaries.";
    }

    private void OpenSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var settings = new Settings.SettingsWindow(configurationStore) { RequestedThemeVariant = RequestedThemeVariant };
        settings.ThemeSaved += theme =>
        {
            ApplyTheme(theme);
            settings.RequestedThemeVariant = RequestedThemeVariant;
            foreach (var owned in OwnedWindows) owned.RequestedThemeVariant = RequestedThemeVariant;
        };
        settings.BorderSaved += ApplyBorder;
        settings.ScaleSaved += scale => this.FindControl<Runtime.AspectRatioViewport>("ScreenViewport")!.RequestedScale = scale;
        settings.ShowDialog(this);
    }

    private void OpenDebugger(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        // No command sink: recording, exports and breakpoints stay visibly unavailable. The one
        // control this window drives, pause, goes through the selected session's existing FIFO.
        var window = new Debugging.DebuggerWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show(this);
        var bound = CurrentRuntime;
        if (bound is null) { window.Detach("No machine selected."); return; }
        _ = RefreshDebugger(window, CurrentSession, bound, () => closed);
    }

    /// <summary>Refreshed readback while the window is open: at most one outstanding capture demand,
    /// copies accepted only when stamped with the bound worker's current generation and a sequence
    /// newer than the last one shown. Started from the UI handler, so the delay continuations resume
    /// on the UI dispatcher where Publish and Detach require access.</summary>
    private async Task RefreshDebugger(Debugging.DebuggerWindow window, MachineSession session, Runtime.EmulationWorker bound, Func<bool> isClosed)
    {
        window.BindPause(() =>
        {
            if (ReferenceEquals(CurrentSession, session) && ReferenceEquals(CurrentRuntime, bound) &&
                !session.Closed && !bound.Completion.IsCompleted)
                session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        });
        // A retained copy is useful to other readers, but it is not a response to this attachment.
        long lastPublished = bound.TakeLatestDebugEvidence()?.Sequence ?? 0;
        long seenGeneration = bound.SessionGeneration;
        var runtimeGeneration = session.RuntimeGeneration;
        window.AwaitSnapshot();
        while (!isClosed())
        {
            if (!ReferenceEquals(CurrentSession, session) || !ReferenceEquals(CurrentRuntime, bound) ||
                session.Closed || session.RuntimeGeneration != runtimeGeneration)
            {
                window.Detach("No machine selected.");
                return;
            }
            if (bound.Completion.IsFaulted)
            {
                window.Detach("Worker failure: " + bound.Completion.Exception?.GetBaseException().Message);
                return;
            }
            if (bound.Completion.IsCompleted)
            {
                window.Detach("No machine selected.");
                return;
            }
            var generation = bound.SessionGeneration;
            if (generation != seenGeneration)
            {
                seenGeneration = generation;
                window.AwaitSnapshot();
            }
            var evidence = bound.TakeLatestDebugEvidence();
            var fresh = evidence is { } found && found.SessionId == bound.DebugSessionId &&
                found.Generation == generation && found.Sequence > lastPublished;
            if (fresh)
            {
                // The owner may retire a generation while the UI reads the immutable mailbox.
                if (bound.ObservationSessionAvailable && bound.SessionGeneration == generation)
                {
                    lastPublished = evidence!.Sequence;
                    window.Publish(evidence.Snapshot, evidence.Generation, evidence.Sequence, evidence.Paused);
                }
                else window.AwaitSnapshot();
            }
            else if (!bound.DebugDemandPending)
                bound.RequestDebugSnapshot();
            await Task.Delay(10);
        }
    }
}

/// <summary>Inspection-only cell grid. It never modifies the displayed bitmap.</summary>
public sealed class CellBoundaryOverlay : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), 1);
        for (int x=0; x<=32; x++) context.DrawLine(pen, new Point(Bounds.Width*x/32, 0), new Point(Bounds.Width*x/32, Bounds.Height));
        for (int y=0; y<=24; y++) context.DrawLine(pen, new Point(0, Bounds.Height*y/24), new Point(Bounds.Width, Bounds.Height*y/24));
    }
}

/// <summary>Direct Spectrum-color fixture. Every 8x8 cell uses only its ink/paper pair.</summary>
public static class Stage1SpectrumDemo
{
    public const string FixtureName = "spectrum-stage1-cell-quantized-v1";
    public static string FixtureSha256
    {
        get
        {
            var frame = CreateFrame();
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Pixels.Span))).ToLowerInvariant();
        }
    }
    public static bool Enabled { get; set; }
    public static Hc2000.Machine.Video.VideoFrame CreateFrame()
    {
        uint[] palette = new Hc2000.Machine.Video.VideoProfile().Palette.ToArray();
        var pixels = new uint[256 * 192];
        for (int y=0; y<192; y++) for (int x=0; x<256; x++)
        {
            int cx=x/8, cy=y/8;
            int attr=((cx*5+cy*3)&7) | (((cx+cy*3)&7)<<3) | (((cx/2+cy/3)&1)*0x40);
            uint ink=palette[(attr&7)+((attr&0x40)>>3)], paper=palette[((attr>>3)&7)+((attr&0x40)>>3)];
            bool disc=(x-128)*(x-128)+(y-96)*(y-96)<58*58;
            bool stripe=((x/12+y/9)&1)==0;
            bool bit=disc ^ stripe ^ (((x*13+y*7+cx*5+cy*11)%19)<9);
            pixels[y*256+x]=bit?ink:paper;
        }
        return new(0,256,192,pixels);
    }
}
