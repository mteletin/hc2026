using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Settings;
using Hc2000.App.Workspace;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class AppUiModernConsistentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-ui-modern-" + Guid.NewGuid().ToString("N"));

    private MainWindow Window() => new(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch { }
    }

    [AvaloniaFact]
    public async Task UnifiedTabStrip_ContainsMachinesAndActionControlsWithNamedStyles()
    {
        var window = Window();
        try
        {
            window.Show();

            // Shell header border exists and carries the shellHeader class
            var shellHeaders = window.GetLogicalDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("shellHeader")).ToList();
            Assert.NotEmpty(shellHeaders);

            // MachineWorkspaceHeading is in the unified header
            var heading = window.GetLogicalDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "MachineWorkspaceHeading");
            Assert.NotNull(heading);
            Assert.Equal("Machines", heading!.Text);
            Assert.Contains("sectionTitle", heading.Classes);

            // NewMachine button has toolbarAction class and accessible name
            var newMachine = window.FindControl<Button>("NewMachine");
            Assert.NotNull(newMachine);
            Assert.Contains("toolbarAction", newMachine!.Classes);
            Assert.Equal("New machine", newMachine.Content);

            // Action buttons have toolbarAction class
            var reset = window.FindControl<Button>("MachineReset");
            Assert.NotNull(reset);
            Assert.Contains("toolbarAction", reset!.Classes);
            Assert.Equal("Reset selected machine", reset.Content);

            var settings = window.FindControl<Button>("OpenSettingsButton");
            Assert.NotNull(settings);
            Assert.Contains("toolbarAction", settings!.Classes);

            var debugger = window.FindControl<Button>("OpenDebuggerButton");
            Assert.NotNull(debugger);
            Assert.Contains("toolbarAction", debugger!.Classes);

            // MachineTabs panel is present
            var tabs = window.FindControl<StackPanel>("MachineTabs");
            Assert.NotNull(tabs);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion;
        }
    }

    [AvaloniaFact]
    public async Task SharedVisualSystem_StylesAppliedByNameToPanelsAndButtons()
    {
        var window = Window();
        try
        {
            window.Show();

            // Prestart panel start button has primaryAction class
            var prestartContent = window.FindControl<ContentControl>("PrestartConfiguration")!.Content;
            var prestart = Assert.IsType<PrestartPanel>(prestartContent);
            var startButton = prestart.GetLogicalDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == "PrestartStart");
            Assert.NotNull(startButton);
            Assert.Contains("primaryAction", startButton!.Classes);

            // Reconfigure panel apply button has primaryAction class
            var reconfigureContent = window.FindControl<ContentControl>("ReconfigureConfiguration")!.Content;
            var reconfigure = Assert.IsType<ReconfigurePanel>(reconfigureContent);
            Assert.NotNull(reconfigure.ApplyButton);
            Assert.Contains("primaryAction", reconfigure.ApplyButton.Classes);

            // ConfigurationCard has section class
            var configCard = window.FindControl<Border>("ConfigurationCard");
            Assert.NotNull(configCard);
            Assert.Contains("section", configCard!.Classes);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion;
        }
    }

    [AvaloniaFact]
    public async Task MachineTabs_StyledWithTabStripItemAndListRow()
    {
        var window = Window();
        try
        {
            window.Show();

            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk 1" }, out var tab1, out _));
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk 2" }, out var tab2, out _));

            var tabsPanel = window.FindControl<StackPanel>("MachineTabs")!;
            Assert.Equal(2, tabsPanel.Children.Count);

            foreach (var child in tabsPanel.Children)
            {
                var border = Assert.IsType<Border>(child);
                Assert.Contains("listRow", border.Classes);
                var tabItem = border.GetLogicalDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Classes.Contains("tabStripItem"));
                Assert.NotNull(tabItem);
            }
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion;
        }
    }

    [AvaloniaFact]
    public async Task KeyDown_CtrlShiftN_OpensNewMachineDraft()
    {
        var window = Window();
        try
        {
            window.Show();

            var prestart1 = Assert.IsType<PrestartPanel>(window.FindControl<ContentControl>("PrestartConfiguration")!.Content);
            var id1 = prestart1.InitialConfiguration.Id;

            // Trigger Ctrl+Shift+N
            var keyEvent = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.N,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift
            };
            window.RaiseEvent(keyEvent);

            var prestart2 = Assert.IsType<PrestartPanel>(window.FindControl<ContentControl>("PrestartConfiguration")!.Content);
            Assert.NotEqual(id1, prestart2.InitialConfiguration.Id);
            Assert.Contains("New draft only", window.FindControl<TextBlock>("RuntimeStatus")!.Text);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion;
        }
    }
}
