using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hc2000.App.Explorer;

internal sealed class HostExplorerPanel : Grid
{
    private readonly HostDirectory directory = new();
    private readonly TextBlock location = new() { Name = "HostLocation", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notice = new() { Name = "HostNotice", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox rows = new() { Name = "HostRows" };
    private readonly Button back = new() { Content = "Back" };
    private readonly Button up = new() { Content = "Up" };
    private readonly Button open = new() { Content = "Open", IsEnabled = false };
    private readonly Button previewButton = new() { Content = "Preview", IsEnabled = false };
    private readonly TextBlock preview = new() { Name = "HostPreview", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox previewMode = new() { ItemsSource = new[] { "Text", "Hex" }, SelectedIndex = 0 };
    private ExplorerPreview? currentPreview;

    internal HostEntry? Selected => rows.SelectedItem as HostEntry;
    internal string? CurrentPath => directory.CurrentPath;
    internal Func<HostEntry, Task>? OpenFile { get; set; }
    internal Action? CopySelectedToImage { get; set; }

    internal void RefreshView()
    {
        directory.Refresh();
        Render();
    }

    internal HostExplorerPanel(string initialPath)
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto");
        var heading = new TextBlock { Text = "Host folder", FontSize = 18 };
        Children.Add(heading);
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(location);
        var navigation = new WrapPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "Refresh" };
        navigation.Children.Add(back);
        navigation.Children.Add(up);
        navigation.Children.Add(refresh);
        navigation.Children.Add(open);
        navigation.Children.Add(previewButton);
        header.Children.Add(navigation);
        header.Children.Add(notice);
        SetRow(header, 1);
        Children.Add(header);
        SetRow(rows, 2);
        Children.Add(rows);
        SetRow(previewMode, 3);
        Children.Add(previewMode);
        var previewScroll = new ScrollViewer { Content = preview, MaxHeight = 110 };
        SetRow(previewScroll, 4);
        Children.Add(previewScroll);

        back.Click += (_, _) => { directory.Back(); Render(); };
        up.Click += (_, _) => { directory.Up(); Render(); };
        refresh.Click += (_, _) => { directory.Refresh(); Render(); };
        open.Click += async (_, _) => await OpenSelectedAsync();
        rows.DoubleTapped += async (_, _) => await OpenSelectedAsync();
        rows.SelectionChanged += (_, _) =>
        {
            currentPreview = null;
            preview.Text = "";
            open.IsEnabled = Selected is not null;
            previewButton.IsEnabled = Selected is { IsDirectory: false };
        };
        previewButton.Click += (_, _) => PreviewSelected();
        previewMode.SelectionChanged += (_, _) => RenderPreview();
        var openMenu = new MenuItem { Header = "Open" };
        var previewMenu = new MenuItem { Header = "Preview" };
        var copyMenu = new MenuItem { Header = "Copy selected file to image" };
        openMenu.Click += async (_, _) => await OpenSelectedAsync();
        previewMenu.Click += (_, _) => PreviewSelected();
        copyMenu.Click += (_, _) => CopySelectedToImage?.Invoke();
        rows.ContextMenu = new ContextMenu { ItemsSource = new[] { openMenu, previewMenu, copyMenu } };
        rows.ContextMenu.Opening += (_, _) =>
        {
            openMenu.IsEnabled = Selected is not null;
            previewMenu.IsEnabled = Selected is { IsDirectory: false };
            copyMenu.IsEnabled = Selected is { IsDirectory: false } && CopySelectedToImage is not null;
        };
        directory.Open(initialPath);
        Render();
    }

    private async Task OpenSelectedAsync()
    {
        if (Selected is not { } selected) return;
        if (selected.IsDirectory)
        {
            directory.Open(selected.Path);
            Render();
        }
        else if (OpenFile is { } action) await action(selected);
    }

    private void PreviewSelected()
    {
        if (Selected is not { IsDirectory: false } selected) return;
        currentPreview = null;
        preview.Text = "";
        try
        {
            currentPreview = ExplorerPreview.FromHostFile(selected.Path);
            RenderPreview();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            notice.Text = "Preview unavailable: " + exception.Message;
        }
    }

    private void RenderPreview()
    {
        if (currentPreview is null) return;
        preview.Text = (previewMode.SelectedIndex == 1 ? currentPreview.Hex : currentPreview.Text)
            + (currentPreview.Truncated ? "\n[Preview limited to 4096 bytes]" : "");
    }

    private void Render()
    {
        currentPreview = null;
        preview.Text = "";
        rows.ItemsSource = directory.Entries;
        rows.SelectedItem = null;
        location.Text = directory.CurrentPath ?? "No folder open";
        notice.Text = directory.Notice;
        back.IsEnabled = directory.CanBack;
        up.IsEnabled = directory.CanUp;
        open.IsEnabled = false;
        previewButton.IsEnabled = false;
    }
}
