using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Explorer;

internal sealed class MediaExplorerWindow : Window
{
    private readonly HostExplorerPanel host;
    private readonly ImageExplorerPanel image = new();
    private readonly InstalledExplorerPanel installed;
    private readonly CpmSearchPanel searchPanel = new();
    private readonly TextBlock notice = new() { Name = "WorkspaceNotice" };
    private readonly Func<MediaHomeRow, Task<(ExplorerImage? Image, string Notice)>> browse;
    private readonly Func<ExplorerImage, CpmEntry, string, string> copyOut;
    private readonly Func<ExplorerImage, Task<(ExplorerImage? Image, string Notice)>> prepareCopy;
    private readonly Func<ExplorerImage, HostEntry, int, Task<(ExplorerImage? Image, string Notice)>> copyIn;
    private readonly Func<MediaHomeRow, ExplorerVolumeRow, Task<(ExplorerImage? Image, string Notice)>> browseVolume;
    private ExplorerImage? currentImage;
    private int request;

    internal MediaExplorerWindow(string hostPath, IReadOnlyList<string> roots,
        Func<MediaHomeRow, Task<(ExplorerImage? Image, string Notice)>> browse,
        Func<ExplorerImage, CpmEntry, string, string> copyOut,
        Func<ExplorerImage, Task<(ExplorerImage? Image, string Notice)>> prepareCopy,
        Func<ExplorerImage, HostEntry, int, Task<(ExplorerImage? Image, string Notice)>> copyIn,
        Func<MediaHomeRow, ExplorerVolumeRow, Task<(ExplorerImage? Image, string Notice)>> browseVolume)
    {
        this.browse = browse;
        this.copyOut = copyOut;
        this.prepareCopy = prepareCopy;
        this.copyIn = copyIn;
        this.browseVolume = browseVolume;
        Title = "HC2000 Explorer";
        Width = 1100;
        Height = 740;
        MinWidth = 700;
        MinHeight = 640;
        host = new(hostPath);
        installed = new(roots);
        installed.OpenMedia = OpenMediaAsync;
        host.OpenFile = OpenHostFileAsync;
        searchPanel.GetHostFolder = () => host.Selected is { IsDirectory: true } dir ? dir.Path : host.CurrentPath;
        searchPanel.OpenMatch = OpenSearchResultAsync;
        image.Cleared = () => notice.Text = "";

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,*") };
        Grid.SetColumn(host, 0);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(image, 2);
        panes.Children.Add(host);
        panes.Children.Add(splitter);
        panes.Children.Add(image);
        var copyButton = new Button { Content = "← Copy selected image file to host" };
        copyButton.Click += (_, _) =>
        {
            if (currentImage is null || image.Selected is not { Refused: false } selected || host.CurrentPath is not { } destination)
            {
                notice.Text = "Select an image file and open a host destination folder.";
                return;
            }
            notice.Text = copyOut(currentImage, selected, destination);
            host.RefreshView();
        };
        var prepareButton = new Button { Content = "Create/open floppy working copy" };
        prepareButton.Click += async (_, _) =>
        {
            if (currentImage is null) { notice.Text = "Open a floppy image first."; return; }
            var version = ++request;
            var result = await prepareCopy(currentImage);
            if (version != request) return;
            currentImage = result.Image;
            image.Bind(result.Image, result.Notice);
            notice.Text = result.Notice;
        };
        var user = new NumericUpDown { Minimum = 0, Maximum = 15, Value = 0, Width = 110 };
        var copyInButton = new Button { Content = "Copy selected host file to image →" };
        copyInButton.Click += async (_, _) =>
        {
            if (currentImage is null || host.Selected is not { IsDirectory: false } selected)
            {
                notice.Text = "Select a host file and open a writable image.";
                return;
            }
            var version = ++request;
            var result = await copyIn(currentImage, selected, (int)(user.Value ?? 0));
            if (version != request) return;
            currentImage = result.Image;
            image.Bind(result.Image, result.Notice);
            notice.Text = result.Notice;
        };
        host.CopySelectedToImage = () => copyInButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        image.CopySelectedToHost = () => copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(copyButton);
        actions.Children.Add(prepareButton);
        actions.Children.Add(new TextBlock { Text = "CP/M user", VerticalAlignment = VerticalAlignment.Center });
        actions.Children.Add(user);
        actions.Children.Add(copyInButton);
        var layout = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        Grid.SetRow(installed, 0);
        Grid.SetRow(searchPanel, 1);
        Grid.SetRow(panes, 2);
        Grid.SetRow(actions, 3);
        Grid.SetRow(notice, 4);
        layout.Children.Add(installed);
        layout.Children.Add(searchPanel);
        layout.Children.Add(panes);
        layout.Children.Add(actions);
        layout.Children.Add(notice);
        Content = layout;
    }

