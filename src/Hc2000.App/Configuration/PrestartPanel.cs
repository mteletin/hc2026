using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Hc2000.App.Audio;
using Hc2000.App.Settings;

namespace Hc2000.App.Configuration;

/// <summary>Detached topic editor. Its callbacks receive validated immutable configuration, never a live machine.</summary>
public sealed class PrestartPanel : UserControl
{
    private readonly SettingsStore store;
    private readonly SettingsLoadResult loaded;
    private readonly WorkspaceSettings workspace;
    private readonly MachineConfiguration original;
    private readonly SettingsEditor editor;
    private readonly TextBox name, speed, scale;
    private readonly SoundSpeedNotice soundNotice;
    private readonly StartupMediaPanel media;
    private readonly StartupHardwarePanel hardware;
    private readonly StartupDisplayPanel displayPanel;
    private readonly bool newConfiguration;
    private readonly ComboBox keyboard;
    private readonly CheckBox saveIntent = new() { Name = "PrestartSaveIntent", Content = "Save configuration after successful Start" };
    private readonly CheckBox overwrite = new() { Name = "PrestartOverwrite", Content = "Replace unreadable/unsupported settings (original bytes will be replaced)", IsVisible = false };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button start = new() { Name = "PrestartStart", Content = "Start machine", Focusable = true };
    private bool starting;
    public Func<MachineConfiguration, Task<bool>>? StartRequested { get; set; }
    public event EventHandler? Started;
    public MachineConfiguration InitialConfiguration => original;
    public SoundSpeedNotice SoundNotice => soundNotice;
    public string? Status => status.Text;

