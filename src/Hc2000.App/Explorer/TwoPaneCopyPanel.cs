using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hc2000.App.Library;
using Hc2000.App.Media;

namespace Hc2000.App.Explorer;

/// <summary>Left pane of the two-pane copy view: scanned disk home, never the write target.</summary>
internal sealed class TwoPaneCopyPanel : StackPanel
{
    internal bool Updating { get; private set; }
    internal ListBox Rows { get; } = new() { Height = 130 };
    internal Button Scan { get; } = new() { Content = "Scan disk home" };
    // hc2000-new-image-tape-list-integration: explicit labelled action for the configured tape
    // home, served by the same shipped scanner (MediaHomeScan) as the media rows list.
    internal Button ScanTapeHome { get; } = new() { Content = "Scan tape home" };
    internal Button Choose { get; } = new() { Content = "Choose disk home" };
    internal Button OpenCopy { get; } = new() { Content = "Open managed copy", IsEnabled = false };
    internal TextBlock Notice { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal MenuItem NewFloppy { get; } = new() { Header = "New 720K floppy" };
    internal MenuItem NewHardDrive { get; } = new() { Header = "New 8MB hard drive" };
    internal MenuItem NewTape { get; } = new() { Header = "New tape" };
    internal ContextMenu NewImageMenu { get; } = new();
    internal TextBox NewImageName { get; } = new() { PlaceholderText = "New image file name", MinWidth = 180 };
    internal Button CreateImage { get; } = new() { Content = "Create image" };
    internal Button CancelImage { get; } = new() { Content = "Cancel" };
    internal StackPanel NewImagePrompt { get; } = new() { Spacing = 6, IsVisible = false };
    internal string? NewImageKind { get; private set; }
    internal MediaHomeMountLane? MountLane { get; set; }
    internal Action<string>? ShowNotice { get; set; }

    internal TwoPaneCopyPanel()
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Disk home", FontSize = 18 });
        Children.Add(new TextBlock
        {
            Text = "Open writes a managed copy of a scanned image; use Disk explorer below to copy in and out. The scanned file is never written.",
            TextWrapping = TextWrapping.Wrap
        });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Scan);
        actions.Children.Add(ScanTapeHome);
        actions.Children.Add(Choose);
        Children.Add(actions);
        Children.Add(Notice);
        Children.Add(Rows);
        NewImageMenu.Items.Add(NewFloppy);
        NewImageMenu.Items.Add(NewHardDrive);
        NewImageMenu.Items.Add(NewTape);
        NewImagePrompt.Children.Add(new TextBlock { Text = "Create in the matching media home (never replaces an existing file).", TextWrapping = TextWrapping.Wrap });
        NewImagePrompt.Children.Add(NewImageName);
        var createActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        createActions.Children.Add(CreateImage);
        createActions.Children.Add(CancelImage);
        NewImagePrompt.Children.Add(createActions);
        Children.Add(NewImagePrompt);
        Children.Add(OpenCopy);
        CancelImage.Click += (_, _) => CancelNewImage();
        Rows.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem item && item.DataContext is ExplorerCopyRow { Kind: "floppy-dsk" } row && MountLane is not null)
            {
                item.ContextMenu = BuildFloppyMenu(row);
            }
        };
        // Use the context-request lifecycle, and keep creation restricted to empty list space.
        Rows.ContextRequested += (_, e) =>
        {
            NewImageMenu.Close();
            if (FindListBoxItem(e.Source as Visual) is { } lbi)
            {
                if (lbi.DataContext is ExplorerCopyRow { Kind: "floppy-dsk" } row && MountLane is not null)
                {
                    lbi.ContextMenu ??= BuildFloppyMenu(row);
                    lbi.ContextMenu.Open(lbi);
                    e.Handled = true;
                }
                return;
            }
            NewImageMenu.Open(Rows);
            e.Handled = true;
        };
        // Open is gated by the selected row's kind: only a scanned floppy DSK opens a managed copy.
        Rows.SelectionChanged += (_, _) =>
            OpenCopy.IsEnabled = Rows.SelectedItem is ExplorerCopyRow selected && selected.Kind == "floppy-dsk";
    }

    internal void BeginNewImage(string kind, string suggestedName)
    {
        NewImageMenu.Close();
        NewImageKind = kind;
        NewImageName.Text = suggestedName;
        NewImagePrompt.IsVisible = true;
        NewImageName.Focus();
        NewImageName.SelectAll();
    }

    internal void CancelNewImage()
    {
        NewImagePrompt.IsVisible = false;
        NewImageKind = null;
    }

    internal ContextMenu BuildFloppyMenu(ExplorerCopyRow row)
    {
        var menu = new ContextMenu();
        var mountA = new MenuItem { Header = "Mount as drive A:" };
        var mountB = new MenuItem { Header = "Mount as drive B:" };
        var ejectA = new MenuItem { Header = "Eject A:" };
        var ejectB = new MenuItem { Header = "Eject B:" };
        mountA.Click += (_, _) => QueueMount(row, DiskSlot.A);
        mountB.Click += (_, _) => QueueMount(row, DiskSlot.B);
        ejectA.Click += (_, _) => QueueEject(DiskSlot.A);
        ejectB.Click += (_, _) => QueueEject(DiskSlot.B);
        menu.Items.Add(mountA);
        menu.Items.Add(mountB);
        menu.Items.Add(ejectA);
        menu.Items.Add(ejectB);
        return menu;
    }

    private void QueueMount(ExplorerCopyRow row, DiskSlot slot)
    {
        if (MountLane is not { } lane) return;
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var result = await lane.MountFromScannedRowAsync(row.Path, slot);
            var text = result.Outcome switch
            {
                MediaHomeMountOutcome.Mounted => $"{row.Name} is mounted as drive {slot}: through the existing library lane. {result.Message}",
                _ => $"Mount refused, nothing mounted: {result.Message}"
            };
            Notice.Text = text;
            ShowNotice?.Invoke(text);
        });
    }

    private void QueueEject(DiskSlot slot)
    {
        if (MountLane is not { } lane) return;
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var result = await lane.EjectAsync(slot);
            var text = result.Ejected
                ? $"Drive {slot}: ejected through the existing media path. {result.Message}"
                : $"Eject refused, nothing changed: {result.Message}";
            Notice.Text = text;
            ShowNotice?.Invoke(text);
        });
    }

    private static ListBoxItem? FindListBoxItem(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
            if (visual is ListBoxItem lbi) return lbi;
        return null;
    }

    internal void Bind(IReadOnlyList<ExplorerCopyRow> rows, string? selectedPath, string notice)
    {
        Updating = true;
        try
        {
            Rows.Items.Clear();
            foreach (var row in rows) Rows.Items.Add(row);
            Rows.SelectedItem = selectedPath is null ? null : rows.FirstOrDefault(row => row.Path == selectedPath);
            Notice.Text = notice;
        }
        finally { Updating = false; }
    }
}
