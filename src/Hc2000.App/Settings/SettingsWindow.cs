using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Hc2000.App.Settings;

public sealed class SettingsWindow : Window
{
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
        var scale = AddField(form, "Integer scale", draft.Scale);
        var audio = AddField(form, "Audio preference (opaque; backend not selected here)", draft.AudioPreference);
        var map = AddField(form, "Key mapping: JSON object of PC key → machine key strings", draft.KeyMapJson);
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
                draft.KeyMapJson = map.Text ?? "";
                if (loaded.Warning is not null && overwrite.IsChecked != true)
                    throw new ArgumentException("Confirm replacement of the original settings before saving.");
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