    private async Task OpenHostFileAsync(HostEntry selected)
    {
        var currentRequest = ++request;
        currentImage = null;
        notice.Text = "";
        image.BackToVolumes = null;
        image.OpenVolume = null;
        image.Bind(null, "Inspecting " + selected.Name + "…");
        var row = await MediaHomeScan.ClassifyAsync("user", selected.Path, default);
        if (currentRequest != request) return;
        if (row is not { Selectable: true })
        {
            notice.Text = "";
            image.Bind(null, row?.Detail ?? "File is unavailable or not a regular image.");
            return;
        }
        await OpenMediaAsync(row);
    }

    private async Task OpenMediaAsync(MediaHomeRow row)
    {
        var currentRequest = ++request;
        currentImage = null;
        notice.Text = "";
        image.BackToVolumes = null;
        image.OpenVolume = null;
        image.Bind(null, "Loading " + row.Name + "…");
        if (row.Kind is "hdd-container" or "tape")
        {
            var listing = await NestedMediaExplorer.ListAsync(row);
            if (currentRequest != request) return;
            if (listing.TapeFiles is not null)
            {
                notice.Text = "";
                image.BindTapeFiles(row.Name, listing);
                return;
            }
            image.OpenVolume = async volume =>
            {
                var volumeRequest = ++request;
                currentImage = null;
                notice.Text = "";
                image.Bind(null, "Loading volume " + volume.Name + "…");
                var opened = await browseVolume(row, volume);
                if (volumeRequest != request) return;
                currentImage = opened.Image;
                image.BackToVolumes = () =>
                {
                    request++;
                    currentImage = null;
                    notice.Text = "";
                    image.BindVolumes(row.Name, listing);
                };
                notice.Text = "";
                image.Bind(opened.Image, opened.Notice);
            };
            notice.Text = "";
            image.BindVolumes(row.Name, listing);
            return;
        }
        var result = await browse(row);
        if (currentRequest != request) return;
        currentImage = result.Image;
        notice.Text = "";
        image.Bind(result.Image, result.Notice);
    }

    private async Task OpenSearchResultAsync(CpmFileLocator.LocateMatch match)
    {
        if (!File.Exists(match.FullPath))
        {
            searchPanel.SetNotice($"Image became unavailable: '{match.ImagePath}' no longer exists.");
            return;
        }

        try
        {
            var fi = new FileInfo(match.FullPath);
            if (fi.LastWriteTimeUtc != match.LastWriteUtc || fi.Length != match.FileLength)
            {
                searchPanel.SetNotice($"Image changed before opening: '{match.ImagePath}' was modified.");
                return;
            }
        }
        catch (Exception ex)
        {
            searchPanel.SetNotice($"Image became unavailable: {ex.Message}");
            return;
        }

        var hostEntry = new HostEntry(Path.GetFileName(match.FullPath), match.FullPath, false, match.FileLength);
        await OpenHostFileAsync(hostEntry);

        bool selected = image.SelectEntry(match.FileName, match.User);
        if (selected)
        {
            notice.Text = $"Opened {match.ImagePath} · selected {match.FileName} (user {match.User})";
        }
    }
}
