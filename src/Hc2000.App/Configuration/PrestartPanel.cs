using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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
    private readonly TextBox speed, scale, diskA, diskB;
    private readonly ComboBox keyboard;
    private readonly CheckBox saveIntent = new() { Name = "PrestartSaveIntent", Content = "Save configuration after successful Start" };
    private readonly CheckBox overwrite = new() { Name = "PrestartOverwrite", Content = "Replace unreadable/unsupported settings (original bytes will be replaced)", IsVisible = false };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button start = new() { Name = "PrestartStart", Content = "Start", Focusable = true };
    private bool starting;
    public Func<MachineConfiguration, Task<bool>>? StartRequested { get; set; }
    public MachineConfiguration InitialConfiguration => original;
    public string? Status => status.Text;

    public PrestartPanel(SettingsStore store, LaunchOptions? launch = null)
    {
        this.store = store;
        loaded = store.Load();
        workspace = loaded.Settings.Workspace ?? WorkspaceSettings.Import(loaded.Settings);
        // CLI/session resolution applies to the initial draft only. All other entries stay untouched.
        original = workspace.Machines.First(m => m.Id == workspace.SelectedId);
        original = original with
        {
            DiskA = Override(launch?.DiskA, original.DiskA, launch?.Writeback),
            DiskB = Override(launch?.DiskB, original.DiskB, launch?.Writeback)
        };
        editor = new SettingsEditor(original.Preferences());
        var machine = new StackPanel { Spacing = 6 };
        machine.Children.Add(new TextBlock { Text = "Faithful whole-chip HC2000 v2 / IF1 only; no ROM selection or hardware upgrades.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        machine.Children.Add(new TextBlock { Text = $"Stored profile: {original.Profile}; ROM preference: {original.RomSet}" });
        speed = Field(machine, "Speed: 0 (unlimited), 100, 200 or 400 percent (not CPU turbo)", editor.SpeedPercent, "PrestartSpeed");
        var display = new StackPanel { Spacing = 6 };
        scale = Field(display, "Requested integer zoom: 1–8 (viewport fits available space)", editor.Scale, "PrestartScale");
        var input = new StackPanel { Spacing = 6 };
        input.Children.Add(new TextBlock { Text = "Keyboard mapping" });
        keyboard = new ComboBox { Name = "PrestartKeyboard", ItemsSource = new[] { "Positional", "Symbolic" }, SelectedIndex = original.KeyboardMode == "Positional" ? 0 : 1 };
        input.Children.Add(keyboard);
        input.Children.Add(new TextBlock { Text = "Stored custom mappings are retained as opaque preferences; not applied.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var media = new StackPanel { Spacing = 6 };
        diskA = Field(media, "A: DSK path (writeback choice retained; default off)", original.DiskA?.Path ?? "", "PrestartDiskA");
        diskB = Field(media, "B: DSK path", original.DiskB?.Path ?? "", "PrestartDiskB");
        media.Children.Add(new TextBlock { Text = "Raw floppy/HDD activation unavailable in this editor. Snapshot selection is retained, never automatically loaded.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var audio = new StackPanel { Spacing = 6 };
        audio.Children.Add(new TextBlock { Text = $"Stored audio preference: {original.AudioPreference} (opaque; not a backend selection)", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var tabs = new TabControl { ItemsSource = new[]
        {
            Topic("Machine", machine), Topic("Display", display), Topic("Input", input), Topic("Media", media), Topic("Audio", audio)
        } };
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new TextBlock { Text = "Configuration — guest stopped until Start", FontSize = 20 });
        form.Children.Add(tabs);
        form.Children.Add(saveIntent);
        overwrite.IsVisible = loaded.Warning is not null;
        form.Children.Add(overwrite);
        status.Text = loaded.Warning ?? "Edits are drafts. Start validates the configuration; Save writes preferences only.";
        form.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        start.Click += async (_, _) => await StartAsync();
        var save = new Button { Content = "Save draft", Focusable = true };
        save.Click += (_, _) =>
        {
            try { var draft = Build(); Save(draft); status.Text = "Configuration saved; guest remains stopped."; }
            catch (Exception ex) when (IsExpected(ex)) { status.Text = "Not saved: " + ex.Message; }
        };
        buttons.Children.Add(start); buttons.Children.Add(save);
        form.Children.Add(buttons);
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
    private static TabItem Topic(string name, Control body) => new() { Header = name, Content = body };
    private static TextBox Field(StackPanel form, string label, string value, string name)
    {
        form.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var box = new TextBox { Text = value, Name = name }; form.Children.Add(box); return box;
    }
    public MachineConfiguration Build()
    {
        editor.SpeedPercent = speed.Text ?? ""; editor.Scale = scale.Text ?? "";
        var preferences = editor.Build();
        FloppyConfiguration? Disk(TextBox field, FloppyConfiguration? previous)
        {
            if (string.IsNullOrWhiteSpace(field.Text)) return null;
            var path = Path.GetFullPath(field.Text);
            return path == previous?.Path ? previous : new(path, previous?.Writeback ?? false);
        }
        var draft = original with
        {
            SpeedPercent = preferences.SpeedPercent, Scale = preferences.Scale,
            KeyboardMode = keyboard.SelectedIndex == 0 ? "Positional" : "Symbolic",
            DiskA = Disk(diskA, original.DiskA), DiskB = Disk(diskB, original.DiskB)
        };
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
        if (!target.Machines.Any(m => m.Id == draft.Id))
            throw new ArgumentException("Workspace changed; reopen the draft before saving.");
        var machines = target.Machines.Select(m => m.Id == draft.Id ? draft : m).ToImmutableArray();
        store.Save(current.Settings with { Workspace = target with { Machines = machines } });
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
            status.Text = "Running. Configuration edits require a new draft; no live Apply is enabled here.";
            IsEnabled = false;
            return true;
        }
        catch (Exception ex) when (IsExpected(ex)) { status.Text = "Not started: " + ex.Message; return false; }
        finally { starting = false; IsEnabled = !adopted; }
    }
    private static bool IsExpected(Exception ex) => ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException or OverflowException;
}
