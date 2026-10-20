using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Workspace;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class ExplorerVolumeCopyInTests
{
    [AvaloniaFact]
    public async Task OpenedHddVolumeAcceptsOneFileCopyInThroughTheFence()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var raw = ExplorerPaneUiTests.HddBytes();
        var path = Path.Combine(domain.Root, "volume.img");
        File.WriteAllBytes(path, raw);
        var window = Window();
        try
        {
            var a = Add(window, new());
            await window.BrowseExplorerImageAsync(a, "volume", path, MediaImageDescriptor.HddRaw8MiB,
                () => Task.FromResult(ImmutableArray.CreateRange(File.ReadAllBytes(path))));
            var image = a.Explorer.Image!;
            Assert.False(image.CanImport); // floppy copy-in lane stays closed for hdd-raw
            Assert.True(image.CanImportVolume);
            Assert.True(Button(window, "Copy in host file…").IsEnabled);
            var data = ImmutableArray.Create<byte>([0x11, 0x22, 0x33]);
            Assert.True(window.CopyExplorerVolumeIn(a, image, "NEW.BIN", 0, false, data));
            var fs = CpmFs.OpenHddVolume(File.ReadAllBytes(path));
            Assert.Equal(data.ToArray(), fs.Get("NEW.BIN").AsSpan(0, data.Length).ToArray());
            Assert.Equal(3, a.Explorer.Image!.Entries.Count);
            Assert.Contains(a.Explorer.Image.Entries, e => e.Name == "NEW.BIN");
            Assert.Empty(Directory.GetFiles(domain.Root, "*.tmp"));
        }
        finally { window.Close(); }
        await window.ShutdownCompletion;
    }

    [AvaloniaFact]
    public async Task VolumeCopyInToALiveSnapshotWithoutAPathIsRefused()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var raw = ExplorerPaneUiTests.HddBytes();
        var path = Path.Combine(domain.Root, "volume.img");
        File.WriteAllBytes(path, raw);
        var before = File.ReadAllBytes(path);
        var window = Window();
        try
        {
            var a = Add(window, new());
            await window.BrowseExplorerImageAsync(a, "live snapshot", null, MediaImageDescriptor.HddRaw8MiB,
                () => Task.FromResult(ImmutableArray.CreateRange(raw)));
            var image = a.Explorer.Image!;
            Assert.Null(image.Path);
            Assert.False(image.CanImportVolume);
            Assert.False(window.CopyExplorerVolumeIn(a, image, "NEW.BIN", 0, false, ImmutableArray.Create<byte>([1])));
            Assert.Contains("refused", a.Explorer.Notice);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(domain.Root, "*.tmp"));
        }
        finally { window.Close(); }
        await window.ShutdownCompletion;
    }

    // An 8 MiB volume can never be mounted through the floppy slots (MediaFiles.MaxDiskBytes = 4 MiB),
    // so the reachable refusal for an opened volume is the live/starting-session ownership refusal;
    // it leaves the file byte-identical, which is what the amendment owes.
    [AvaloniaFact]
    public async Task StartingSessionRefusesVolumeCopyInByteIdentical()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var raw = ExplorerPaneUiTests.HddBytes();
        var path = Path.Combine(domain.Root, "volume.img");
        File.WriteAllBytes(path, raw);
        var before = File.ReadAllBytes(path);
        var window = Window();
        try
        {
            var a = Add(window, new());
            await window.BrowseExplorerImageAsync(a, "volume", path, MediaImageDescriptor.HddRaw8MiB,
                () => Task.FromResult(ImmutableArray.CreateRange(File.ReadAllBytes(path))));
            a.StartingConfiguration = new MachineConfiguration();
            var image = a.Explorer.Image!;
            Assert.True(image.CanImportVolume);
            Assert.True(Button(window, "Copy in host file…").IsEnabled);
            Assert.False(window.CopyExplorerVolumeIn(a, image, "NEW.BIN", 0, false, ImmutableArray.Create<byte>([1])));
            Assert.Contains("cannot prove empty media ownership", a.Explorer.Notice);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(domain.Root, "*.tmp"));
        }
        finally { window.Close(); }
        await window.ShutdownCompletion;
    }

    private static MainWindow Window()
    {
        var window = new MainWindow(new LaunchOptions(null, null, false, false));
        window.Show();
        return window;
    }
    private static MachineSession Add(MainWindow window, MachineConfiguration configuration)
    {
        Assert.True(window.TryAddTabView(configuration, out var tab, out var refusal), refusal);
        return tab!.Session;
    }
    private static Button Button(MainWindow window, string name)
        => window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, name));
}
