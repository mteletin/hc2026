using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Hc2000.App.Explorer;
using Hc2000.App.Settings;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Contract section 4: exercise the App's menu, chosen name and create button over the
/// shared writers, including refusals. No direct formatter implementation belongs in the UI.</summary>
public sealed class NewImageMediaHomeTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(bool withHomes)
        {
            Root = Path.Combine(Path.GetTempPath(), "hc-new-image-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DiskHome = Path.Combine(Root, "disk");
            TapeHome = Path.Combine(Root, "tape");
            if (withHomes)
            {
                Directory.CreateDirectory(DiskHome);
                Directory.CreateDirectory(TapeHome);
            }
            Store = new SettingsStore(Root);
            if (withHomes) Store.Save(new AppSettings { DiskHome = DiskHome, TapeHome = TapeHome });
            Window = new MainWindow(new LaunchOptions(null, null, false, false), null, Store);
            Window.Show();
        }

        public string Root { get; }
        public string DiskHome { get; }
        public string TapeHome { get; }
        public SettingsStore Store { get; }
        public MainWindow Window { get; }
        public Media.MediaPanel Media => Window.FindControl<Media.MediaPanel>("MediaControls")!;
        public TwoPaneCopyPanel Panel => Media.TwoPane;

        public void Create(MenuItem action, string name)
        {
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(Panel.NewImagePrompt.IsVisible);
            Panel.NewImageName.Text = name;
            Panel.CreateImage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(Root, true);
        }
    }

    [AvaloniaFact]
    public void RightClickOnEmptyMediaListSpaceOffersTheThreeNewImageActionsButNotOnARow()
    {
        var panel = new TwoPaneCopyPanel();
        var window = new Window { Content = panel, Width = 600, Height = 600 };
        window.Show();
        try
        {
            Assert.Equal(3, panel.NewImageMenu.Items.Count);
            Assert.Equal("New 720K floppy", panel.NewFloppy.Header);
            Assert.Equal("New 8MB hard drive", panel.NewHardDrive.Header);
            Assert.Equal("New tape", panel.NewTape.Header);
            window.UpdateLayout();
            var emptyPoint = panel.Rows.TranslatePoint(new Point(20, 180), window);
            Assert.NotNull(emptyPoint);
            window.MouseDown(emptyPoint.Value, MouseButton.Right);
            window.MouseUp(emptyPoint.Value, MouseButton.Right);
            Assert.True(panel.NewImageMenu.IsOpen);
            panel.NewImageMenu.Close();

            var row = new ListBoxItem { Content = "Existing image", Height = 30 };
            panel.Rows.Items.Add(row);
            window.UpdateLayout();
            var rowPoint = row.TranslatePoint(new Point(10, 10), window);
            Assert.NotNull(rowPoint);
            window.MouseDown(rowPoint.Value, MouseButton.Right);
            window.MouseUp(rowPoint.Value, MouseButton.Right);
            Assert.False(panel.NewImageMenu.IsOpen);
        }
        finally
        {
            panel.NewImageMenu.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EachNewImageActionCreatesTheRightKindOfImageInTheRightHome()
    {
        await using var f = new Fixture(withHomes: true);
        var catalogBefore = f.Media.DiskLibrary?.Entries.ToArray();
        f.Create(f.Panel.NewFloppy, "chosen-floppy.dsk");
        var floppyBytes = File.ReadAllBytes(Path.Combine(f.DiskHome, "chosen-floppy.dsk"));
        Assert.Equal(MediaBlanks.Floppy720(), floppyBytes);
        Assert.Equal(DiskFormat.Extended, Dsk.Load(floppyBytes).Format);
        Assert.False(f.Panel.NewImagePrompt.IsVisible);

        f.Create(f.Panel.NewHardDrive, "chosen-volume.hdd");
        var hddBytes = File.ReadAllBytes(Path.Combine(f.DiskHome, "chosen-volume.hdd"));
        Assert.Equal(MediaBlanks.HddVolume8M(), hddBytes);
        Assert.Equal(8_388_608, hddBytes.Length);
        CpmFs.OpenHddVolume(hddBytes);

        f.Create(f.Panel.NewTape, "chosen-tape.tap");
        var tapeBytes = File.ReadAllBytes(Path.Combine(f.TapeHome, "chosen-tape.tap"));
        Assert.Equal(MediaBlanks.TapeBlank(), tapeBytes);
        Assert.Empty(tapeBytes);
        Tap.Load(tapeBytes);
        Assert.Single(Directory.EnumerateFiles(f.TapeHome));
        Assert.Equal(2, Directory.EnumerateFiles(f.DiskHome).Count());
        Assert.Equal(catalogBefore, f.Media.DiskLibrary?.Entries.ToArray());
        Assert.Contains("not registered", f.Panel.Notice.Text);
        var scan = TwoPaneCopyListing.Scan(f.DiskHome);
        Assert.Contains(scan.Rows, row => row.Name == "chosen-floppy.dsk" && row.Kind == "floppy-dsk");
        Assert.Contains(scan.Rows, row => row.Name == "chosen-volume.hdd" && row.Kind == "hdd-raw");
    }

    [AvaloniaFact]
    public async Task AnExistingNameIsRefusedWithoutTouchingTheExistingFile()
    {
        await using var f = new Fixture(withHomes: true);
        foreach (var (action, home) in new[]
        {
            (f.Panel.NewFloppy, f.DiskHome), (f.Panel.NewHardDrive, f.DiskHome), (f.Panel.NewTape, f.TapeHome)
        })
        {
            var existing = Path.Combine(home, "taken.bin");
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(existing, original);
            f.Create(action, "taken.bin");
            Assert.Equal(original, File.ReadAllBytes(existing));
            Assert.StartsWith("New image refused: Refusing to replace an existing path", f.Panel.Notice.Text);
            Assert.True(f.Panel.NewImagePrompt.IsVisible);
        }
        Directory.CreateDirectory(Path.Combine(f.DiskHome, "DIRNAME"));
        f.Create(f.Panel.NewHardDrive, "DIRNAME");
        Assert.StartsWith("New image refused: Refusing to replace an existing path", f.Panel.Notice.Text);
    }

    [AvaloniaFact]
    public async Task WithNoHomeSetEveryActionIsRefusedWithAStatedReason()
    {
        await using var f = new Fixture(withHomes: false);
        foreach (var action in new[] { f.Panel.NewFloppy, f.Panel.NewHardDrive, f.Panel.NewTape })
        {
            f.Create(action, "new-image.bin");
            Assert.StartsWith("New image refused: Media home is not set.", f.Panel.Notice.Text);
        }
        Assert.DoesNotContain(Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path) == "new-image.bin");
    }

    [AvaloniaFact]
    public async Task MissingAndNonDirectoryHomesAreRefusedForEveryAction()
    {
        await using var f = new Fixture(withHomes: false);
        var notDirectory = Path.Combine(f.Root, "ordinary-file");
        File.WriteAllText(notDirectory, "unchanged");
        foreach (var home in new[] { Path.Combine(f.Root, "never-created"), notDirectory })
        {
            f.Store.Save(new AppSettings { DiskHome = home, TapeHome = home });
            foreach (var action in new[] { f.Panel.NewFloppy, f.Panel.NewHardDrive, f.Panel.NewTape })
            {
                f.Create(action, "new-image.bin");
                Assert.StartsWith("New image refused: Media home is missing or not a directory", f.Panel.Notice.Text);
            }
        }
        Assert.Equal("unchanged", File.ReadAllText(notDirectory));
        Assert.DoesNotContain(Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path) == "new-image.bin");
    }

    [AvaloniaFact]
    public async Task CancelDoesNotCreateAndAnInvalidNameCanBeCorrected()
    {
        await using var f = new Fixture(withHomes: true);
        f.Panel.NewFloppy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("NEW720.DSK", f.Panel.NewImageName.Text);
        f.Panel.CancelImage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(f.Panel.NewImagePrompt.IsVisible);
        Assert.Null(f.Panel.NewImageKind);
        Assert.Empty(Directory.EnumerateFiles(f.DiskHome));

        f.Create(f.Panel.NewFloppy, "../outside.dsk");
        Assert.StartsWith("New image refused: Name must stay inside the media home.", f.Panel.Notice.Text);
        Assert.True(f.Panel.NewImagePrompt.IsVisible);
        Assert.False(File.Exists(Path.Combine(f.Root, "outside.dsk")));
        f.Panel.NewImageName.Text = "corrected.dsk";
        f.Panel.CreateImage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(File.Exists(Path.Combine(f.DiskHome, "corrected.dsk")));
        Assert.False(f.Panel.NewImagePrompt.IsVisible);
    }

    [AvaloniaFact]
    public async Task UnwritableHomeShowsARefusalAndKeepsThePromptOpen()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var f = new Fixture(withHomes: true);
        var originalMode = File.GetUnixFileMode(f.DiskHome);
        try
        {
            File.SetUnixFileMode(f.DiskHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            f.Create(f.Panel.NewFloppy, "denied.dsk");
            Assert.StartsWith("New image refused: ", f.Panel.Notice.Text);
            Assert.True(f.Panel.NewImagePrompt.IsVisible);
            Assert.Empty(Directory.EnumerateFiles(f.DiskHome));
        }
        finally
        {
            File.SetUnixFileMode(f.DiskHome, originalMode);
        }
    }

    [AvaloniaFact]
    public async Task DiskHomeOverrideDoesNotRedirectTapeCreation()
    {
        await using var f = new Fixture(withHomes: true);
        var selectedHome = Path.Combine(f.Root, "selected");
        Directory.CreateDirectory(selectedHome);
        f.Media.TwoPaneHomeOverride = selectedHome;
        f.Create(f.Panel.NewFloppy, "selected.dsk");
        f.Create(f.Panel.NewHardDrive, "selected.hdd");
        f.Create(f.Panel.NewTape, "selected.tap");
        Assert.Equal(2, Directory.EnumerateFiles(selectedHome).Count());
        Assert.Empty(Directory.EnumerateFiles(f.DiskHome));
        Assert.True(File.Exists(Path.Combine(f.TapeHome, "selected.tap")));
    }
}
