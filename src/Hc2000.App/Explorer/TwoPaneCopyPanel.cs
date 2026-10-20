using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Hc2000.App.Explorer;

/// <summary>Left pane of the two-pane copy view: scanned disk home, never the write target.</summary>
internal sealed class TwoPaneCopyPanel : StackPanel
{
    internal bool Updating { get; private set; }
    internal ListBox Rows { get; } = new() { Height = 220 };
    internal Button Scan { get; } = new() { Content = "Scan disk home" };
    internal Button Choose { get; } = new() { Content = "Choose disk home…" };
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

    internal TwoPaneCopyPanel()
    {
        Spacing = 6;
        MinWidth = 280;
        Children.Add(new TextBlock { Text = "Disk home", FontSize = 18 });
        Children.Add(new TextBlock
        {
            Text = "Scanned images stay on the left. Open writes a managed copy, then copy in and out on the right. The scanned file is never written.",
            TextWrapping = TextWrapping.Wrap
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Scan);
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
        // Open the popup explicitly, so right-clicking a row cannot offer creation actions.
        Rows.RightTapped += (_, e) =>
        {
            NewImageMenu.Close();
            if (OnRow(e.Source as Visual)) return;
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

    private static bool OnRow(Visual? source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
            if (visual is ListBoxItem) return true;
        return false;
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
