using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace Hc2000.App.Configuration;

internal static class ConfigurationSections
{
    public static TabControl Tabs(string name, params TabItem[] topics) => new()
    {
        Name = name, Classes = { "configurationTabs" }, ItemsSource = topics,
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal })
    };

    public static TabItem Topic(string name, Control body) => new()
    {
        Header = name,
        Content = new Border
        {
            Classes = { "configurationContent" }, Padding = new Thickness(8, 12), Child = body
        }
    };
}
