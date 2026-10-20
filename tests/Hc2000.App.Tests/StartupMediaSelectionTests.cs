using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class StartupMediaSelectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hc2000-startup-media-" + Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(root);
    private static T Field<T>(Control panel, string name) where T : Control => panel.GetLogicalDescendants().OfType<T>().Distinct().Single(c => c.Name == name);
    private static void SelectMediaTopic(PrestartPanel panel)
    {
        var topics = Field<TabControl>(panel, "ConfigurationTopics");
        topics.SelectedItem = topics.Items.OfType<TabItem>().Single(t => Equals(t.Header, "Media"));
    }

    [AvaloniaFact]
    public async Task ConfiguredRawMediaCanBeChosenForAnotherSlotWithoutTypingAndRemainsADraft()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "known.raw");
        File.WriteAllBytes(path, new byte[80 * 2 * 9 * 512]);
        var source = new FloppyConfiguration(path, Image: new("RawFloppy", 80, 2, 9, 512));
        var selected = new MachineConfiguration { DiskB = source };
        var other = new MachineConfiguration { Name = "Other machine" };
        Store.Save(new() { Workspace = new(1, selected.Id, [selected, other]) });
        var before = File.ReadAllBytes(Store.FilePath);
        var panel = new PrestartPanel(Store);
        var window = new Window { Content = panel };
        try
        {
            window.Show(); // realize the configuration-topic templates before looking up media actions
            SelectMediaTopic(panel);
            var choose = panel.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Content, "Use for A:"));
            Assert.NotNull(choose);
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Field<TextBox>(panel, "PrestartDiskA").Text != path) await Task.Delay(5, deadline.Token);
            var draft = panel.Build();
            Assert.Equal(source, draft.DiskA);
            Assert.Equal(source, draft.DiskB);
            Assert.Equal(before, File.ReadAllBytes(Store.FilePath));
            Assert.Equal(other, Store.Load().Settings.Workspace!.Machines[1]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OrdinaryLaunchSelectsBundledDiskInlineAndStartAdoptsItWithoutPastedPath()
    {
        var disks = Path.Combine(root, "disks"); Directory.CreateDirectory(disks);
        var path = Path.Combine(disks, "CPM22-HC.DSK");
        var original = File.ReadAllBytes(Path.Combine(RomSelectionFixture.RepositoryRoot(), "disks", "cpm", "CPM22-HC.DSK"));
        File.WriteAllBytes(path, original);
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), Store);
        try
        {
            window.Show();
            var panel = (PrestartPanel)window.FindControl<ContentControl>("PrestartConfiguration")!.Content!;
            SelectMediaTopic(panel);
            var media = panel.GetLogicalDescendants().OfType<StartupMediaPanel>().Distinct().Single();
            var row = media.Listed.Single(r => r.Selectable && r.Path == path);
            Assert.Contains("Bundled", row.Name);
            Assert.Null(window.CurrentRuntime);
            Assert.Null(window.LatestPresentedFrame);
            var button = media.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, "Use for A:"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Field<TextBox>(panel, "PrestartDiskA").Text != path) await Task.Delay(5, deadline.Token);
            Assert.Null(window.CurrentRuntime); // choosing/refreshing is not Start
            Assert.False(File.Exists(Store.FilePath));
            Assert.True(await panel.StartAsync(), panel.Status);
            var running = window.FindControl<Hc2000.App.Media.MediaPanel>("MediaControls")!;
            await running.InspectAsync();
            Assert.Contains(running.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("CPM22-HC.DSK") == true);
            Assert.NotNull(window.CurrentRuntime);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(Store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
