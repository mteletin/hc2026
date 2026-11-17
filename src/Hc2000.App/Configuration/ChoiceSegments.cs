using Avalonia.Controls;

namespace Hc2000.App.Configuration;

/// <summary>Shared choices for setup and running controls; the bound field remains
/// the source read by validation and the sound notice.</summary>
internal static class ChoiceSegments
{
    public static Control Speed(StackPanel target, TextBox bound) => Build(target, "Emulation speed", bound, new[]
    {
        ("100", "Normal", "The original speed."),
        ("200", "2x", "2x the original speed; sound is off at this speed."),
        ("400", "4x", "4x the original speed; sound is off at this speed."),
        ("0", "Max", "As fast as the host allows; sound is off at this speed.")
    });

    public static Control Zoom(StackPanel target, TextBox bound) => Build(target, "Screen zoom", bound,
        Enumerable.Range(0, 9).Select(value => (value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value == 0 ? "Fit" : $"{value}x", value == 0 ? "Fit the screen to the available space." : $"Up to {value}x, fitting available space.")).ToArray());

    public static Control Border(StackPanel target, TextBox bound) => Build(target, "Border", bound, new[]
    {
        ("Thin", "Thin", "Thin 8-pixel border around the 256×192 picture (272×208)."),
        ("None", "None", "Only the 256×192 picture with no border."),
        ("Full", "Full", "The full 352×296 video frame.")
    });

    public static Control Build(StackPanel target, string label, TextBox bound, (string Value, string Label, string Tip)[] options)
    {
        target.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } });
        var segments = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, ItemSpacing = 6 };
        foreach (var (value, text, tip) in options)
        {
            var segment = new RadioButton { Name = bound.Name + "-" + value, GroupName = bound.Name, Content = text, Classes = { "segment" },
                IsChecked = (bound.Text ?? "").Trim() == value };
            segment.IsCheckedChanged += (_, _) => { if (segment.IsChecked == true && bound.Text != value) bound.Text = value; };
            bound.PropertyChanged += (_, change) =>
            {
                if (change.Property != TextBox.TextProperty) return;
                var on = (bound.Text ?? "").Trim() == value;
                if (segment.IsChecked != on) segment.IsChecked = on;
            };
            ToolTip.SetTip(segment, tip);
            segments.Children.Add(segment);
        }
        target.Children.Add(segments);
        return segments;
    }
}
