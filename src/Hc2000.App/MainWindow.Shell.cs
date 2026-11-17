using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Hc2000.App.Configuration;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.App.Settings;
using Hc2000.App.Workspace;
using Hc2000.HostFiles;
using Hc2000.Machine.Capabilities;

namespace Hc2000.App;

public partial class MainWindow
{
    private readonly SettingsStore configurationStore;
    // Presentation evidence from the successful prepared/owner-acknowledged cold Start, never
    // authority inferred from an edited profile string or from the currently selected tab label.
    private readonly Dictionary<MachineSession, string> adoptedProfiles = new();
    private readonly Dictionary<MachineSession, bool> adoptedOverlays = new();
    private readonly Dictionary<MachineSession, int> adoptedTurbos = new();
    private readonly Dictionary<MachineSession, Runtime.EmulationWorker> displayedStops = new();
    private bool startingConfiguration;
    // A detached setup lives in the right panel alongside the selected machine's live screen.
    private bool draftInFront;
    private bool hasDetachedSetup;
    private bool filesInFront;
    private string selectedRailPanel = "Machine";

    private void WireMediaFullView()
    {
        var media = this.FindControl<Media.MediaPanel>("MediaControls")!;
        media.Children.Remove(media.DriveStrip);
        this.FindControl<ContentControl>("MediaDriveStrip")!.Content = media.DriveStrip;
        var explorerRow = (Grid)media.TwoPane.Parent!;
        explorerRow.Children.Remove(media.TwoPane);
        explorerRow.Children.Remove(media.Explorer);
        media.Children.Remove(explorerRow);
        this.FindControl<ContentControl>("MediaHomeCopyHost")!.Content = media.TwoPane;
        this.FindControl<ContentControl>("MediaFileHost")!.Content = media.Explorer;
        this.FindControl<Button>("OpenMediaExplorer")!.Click += (_, _) => ShowMediaFullView();
        this.FindControl<Button>("CloseMediaExplorer")!.Click += (_, _) =>
        {
            filesInFront = false;
            RefreshShellView();
        };
    }

    private void ShowMediaFullView()
    {
        filesInFront = true;
        SetSideRailOpen(true);
        RefreshShellView();
    }

    private void OpenNewMachineDraft()
    {
        if (closing || startingConfiguration) return;
        if (hasDetachedSetup)
        {
            ShowDetachedSetup();
            return;
        }
        if (workspace.Tabs.Count >= MachineWorkspace.Capacity)
        {
            this.FindControl<TextBlock>("StatusLine")!.Text = "Four machines are open. Close one before starting another; existing machines were not changed.";
            return;
        }
        var previous = prestart.InitialConfiguration;
        var number = 1;
        while (workspace.Tabs.Any(t => t.Configuration.Name == $"Machine {number}")) number++;
        // Only harmless preferences cross this boundary. A new record supplies a fresh Id, the new-instance
        // hardware (the full workstation, hc2000-full-workstation-default), no media/writeback authority and no
        // last-snapshot selection.
        var draft = new MachineConfiguration
        {
            Name = $"Machine {number}", SpeedPercent = previous.SpeedPercent, Scale = previous.Scale,
            KeyboardMode = previous.KeyboardMode, AudioPreference = previous.AudioPreference, KeyMap = previous.KeyMap
        }.WithNewInstanceHardware();
        prestart = new PrestartPanel(configurationStore, initialConfiguration: draft, assetRoot: assetRootOverride);
        BindPrestartEvents(prestart);
        this.FindControl<ContentControl>("PrestartConfiguration")!.Content = prestart;
        this.FindControl<TextBlock>("StatusLine")!.Text = "Set up the new machine on the right. Existing machines keep running.";
        hasDetachedSetup = true;
        ShowDetachedSetup();
    }

    private void ShowDetachedSetup()
    {
        HideX2BenchmarkResultsPanel();
        draftInFront = true;
        filesInFront = false;
        // Setup needs the panel, but this does not pin the running-machine controls open.
        railOpen = true;
        UpdateRailPanelWidth();
        RefreshTabsView();
    }

