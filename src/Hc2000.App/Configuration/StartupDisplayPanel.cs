using Avalonia.Controls;

namespace Hc2000.App.Configuration;

/// <summary>Per-machine cold display choices. The text overlay is a cold hardware choice: it is adopted
/// at the next cold Start, is kept by the selected Reset, and a live change is refused by the running
/// machine. Live presentation enhancements (finer text and 50/50 comparison) are controlled on the running machine.</summary>
public sealed class StartupDisplayPanel : UserControl
{
    private readonly CheckBox overlay = new()
    {
        Name = "text-overlay",
        Content = "Text overlay (apply at Start; kept by Reset)"
    };

    public StartupDisplayPanel(MachineConfiguration draft)
    {
        overlay.IsChecked = draft.TextOverlay;
        ToolTip.SetTip(overlay, "Cold choice: to change it on a running machine, use New machine.");
        Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                overlay,
                new TextBlock
                {
                    Text = "Cold hardware choice: derived transparent-paper overlay adopted at cold Start. Live known-font crisp text and 50/50 comparison are available in the Machine panel without cold restart.",
                    Classes = { "caption" },
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            }
        };
    }

    public MachineConfiguration ApplyTo(MachineConfiguration draft) => draft with { TextOverlay = overlay.IsChecked == true };
}
