using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Media;
using Hc2000.App.Configuration;
using Hc2000.App.Runtime;
using Hc2000.App.Settings;
using Hc2000.Machine.Storage;
using Hc2000.HostFiles;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class FactoryHddImagesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hc2000-factory-hdd-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Slots = ["D", "E", "F"];

    private string Images()
    {
        var directory = Path.Combine(root, "disks");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "disks", HddFactoryImages.ContainerName), Path.Combine(directory, HddFactoryImages.ContainerName));
        return directory;
    }

    [Fact]
    public void BuildOutputContainsThreeFormattedVolumesAndExactHashReceiptButNoC()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "disks");
        var receipt = new StringBuilder();
        var path = Path.Combine(directory, HddFactoryImages.ContainerName);
        Assert.True(File.Exists(path), "Build must generate " + path);
        var bytes = File.ReadAllBytes(path);
        const int arenaBytes = 3 * 8 * 1024 * 1024;
        var limits = new HddLimits(40 * 1024 * 1024, 4, 4096, 8192);
        Assert.Equal("HCVD", Encoding.ASCII.GetString(bytes, arenaBytes, 4));
        Assert.Equal("HCVM1END", Encoding.ASCII.GetString(bytes, bytes.Length - 64, 8));
        var decoded = HddCatalogCodec.DecodeHdd(bytes.AsSpan(bytes.Length - 64), bytes.AsSpan(arenaBytes, bytes.Length - arenaBytes - 64), (ulong)bytes.Length, limits);
        Assert.True(decoded.Success, decoded.Failure?.Detail);
        Assert.Equal(Slots.Select(slot => slot + ".DSK"), decoded.Value.Entries.Select(entry => entry.Name));
        foreach (var entry in decoded.Value.Entries)
        {
            var volume = bytes.AsSpan(checked((int)entry.SlotOrdinal.Value * 8 * 1024 * 1024), 8 * 1024 * 1024).ToArray();
            Assert.Equal(8u * 1024 * 1024, entry.ImageBytes);
            Assert.False(entry.ReadOnly);
            Assert.Equal(VolumeCatalogMetadata.Crc32(volume), entry.ImageCrc);
            Assert.Equal(CpmFs.FormatHddVolume(), volume);
            Assert.Equal(-1, volume.AsSpan().IndexOfAnyExcept((byte)0xe5));
            Assert.Empty(CpmFs.OpenHddVolume(volume).List());
            receipt.Append(Convert.ToHexString(SHA256.HashData(volume)).ToLowerInvariant()).Append("  ").Append(HddFactoryImages.ContainerName).Append('#').Append(entry.Name).Append('\n');
        }
        receipt.Append(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()).Append("  ").Append(HddFactoryImages.ContainerName).Append('\n');
        Assert.Equal(receipt.ToString(), File.ReadAllText(Path.Combine(directory, HddFactoryImages.ReceiptName)));
        Assert.False(File.Exists(Path.Combine(directory, "HC2000-C.img")));
    }

    [Fact]
    public void TwoIndependentFactoryBuildsAreByteIdenticalIncludingMetadataAndReceipt()
    {
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        HddFactoryImages.GenerateInto(first);
        HddFactoryImages.GenerateInto(second);
        Assert.Equal(new[] { HddFactoryImages.ContainerName, HddFactoryImages.ReceiptName }.Order(), Directory.GetFiles(first).Select(Path.GetFileName).Order());
        foreach (var name in new[] { HddFactoryImages.ContainerName, HddFactoryImages.ReceiptName })
            Assert.Equal(File.ReadAllBytes(Path.Combine(first, name)), File.ReadAllBytes(Path.Combine(second, name)));
    }

    [AvaloniaFact]
    public async Task CleanDraftHddToggleSelectsWritableDEFBeforeStartAndStartsWithoutC()
    {
        var directory = Images();
        var original = new MachineConfiguration();
        var panel = new StartupMediaPanel(original, new SettingsStore(root), configDirectories: [], baseDirectory: root);
        Assert.Empty(panel.ApplyTo(original).Hdd); // merely opening settings must not enable hardware
        panel.SetHddEnabled(true);
        var selected = panel.ApplyTo(original with { Profile = "hc2000-v2-hdd-v1" });
        Assert.Equal(Slots, selected.Hdd.Select(h => h.Slot));
        Assert.Single(selected.Hdd.Select(h => h.Path).Distinct());
        Assert.All(selected.Hdd, h => { Assert.False(h.WriteProtected); Assert.Equal(h.Slot + ".DSK", h.Volume); });
        var before = selected.Hdd.Select(h => File.ReadAllBytes(h.Path)).ToArray();
        var prepared = await StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), selected);
        Assert.Equal(new byte[] { 3, 4, 5 }, prepared.InitialHdd.Select(h => h.Drive));
        Assert.All(prepared.InitialHdd, h => Assert.False(h.WriteProtected));
        Assert.NotNull(prepared.ColdAdmission);
        prepared.ColdAdmission.CancelBeforeEnqueue(); // No worker was constructed or queued.
        foreach (var (image, index) in selected.Hdd.Select((h, i) => (h, i)))
            Assert.Equal(before[index], File.ReadAllBytes(image.Path));
        Assert.Empty(original.Hdd);
        Assert.Single(Directory.GetFiles(directory));
    }

    [AvaloniaFact]
    public async Task OrdinaryFreshLaunchSelectsInstalledVolumesAndStartsTheOwnerThroughTheHardwareUi()
    {
        var store = new SettingsStore(root);
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), store);
        try
        {
            window.Show();
            var draft = Assert.IsType<PrestartPanel>(window.FindControl<ContentControl>("PrestartConfiguration")!.Content);
            Assert.Empty(draft.Build().Hdd);
            var hardware = draft.GetLogicalDescendants().OfType<ComboBox>().Distinct().Single(control => control.Name == "PrestartHardwareProfile");
            hardware.SelectedItem = hardware.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "hc2000-v2-hdd-v1"));
            Assert.Equal(Slots, draft.Build().Hdd.Select(image => image.Slot));
            Assert.Null(window.CurrentRuntime);
            Assert.False(File.Exists(store.FilePath));
            Assert.True(await draft.StartAsync().WaitAsync(TimeSpan.FromSeconds(20)), draft.Status);
            var inspected = await window.CurrentSession.MediaBridge!.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect)))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(inspected.Error);
            Assert.Equal(new byte[] { 3, 4, 5 }, inspected.Hdd!.Drives.Where(drive => drive.Present).Select(drive => drive.Drive));
            Assert.All(inspected.Hdd.Drives.Where(drive => drive.Present), drive =>
            {
                Assert.False(drive.WriteProtected);
                Assert.False(drive.Dirty);
            });
            Assert.False(File.Exists(store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("CUSTOM.DSK")]
    public void FactoryDefaultsNeverOverwriteCustomSelectionsOrRepopulateAnExplicitClear(string? volume)
    {
        Images();
        var custom = new MachineConfiguration { Hdd = [new("E", Path.Combine(root, "custom.hdd"), true, volume)] };
        var panel = new StartupMediaPanel(custom, new SettingsStore(root), configDirectories: [], baseDirectory: root);
        panel.SetHddEnabled(true);
        Assert.Equal(custom.Hdd, panel.ApplyTo(custom).Hdd);
        var empty = new MachineConfiguration();
        panel = new StartupMediaPanel(empty, new SettingsStore(root), configDirectories: [], baseDirectory: root);
        panel.SetHddEnabled(true);
        Assert.Equal(3, panel.ApplyTo(empty).Hdd.Length);
        panel.ClearHdd("D"); panel.ClearHdd("E"); panel.ClearHdd("F");
        panel.SetHddEnabled(false); panel.SetHddEnabled(true); panel.Refresh();
        Assert.Empty(panel.ApplyTo(empty).Hdd);
    }

    [AvaloniaFact]
    public async Task FactoryDefaultsRetainTheNamedContainerAndABColdFence()
    {
        Images();
        var panel = new StartupMediaPanel(new(), new SettingsStore(root), configDirectories: [], baseDirectory: root);
        panel.SetHddEnabled(true);
        var selected = panel.ApplyTo(new() { Profile = "hc2000-v2-hdd-v1" }) with { DiskA = new(Path.Combine(root, "A.DSK")) };
        var refused = await Assert.ThrowsAsync<IOException>(() => StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), selected));
        Assert.Contains("single cold-read fence", refused.Message);
        selected = selected with { DiskA = null, Hdd = selected.Hdd.SetItem(2, selected.Hdd[2] with { Path = Path.Combine(root, "another.hdd") }) };
        refused = await Assert.ThrowsAsync<IOException>(() => StartupHardwarePreparation.PrepareAsync(StartupHardwareTests.Roms(), selected));
        Assert.Contains("one container", refused.Message);
    }

    [AvaloniaFact]
    public void MissingFactorySetDoesNotInventPathsOrSelectC()
    {
        var directory = Images();
        File.Delete(Path.Combine(directory, HddFactoryImages.ContainerName));
        var panel = new StartupMediaPanel(new(), new SettingsStore(root), configDirectories: [], baseDirectory: root);
        panel.SetHddEnabled(true);
        Assert.Empty(panel.ApplyTo(new()).Hdd);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