    private void AddDetachedSetupTab(StackPanel panel)
    {
        if (!hasDetachedSetup) return;
        var select = new Button
        {
            Name = "SelectSetupTab", Content = prestart.InitialConfiguration.Name,
            Focusable = false, Padding = new Thickness(10, 4), Classes = { "tabStripItem" }
        };
        Avalonia.Automation.AutomationProperties.SetName(select, "Select machine setup");
        ToolTip.SetTip(select, "Not started");
        select.Click += (_, _) => ShowDetachedSetup();
        var close = new Button
        {
            Name = "CloseSetupTab", Content = "\u00D7", Focusable = false,
            Width = 26, Padding = new Thickness(0), Classes = { "ghostAction" }
        };
        Avalonia.Automation.AutomationProperties.SetName(close, "Close machine setup");
        close.Click += (_, _) =>
        {
            hasDetachedSetup = false;
            draftInFront = false;
            prestart.IsVisible = false;
            RefreshTabsView();
        };
        var state = new TextBlock { Text = "\u25CF", FontSize = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        state.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppStatusStoppedBrush"));
        ToolTip.SetTip(state, "Not started");
        var frame = new Border
        {
            Classes = { "tab" },
            Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { state, select, close } }
        };
        if (draftInFront)
        {
            select.Classes.Add("selected");
            frame.Classes.Add("selected");
        }
        panel.Children.Add(frame);
    }

    private void BindPrestartEvents(PrestartPanel panel)
    {
        panel.SyncBorder(configurationStore.Load().Settings.Border);
        panel.BorderChanged += SetRequestedBorder;
        panel.LibraryHddAction = (row, slot) => SelectEditorLibraryHddAsync(panel, row, slot);
        panel.StartRequested = StartConfigurationAsync;
        panel.OpenExplorerWorkspaceRequested += (_, _) => OpenExplorerWorkspace();
        panel.Started += (_, _) =>
        {
            // Saving runs after the actual Start receipt. Only hide that completed editor and
            // carry its save warning forward; never hide or overwrite a newer detached draft.
            panel.IsVisible = false;
            draftInFront = false;
            if (ReferenceEquals(prestart, panel))
            {
                hasDetachedSetup = false;
                if (CurrentSession.StartingConfiguration?.Id == panel.InitialConfiguration.Id)
                    this.FindControl<TextBlock>("StatusLine")!.Text = panel.Status;
                RefreshTabsView();
            }
        };
        panel.DraftChanged += (_, _) => OnDraftHardwareChanged(panel);
    }

    private void OnDraftHardwareChanged(PrestartPanel panel)
    {
        if (!draftInFront) return;
        var session = CurrentSession;
        var live = !session.Closed && session.Worker is { Completion.IsCompleted: false };
        if (!live || !adoptedProfiles.TryGetValue(session, out var adoptedProfile)) return;

        try
        {
            var draft = panel.Build();
            var adoptedOverlay = adoptedOverlays.TryGetValue(session, out var o) && o;
            var adoptedTurbo = adoptedTurbos.TryGetValue(session, out var t) && t > 0 ? t : 1;
            if (draft.Profile != adoptedProfile || draft.TextOverlay != adoptedOverlay || draft.CpuTurboMultiplier != adoptedTurbo)
            {
                this.FindControl<TextBlock>("StatusLine")!.Text = CapabilityCatalog.Cold(adoptedProfile);
            }
            else
            {
                this.FindControl<TextBlock>("StatusLine")!.Text = "New draft only. Existing machines keep running; Start machine creates a separate machine.";
            }
        }
        catch
        {
            // Draft not buildable yet
        }
    }

    private DiskLibraryCatalog? editorHddCatalog;

