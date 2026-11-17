using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hc2000.App.Input;

/// <summary>Read-only keyboard reference: one rebuilt text tree for the active machine and
/// mapping. It sends nothing to the guest; the owning window is responsible for focus.</summary>
public sealed class KeyboardReferenceView : UserControl
{
    private readonly StackPanel page;
    private (string machine, KeyMappingMode mode) shown;

    public KeyboardReferenceView(string machine, KeyMappingMode mode)
    {
        shown = (machine, mode);
        page = new StackPanel { Spacing = 4, Margin = new Thickness(14) };
        Content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };
        Render();
    }

    private void Render()
    {
        page.Children.Clear();
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock { Text = $"Machine: {shown.machine}", FontWeight = Avalonia.Media.FontWeight.Bold });
        header.Children.Add(new TextBlock { Text = "Mapping: " + ModeName, FontWeight = Avalonia.Media.FontWeight.Bold });
        header.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "Read-only: the machine keeps running while this is open. Press and release each chord before the next one." });
        page.Children.Add(header);

        page.Children.Add(Heading("BASIC"));
        page.Children.Add(Wrap(KeyboardReference.PrintExample(shown.mode)));
        page.Children.Add(Heading("BASIC keys"));
        foreach (var row in KeyboardReference.Basic(shown.mode))
            page.Children.Add(KeyRow(row));

        page.Children.Add(Heading("CP/M"));
        page.Children.Add(Wrap(KeyboardReference.BootExample(shown.mode)));
        page.Children.Add(Heading("CP/M editing and control keys"));
        foreach (var row in KeyboardReference.Cpm())
            page.Children.Add(KeyRow(row));

        page.Children.Add(Wrap(shown.mode == KeyMappingMode.Symbolic
            ? "Symbolic: characters follow the host layout's text. Press and release Ctrl alone for BASIC extended mode; then type l for USR. Shift+Alt is not delivered as a physical chord in this mode. CP/M Ctrl+letter keys still apply."
            : "Positional: host keys are matched to HC key positions. Shift+Alt together is BASIC extended mode; Ctrl+letter is the CP/M control code."));

        page.Children.Add(Wrap("Tab's physical wiring is drawing-derived and remains unverified. Reference content describes mappings, not a guarantee every CP/M program handles each control code."));
    }

    private string ModeName => shown.mode == KeyMappingMode.Positional ? "Positional" : "Symbolic";

    /// <summary>Redraw when the selected machine or mapping changed; false when unchanged.</summary>
    public bool Refresh(string machine, KeyMappingMode mode)
    {
        if (machine == shown.machine && mode == shown.mode) return false;
        shown = (machine, mode);
        Render();
        return true;
    }

    private static Border Heading(string text)
        => new()
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Avalonia.Media.Brushes.DimGray,
            Margin = new Thickness(0, 10, 0, 4),
            Child = new TextBlock { Text = text, FontWeight = Avalonia.Media.FontWeight.Bold }
        };

    private static TextBlock Wrap(string text)
        => new() { TextWrapping = TextWrapping.Wrap, Text = text };

    private static Border KeyRow(KeyboardReferenceRow row)
        => new()
        {
            Margin = new Thickness(0, 1, 0, 1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("180,*"),
                Children =
                {
                    new TextBlock { Text = row.Action, FontWeight = Avalonia.Media.FontWeight.SemiBold, MinWidth = 180 },
                    Value(row)
                }
            }
        };

    private static TextBlock Value(KeyboardReferenceRow row)
    {
        var value = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = row.HostKeys };
        Grid.SetColumn(value, 1);
        return value;
    }
}
