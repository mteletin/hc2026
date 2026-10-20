using System.Collections.Immutable;
// Hc2000.App: Avalonia desktop app. UI dependencies live here only.
// Skeleton for slice 00a; the real app lands in its own epic (epic 06, spec 06-app.md).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Video;

namespace Hc2000.App;

public static partial class Program
{
    public static bool Z80VisualDemoEnabled { get; private set; }

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
        if (Stage1SpectrumDemo.Enabled && Z80VisualDemoEnabled)
            throw new ArgumentException("Select only one --demo mode.");
        // One deployed selection path. The launcher forwards these args; this does not start the GUI.
        if (Runtime.CpuTurboSelection.IsRequest(args))
            return Runtime.CpuTurboSelection.Run(args, Console.Out, Console.Error);
        // --help wins over a GUI start. Demo args are not launch options and keep their own path.
        if (!Stage1SpectrumDemo.Enabled && !Z80VisualDemoEnabled)
        {
            var parsed = LaunchOptions.Parse(args);
            var early = LaunchStartup.EarlyExit(parsed, Console.Out, Console.Error);
            if (early is int code) return code;
            var session = LaunchStartup.ApplySession(parsed.Options!);
            if (!session.Ok)
            {
                Console.Error.WriteLine(session.StatusLine);
                Console.Error.WriteLine(LaunchStartup.Usage);
                return 1;
            }
            CurrentLaunch = session.Options;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
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
            desktop.MainWindow = new MainWindow(Program.CurrentLaunch);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public partial class MainWindow : Window
{
    public InputQueue InputQueue { get; } = new();
    public KeyboardInputAdapter KeyboardInput { get; }

    private readonly LaunchOptions? launchOptions;
    private readonly string? assetRootOverride;
    private readonly Configuration.PrestartPanel prestart;
    private Configuration.MachineConfiguration? startingConfiguration;
    private Runtime.EmulationWorker? worker;
    private bool closing;
    private bool replacingRuntime;
    private long runtimeGeneration;
    private Task basicReplacementCompletion = Task.CompletedTask;
    private Task shutdownCompletion = Task.CompletedTask;
    private bool basicRuntimeActive;
    private bool closeConfirmed, checkingClose;
    private int mediaPollTicks;
    private readonly string inputEvidenceWindowId = Guid.NewGuid().ToString("N");
    private long observedWorkerSession = -1, observedRuntimeGeneration = -1;
    private bool syncingInputEvidenceUi;
    private readonly CheckBox inputEvidenceToggle = new() { Content = "Observe future emulator-window input (volatile, bounded)", Focusable = false };
    private readonly TextBlock inputEvidenceText = new() { Text = FrontendInputEvidence.Coverage, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Avalonia.Threading.DispatcherTimer presentation = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private Media.DiskWritebackHost? writebackHost;

    internal Runtime.EmulationWorker? CurrentRuntime => worker;
    internal Task ShutdownCompletion => shutdownCompletion;
    /// <summary>Last frame the window presented. The startup test reads this, not a worker it composed.</summary>
    internal VideoFrame? LatestPresentedFrame { get; private set; }
    /// <summary>IF1 shadow length composed at window open. 0 until the whole-chip boot attaches.</summary>
    internal int BootedIf1Length { get; private set; }

    /// <summary>Composition root attaches the worker; no synthetic CPU is used for a fake boot.</summary>
    public void AttachRuntime(Runtime.EmulationWorker runtime)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        if (worker is not null) throw new InvalidOperationException("Runtime already attached.");
        worker = runtime;
        runtimeGeneration++;
        runtime.SessionRetired += KeyboardInput.Evidence.EndSession;
        RefreshInputEvidenceSession();
        this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Runtime attached";
        presentation.Start();
    }

    private async Task RestartZ80DemoAsync()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        if (closing || replacingRuntime || !Program.Z80VisualDemoEnabled) return;
        replacingRuntime = true;
        var button = this.FindControl<Button>("Z80DemoRerun")!;
        button.IsEnabled = false;
        presentation.Stop();
        this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
        var old = worker;
        KeyboardInput.Evidence.EndSession();
        ResetInputEvidenceView();
        worker = null; // no old frame or completion can reach the new session
        runtimeGeneration++;
        old?.Dispose();
        try
        {
            if (old is not null) await old.Completion;
            if (closing) return;
            AttachRuntime(Runtime.Z80VisualDemo.CreateWorker());
            this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Fresh Z80 session running; completed image is CPU-painted";
        }
        catch (Exception ex)
        {
            if (!closing) this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Demo runtime failed: " + ex.GetBaseException().Message;
        }
        finally
        {
            replacingRuntime = false;
            if (!closing) button.IsEnabled = true;
        }
    }

    private void ResetWholeChipMachine()
    {
        if (closing || !basicRuntimeActive || worker?.Completion.IsCompleted != false || writebackHost?.Frozen == true) return;
        _ = writebackHost?.InvalidateAsync();
        KeyboardInput.ReleaseFocus(InputReleaseOrigin.Reset);
        KeyboardInput.Evidence.EndSession();
        ResetInputEvidenceView();
        this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
        LatestPresentedFrame = null;
        InputQueue.Enqueue(new RuntimeControl(InputControlKind.Reset));
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

    /// <summary>Whole-chip boot after explicit Start, or explicit CLI automation.
    /// Disks still go through <see cref="LaunchStartup"/> after the worker exists. Skipped when a
    /// runtime is already attached (demo, or a test that composed its own worker before Show).
    /// The chip read runs off the UI thread: LoadWholeChipV2 blocks on ReadAsync, which deadlocks
    /// a UI sync context.</summary>
    private async Task BootWholeChipMachine(ImmutableArray<HddInitialMedia>? initialHdd = null,
        Media.HddColdVolume? coldVolume = null, byte coldDrive = 2)
    {
        if (closing || worker is not null || replacingRuntime || Program.Z80VisualDemoEnabled || Stage1SpectrumDemo.Enabled)
        {
            coldVolume?.Admission.CancelBeforeEnqueue();
            return;
        }
        var ownerStarted = false;
        var generation = runtimeGeneration;
        var panel = this.FindControl<Media.ProfileRomPanel>("ProfileRomControls");
        if (panel is not null) panel.IsVisible = false;
        try
        {
            var assetRoot = assetRootOverride ?? ProfileRomSelection.DefaultAssetRoot();
            var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(assetRoot));
            if (closing || worker is not null || replacingRuntime || generation != runtimeGeneration) return;
            var disks = startingConfiguration is { } configuration
                ? await Configuration.PrestartMedia.ReadAsync(configuration) : [];
            if (closing || worker is not null || generation != runtimeGeneration) return;
            var prepared = coldVolume is not null
                ? Runtime.BasicMachineComposition.PrepareAdmittedHddWholeChipV2(roms, coldVolume.Media(coldDrive), coldVolume.Admission)
                : initialHdd is { } images
                    ? Runtime.BasicMachineComposition.PrepareHddWholeChipV2(roms, images)
                    : Runtime.BasicMachineComposition.PrepareWholeChipV2(roms);
            var media = this.FindControl<Media.MediaPanel>("MediaControls")!;
            media.Disconnect();
            var bridge = new Media.MediaBridge(InputQueue);
            var ledger = new Media.DiskWritebackOwnerLedger();
            writebackHost = new Media.DiskWritebackHost(ledger, bridge, launchOptions?.Writeback == true);
            media.TryWritebackEject = writebackHost.TryEjectAsync;
            media.DiskMounted = writebackHost.NoteMounted;
            media.WritebackBeforeClose = writebackHost.BeforeCloseAsync;
            if (LaunchStartup.HasDisk(launchOptions) || initialHdd.HasValue || coldVolume is not null || startingConfiguration is not null)
                media.Attach(bridge);
            else
                AttachMedia(bridge);
            var snapshots = new Snapshots.HcsBridge(InputQueue);
            this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
            AttachSnapshots(snapshots);
            // The existing FIFO consumes Pause before its first instruction. Mount receipts are
            // acknowledged while paused; a failed mount must never resume a partially configured guest.
            if (startingConfiguration is { } draft)
            {
                InputQueue.Enqueue(new RuntimeControl(InputControlKind.Pause));
                InputQueue.Enqueue(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: draft.SpeedPercent));
            }
            var runtime = Runtime.BasicMachineComposition.CreateWorker(prepared, InputQueue, bridge, snapshots, audioEnabled: true, writeback: ledger);
            ownerStarted = true; // Only the actual owner may now acknowledge/release admission.
            AttachRuntime(runtime);
            foreach (var disk in disks)
            {
                var receipt = await bridge.RequestAsync(new(MediaAction.InsertDisk, disk.Slot, disk.Bytes, Path.GetFileName(disk.Path)));
                if (receipt.Error is not null) throw new InvalidDataException(receipt.Error);
                await writebackHost.NoteMounted(disk.Slot, disk.Path, disk.Bytes, receipt);
            }
            if (startingConfiguration is not null)
            {
                // Even an empty draft requires an actual owner reply, not just a returned
                // thread/worker constructor, before successful Start and any save intent.
                var installed = await bridge.RequestAsync(new(MediaAction.Inspect));
                if (installed.Error is not null) throw new InvalidDataException(installed.Error);
                if (closing) return;
                InputQueue.Enqueue(new RuntimeControl(InputControlKind.Resume));
            }
            BootedIf1Length = roms.If1.Length;
            basicRuntimeActive = true;
            this.FindControl<Button>("MachineReset")!.IsEnabled = true;
            this.FindControl<TextBlock>("RuntimeStatus")!.Text =
                initialHdd.HasValue || coldVolume is not null ? "Real hc2000-v2-hdd-v1 whole-chip machine running; original BIOS, live switching unavailable" :
                "Real hc2000-v2 whole-chip machine running";
            if (initialHdd.HasValue || coldVolume is not null) await media.Hdd.InspectAsync();
            if (startingConfiguration is null) _ = ApplyLaunchDisksAsync(media);
        }
        catch (Exception ex)
        {
            Exception failure = ex;
            if (startingConfiguration is not null && worker is { } failed)
            {
                worker = null;
                failed.Dispose();
                try { await failed.Completion; }
                catch (Exception ownerFailure) { failure = ownerFailure; } // Report the original owner fault after drain.
                presentation.Stop();
                this.FindControl<Media.MediaPanel>("MediaControls")!.Disconnect();
                this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
            }
            if (!closing)
                this.FindControl<TextBlock>("RuntimeStatus")!.Text =
                    "BASIC runtime failed: " + failure.GetBaseException().Message;
        }
        finally
        {
            if (!ownerStarted) coldVolume?.Admission.CancelBeforeEnqueue();
        }
    }

