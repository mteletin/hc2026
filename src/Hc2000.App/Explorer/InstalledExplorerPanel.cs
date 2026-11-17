using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hc2000.App.Media;

namespace Hc2000.App.Explorer;

internal sealed record InstalledExplorerRow(InstalledMediaEntry Entry)
{
    public override string ToString() => Entry.IsDirectory ? Entry.Name + "/"
        : $"{Entry.Name} · {Entry.Media?.Kind} · {Entry.Media?.Detail}";
}

internal sealed class InstalledExplorerPanel : StackPanel
{
    private readonly ComboBox roots = new() { Name = "InstalledRoots" };
    private readonly TextBlock location = new() { Name = "InstalledLocation", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notice = new() { Name = "InstalledNotice", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox rows = new() { Name = "InstalledRows", Height = 100 };
    private readonly Stack<string> history = new();
    private readonly Button back = new() { Content = "Back", IsEnabled = false };
    private readonly Button up = new() { Content = "Up" };
    private readonly Button open = new() { Content = "Open", IsEnabled = false };
    private string? root;
    private string? path;
    private int request;

    internal Func<MediaHomeRow, Task>? OpenMedia { get; set; }

    internal InstalledExplorerPanel(IReadOnlyList<string> sourceRoots)
    {
        Spacing = 4;
        Children.Add(new TextBlock { Text = "Installed media", FontSize = 16 });
        roots.ItemsSource = sourceRoots;
        Children.Add(roots);
        Children.Add(location);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(back);
        actions.Children.Add(up);
        actions.Children.Add(open);
        Children.Add(actions);
        Children.Add(notice);
        Children.Add(rows);
        roots.SelectionChanged += async (_, _) =>
        {
            if (roots.SelectedItem is not string selected) return;
            root = selected;
            path = null;
            history.Clear();
            await NavigateAsync(selected);
        };
        rows.SelectionChanged += (_, _) =>
            open.IsEnabled = rows.SelectedItem is InstalledExplorerRow selected
                && (selected.Entry.IsDirectory || selected.Entry.Media?.Selectable == true);
        open.Click += async (_, _) => await OpenSelectedAsync();
        rows.DoubleTapped += async (_, _) => await OpenSelectedAsync();
        var openMenu = new MenuItem { Header = "Open" };
        openMenu.Click += async (_, _) => await OpenSelectedAsync();
        rows.ContextMenu = new ContextMenu { ItemsSource = new[] { openMenu } };
        rows.ContextMenu.Opening += (_, _) => openMenu.IsEnabled = open.IsEnabled;
        back.Click += async (_, _) =>
        {
            if (history.Count != 0) await NavigateAsync(history.Pop(), remember: false);
        };
        up.Click += async (_, _) =>
        {
            if (root is null || path is null || path == root) return;
            if (Directory.GetParent(path) is { } parent) await NavigateAsync(parent.FullName);
        };
        if (sourceRoots.Count != 0)
        {
            var available = sourceRoots.ToList().FindIndex(Directory.Exists);
            roots.SelectedIndex = available < 0 ? 0 : available;
        }
        else notice.Text = "No installed media roots are available.";
    }

    internal async Task NavigateAsync(string target, bool remember = true)
    {
        if (root is null) return;
        var previous = path;
        var currentRequest = ++request;
        path = null;
        rows.ItemsSource = Array.Empty<InstalledExplorerRow>();
        location.Text = target;
        notice.Text = "Loading installed media…";
        up.IsEnabled = false;
        var listing = await InstalledMediaDirectory.ListAsync(root, target);
        if (currentRequest != request) return;
        if (remember && previous is not null && !string.Equals(previous, target,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) history.Push(previous);
        notice.Text = listing.Notice + (listing.Truncated ? " Listing truncated." : "");
        rows.ItemsSource = listing.Entries.Select(entry => new InstalledExplorerRow(entry)).ToArray();
        path = listing.Available ? listing.Path : null;
        location.Text = path ?? target;
        up.IsEnabled = path is not null && !string.Equals(path, root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        back.IsEnabled = history.Count != 0;
    }

    private async Task OpenSelectedAsync()
    {
        if (rows.SelectedItem is not InstalledExplorerRow selected) return;
        if (selected.Entry.IsDirectory) await NavigateAsync(selected.Entry.Path);
        else if (selected.Entry.Media is { Selectable: true } media && OpenMedia is { } action) await action(media);
    }
}