    public PrestartPanel(SettingsStore store, LaunchOptions? launch = null,
        MachineConfiguration? initialConfiguration = null, string? assetRoot = null)
    {
        this.store = store;
        loaded = store.Load();
        workspace = loaded.Settings.Workspace ?? WorkspaceSettings.Import(loaded.Settings);
        // CLI/session resolution applies to the initial draft only. All other entries stay untouched.
        original = initialConfiguration ?? workspace.Machines.First(m => m.Id == workspace.SelectedId);
        newConfiguration = initialConfiguration is not null && !workspace.Machines.Any(m => m.Id == original.Id);
        original = original with
        {
            DiskA = Override(launch?.DiskA, original.DiskA, launch?.Writeback),
            DiskB = Override(launch?.DiskB, original.DiskB, launch?.Writeback)
        };
        editor = new SettingsEditor(original.Preferences());
        var machine = new StackPanel { Spacing = 6 };
        hardware = new StartupHardwarePanel(original);
        media = new StartupMediaPanel(original, store, assetRoot: assetRoot);
        media.SetHddEnabled(hardware.HddEnabled);
        hardware.HddEnabledChanged += (_, _) => media.SetHddEnabled(hardware.HddEnabled);
        machine.Children.Add(hardware);
        speed = Field(machine, "Speed: 0 (unlimited), 100, 200 or 400 percent (not CPU turbo)", editor.SpeedPercent, "PrestartSpeed");
        // The draft's speed decides whether this machine will be audible once started, so the draft
        // says so here; its control puts the draft back to 100 without touching any running machine.
        soundNotice = new SoundSpeedNotice(speed);
        machine.Children.Add(soundNotice);
        var display = new StackPanel { Spacing = 6 };
        scale = Field(display, "Requested integer zoom: 1–8 (viewport fits available space)", editor.Scale, "PrestartScale");
        displayPanel = new StartupDisplayPanel(original);
        display.Children.Add(displayPanel);
        var input = new StackPanel { Spacing = 6 };
        input.Children.Add(new TextBlock { Text = "Keyboard mapping" });
        keyboard = new ComboBox { Name = "PrestartKeyboard", ItemsSource = new[] { "Positional", "Symbolic" }, SelectedIndex = original.KeyboardMode == "Positional" ? 0 : 1 };
        input.Children.Add(keyboard);
        input.Children.Add(new TextBlock { Text = "Stored custom mappings are retained as opaque preferences; not applied.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var audio = new StackPanel { Spacing = 6 };
        audio.Children.Add(new TextBlock { Text = $"Stored audio preference: {original.AudioPreference} (opaque; not a backend selection)", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var tabs = new TabControl { Name = "ConfigurationTopics", ItemsSource = new[]
        {
            Topic("Hardware", machine), Topic("Display", display), Topic("Input", input), Topic("Media", media), Topic("Audio", audio)
        } };
        var form = new StackPanel { Spacing = 10 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock { Name = "ConfigurationHeading", Text = "Machine configuration", FontSize = 20,
            FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(start, 1);
        heading.Children.Add(start);
        form.Children.Add(heading);
        form.Children.Add(new TextBlock { Text = "One draft, five settings topics. Nothing runs or changes another machine until Start.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        name = Field(form, "Draft name", original.Name, "PrestartName");
        form.Children.Add(tabs);
        form.Children.Add(saveIntent);
        overwrite.IsVisible = loaded.Warning is not null;
        form.Children.Add(overwrite);
        status.Text = loaded.Warning ?? "Draft only. Start validates all selected media; settings are saved only if requested after a successful Start.";
        form.Children.Add(status);
        start.Click += async (_, _) => await StartAsync();
        Content = form;
    }

    private static FloppyConfiguration? Override(string? cli, FloppyConfiguration? stored, bool? writeback)
    {
        if (cli is null) return writeback is bool value && stored is not null ? stored with { Writeback = value } : stored;
        var path = cli[2..];
        if (LaunchStartup.TryResolveDisk(path, InstalledLayout.ConfigDirectories(), AppContext.BaseDirectory, out var resolved, out _))
            path = resolved;
        return new(Path.GetFullPath(path), writeback ?? false); // replaced path clears inherited raw geometry
    }
    private static TabItem Topic(string name, Control body) => new()
    {
        Header = name,
        // Keep detached topic controls in the logical tree before templates are applied.
        // The window owns scrolling, so fields remain reachable at compact sizes too.
        Content = new Border { Padding = new Thickness(4, 12), Child = body }
    };
    private static TextBox Field(StackPanel form, string label, string value, string name)
    {
        form.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var box = new TextBox { Text = value, Name = name }; form.Children.Add(box); return box;
    }
    public MachineConfiguration Build()
    {
        editor.SpeedPercent = speed.Text ?? ""; editor.Scale = scale.Text ?? "";
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
        if (!hardware.HddEnabled && !draft.Hdd.IsEmpty)
            throw new ArgumentException("Remove the selected HDD slots in Media before choosing non-HDD hardware; no slots were silently cleared.");
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
        store.Save(current.Settings with { Workspace = target with { SelectedId = draft.Id, Machines = machines } });
    }
    public async Task<bool> StartAsync()
    {
        if (starting || StartRequested is null) return false;
        starting = true; IsEnabled = false;
        var adopted = false;
        var saveRequested = saveIntent.IsChecked == true;
        try
        {
            var draft = Build(); draft.ValidateEffective();
            if (saveRequested && loaded.Warning is not null && overwrite.IsChecked != true)
                throw new ArgumentException("Confirm replacement of the original settings before saving.");
            if (!await StartRequested(draft)) { status.Text = "Start failed; draft not saved."; return false; }
            adopted = true;
            if (saveRequested)
            {
                try { Save(draft); }
                catch (Exception ex) when (IsExpected(ex)) { status.Text = "Running configuration not saved: " + ex.Message; return true; }
            }
            status.Text = "Machine started. Reset and supported live preferences affect only the selected machine.";
            IsEnabled = false;
            return true;
        }
        catch (Exception ex) when (IsExpected(ex)) { status.Text = "Not started: " + ex.Message; return false; }
        finally
        {
            starting = false; IsEnabled = !adopted;
            if (adopted) Started?.Invoke(this, EventArgs.Empty);
        }
    }
    private static bool IsExpected(Exception ex) => ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException or OverflowException;
}