    private void BindLibraryRemaps(MachineSession session, HddPanel panel)
    {
        panel.ConfigureLibraryRemaps([], null);
        var root = Path.Combine(Path.GetDirectoryName(configurationStore.FilePath)!, "hdd-library");
        var path = Path.Combine(root, "catalog.json");
        if (!NativeImageAdmission.IsGuarded || !File.Exists(path)) return;
        try
        {
            var catalog = editorHddCatalog ??= DiskLibraryCatalog.Load(path);
            var entries = catalog.Entries.Where(e => e.Kind == DiskLibraryKind.Hdd && e.Format == DiskLibraryFormat.Flat512)
                .Select(e => (e.Id, e.Name)).ToImmutableArray();
            panel.ConfigureLibraryRemaps(entries, async entryId =>
            {
                if (closing || session.Closed || !ReferenceEquals(CurrentSession, session))
                    return new(DiskLibraryHddContainerStatus.Refused, "selected runtime changed");
                var copies = catalog.Copies.Where(c => c.EntryId == entryId).ToArray();
                if (copies.Length != 1)
                    return new(DiskLibraryHddContainerStatus.Refused, "prepare exactly one library working medium before Start; no live creation or ambiguous-copy selection");
                var target = await catalog.InspectPreparedHddContainerAsync(copies[0].Id, root);
                return closing || session.Closed || !ReferenceEquals(CurrentSession, session)
                    ? new(DiskLibraryHddContainerStatus.Refused, "selected runtime changed") : target;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DiskLibrarySchemaException or ArgumentException)
        { panel.ConfigureLibraryRemaps([], null); }
    }

    private async Task<string> SelectEditorLibraryHddAsync(PrestartPanel editor, InlineMediaRow row, string slot)
    {
        if (!NativeImageAdmission.IsGuarded || closing || startingConfiguration || libraryHddPreparing ||
            !ReferenceEquals(prestart, editor) || !editor.IsVisible)
            return "Library HDD selection refused: no idle selected editor; nothing was recorded.";
        var revision = editor.DraftRevision;
        var draft = editor.Build();
        if (draft.Profile != "hc2000-v2-hdd-v1" || row.Path is null || row.Size != DiskLibrarySchema.HddBytes)
            return "Library HDD selection refused: enable HDD and choose an exact flat512 source; nothing was recorded.";
        libraryHddPreparing = true;
        try
        {
            // A fresh owned private root, never chmod the shared settings directory or a user's source.
            var root = Path.Combine(Path.GetDirectoryName(configurationStore.FilePath)!, "hdd-library");
            using var createdRoot = ImageAdmissionNative.Directory(root, create: true);
            using var rootHandle = ImageAdmissionNative.Directory(root, create: false);
            ImageAdmissionNative.RequirePrivateDirectory(rootHandle);
            editorHddCatalog ??= DiskLibraryCatalog.Load(Path.Combine(root, "catalog.json"));
            var detached = new MachineWorkspace();
            if (!detached.TryAddDraft(draft, out var tab, out _)) return "Library HDD selection refused: invalid draft.";
            var result = await MediaHomeHddDraftAction.RunLibraryRowAsync(detached, editorHddCatalog,
                new("disk", "hdd-raw", row.Name, row.Path, row.Size, row.Detail, row.Selectable), root, slot, () =>
                    closing || startingConfiguration || coldStartsInFlight != 0 || AllSessions().Any(s => !s.Closed &&
                        (s.ReplacingRuntime || s.Worker is not null || s.HeldOperation is not null || s.WritebackHost is not null))
                        ? null : DiskLibraryHostAdmission.StoppedHost);
            if (tab!.DraftRevision == 0) return result;
            if (!ReferenceEquals(prestart, editor) || !editor.IsVisible || closing ||
                !editor.TryApplyLibraryHdd(revision, tab.Configuration.Hdd))
                return "The selected editor changed; private container retained, nothing was recorded.";
            return result;
        }
        finally { libraryHddPreparing = false; }
    }

    private void RefreshShellView()
    {
        var session = CurrentSession;
        SyncX2Screen();
        var live = !session.Closed && session.Worker is { Completion.IsCompleted: false };
        this.FindControl<TextBlock>("SelectedMachineName")!.Text = workspace.Selected?.Configuration.Name ?? "HC2000";
        this.FindControl<TextBlock>("EffectiveHardware")!.Text = adoptedProfiles.TryGetValue(session, out var profile)
            ? (live ? "Effective hardware: " : "Stopped hardware: ") + profile
            : session.Worker is not null ? "Attached runtime — no cold-start hardware claim." : "Draft only — no hardware adopted or guest executing.";
        var overlayControl = this.FindControl<TextBlock>("EffectiveOverlay");
        if (overlayControl is not null)
        {
            var hasProfile = adoptedProfiles.ContainsKey(session);
            var overlayActive = adoptedOverlays.TryGetValue(session, out var ov) && ov;
            overlayControl.Text = overlayActive
                ? (live ? "X1 text overlay: active" : "Stopped X1 overlay: active")
                : (live ? "X1 text overlay: inactive" : "Stopped X1 overlay: inactive");
            overlayControl.IsVisible = hasProfile;
        }
        var running = session.Worker is not null || Stage1SpectrumDemo.Enabled
            || launchOptions?.Start == true || launchOptions?.HddEnabled == true;
        var draft = prestart.IsVisible && (draftInFront || !running);
        if (!draft && Width < RailAutoCollapseWidth && !railUserPinned)
            railOpen = false;
        this.FindControl<ScrollViewer>("DraftScroll")!.IsVisible = draft && !filesInFront;
        this.FindControl<Grid>("ScreenHost")!.IsVisible = running && (!draft || live);
        this.FindControl<Border>("PoweredOffScreen")!.IsVisible = !running || (draft && !live);
        this.FindControl<TextBlock>("PoweredOffName")!.Text = draft
            ? prestart.InitialConfiguration.Name : workspace.Selected?.Configuration.Name ?? "HC2000";
        this.FindControl<Grid>("MediaFullView")!.IsVisible = filesInFront;
        this.FindControl<Border>("ConfigurationCard")!.IsVisible = draft;
        RefreshRightPanels(draft);
        var add = this.FindControl<Button>("NewMachine")!;
        add.IsEnabled = !closing && !startingConfiguration && workspace.Tabs.Count < MachineWorkspace.Capacity;
        ToolTip.SetTip(add, workspace.Tabs.Count >= MachineWorkspace.Capacity
            ? "Four-machine limit. Close a machine before adding another."
            : "Open a new detached configuration; existing machines keep running.");
        this.FindControl<Button>("MachineReset")!.IsEnabled = live && session.BasicRuntimeActive;
        UpdateRailPanelWidth();
    }

    private Hc2000.Machine.Video.VideoFrame? TakeSelectedVideo(MachineSession session)
    {
        var worker = session.Worker;
        if (worker is null) return null;
        var generation = worker.SessionGeneration;
        if (session.ObservedVideoGeneration != generation)
        {
            session.Video.ClearFrames(worker);
            session.ObservedVideoGeneration = generation;
            LatestPresentedFrame = null;
            LatestPresentedGeneration = -1;
            var replaced = this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
            worker.Recycle(replaced);
            ClearX2Presentation(session);
        }
        var ula = worker.TakeLatestStampedFrame();
        var x2 = worker.TakeLatestStampedExpansionFrame();
        if (generation != worker.SessionGeneration)
        {
            if (ula?.Frame is { } uf) worker.Recycle(uf);
            if (x2?.Frame is { } xf) worker.Recycle(xf);
            session.Video.ClearFrames(worker);
            session.ObservedVideoGeneration = -1;
            LatestPresentedFrame = null;
            LatestPresentedGeneration = -1;
            var replaced = this.FindControl<Runtime.VideoSurface>("Screen")!.ClearPresentation();
            worker.Recycle(replaced);
            ClearX2Presentation(session);
            return null;
        }
        if (ula is not null && ula.Generation != generation) worker.Recycle(ula.Frame);
        if (x2 is not null && x2.Generation != generation) worker.Recycle(x2.Frame);
        session.Video.Update(ula?.Generation == generation ? ula.Frame : null,
            x2?.Generation == generation ? x2.Frame : null, worker);
        var settings = SelectedX2Screen;
        bool vgaPrimary = X2ScreenOffered(session) && settings.VgaIsPrimary;
        return vgaPrimary ? session.Video.TakeX2() : session.Video.TakeUla();
    }

    internal void PresentTick()
    {
        var session = CurrentSession;
        var frame = TakeSelectedVideo(session);
        if (frame is not null)
        {
            LatestPresentedFrame = frame;
            LatestPresentedGeneration = session.ObservedVideoGeneration;
            var replaced = this.FindControl<Runtime.VideoSurface>("Screen")!.Present(frame);
            session.Worker?.Recycle(replaced);
        }
        PresentX2Screen(session, frame);
    }

    // The right panel holds setup as well as running controls; its icon rail stays visible
    // when collapsed. The screen's grid column never changes ownership.
    private const double RailPanelWidth = 340;
    private const double RailAutoCollapseWidth = 1100;
    private bool railOpen = true;
    private bool railUserPinned;
    private double lastWindowWidth;

    public bool IsSideRailOpen => railOpen;

    /// <summary>Open or collapse the side-rail panel. An explicit open is pinned so the
    /// auto-collapse rule does not immediately undo it (F4).</summary>
    public void SetSideRailOpen(bool open)
    {
        railOpen = open;
        railUserPinned = open;
        UpdateRailPanelWidth();
    }

    // F4: the rail button and Ctrl+B toggle the panel; a user open is pinned.
    public void ToggleSideRail() => SetSideRailOpen(!railOpen);

    private void UpdateRailPanelWidth()
    {
        var rail = this.FindControl<Grid>("SideRail");
        if (rail is null || rail.ColumnDefinitions.Count < 2) return;
        rail.ColumnDefinitions[1].Width = new GridLength(railOpen ? RailPanelWidth : 0);
    }

    // F4 auto-collapse: crossing the 1100 px threshold downward collapses the panel unless the
    // user has pinned it open. A window that starts narrow is collapsed up front in the ctor.
    private void HandleWindowWidthChanged(double width)
    {
        if (lastWindowWidth >= RailAutoCollapseWidth && width < RailAutoCollapseWidth && !railUserPinned)
            railOpen = false;
        lastWindowWidth = width;
        UpdateRailPanelWidth();
    }

    // Select which running-machine panel the rail shows; the selected icon gets the underline.
    private void SelectRailPanel(string name)
    {
        selectedRailPanel = name;
        filesInFront = false;
        RefreshShellView();
        SetRailIconSelected("RailMachine", name == "Machine");
        SetRailIconSelected("RailMedia", name == "Media");
        SetRailIconSelected("RailSnapshots", name == "Snapshots");
        SetRailIconSelected("RailInput", name == "Input");
        if (prestart.IsVisible)
        {
            if (name == "Media") prestart.SelectTopic("Media");
            else if (name == "Machine") prestart.SelectTopic("Hardware");
            else if (name == "Input") prestart.SelectTopic("Input");
        }
    }

    private void RefreshRightPanels(bool draft)
    {
        var controls = !filesInFront && !draft;
        SetRailPanelVisible("RailMachinePanel", controls && selectedRailPanel == "Machine");
        SetRailPanelVisible("RailMediaPanel", controls && selectedRailPanel == "Media");
        SetRailPanelVisible("RailSnapshotsPanel", controls && selectedRailPanel == "Snapshots");
        SetRailPanelVisible("RailInputPanel", controls && selectedRailPanel == "Input");
    }

    private void SetRailPanelVisible(string name, bool visible)
        => this.FindControl<Grid>(name)!.IsVisible = visible;

    private void SetRailIconSelected(string name, bool selected)
    {
        var icon = this.FindControl<Button>(name)!;
        if (selected) icon.Classes.Add("selected");
        else icon.Classes.Remove("selected");
    }
}
