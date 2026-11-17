using Avalonia;
using Avalonia.Controls;

namespace Hc2000.App.Media;

public sealed class MediaExplorerLayout : Grid
{
    private bool? stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        var narrow = availableSize.Width < 700;
        if (stacked != narrow)
        {
            stacked = narrow;
            ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
            RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
            ColumnSpacing = narrow ? 0 : 8;
            RowSpacing = narrow ? 8 : 0;
            for (var index = 0; index < Children.Count; index++)
            {
                SetColumn(Children[index], narrow ? 0 : index);
                SetRow(Children[index], narrow ? index : 0);
            }
        }
        return base.MeasureOverride(availableSize);
    }
}
