using Avalonia.Controls;

namespace Hc2000.App.Configuration;

/// <summary>Per-machine cold display choices. The text overlay is a cold hardware choice: it is adopted
/// at the next cold Start, is kept by the selected Reset, and a live change is refused by the running
/// machine. No viewport, zoom or persistence control belongs here.</summary>
public sealed class StartupDisplayPanel : UserControl
{
    private readonly CheckBox overlay = new()
    {
        Name = "PrestartTextOverlay",
        Content = "Text overlay (apply at Start; kept by Reset)"
    };

    public StartupDisplayPanel(MachineConfiguration draft)
    {
        overlay.IsChecked = draft.TextOverlay;
        ToolTip.SetTip(overlay, "Cold choice: to change it on a running machine, use New machine.");
        Content = new StackPanel { Spacing = 6, Children = { overlay } };
    }

    public MachineConfiguration ApplyTo(MachineConfiguration draft) => draft with { TextOverlay = overlay.IsChecked == true };
}
