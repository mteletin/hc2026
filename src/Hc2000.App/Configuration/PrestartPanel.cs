using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Hc2000.App.Audio;
using Hc2000.App.Settings;
using Hc2000.Machine.Capabilities;

namespace Hc2000.App.Configuration;

/// <summary>Detached topic editor. Its callbacks receive validated immutable configuration, never a live machine.</summary>
public sealed class PrestartPanel : UserControl
{
    private readonly SettingsStore store;
    private readonly SettingsLoadResult loaded;
    private readonly WorkspaceSettings workspace;
    private readonly MachineConfiguration original;
    private readonly SettingsEditor editor;
    private readonly TextBox name, speed, scale, border;
    private readonly SoundSpeedNotice soundNotice;
    private readonly StartupMediaPanel media;
    private readonly StartupHardwarePanel hardware;
    private readonly StartupDisplayPanel displayPanel;
    private readonly bool newConfiguration;
    private readonly ComboBox keyboard;
    private readonly TabControl topics;
    private readonly CheckBox saveIntent = new() { Name = "PrestartSaveIntent", Content = "Save configuration after successful Start" };
    private readonly CheckBox overwrite = new() { Name = "PrestartOverwrite", Content = "Replace unreadable/unsupported settings (original bytes will be replaced)", IsVisible = false };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "notice" } };
    private readonly Button start = new() { Name = "PrestartStart", Content = "Start machine", Focusable = true, Classes = { "primaryAction" } };
    // Readiness sits next to Start and says, before the click, whether this draft can start and if not why
    // (a refusal shown only far below the button was easy to miss).
    private readonly Avalonia.Controls.Shapes.Ellipse readinessDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock readiness = new() { Name = "StartReadiness", VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 };
    private readonly ProgressBar progress = new() { Name = "StartProgress", IsIndeterminate = true, IsVisible = false, Height = 4 };
    private readonly TextBlock progressText = new() { Name = "StartProgressText", IsVisible = false, Classes = { "caption" } };
    private readonly Border failureBanner = new() { Name = "StartFailureBanner", IsVisible = false, Classes = { "blockingBanner" }, Padding = new Thickness(8) };
    private readonly TextBlock failureText = new() { Name = "StartFailureText", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock failureDetails = new() { Name = "StartFailureDetails", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "caption" } };
    private bool starting, readinessQueued, syncingBorder;
    public Func<MachineConfiguration, Task<bool>>? StartRequested { get; set; }
    public event EventHandler? Started;
    public event EventHandler? DraftChanged;
    public event Action<string>? BorderChanged;
    public event EventHandler? OpenExplorerWorkspaceRequested;

    public void SyncBorder(string value)
    {
        syncingBorder = true;
        try
        {
            editor.Border = value;
            border.Text = value;
        }
        finally { syncingBorder = false; }
    }
    public MachineConfiguration InitialConfiguration => original;
    private long draftRevision;
    private string? draftFingerprint;
    public long DraftRevision => draftRevision;
    public Func<Media.InlineMediaRow, string, Task<string>>? LibraryHddAction
    {
        get => media.LibraryHddAction;
        set => media.LibraryHddAction = value;
    }

    public bool TryApplyLibraryHdd(long revision, ImmutableArray<HddConfiguration> selections)
    {
        if (starting || revision != draftRevision) return false;
        media.ApplyLibraryHdd(selections);
        draftRevision++;
        QueueReadiness();
        return true;
    }
    public SoundSpeedNotice SoundNotice => soundNotice;
    public StartupHardwarePanel HardwarePanel => hardware;
    public string? Status => status.Text;
    /// <summary>Null when this draft can start; otherwise the first reason it cannot, as shown next to Start.</summary>
    public string? NotReadyReason { get; private set; }
    /// <summary>The message of the last failed Start, as the failure banner shows it; null while the banner is hidden.</summary>
    public string? FailureMessage => failureBanner.IsVisible ? failureText.Text : null;
    /// <summary>Where the Start owner put its reason when it returned false (the main window's runtime status).</summary>
    public Func<string?>? StartFailureReason { get; set; }
    /// <summary>Catalog refusal for a submitted CLI hardware set that cannot map to a profile; the
    /// panel stays read-only and Start never boots. null once the set admitted a profile.</summary>
    public string? LaunchRefusal { get; private set; }

    public PrestartPanel(SettingsStore store, LaunchOptions? launch = null,
        MachineConfiguration? initialConfiguration = null, string? assetRoot = null)
    {
        this.store = store;
        loaded = store.Load();
        // A new installation starts with a new instance (hc2000-full-workstation-default); a settings file from before
        // workspaces keeps its stored machine. A CLI hardware set describes the machine from the faithful base, so on a
        // new installation it overlays that base, never the new instance's hardware.
        var cliHardware = launch?.Capabilities is { Names.Count: > 0 };
        workspace = loaded.Settings.Workspace
            ?? (loaded.NoFile && store.NewInstallationDefaults && !cliHardware
                ? WorkspaceSettings.NewInstance(loaded.Settings) : WorkspaceSettings.Import(loaded.Settings));
        // CLI/session resolution applies to the initial draft only. All other entries stay untouched.
        original = initialConfiguration ?? workspace.Machines.First(m => m.Id == workspace.SelectedId);
        newConfiguration = initialConfiguration is not null && !workspace.Machines.Any(m => m.Id == original.Id);
        // A named-HDD cold draft never cold-reads the launch A/B: the single cold-read fence belongs
        // to the disk reservation (StartupHardwarePreparation), so on such a draft the launch
        // overrides stay launch options and ride the warm insertion lane after the acknowledged
        // start (adr-saved-draft-launch-638412). The draft's own saved A/B is never touched.
        var namedHddColdDraft = original.Hdd.Any(h => h.Volume is not null);
        original = original with
        {
            DiskA = namedHddColdDraft ? original.DiskA : Override(launch?.DiskA, original.DiskA, launch?.Writeback),
            DiskB = namedHddColdDraft ? original.DiskB : Override(launch?.DiskB, original.DiskB, launch?.Writeback)
        };
        // A submitted CLI hardware set resolves against the catalog before any panel seeds:
        // it overlays the stored profile's row values and must map to one admitted profile,
        // otherwise the catalog sentence is the whole refusal and nothing boots.
        if (launch?.Capabilities is { } submitted && submitted.Names.Count > 0)
        {
            var seed = CapabilityCatalog.FromProfile(original.Profile, original.TextOverlay, original.CpuTurboMultiplier,
                original.ExpansionDriverCount, original.X2Vga, original.X3Sound, original.X4Math, original.X6Net);
            if (seed is null)
            {
                LaunchRefusal = CapabilityCatalog.NoMapping;
            }
            else
            {
                var set = seed;
                foreach (var name in submitted.Names) set = set.With(name, submitted.Get(name)!);
                var result = CapabilityCatalog.ToProfile(set);
                if (result.Refusal is not null)
                    LaunchRefusal = result.Refusal;
                else
                    original = original with
                    {
                        Profile = result.Profile!,
                        CpuTurboMultiplier = int.Parse(CapabilityCatalog.Effective(set, "cpu-turbo"), CultureInfo.InvariantCulture),
                        ExpansionDriverCount = result.Profile is "hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1"
                            or "hc2000-v2-full-workstation-v1"
                            ? int.Parse(CapabilityCatalog.Effective(set, "driver-z80"), CultureInfo.InvariantCulture) : 0,
                        X2Vga = CapabilityCatalog.IsOn(set, "x2-vga"),
                        X3Sound = CapabilityCatalog.IsOn(set, "x3-sound"),
                        X4Math = CapabilityCatalog.IsOn(set, "x4-math"),
                        X6Net = CapabilityCatalog.IsOn(set, "x6-net"),
                        // The card comes with its gateway on; the status bar switches it off.
                        NetworkGateway = CapabilityCatalog.IsOn(set, "x6-net"),
                        TextOverlay = CapabilityCatalog.IsOn(set, "text-overlay")
                    };
            }
        }
        editor = new SettingsEditor(original.Preferences() with { Border = loaded.Settings.Border });
        var machine = new StackPanel { Spacing = 6 };
        hardware = new StartupHardwarePanel(original);
        var options = new StackPanel { Spacing = 10 };
        media = new StartupMediaPanel(original, store, assetRoot: assetRoot);
        media.SetHddEnabled(hardware.HddEnabled, hardware.SelectedProfile);
        hardware.HddEnabledChanged += (_, _) => media.SetHddEnabled(hardware.HddEnabled, hardware.SelectedProfile);
        hardware.Changed += (_, _) => { NoteDraftEdit(); media.SetSelectedProfile(hardware.SelectedProfile); };
        machine.Children.Add(hardware);
        hardware.AddDetail(new TextBlock { Name = "PrestartRomSelection", Text = "ROM: HC2000 v2 + IF1, the whole M27C512 chip (64 KiB), on every machine.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } });
        // Emulation speed as four choices instead of a number to type; the PrestartSpeed field stays the one
        // source the editor and the sound notice read, out of sight.
        speed = Field(options, "Speed", editor.SpeedPercent, "PrestartSpeed");
        foreach (var child in options.Children) child.IsVisible = false;
        ChoiceSegments.Speed(options, speed);
        // The draft's speed decides whether this machine will be audible once started, so the draft
        // says so here; its control puts the draft back to 100 without touching any running machine.
        soundNotice = new SoundSpeedNotice(speed);
        options.Children.Add(soundNotice);
        hardware.AddOption(options);
        var display = new StackPanel { Spacing = 6 };
        scale = Field(display, "Zoom", editor.Scale, "PrestartScale");
        border = Field(display, "Border", editor.Border, "PrestartBorder");
        foreach (var child in display.Children) child.IsVisible = false; // keep the field as the source, out of sight
        ChoiceSegments.Zoom(display, scale);
        ChoiceSegments.Border(display, border);
        border.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty || syncingBorder) return;
            var next = (border.Text ?? "").Trim();
            if (next is "Thin" or "None" or "Full" && editor.Border != next)
            {
                editor.Border = next;
                BorderChanged?.Invoke(next);
            }
        };
        displayPanel = new StartupDisplayPanel(original);
        display.Children.Add(displayPanel);
        var input = new StackPanel { Spacing = 6 };
        input.Children.Add(new TextBlock { Text = "Keyboard mapping" });
        keyboard = new ComboBox { Name = "PrestartKeyboard", ItemsSource = new[] { "Positional", "Symbolic" }, SelectedIndex = original.KeyboardMode == "Positional" ? 0 : 1 };
        input.Children.Add(keyboard);
        input.Children.Add(new TextBlock { Text = "Saved custom key mappings are kept, but are not used yet.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var audio = new StackPanel { Spacing = 6 };
        audio.Children.Add(new TextBlock { Text = "Sound plays at normal emulation speed. Saved audio preferences are kept; choosing an audio device is not available yet.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        topics = ConfigurationSections.Tabs("ConfigurationTopics",
            Topic("Hardware", machine), Topic("Display", display), Topic("Input", input), Topic("Media", media), Topic("Audio", audio)
        );
        var openWorkspace = new Button
        {
            Name = "OpenExplorerWorkspace",
            Content = "Open Explorer workspace",
            Focusable = true,
            Classes = { "secondaryAction" },
            HorizontalAlignment = HorizontalAlignment.Left
        };
        openWorkspace.Click += (_, _) => OpenExplorerWorkspaceRequested?.Invoke(this, EventArgs.Empty);
        ToolTip.SetTip(openWorkspace, "Open modeless Explorer workspace to search and browse CP/M images (Ctrl+Shift+E).");
        var form = new StackPanel { Spacing = 12 };
        // One header line: what this is, its name, whether it can start, and Start.
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        heading.Children.Add(new TextBlock { Name = "ConfigurationHeading", Text = "Machine configuration", FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "sectionTitle" } });
        ToolTip.SetTip(heading.Children[0], "Set up this machine before starting it. Other machines are unaffected.");
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(nameRow, 1);
        Grid.SetColumnSpan(nameRow, 2);
        heading.Children.Add(nameRow);
        var ready = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ready.Children.Add(readinessDot); ready.Children.Add(readiness);
        Grid.SetRow(ready, 2);
        Grid.SetColumnSpan(ready, 2);
        heading.Children.Add(ready);
        Grid.SetRow(openWorkspace, 3);
        Grid.SetColumnSpan(openWorkspace, 2);
        heading.Children.Add(openWorkspace);
        Grid.SetColumn(start, 1);
        heading.Children.Add(start);
        form.Children.Add(heading);
        form.Children.Add(progress);
        form.Children.Add(progressText);
        var failure = new StackPanel { Spacing = 6 };
        failure.Children.Add(failureText);
        failure.Children.Add(new Expander { Header = "Details", Content = failureDetails });
        var dismiss = new Button { Name = "StartFailureDismiss", Content = "Dismiss", Classes = { "secondaryAction" } };
        dismiss.Click += (_, _) => failureBanner.IsVisible = false;
        failure.Children.Add(dismiss);
        failureBanner.Child = failure;
        form.Children.Add(failureBanner);
        name = new TextBox { Name = "PrestartName", Text = original.Name, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(new TextBlock { Text = "Name", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        nameRow.Children.Add(name);
        form.Children.Add(topics);
        form.Children.Add(saveIntent);
        overwrite.IsVisible = loaded.Warning is not null;
        form.Children.Add(overwrite);
        status.Text = loaded.Warning ?? "Not started yet. Save settings after starting only if you choose to.";
        form.Children.Add(status);
        start.Click += async (_, _) => await StartAsync();
        // Any edit anywhere in the draft re-checks readiness (once per dispatcher turn).
        form.AddHandler(TextBox.TextChangedEvent, (_, _) => NoteDraftEdit());
        form.AddHandler(Avalonia.Controls.Primitives.SelectingItemsControl.SelectionChangedEvent, (_, _) => NoteDraftEdit());
        form.AddHandler(Avalonia.Controls.Primitives.ToggleButton.IsCheckedChangedEvent, (_, _) => NoteDraftEdit());
        form.AddHandler(Button.ClickEvent, (_, _) => QueueReadiness());
        saveIntent.IsCheckedChanged += (_, _) => QueueReadiness();
        overwrite.IsCheckedChanged += (_, _) => QueueReadiness();
        Content = form;
        try { draftFingerprint = JsonSerializer.Serialize(Build()); }
        catch (Exception ex) when (IsExpected(ex)) { }
        RefreshReadiness();
    }

    private void NoteDraftEdit()
    {
        string? fingerprint = null;
        try { fingerprint = JsonSerializer.Serialize(Build()); }
        catch (Exception ex) when (IsExpected(ex)) { }
        // Layout/topic selection and delayed initial TextChanged events are not edits.
        // Invalid intermediate inputs still invalidate any captured async selection.
        if (fingerprint is null || fingerprint != draftFingerprint) draftRevision++;
        draftFingerprint = fingerprint;
        QueueReadiness();
    }

    private void QueueReadiness()
    {
        if (readinessQueued) return;
        readinessQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { readinessQueued = false; RefreshReadiness(); });
    }

    /// <summary>The same checks Start runs, without starting: so a disabled Start and a refused Start can never disagree.</summary>
    public void RefreshReadiness()
    {
        string? reason = null;
        try
        {
            CheckMedia(Build());
            if (saveIntent.IsChecked == true && loaded.Warning is not null && overwrite.IsChecked != true)
                reason = "Confirm replacement of the original settings before saving.";
        }
        catch (Exception ex) when (IsExpected(ex)) { reason = ex.Message; }
        NotReadyReason = reason;
        readiness.Text = reason ?? (media.HddNotAttached ? "Ready to start (hard disk images not attached)" : "Ready to start");
        readinessDot.Fill = reason is null ? Brush("AppStatusRunningBrush", "#2F855A") : Brush("AppWarningBrush", "#B7791F");
        start.IsEnabled = reason is null && !starting;
        ToolTip.SetTip(start, reason is null ? "Check this setup and its media, then start this machine." : "Can't start yet: " + reason);
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private Avalonia.Media.IBrush Brush(string key, string fallback) =>
        this.TryFindResource(key, out var value) && value is Avalonia.Media.IBrush brush ? brush : Avalonia.Media.Brush.Parse(fallback);

    private void ShowFailure(string message, string details)
    {
        failureText.Text = message;
        failureDetails.Text = details + Environment.NewLine + "Every Start is recorded in " + Diagnostics.StartLog.PathFor(store.FilePath);
        failureBanner.IsVisible = true;
    }

    private static FloppyConfiguration? Override(string? cli, FloppyConfiguration? stored, bool? writeback)
    {
        if (cli is null) return writeback is bool value && stored is not null ? stored with { Writeback = value } : stored;
        var path = cli[2..];
        if (LaunchStartup.TryResolveDisk(path, InstalledLayout.ConfigDirectories(), AppContext.BaseDirectory, out var resolved, out _))
            path = resolved;
        return new(Path.GetFullPath(path), writeback ?? false); // replaced path clears inherited raw geometry
    }
    private static TabItem Topic(string name, Control body) => ConfigurationSections.Topic(name, body);
    private static TextBox Field(StackPanel form, string label, string value, string name)
    {
        form.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var box = new TextBox { Text = value, Name = name }; form.Children.Add(box); return box;
    }
    public MachineConfiguration Build()
    {
        editor.SpeedPercent = speed.Text ?? ""; editor.Scale = scale.Text ?? "";
        editor.Border = string.IsNullOrWhiteSpace(border.Text) ? "Thin" : border.Text.Trim();
        var preferences = editor.Build();
        var draft = original with
        {
            Name = name.Text ?? "",
            SpeedPercent = preferences.SpeedPercent, Scale = preferences.Scale,
            KeyboardMode = keyboard.SelectedIndex == 0 ? "Positional" : "Symbolic"
        };
        draft = media.ApplyTo(draft);
        draft = hardware.ApplyTo(draft);
        draft = displayPanel.ApplyTo(draft);
        // A machine without the HDD controller does not take the draft's C:-F: images; the Media topic says so
        // and offers to remove them, instead of a refusal at Start.
        if (!hardware.HddEnabled && !draft.Hdd.IsEmpty) draft = draft with { Hdd = [] };
        draft.Validate();
        return draft;
    }
    private void Save(MachineConfiguration draft)
    {
        if (loaded.Warning is not null && overwrite.IsChecked != true)
            throw new ArgumentException("Confirm replacement of the original settings before saving.");
        // Settings… is modal, but may have saved legacy preferences since this draft opened.
        // Preserve that writer's latest section rather than replaying stale top-level values.
        var current = store.Load();
        if (current.Warning is not null && overwrite.IsChecked != true)
            throw new ArgumentException("Original settings became unreadable; confirm replacement before saving.");
        var target = current.Settings.Workspace ?? workspace;
        var exists = target.Machines.Any(m => m.Id == draft.Id);
        if (newConfiguration && exists)
            throw new ArgumentException("Workspace identity changed; the existing configuration was not replaced.");
        if (!newConfiguration && !exists)
            throw new ArgumentException("Workspace changed; reopen the draft before saving.");
        if (newConfiguration && target.Machines.Length >= 4)
            throw new ArgumentException("Workspace already contains four configurations; the running machine was not saved.");
        var machines = newConfiguration ? target.Machines.Add(draft)
            : target.Machines.Select(m => m.Id == draft.Id ? draft : m).ToImmutableArray();
        store.Save(current.Settings with { Border = editor.Border, Workspace = target with { SelectedId = draft.Id, Machines = machines } });
    }
    public async Task<bool> StartAsync()
    {
        // Every Start outcome is reported on this panel's status line AND to the App's start.log
        // (StartLog), so a Start that does nothing on an installed release names its cause in both;
        // a refusal or a failure also stays visible as the banner under the heading until dismissed.
        if (starting)
        { status.Text = "Start ignored: already starting."; Diagnostics.StartLog.Write(store.FilePath, "Start ignored: already starting."); return false; }
        if (StartRequested is null)
        { status.Text = "Start unavailable: the Start action is not wired."; Diagnostics.StartLog.Write(store.FilePath, "Start unavailable: the Start action is not wired."); return false; }
        starting = true; IsEnabled = false;
        failureBanner.IsVisible = false;
        var adopted = false;
        var saveRequested = saveIntent.IsChecked == true;
        try
        {
            var draft = Build(); CheckMedia(draft);
            if (saveRequested && loaded.Warning is not null && overwrite.IsChecked != true)
                throw new ArgumentException("Confirm replacement of the original settings before saving.");
            start.Content = "Starting";
            progress.IsVisible = progressText.IsVisible = true;
            progressText.Text = StartupHardwarePanel.Cards.FirstOrDefault(card => card.Profile == draft.Profile)?.LargeMemory == true
                ? "Reserving 1 GiB for the machine's memory" : "Starting the machine";
            if (!await StartRequested(draft))
            {
                var reason = StartFailureReason?.Invoke();
                status.Text = "Start failed; settings were not saved.";
                Diagnostics.StartLog.Write(store.FilePath, "Start failed: " + draft.Name + " (" + draft.Profile + "); draft not saved." + (string.IsNullOrWhiteSpace(reason) ? "" : " " + reason));
                ShowFailure("The machine didn't start. Your settings are still here.", string.IsNullOrWhiteSpace(reason) ? "No reason was reported." : reason);
                return false;
            }
            adopted = true;
            if (saveRequested)
            {
                try { Save(draft); }
                catch (Exception ex) when (IsExpected(ex)) { status.Text = "Running configuration not saved: " + ex.Message; Diagnostics.StartLog.Write(store.FilePath, "Start succeeded but configuration not saved: " + ex.Message); return true; }
            }
            status.Text = "Machine started. Reset and supported live preferences affect only the selected machine.";
            Diagnostics.StartLog.Write(store.FilePath, "Machine started: " + draft.Name + " (" + draft.Profile + ").");
            IsEnabled = false;
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            status.Text = "Not started: " + ex.Message;
            Diagnostics.StartLog.Write(store.FilePath, "Not started: " + ex.Message);
            ShowFailure("Not started: " + ex.Message, ex.GetType().Name);
            return false;
        }
        catch (OutOfMemoryException ex)
        {
            // A 1 GiB machine on a host without that much free memory: say so instead of failing silently.
            status.Text = "Not started: not enough free memory.";
            Diagnostics.StartLog.Write(store.FilePath, "Not started: out of memory: " + ex.Message);
            ShowFailure("Not started: there isn't enough free memory for this machine. Close other machines or pick a smaller one.", ex.Message);
            return false;
        }
        finally
        {
            starting = false; IsEnabled = !adopted;
            start.Content = "Start machine";
            progress.IsVisible = progressText.IsVisible = false;
            if (!adopted) RefreshReadiness();
            if (adopted) Started?.Invoke(this, EventArgs.Empty);
        }
    }
    /// <summary>The configuration rules plus the media a Start would open: an image path that is not there is a reason
    /// not to start, said before Start rather than after the machine fails to compose.</summary>
    private static void CheckMedia(MachineConfiguration draft)
    {
        draft.ValidateEffective();
        foreach (var (slot, path) in new[] { ("A", draft.DiskA?.Path), ("B", draft.DiskB?.Path) }.Concat(draft.Hdd.Select(h => (h.Slot, (string?)h.Path))))
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
                throw new FileNotFoundException($"The disk image for {slot}: is not there: {path}", path);
    }

    public void SelectTopic(string name)
    {
        if (topics.ItemsSource is IReadOnlyList<TabItem> items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (Equals(items[i].Header, name))
                {
                    topics.SelectedIndex = i;
                    break;
                }
            }
        }
        else if (topics.Items is { } itemList)
        {
            for (var i = 0; i < itemList.Count; i++)
            {
                if (itemList[i] is TabItem item && Equals(item.Header, name))
                {
                    topics.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private static bool IsExpected(Exception ex) => ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException or OverflowException;
}
