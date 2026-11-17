using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Explorer;

internal sealed record ImageFileRow(CpmEntry Entry)
{
    public override string ToString() => $"user {Entry.User} · {Entry.Name} · {Entry.Size:N0} bytes"
        + (Entry.ReadOnly ? " · R/O" : "") + (Entry.System ? " · SYS" : "")
        + (Entry.Refused ? " · refused: " + Entry.Refusal : "");
}

internal sealed record ExplorerVolumeRow(string Name, long Size, bool ReadOnly)
{
    public override string ToString() => $"{Name} · {Size:N0} bytes" + (ReadOnly ? " · R/O" : "");
}

internal sealed record TapeFileRow(TapeFile File)
{
    public override string ToString() => $"{File.Name} · TAP type {File.Type} · {File.Data.Length:N0} bytes · start {File.Start}";
}

internal sealed record ExplorerVolumeListing(IReadOnlyList<ExplorerVolumeRow> Volumes, string Notice,
    IReadOnlyList<TapeFile>? TapeFiles = null);

internal sealed class ImageExplorerPanel : Grid
{
    private readonly TextBlock location = new() { Name = "ImageLocation", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notice = new() { Name = "ImageNotice", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox rows = new() { Name = "ImageRows" };
    private readonly Button previewButton = new() { Content = "Preview", IsEnabled = false };
    private readonly Button openVolume = new() { Content = "Open volume", IsEnabled = false };
    private readonly Button back = new() { Content = "Back to volumes", IsVisible = false };
    private readonly ComboBox previewMode = new() { ItemsSource = new[] { "Text", "Hex" }, SelectedIndex = 0 };
    private readonly TextBlock preview = new() { Name = "ImagePreview", TextWrapping = TextWrapping.Wrap };
    private ExplorerImage? image;
    private ExplorerPreview? currentPreview;

    internal CpmEntry? Selected => (rows.SelectedItem as ImageFileRow)?.Entry;
    internal Func<ExplorerVolumeRow, Task>? OpenVolume { get; set; }
    internal Action? BackToVolumes { get; set; }
    internal Action? CopySelectedToHost { get; set; }
    internal Action? Cleared { get; set; }

    internal ImageExplorerPanel()
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto");
        Children.Add(new TextBlock { Text = "Image contents", FontSize = 18 });
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(location);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(back);
        actions.Children.Add(openVolume);
        actions.Children.Add(previewButton);
        header.Children.Add(actions);
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
        rows.SelectionChanged += (_, _) =>
        {
            currentPreview = null;
            preview.Text = "";
            previewButton.IsEnabled = Selected is { Refused: false } || rows.SelectedItem is TapeFileRow;
            openVolume.IsEnabled = rows.SelectedItem is ExplorerVolumeRow && OpenVolume is not null;
        };
        openVolume.Click += async (_, _) => await OpenSelectedVolumeAsync();
        rows.DoubleTapped += async (_, _) => await OpenSelectedVolumeAsync();
        back.Click += (_, _) => BackToVolumes?.Invoke();
        previewMode.SelectionChanged += (_, _) => RenderPreview();
        previewButton.Click += (_, _) => PreviewSelected();
        var openMenu = new MenuItem { Header = "Open volume" };
        var previewMenu = new MenuItem { Header = "Preview" };
        var copyMenu = new MenuItem { Header = "Copy selected file to host" };
        openMenu.Click += async (_, _) => await OpenSelectedVolumeAsync();
        previewMenu.Click += (_, _) => PreviewSelected();
        copyMenu.Click += (_, _) => CopySelectedToHost?.Invoke();
        rows.ContextMenu = new ContextMenu { ItemsSource = new[] { openMenu, previewMenu, copyMenu } };
        rows.ContextMenu.Opening += (_, _) =>
        {
            openMenu.IsEnabled = rows.SelectedItem is ExplorerVolumeRow && OpenVolume is not null;
            previewMenu.IsEnabled = Selected is { Refused: false } || rows.SelectedItem is TapeFileRow;
            copyMenu.IsEnabled = Selected is { Refused: false } && CopySelectedToHost is not null;
        };
        Bind(null, "Open a supported image to see its files.");
    }

    internal void Bind(ExplorerImage? value, string message)
    {
        image = value;
        if (value is null) Cleared?.Invoke();
        currentPreview = null;
        preview.Text = "";
        rows.ItemsSource = value?.Entries.Select(entry => new ImageFileRow(entry)).ToArray() ?? [];
        rows.SelectedItem = null;
        previewButton.IsEnabled = false;
        openVolume.IsEnabled = false;
        back.IsVisible = BackToVolumes is not null;
        location.Text = value?.Label ?? "No image open";
        notice.Text = message;
    }

    internal bool SelectEntry(string fileName, int user)
    {
        if (rows.ItemsSource is IEnumerable<ImageFileRow> fileRows)
        {
            var match = fileRows.FirstOrDefault(r => r.Entry.User == user && string.Equals(r.Entry.Name, fileName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                rows.SelectedItem = match;
                rows.ScrollIntoView(match);
                return true;
            }
        }
        return false;
    }

    internal void BindVolumes(string label, ExplorerVolumeListing listing)
    {
        image = null;
        Cleared?.Invoke();
        currentPreview = null;
        preview.Text = "";
        rows.ItemsSource = listing.Volumes;
        rows.SelectedItem = null;
        previewButton.IsEnabled = false;
        openVolume.IsEnabled = false;
        back.IsVisible = false;
        location.Text = label;
        notice.Text = listing.Notice;
    }

    internal void BindTapeFiles(string label, ExplorerVolumeListing listing)
    {
        image = null;
        Cleared?.Invoke();
        currentPreview = null;
        preview.Text = "";
        rows.ItemsSource = listing.TapeFiles?.Select(file => new TapeFileRow(file)).ToArray() ?? [];
        rows.SelectedItem = null;
        previewButton.IsEnabled = false;
        openVolume.IsEnabled = false;
        back.IsVisible = false;
        location.Text = label;
        notice.Text = listing.Notice;
    }

    private async Task OpenSelectedVolumeAsync()
    {
        if (rows.SelectedItem is ExplorerVolumeRow selected && OpenVolume is { } action) await action(selected);
    }

    private void PreviewSelected()
    {
        currentPreview = null;
        preview.Text = "";
        try
        {
            byte[] bytes;
            if (rows.SelectedItem is TapeFileRow tape) bytes = tape.File.Data;
            else if (image is not null && Selected is { Refused: false } selected && image.Entries.Contains(selected))
                bytes = ExplorerCopyOut.Extract(image.Open(), selected.Name, selected.User).Data;
            else return;
            currentPreview = ExplorerPreview.FromBytes(bytes);
            RenderPreview();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
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
}
