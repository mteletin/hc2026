using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Explorer;

/// <summary>File listing for the selected tab; media selection stays in the existing inline lists.</summary>
internal sealed class DiskExplorerPanel : StackPanel
{
    internal void Bind(ExplorerPaneState state, Func<ExplorerImage, Task> import,
        Func<ExplorerImage, CpmEntry, Task> export)
    {
        Children.Clear();
        Spacing = 4;
        Children.Add(new TextBlock { Text = $"Disk explorer — {state.MachineName}", FontSize = 18 });
        Children.Add(new TextBlock { Name = "ExplorerNotice", Text = state.Notice, TextWrapping = TextWrapping.Wrap });
        var image = state.Image;
        if (state.Refresh is { } refresh)
        {
            var button = new Button { Content = "Refresh files" };
            button.Click += async (_, _) => await refresh();
            Children.Add(button);
        }
        if (image is null) return;
        Children.Add(new TextBlock { Name = "ExplorerSource", Text = image.Label, TextWrapping = TextWrapping.Wrap });
        Children.Add(new TextBlock { Text = "Stored records only: sizes are rounded to 128 bytes; terminal 0x00 / 0x1A are kept.", TextWrapping = TextWrapping.Wrap });
        foreach (var entry in image.Entries)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = $"user {entry.User} · {entry.Name} · {entry.Records} records / {entry.Size} bytes" +
                    (entry.ReadOnly ? " · R/O" : "") + (entry.System ? " · SYS" : "") +
                    (entry.Refused ? $" · REFUSED: {entry.Refusal}" : ""), TextWrapping = TextWrapping.Wrap
            });
            var button = new Button { Content = $"Copy out {entry.Name}…", IsEnabled = !entry.Refused };
            button.Click += async (_, _) => await export(image, entry);
            row.Children.Add(button);
            Children.Add(row);
        }
        if (image.Entries.Count == 0) Children.Add(new TextBlock { Text = "No files in this valid directory." });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var name = new TextBox { Name = "ExplorerImportName", Text = state.Name, PlaceholderText = "CP/M 8.3 name", Width = 160 };
        // TextChanged is deferred by Avalonia: preserve edits even if a tab is switched immediately.
        name.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) state.Name = name.Text ?? ""; };
        var user = new NumericUpDown { Name = "ExplorerImportUser", Minimum = 0, Maximum = 15, Value = state.User, Width = 90 };
        user.ValueChanged += (_, _) => state.User = (int)(user.Value ?? 0);
        var replace = new CheckBox { Content = "Explicitly replace existing CP/M file", IsChecked = state.Replace };
        replace.IsCheckedChanged += (_, _) => state.Replace = replace.IsChecked == true;
        var copy = new Button { Content = "Copy in host file…", IsEnabled = image.CanImport || image.CanImportVolume };
        copy.Click += async (_, _) => await import(image);
        controls.Children.Add(new TextBlock { Text = "User 0–15:" }); controls.Children.Add(user);
        controls.Children.Add(name); controls.Children.Add(replace); controls.Children.Add(copy);
        Children.Add(controls);
        Children.Add(new TextBlock
        {
            Text = image.Path is null ? "Live boundary snapshot: read-only; unmount the image before copy-in."
                : !image.CanImport && !image.CanImportVolume ? "HDD listing and copy-out only: the existing copy-in lane does not support flat HDD writes."
                : image.CanImportVolume ? "Flat 8 MiB volume: one-file copy-in reopens this file through the all-session mounted-image fence; it never edits live media."
                : "Copy-in reopens the host image through the all-session mounted-image fence; it never edits live media.",
            TextWrapping = TextWrapping.Wrap
        });
        Children.Add(new TextBlock { Text = MainWindow.ExplorerWriteScope, TextWrapping = TextWrapping.Wrap });
    }
}
