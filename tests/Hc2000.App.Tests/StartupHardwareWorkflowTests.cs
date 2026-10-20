using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Media;
using Hc2000.App.Settings;
using Hc2000.App.Snapshots;
using Hc2000.Machine;
using Hc2000.Machine.Snapshots;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Ordinary UI selection reaches the existing actual owner, not just a persisted profile string.</summary>
public sealed class StartupHardwareWorkflowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-hardware-workflow-" + Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private static PrestartPanel Draft(MainWindow window) =>
        Assert.IsType<PrestartPanel>(window.FindControl<ContentControl>("PrestartConfiguration")!.Content);
    private static T Named<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetLogicalDescendants().OfType<T>().Distinct(), control => control.Name == name);
    private static void SelectHardware(PrestartPanel panel, string profile)
    {
        var choices = Named<ComboBox>(panel, "PrestartHardwareProfile");
        choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, profile));
    }

    [AvaloniaFact]
    public async Task OrdinaryHardwareSelectionStartsAnActualBankedOwnerWithoutFlagsOrSavingTheDraft()
    {
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), Store);
        try
        {
            window.Show();
            var draft = Draft(window);
            SelectHardware(draft, "hc2000-v2-ram-banks-v1");
            Assert.Equal("hc2000-v2-ram-banks-v1", draft.Build().Profile);
            Assert.Null(window.CurrentRuntime);
            Assert.False(File.Exists(Store.FilePath));
            Assert.True(await draft.StartAsync().WaitAsync(TimeSpan.FromSeconds(20)), draft.Status);
            Assert.Equal("Effective hardware: hc2000-v2-ram-banks-v1", Named<TextBlock>(window, "EffectiveHardware").Text);
            var saved = await window.CurrentSession.SnapshotBridge!.RequestAsync(new(HcsAction.Save)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(saved.Applied, saved.Error);
            using var stream = new MemoryStream(saved.Snapshot.ToArray());
            var snapshot = HcsCodec.Read(stream, new MachineSchema(MachineProfile.Hc2000V2RamBanksV1));
            var ram = snapshot.Manifest.GetProperty("assets").EnumerateArray().Single(asset => asset.GetProperty("id").GetString() == "ram");
            Assert.Equal(0x110000.ToString(CultureInfo.InvariantCulture), ram.GetProperty("byteLength").GetString());
            Assert.False(File.Exists(Store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task OrdinaryHddHardwareSelectionAdoptsKnownRawMediaAndRetainsSnapshotAndHostWriteGuards()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "known-c.raw");
        var bytes = new byte[8 * 1024 * 1024]; bytes[0] = 0x5a;
        File.WriteAllBytes(path, bytes);
        var configuration = new MachineConfiguration { Hdd = [new("C", path, WriteProtected: false)] };
        Store.Save(new() { Workspace = new(1, configuration.Id, [configuration]) });
        var settingsBefore = File.ReadAllBytes(Store.FilePath);
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), Store);
        try
        {
            window.Show();
            var draft = Draft(window);
            SelectHardware(draft, "hc2000-v2-hdd-v1");
            Assert.Equal(configuration.Hdd, draft.Build().Hdd);
            Assert.Null(window.CurrentRuntime);
            Assert.Equal(settingsBefore, File.ReadAllBytes(Store.FilePath));
            Assert.True(await draft.StartAsync().WaitAsync(TimeSpan.FromSeconds(20)), draft.Status);
            Assert.Equal("Effective hardware: hc2000-v2-hdd-v1", Named<TextBlock>(window, "EffectiveHardware").Text);
            var inspected = await window.CurrentSession.MediaBridge!.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect)))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(inspected.Error);
            var drive = inspected.Hdd!.Drives.Single(view => view.Drive == 2);
            Assert.True(drive.Present); Assert.False(drive.WriteProtected); Assert.False(drive.Dirty);
            Assert.Equal("known-c.raw", drive.Name);
            var refused = await window.CurrentSession.SnapshotBridge!.RequestAsync(new(HcsAction.Save)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(refused.Applied); Assert.Contains("HDD logical media", refused.Error);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(settingsBefore, File.ReadAllBytes(Store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
