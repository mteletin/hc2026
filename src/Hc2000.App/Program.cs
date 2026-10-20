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
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Workspace;
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
    private readonly MachineWorkspace workspace = new();
    private readonly MachineSession implicitSession = new(KeyMappingMode.Positional);

    private readonly LaunchOptions? launchOptions;
    private readonly string? assetRootOverride;
    private Configuration.PrestartPanel prestart;
    private readonly Configuration.ReconfigurePanel reconfigure;
    private bool closing;
    private Task shutdownCompletion = Task.CompletedTask;
    private bool closeConfirmed, checkingClose;
    private bool syncingInputEvidenceUi;
    private readonly CheckBox inputEvidenceToggle = new() { Content = "Observe future emulator-window input (volatile, bounded)", Focusable = false };
    private readonly TextBlock inputEvidenceText = new() { Text = FrontendInputEvidence.Coverage, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Avalonia.Threading.DispatcherTimer presentation = new() { Interval = TimeSpan.FromMilliseconds(20) };

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

    private IEnumerable<MachineSession> AllSessions()
    {
        yield return implicitSession;
        foreach (var tab in workspace.Tabs) yield return tab.Session;
    }

    private void WatchInputRejected(MachineSession session)
        => session.KeyboardInput.Rejected += message => this.FindControl<TextBlock>("InputNotice")!.Text = message;

    // The one shared media/snapshot panel set re-binds to the selected session. A session without
    // a live runtime (draft, or a completed one) leaves the panel disconnected; selection never
    // touches the runtime state of another session.
    private void BindSelectedSessionView()
    {
        var media = this.FindControl<Media.MediaPanel>("MediaControls")!;
        var snapshots = this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!;
        media.Unbind();
        snapshots.Unbind();
        var session = CurrentSession;
        // Per-machine snapshot scope: this machine's persisted last snapshot selection, from the
        // tab's own configuration (drafts never carry StartingConfiguration). The list is
        // refreshed for the bound machine only; selection dispatches through the bound bridge.
        var configuration = workspace.Selected?.Configuration ?? session.StartingConfiguration;
        BindExplorer(session, configuration);
        var lastSnapshot = configuration?.LastSnapshot;
        snapshots.LastSnapshot = lastSnapshot;
        snapshots.SnapshotDirectory = lastSnapshot is null ? null : Path.GetDirectoryName(lastSnapshot);
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

    private void RefreshTabsView()
    {
        var panel = this.FindControl<StackPanel>("MachineTabs")!;
        panel.Children.Clear();
        panel.IsVisible = workspace.Tabs.Count > 0;
        RefreshShellView();
        foreach (var tab in workspace.Tabs)
        {
            var selected = ReferenceEquals(workspace.Selected, tab);
            var select = new Button
            {
                Content = (selected ? "\u25CF " : "") + tab.Configuration.Name,
                Focusable = false,
                Padding = new Thickness(10, 6),
                FontWeight = selected ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal
            };
            Avalonia.Automation.AutomationProperties.SetName(select, "Select machine " + tab.Configuration.Name);
            ToolTip.SetTip(select, (selected ? "Selected machine: " : "Select machine: ") + tab.Configuration.Name);
            select.Click += (_, _) =>
            {
                if (workspace.Select(tab.TabId))
                {
                    BindSelectedSessionView();
                    SyncInputView();
                    BindReconfigureView();
                    RefreshTabsView();
                    this.FindControl<TextBlock>("RuntimeStatus")!.Text = CurrentSession.Worker is null
                        ? "Selected draft: no guest executing."
                        : CurrentSession.Worker.Completion.IsCompleted ? "Selected machine stopped: " + tab.Configuration.Name + "."
                        : "Selected machine runtime; actions below affect only " + tab.Configuration.Name + ".";
                }
            };
            var close = new Button { Content = "\u00D7", Focusable = false, Width = 30, Margin = new Thickness(4, 0, 0, 0) };
            Avalonia.Automation.AutomationProperties.SetName(close, "Close machine " + tab.Configuration.Name);
            ToolTip.SetTip(close, "Close only " + tab.Configuration.Name);
            close.Click += async (_, _) => await CloseTabAsync(tab);
            var state = new TextBlock { Text = tab.Session.Worker is null ? "Draft" : tab.Session.Worker.Completion.IsCompleted ? "Stopped" : "Running",
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
            panel.Children.Add(new Border
            {
                BorderThickness = new Thickness(selected ? 2 : 1), CornerRadius = new CornerRadius(4),
                BorderBrush = select.Foreground, Padding = new Thickness(2),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { select, state, close } }
            });
        }
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
        this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Runtime attached";
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
            this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Fresh Z80 session running; completed image is CPU-painted";
        }
        catch (Exception ex)
        {
            if (!closing) this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Demo runtime failed: " + ex.GetBaseException().Message;
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
            this.FindControl<TextBlock>("RuntimeStatus")!.Text =
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
        Media.HddColdVolume? coldVolume = null, byte coldDrive = 2, bool useColdMap = false)
    {
        var session = CurrentSession;
        if (closing || session.Worker is not null || session.ReplacingRuntime || session.Closed || Program.Z80VisualDemoEnabled || Stage1SpectrumDemo.Enabled)
        {
            coldVolume?.Admission.CancelBeforeEnqueue();
            return;
        }
        var ownerStarted = false;
        var startingEditor = prestart;
        var generation = session.RuntimeGeneration;
        var panel = this.FindControl<Media.ProfileRomPanel>("ProfileRomControls");
        if (panel is not null) panel.IsVisible = false;
        try
        {
            var assetRoot = assetRootOverride ?? ProfileRomSelection.DefaultAssetRoot();
            var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(assetRoot));
            if (closing || session.Closed || session.Worker is not null || session.ReplacingRuntime || generation != session.RuntimeGeneration) return;
            var disks = session.StartingConfiguration is { } configuration
                ? await Configuration.PrestartMedia.ReadAsync(configuration, session.ClaimHeldConfiguredStart(configuration)) : [];
            if (closing || session.Closed || session.Worker is not null || generation != session.RuntimeGeneration)
            {
                session.ReleaseHeldRead("machine restart");
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
            if (closing || session.Closed || session.Worker is not null || generation != session.RuntimeGeneration) return;
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
            if (ReferenceEquals(CurrentSession, session))
            {
                this.FindControl<Snapshots.HcsPanel>("SnapshotControls")!.Disconnect();
                AttachSnapshots(snapshots);
            }
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
                    this.FindControl<TextBlock>("RuntimeStatus")!.Text = soundDumpResolution.Refusal;
            }
            var runtime = Runtime.BasicMachineComposition.CreateWorker(prepared, session.InputQueue, bridge, snapshots,
                audioEnabled: true, writeback: ledger, soundDump: soundDumpResolution.Path);
            ownerStarted = true; // Only the actual owner may now acknowledge/release admission.
            session.AttachWorker(runtime);
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
                if (closing || session.Closed || ownerGeneration != session.RuntimeGeneration) return;
                session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.Resume));
            }
            BootedIf1Length = roms.If1.Length;
            session.BasicRuntimeActive = true;
            adoptedProfiles[session] = prepared.Roms.Profile.Name;
            // An automation Start completes the same editor presentation as a UI Start.
            // Never hide a newer detached draft, or one now starting on another session.
            if (session.StartingConfiguration is null && ReferenceEquals(CurrentSession, session)
                && ReferenceEquals(prestart, startingEditor) && !startingConfiguration)
                startingEditor.IsVisible = false;
            RefreshTabsView();
            if (ReferenceEquals(CurrentSession, session))
            {
                BindReconfigureView();
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Real " + prepared.Roms.Profile.Name + " whole-chip machine running"
                    + (soundDumpResolution.Refusal is not null ? " | " + soundDumpResolution.Refusal : "");
                if (prepared.Roms.Profile.Name == "hc2000-v2-hdd-v1" || initialHdd.HasValue || coldVolume is not null)
                    await media.Hdd.InspectAsync();
            }
            if (session.StartingConfiguration is null) _ = ApplyLaunchDisksAsync(media, session);
        }
        catch (Exception ex)
        {
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
            if (!closing && ReferenceEquals(CurrentSession, session))
                this.FindControl<TextBlock>("RuntimeStatus")!.Text =
                    (session.StartingConfiguration is { } failedDraft ? "Could not start " + failedDraft.Name + ": " : "BASIC runtime failed: ")
                    + failure.GetBaseException().Message;
        }
        finally
        {
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
                (slot, path) => media.InsertDiskAsync(path, slot));
            if (!closing && diskStatus is not null)
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = diskStatus;
            else if (!closing && session.WritebackHost is { ReportsTarget: true } host)
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
        WatchInputRejected(implicitSession);
        configurationStore = settingsStore ?? new Settings.SettingsStore();
        prestart = new Configuration.PrestartPanel(configurationStore, launch, assetRoot: assetRoot);
        this.FindControl<ContentControl>("PrestartConfiguration")!.Content = prestart;
        BindPrestartEvents(prestart);
        prestart.IsVisible = !Program.Z80VisualDemoEnabled && !Stage1SpectrumDemo.Enabled && launch?.Start != true && launch?.HddEnabled != true;
        reconfigure = new Configuration.ReconfigurePanel();
        this.FindControl<ContentControl>("ReconfigureConfiguration")!.Content = reconfigure;
        reconfigure.ApplyRequested = ApplyRunningDraftAsync;
        this.FindControl<Button>("NewMachine")!.Click += (_, _) => OpenNewMachineDraft();
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
            var session = CurrentSession;
            if (launchOptions?.HddEnabled == true && session.Worker is null && !Program.Z80VisualDemoEnabled && !Stage1SpectrumDemo.Enabled)
            {
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = "HDD opt-in: prepare images, then start a NEW machine. No guest HDD BIOS or live switching.";
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
            else if (launchOptions?.Start == true || Program.Z80VisualDemoEnabled || Stage1SpectrumDemo.Enabled)
                session.BasicReplacementCompletion = BootWholeChipMachine();
            else if (session.Worker is null)
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Draft: configure, then Start. No guest executing.";
            if (session.Worker is not null || launchOptions?.Start == true)
                this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
        };

        KeyboardInput.Rejected += message => this.FindControl<TextBlock>("InputNotice")!.Text = message;
        var inputModeBox = this.FindControl<ComboBox>("InputModeBox")!;
        // Reflect the adapter's startup policy instead of maintaining a second XAML default.
        inputModeBox.SelectedIndex = KeyboardInput.Mode == KeyMappingMode.Positional ? 1 : 0;
        inputModeBox.SelectionChanged += (_, _) =>
            CurrentSession.KeyboardInput.Mode = inputModeBox.SelectedIndex == 1
                ? KeyMappingMode.Positional : KeyMappingMode.Symbolic;
        inputModeBox.DropDownClosed += (_, _) => this.FindControl<Runtime.VideoSurface>("Screen")!.Focus();
        Deactivated += (_, _) => KeyboardInput.ReleaseFocus(InputReleaseOrigin.Deactivated);
        presentation.Tick += (_, _) =>
        {
            RefreshInputEvidenceSession();
            var session = CurrentSession;
            if (session.Worker?.TakeLatestFrame() is { } frame)
            {
                LatestPresentedFrame = frame;
                this.FindControl<Runtime.VideoSurface>("Screen")!.Present(frame);
            }
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
                this.FindControl<TextBlock>("RuntimeStatus")!.Text = runtime.Completion.IsFaulted
                    ? "Runtime stopped: " + runtime.Completion.Exception!.GetBaseException().Message : "Runtime stopped";
                if (session.BasicRuntimeActive)
                {
                    session.BasicRuntimeActive = false;
                    this.FindControl<Button>("MachineReset")!.IsEnabled = false;
                    BindReconfigureView();
                }
                RefreshTabsView();
            }
        };
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
            this.FindControl<TextBlock>("RuntimeStatus")!.Text = refusal ?? "Max 4 machines";
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
        if (session.WritebackHost is { } host && !host.ResetGuardClear())
            return "a writeback fence is unresolved; the machine was not reset and its mounted media and bindings were retained.";
        var previous = tab.Configuration;
        if (!workspace.TryUpdateDraft(target, draft))
            return "the machine draft changed while this apply was in flight.";
        // The reset is the owner's work: the new pacing mode (only when it changed) and the reset
        // are enqueued on the TARGET session's own FIFO only; the other tabs' queues are untouched.
        if (previous.SpeedPercent != draft.SpeedPercent)
            session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: draft.SpeedPercent));
        session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.Reset));
        session.ReleaseHeldRead("running configuration applied; machine reset");
        session.KeyboardInput.ReleaseFocus(InputReleaseOrigin.Reset);
        session.KeyboardInput.Evidence.EndSession();
        if (ReferenceEquals(workspace.Selected, tab))
        {
            if (previous.Scale != draft.Scale)
                this.FindControl<Runtime.AspectRatioViewport>("ScreenViewport")!.RequestedScale = draft.Scale;
            ResetInputEvidenceView();
            this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
            LatestPresentedFrame = null;
            SyncInputView();
            this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Applied; only this machine was reset. No settings saved.";
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
        if (!running) { reconfigure.IsVisible = false; return; }
        if (workspace.CaptureSelectedDraft() is { } captured) reconfigure.Bind(captured, tab!.Configuration);
        reconfigure.IsVisible = true;
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
        if (IsTextEditor(e.Source)) return;
        // Preserve CP/M Ctrl+V (0x16); clipboard paste must use a distinct shortcut.
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (CurrentSession.WritebackHost is { } host) _ = host.ShortcutAsync().ContinueWith(done =>
            {
                if (done.IsFaulted) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    this.FindControl<TextBlock>("RuntimeStatus")!.Text = host.StatusLine());
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