    private async Task ApplyLaunchDisksAsync(Media.MediaPanel media)
    {
        try
        {
            var diskStatus = await LaunchStartup.ApplyDisksAsync(launchOptions,
                (slot, path) => media.InsertDiskAsync(path, slot));
            if (!closing && diskStatus is not null)
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = diskStatus;
            else if (!closing && writebackHost is { ReportsTarget: true } host)
            {
                var status = this.FindControl<TextBlock>("RuntimeStatus")!;
                var report = host.StatusLine();
                if (status.Text?.Contains(report, StringComparison.Ordinal) != true)
                    status.Text = string.IsNullOrEmpty(status.Text) ? report : status.Text + "; " + report;
            }
        }
        catch (Exception ex)
        {
            if (!closing)
                this.FindControl<TextBlock>("RuntimeStatus")!.Text =
                    "BASIC runtime failed: " + ex.GetBaseException().Message;
        }
    }

    public MainWindow() : this(null) { }

    public MainWindow(LaunchOptions? launch, string? assetRoot = null, Settings.SettingsStore? settingsStore = null)
    {
        launchOptions = launch;
        assetRootOverride = assetRoot;
        AvaloniaXamlLoader.Load(this);
        Title = "HC2000";
        Width = 800;
        Height = 750;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, MediaDragOver);
        AddHandler(DragDrop.DropEvent, MediaDrop);
        KeyboardInput = new KeyboardInputAdapter(InputQueue);
        prestart = new Configuration.PrestartPanel(settingsStore ?? new Settings.SettingsStore(), launch);
        this.FindControl<ContentControl>("PrestartConfiguration")!.Content = prestart;
        prestart.StartRequested = StartConfigurationAsync;
        prestart.IsVisible = !Program.Z80VisualDemoEnabled && !Stage1SpectrumDemo.Enabled && launch?.Start != true && launch?.HddEnabled != true;
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
            this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Running actual Z80 program; Reset / Run again starts fresh nonboot session";
        }
        if (Stage1SpectrumDemo.Enabled)
        {
            Title = "HC2000 — Stage 1 Spectrum renderer fixture (no CPU/boot)";
            var screen = this.FindControl<Runtime.VideoSurface>("Screen")!;
            screen.Present(Stage1SpectrumDemo.CreateFrame());
            screen.SetScale(2);
            this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Stage 1 fixture: renderer proof only; no CPU or boot";
            var overlay = this.FindControl<CheckBox>("CellOverlayToggle")!;
            var boundaries = this.FindControl<CellBoundaryOverlay>("CellBoundaries")!;
            overlay.IsCheckedChanged += (_, _) => boundaries.IsVisible = overlay.IsChecked == true;
        }
        // Ordinary open stays Draft. Explicit CLI --start and the existing cold HDD path retain their semantics.
        Opened += (_, _) =>
        {
            if (launchOptions?.HddEnabled == true && worker is null && !Program.Z80VisualDemoEnabled && !Stage1SpectrumDemo.Enabled)
            {
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = "HDD opt-in: prepare images, then start a NEW machine. No guest HDD BIOS or live switching.";
                this.FindControl<Media.MediaPanel>("MediaControls")!.Hdd.ConfigureCold(images =>
                {
                    basicReplacementCompletion = BootWholeChipMachine(images);
                    return basicReplacementCompletion;
                }, (volume, drive) =>
                {
                    basicReplacementCompletion = BootWholeChipMachine(coldVolume: volume, coldDrive: drive);
                    return basicReplacementCompletion;
                });
            }
            else if (launchOptions?.Start == true || Program.Z80VisualDemoEnabled || Stage1SpectrumDemo.Enabled)
                basicReplacementCompletion = BootWholeChipMachine();
            else if (worker is null)
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Draft: configure, then Start. No guest executing.";
            if (worker is not null || launchOptions?.Start == true)
                this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
        };

        KeyboardInput.Rejected += message => this.FindControl<TextBlock>("InputNotice")!.Text = message;
        var inputModeBox = this.FindControl<ComboBox>("InputModeBox")!;
        // Reflect the adapter's startup policy instead of maintaining a second XAML default.
        inputModeBox.SelectedIndex = KeyboardInput.Mode == KeyMappingMode.Positional ? 1 : 0;
        inputModeBox.SelectionChanged += (_, _) =>
            KeyboardInput.Mode = inputModeBox.SelectedIndex == 1
                ? KeyMappingMode.Positional : KeyMappingMode.Symbolic;
        inputModeBox.DropDownClosed += (_, _) => this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
        Deactivated += (_, _) => KeyboardInput.ReleaseFocus(InputReleaseOrigin.Deactivated);
        presentation.Tick += (_, _) =>
        {
            RefreshInputEvidenceSession();
            if (worker?.TakeLatestFrame() is { } frame)
            {
                LatestPresentedFrame = frame;
                this.FindControl<Runtime.VideoSurface>("Screen")!.Present(frame);
            }
            if (basicRuntimeActive && !checkingClose && ++mediaPollTicks % 10 == 0)
                _ = this.FindControl<Media.MediaPanel>("MediaControls")!.PollAsync();
            if (worker?.Completion.IsCompleted == true)
            {
                presentation.Stop();
                this.FindControl<Media.MediaPanel>("MediaControls")!.Disconnect();
                this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = worker.Completion.IsFaulted
                    ? "Runtime stopped: " + worker.Completion.Exception!.GetBaseException().Message : "Runtime stopped";
                if (basicRuntimeActive)
                {
                    basicRuntimeActive = false;
                    this.FindControl<Button>("MachineReset")!.IsEnabled = false;
                }
            }
        };
        Closing += async (_, e) =>
        {
            if (closeConfirmed || !basicRuntimeActive || worker?.Completion.IsCompleted != false) return;
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
        Closed += (_, _) =>
        {
            closing = true;
            runtimeGeneration++;
            KeyboardInput.ReleaseFocus(InputReleaseOrigin.SessionEnd);
            KeyboardInput.Evidence.EndSession();
            ResetInputEvidenceView();
            InputQueue.Enqueue(new RuntimeControl(InputControlKind.Shutdown));
            presentation.Stop();
            var old = worker;
            worker = null;
            old?.Dispose();
            shutdownCompletion = AwaitShutdownAndDiscardPendingAsync(old, basicReplacementCompletion);
            this.FindControl<Media.MediaPanel>("MediaControls")!.Disconnect();
            this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
            this.FindControl<Runtime.VideoSurface>("Screen")!.Dispose();
        };
    }

    private async Task<bool> StartConfigurationAsync(Configuration.MachineConfiguration configuration)
    {
        if (closing || worker is not null || replacingRuntime) return false;
        configuration.ValidateEffective();
        startingConfiguration = configuration;
        KeyboardInput.ReleaseFocus(InputReleaseOrigin.Reset);
        KeyboardInput.Mode = configuration.KeyboardMode == "Positional" ? KeyMappingMode.Positional : KeyMappingMode.Symbolic;
        this.FindControl<ComboBox>("InputModeBox")!.SelectedIndex = KeyboardInput.Mode == KeyMappingMode.Positional ? 1 : 0;
        this.FindControl<Runtime.AspectRatioViewport>("ScreenViewport")!.RequestedScale = configuration.Scale;
        basicReplacementCompletion = BootWholeChipMachine();
        await basicReplacementCompletion;
        if (worker is null || closing) { startingConfiguration = null; return false; }
        this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
        return true;
    }

    private void AddInputEvidenceControls()
    {
        var read = new Button { Content = "Read bounded input evidence", Focusable = false };
        var clear = new Button { Content = "Clear input evidence", Focusable = false };
        var contents = new StackPanel { Spacing = 8 };
        contents.Children.Add(inputEvidenceToggle); contents.Children.Add(read);
        contents.Children.Add(clear); contents.Children.Add(inputEvidenceText);
        ((StackPanel)this.FindControl<TextBlock>("InputNotice")!.Parent!).Children.Add(
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
        var current = worker;
        if (closing || replacingRuntime || current is null || current.Completion.IsCompleted || !current.ObservationSessionAvailable)
        {
            KeyboardInput.Evidence.EndSession();
            observedWorkerSession = -1;
            ResetInputEvidenceView();
            return;
        }
        var generation = current.SessionGeneration;
        if (observedWorkerSession != generation || observedRuntimeGeneration != runtimeGeneration)
        {
            KeyboardInput.Evidence.BeginSession($"window-{inputEvidenceWindowId}/{runtimeGeneration}/machine-{generation}", current.HostClock,
                () => current.ObservationSessionAvailable && !current.Completion.IsCompleted && current.SessionGeneration == generation);
            observedWorkerSession = generation; observedRuntimeGeneration = runtimeGeneration;
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

    private async Task AwaitShutdownAndDiscardPendingAsync(Runtime.EmulationWorker? old, Task replacement)
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
        if (old is not null) InputQueue.DiscardPending();
    }

    private static string? DroppedDisk(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files?.Length != 1) return null;
        var path = files[0].TryGetLocalPath();
        return path is not null && string.Equals(Path.GetExtension(path), ".dsk", StringComparison.OrdinalIgnoreCase) ? path : null;
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
        if (IsTextEditor(e.Source)) return;
        // Preserve CP/M Ctrl+V (0x16); clipboard paste must use a distinct shortcut.
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (writebackHost is not null) _ = writebackHost.ShortcutAsync().ContinueWith(done =>
            {
                if (done.IsFaulted) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    this.FindControl<TextBlock>("RuntimeStatus")!.Text = writebackHost.StatusLine());
            }, TaskScheduler.Default);
            return;
        }
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            PasteClipboard();
            return;
        }
        e.Handled |= KeyboardInput.KeyDown(e.Key, e.KeyModifiers);
    }

    private void MachineKeyUp(object? sender, KeyEventArgs e)
    {
        KeyboardInput.KeyUp(e.Key); // Always release, even if focus moved into a text editor.
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!IsTextEditor(e.Source)) e.Handled |= KeyboardInput.TextInput(e.Text);
    }

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
            this.FindControl<TextBlock>("InputNotice")!.Text = "Clipboard unavailable: " + ex.Message;
        }
    }

    private void PasteInput(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (KeyboardInput.Paste(this.FindControl<TextBox>("PasteTextBox")!.Text ?? ""))
            this.FindControl<TextBlock>("InputNotice")!.Text = "Paste queued; consumed only at emulation boundaries.";
    }

    private void OpenSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
        => new Settings.SettingsWindow(new Settings.SettingsStore()).ShowDialog(this);

    private void OpenDebugger(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
        => new Debugging.DebuggerWindow().Show(this);
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
