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
        var profile = AddField(form, "Profile", draft.Profile);
        var rom = AddField(form, "ROM set (path)", draft.RomSet);
        var speed = AddField(form, "Speed: 100, 200, 400 or 0 (unlimited)", draft.SpeedPercent);
        // Saved preferences decide the speed the next machine starts at, so the silence they would
        // cause is stated here too; its control puts the saved draft back to 100 before Save.
        soundNotice = new SoundSpeedNotice(speed);
        form.Children.Add(soundNotice);
        var scale = AddField(form, "Integer scale", draft.Scale);
        var audio = AddField(form, "Audio preference (opaque; backend not selected here)", draft.AudioPreference);
        var map = AddField(form, "Key mapping: JSON object of PC key → machine key strings", draft.KeyMapJson);
        var diskHome = AddField(form, "Disk home (directory; optional, blank clears)", draft.DiskHome);
        var tapeHome = AddField(form, "Tape home (directory; optional, blank clears)", draft.TapeHome);
        var snapshotHome = AddField(form, "Snapshot home (directory; optional, blank clears)", draft.SnapshotHome);
        // The sound-dump route (hc2000-app-sound-dump-route): same name and same fence as the CLI
        // flag; the field is fenced once on Save, before anything is written.
        var soundDump = AddField(form, "Sound dump: WAV file path (sound-dump; inside your user home; optional, blank clears)", draft.SoundDump);
        soundDump.Name = Audio.SoundDumpGate.Name;
        map.AcceptsReturn = true;
        map.MinHeight = 100;
        var status = new TextBlock
        {
            Text = loaded.Warning ?? $"Config: {store.FilePath}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        form.Children.Add(status);
        var overwrite = new CheckBox { Content = "Replace unreadable/unsupported original settings", IsVisible = loaded.Warning is not null };
        form.Children.Add(overwrite);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "Save" };
        save.Click += (_, _) =>
        {
            try
            {
                draft.Profile = profile.Text ?? "";
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
                store.Save(draft.Build());
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

    private static TextBox AddField(StackPanel form, string label, string value)
    {
        form.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var field = new TextBox { Text = value };
        form.Children.Add(field);
        return field;
    }
}
