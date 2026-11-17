using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Hc2000.App.Input;

namespace Hc2000.App;

public sealed class KeyboardReferenceWindow : Window
{
    private readonly KeyboardReferenceView reference;

    public KeyboardReferenceWindow(string machine, KeyMappingMode mode)
    {
        Title = "Keyboard reference";
        Width = 660;
        Height = 620;
        MinWidth = 420;
        MinHeight = 320;
        reference = new KeyboardReferenceView(machine, mode);
        var close = new Button { Name = "CloseKeyboardReference", Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        close.Click += (_, _) => Close();
        var layout = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(reference);
        Content = layout;
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        };
    }

    public void Refresh(string machine, KeyMappingMode mode) => reference.Refresh(machine, mode);
}
