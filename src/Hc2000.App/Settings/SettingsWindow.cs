using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Hc2000.App.Audio;

namespace Hc2000.App.Settings;

public sealed class SettingsWindow : Window
{
    private readonly SoundSpeedNotice soundNotice;
    public SoundSpeedNotice SoundNotice => soundNotice;
    public event Action<string>? ThemeSaved;
    public event Action<string>? BorderSaved;
    public event Action<int>? ScaleSaved;

    public SettingsWindow(SettingsStore store)
    {
        Title = "HC2000 settings";
        Width = 560;
        Height = 700;
        var loaded = store.Load();
        var draft = new SettingsEditor(loaded.Settings);
        var form = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        form.Children.Add(new TextBlock
        {
            Text = "Preferences are saved for the next startup. Runtime integration is separate.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        Section(form, "Machine");
        var profile = AddField(form, "Machine profile", draft.Profile, "SettingsProfile");
        // hc2000-accelerated-clock-domains: the main-Z80 clock choice, saved for the next startup;
        // the running machine can also switch it live (Reconfigure panel) without a reset.
        // 280 MHz is a target, not a promise: the running UI shows achieved speed or overload.
        var mainCpu = new ComboBox
        {
            Name = "SettingsMainCpu",
            ItemsSource = new[] { Hc2000.Machine.MainCpuSpeedInfo.Auto }.Concat(Hc2000.Machine.MainCpuSpeedInfo.All.Select(speed => Hc2000.Machine.MainCpuSpeedInfo.Setting(speed))).ToArray(),
            SelectedItem = draft.MainCpuTarget
        };
        form.Children.Add(new TextBlock
        {
            Text = "Main CPU clock (auto: 140 MHz on the CP/M workstations, faithful on Classic and the BASIC expansion machines; or faithful 3.5 MHz class, or a 7/14/28/140/280 MHz target; from 140 MHz up timing is relaxed for speed, and the target is not a promise)",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        form.Children.Add(mainCpu);
        var rom = AddField(form, "ROM selection (set path)", draft.RomSet, "SettingsRomSet");
        Section(form, "Display");
        form.Children.Add(new TextBlock { Text = "Theme" });
        var theme = new ComboBox { Name = "SettingsTheme", ItemsSource = new[] { "Dark", "Light", "System" }, SelectedItem = draft.Theme };
        form.Children.Add(theme);
        var speed = AddField(form, "Speed (percent: 100, 200, 400; 0 = unlimited)", draft.SpeedPercent, "SettingsSpeed");
        // Saved preferences decide the speed the next machine starts at, so the silence they would
        // cause is stated here too; its control puts the saved draft back to 100 before Save.
        soundNotice = new SoundSpeedNotice(speed);
        form.Children.Add(soundNotice);
        var scale = AddField(form, "Screen zoom (0 = Fit, 1–8 = cap)", draft.Scale, "SettingsScale");
        form.Children.Add(new TextBlock { Text = "Border" });
        var border = new ComboBox { Name = "SettingsBorder", ItemsSource = new[] { "Thin", "None", "Full" }, SelectedItem = draft.Border };
        Avalonia.Automation.AutomationProperties.SetName(border, "Border");
        form.Children.Add(border);
        Section(form, "Folders");
        var diskHome = AddField(form, "Disk home (directory; optional, blank clears)", draft.DiskHome);
        var tapeHome = AddField(form, "Tape home (directory; optional, blank clears)", draft.TapeHome);
        var snapshotHome = AddField(form, "Snapshot home (directory; optional, blank clears)", draft.SnapshotHome);
        // The sound-dump route (hc2000-app-sound-dump-route): same name and same fence as the CLI
        // flag; the field is fenced once on Save, before anything is written.
        var soundDump = AddField(form, "Sound dump: WAV file path (sound-dump; inside your user home; optional, blank clears)", draft.SoundDump);
        soundDump.Name = Audio.SoundDumpGate.Name;
        var advanced = new StackPanel { Spacing = 8 };
        var audio = AddField(advanced, "Audio preference (opaque; backend not selected here)", draft.AudioPreference, "SettingsAudio");
        var map = AddField(advanced, "Key mapping: JSON object of PC key → machine key strings", draft.KeyMapJson, "SettingsKeyMap");
        map.AcceptsReturn = true;
        map.MinHeight = 100;
        form.Children.Add(new Expander { Name = "SettingsAdvanced", Header = "Advanced", IsExpanded = false, Content = advanced });
        var status = new TextBlock
        {
            Text = loaded.Warning ?? $"Config: {store.FilePath}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        form.Children.Add(status);
        var overwrite = new CheckBox { Content = "Replace unreadable/unsupported original settings", IsVisible = loaded.Warning is not null };
        form.Children.Add(overwrite);
        border.SelectionChanged += (_, _) =>
        {
            if (border.SelectedItem is not string selected || selected == draft.Border) return;
            draft.Border = selected;
            BorderSaved?.Invoke(selected);
            if (loaded.Warning is not null && overwrite.IsChecked != true) return;
            try
            {
                var current = store.Load();
                if (current.Warning is null || overwrite.IsChecked == true)
                {
                    store.Save(current.Settings with { Border = selected });
                    status.Text = $"Saved to {store.FilePath}.";
                }
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
            {
                status.Text = $"Settings not saved: {exception.Message}";
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "Save" };
        save.Click += (_, _) =>
        {
            try
            {
                draft.Theme = theme.SelectedItem as string ?? "Dark";
                draft.Border = border.SelectedItem as string ?? "Thin";
                draft.Profile = profile.Text ?? "";
                draft.MainCpuTarget = mainCpu.SelectedItem as string ?? "faithful";
                draft.RomSet = rom.Text ?? "";
                draft.SpeedPercent = speed.Text ?? "";
                draft.Scale = scale.Text ?? "";
                draft.AudioPreference = audio.Text ?? "";
                draft.DiskHome = diskHome.Text ?? "";
                draft.TapeHome = tapeHome.Text ?? "";
                draft.SnapshotHome = snapshotHome.Text ?? "";
                draft.SoundDump = soundDump.Text ?? "";
                draft.KeyMapJson = map.Text ?? "";
                if (loaded.Warning is not null && overwrite.IsChecked != true)
                    throw new ArgumentException("Confirm replacement of the original settings before saving.");
                // The dump-route fence, once, before the save: the one refusal sentence, identical to
                // the CLI flag's (PO ruling board 634859).
                var dump = Audio.SoundDumpGate.Resolve(draft.SoundDump);
                if (!dump.Ok) throw new ArgumentException(dump.Refusal!);
                var built = draft.Build();
                store.Save(built);
                ThemeSaved?.Invoke(draft.Theme);
                BorderSaved?.Invoke(draft.Border);
                ScaleSaved?.Invoke(built.Scale);
                status.Text = $"Saved to {store.FilePath}.";
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
            {
                status.Text = $"Settings not saved: {exception.Message}";
            }
        };
        var cancel = new Button { Content = "Close / discard unsaved edits" };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        form.Children.Add(buttons);
        Content = new ScrollViewer { Content = form };
    }

    private static void Section(StackPanel form, string title) =>
        form.Children.Add(new TextBlock { Text = title, Classes = { "sectionTitle" } });

    private static TextBox AddField(StackPanel form, string label, string value, string? name = null)
    {
        form.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var field = new TextBox { Text = value, Name = name };
        form.Children.Add(field);
        return field;
    }
}
