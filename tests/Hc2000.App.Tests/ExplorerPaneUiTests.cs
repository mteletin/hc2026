using System.Collections.Immutable;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Explorer;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Workspace;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Real shared MainWindow composition, not an isolated view model or fake media catalog.</summary>
public sealed class ExplorerPaneUiTests
{
    [AvaloniaFact]
    public void RealWindowOffersFileBrowsingOnTheExistingDriveRows()
    {
        var window = Window();
        try
        {
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            var buttons = media.GetLogicalDescendants().OfType<Button>().Select(b => b.Content).ToArray();
            Assert.Contains("Browse A files", buttons);
            Assert.Contains("Browse B files", buttons);
            Assert.Contains("Browse C files", buttons);
            Assert.Contains("Browse F files", buttons);
            Assert.Contains("Disk explorer — HC2000", Texts(window));
            Button(window, "Browse A files").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.Contains("no configured image", window.CurrentSession.Explorer.Notice);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LateBrowseRestoresOnlyItsOriginalTabAndFieldsStayPerMachine()
    {
        var window = Window();
        try
        {
            var a = Add(window, new() { Name = "Alpha" });
            var pending = new TaskCompletionSource<ImmutableArray<byte>>();
            var loading = window.BrowseExplorerImageAsync(a, "A frozen", null, MediaImageDescriptor.DskContainer, () => pending.Task);
            var b = Add(window, new() { Name = "Beta" });
            await Browse(window, b, "B frozen", Disk("BETA.BIN", 0));
            var bImage = b.Explorer.Image;
            Field<TextBox>(window, "ExplorerImportName").Text = "BETA.NEW";
            Field<NumericUpDown>(window, "ExplorerImportUser").Value = 7;
            pending.SetResult(Disk("ALPHA.BIN", 3));
            await loading;
            Assert.Same(bImage, b.Explorer.Image);
            Assert.DoesNotContain(Texts(window), t => t.Contains("ALPHA.BIN"));
            Select(window, "Alpha");
            Assert.Contains(Texts(window), t => t.Contains("ALPHA.BIN") && t.Contains("user 3"));
            Assert.Equal("", Field<TextBox>(window, "ExplorerImportName").Text);
            Field<TextBox>(window, "ExplorerImportName").Text = "ALPHA.NEW";
            Select(window, "Beta");
            Assert.Equal("BETA.NEW", Field<TextBox>(window, "ExplorerImportName").Text);
            Assert.Equal(7, Field<NumericUpDown>(window, "ExplorerImportUser").Value);
            Assert.Contains(Texts(window), t => t.Contains("BETA.BIN"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NewerBrowseAndRuntimeRetirementBothInvalidateLateResults()
    {
        var window = Window();
        try
        {
            var a = Add(window, new() { Name = "Alpha" });
            var pending = new TaskCompletionSource<ImmutableArray<byte>>();
            var old = window.BrowseExplorerImageAsync(a, "old", null, MediaImageDescriptor.DskContainer, () => pending.Task);
            await Browse(window, a, "new", Disk("NEW.BIN"));
            var image = a.Explorer.Image;
            pending.SetResult(Disk("OLD.BIN"));
            await old;
            Assert.Same(image, a.Explorer.Image);
            var retired = new TaskCompletionSource<ImmutableArray<byte>>();
            var retiring = window.BrowseExplorerImageAsync(a, "retiring", null, MediaImageDescriptor.DskContainer, () => retired.Task);
            a.BumpGeneration();
            retired.SetResult(Disk("RETIRED.BIN"));
            await retiring;
            Assert.Null(a.Explorer.Image);
            Select(window, "Alpha");
            Assert.DoesNotContain(Texts(window), t => t.Contains("RETIRED.BIN"));
            Assert.Null(a.Explorer.Refresh);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RetiredSessionCannotPublishLateBrowseOrCopyCallbacks()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var window = Window();
        try
        {
            var a = Add(window, new() { Name = "Alpha" });
            await Browse(window, a, "first", Disk("FIRST.BIN"));
            var image = a.Explorer.Image!;
            var entry = Assert.Single(image.Entries);
            var pending = new TaskCompletionSource<ImmutableArray<byte>>();
            var loading = window.BrowseExplorerImageAsync(a, "late", null, MediaImageDescriptor.DskContainer, () => pending.Task);
            a.Retire();
            pending.SetResult(Disk("LATE.BIN"));
            await loading;
            Assert.Null(a.Explorer.Image);
            var target = Path.Combine(domain.Root, "retired.bin");
            Assert.Null(window.CopyExplorerOut(a, image, entry, target));
            Assert.False(File.Exists(target));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task IntegratedTabsSmokeUsesConfiguredDriveRowsAndBothCopyLanes()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var pathA = Path.Combine(domain.Root, "a.dsk");
        var pathB = Path.Combine(domain.Root, "b.dsk");
        File.WriteAllBytes(pathA, Disk("ALPHA.BIN").ToArray());
        File.WriteAllBytes(pathB, Disk("BETA.BIN", 4).ToArray());
        var beforeA = File.ReadAllBytes(pathA);
        var window = Window();
        try
        {
            var a = Add(window, new() { Name = "Alpha", DiskA = new(pathA) });
            Button(window, "Browse A files").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.Equal("ALPHA.BIN", Assert.Single(a.Explorer.Image!.Entries).Name);
            var aImage = a.Explorer.Image;
            var b = Add(window, new() { Name = "Beta", DiskB = new(pathB) });
            Button(window, "Browse B files").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.Contains(Texts(window), t => t.Contains("BETA.BIN") && t.Contains("user 4"));
            var image = b.Explorer.Image!;
            var entry = Assert.Single(image.Entries);
            var output = Path.Combine(domain.Root, "beta-out.bin");
            Assert.NotNull(window.CopyExplorerOut(b, image, entry, output));
            Assert.Equal(image.Open().Get("BETA.BIN", 4), File.ReadAllBytes(output));
            Assert.True(window.CopyExplorerIn(b, image, "new.bin", 15, false, [0x41, 0, 0x1a]));
            Assert.Contains(b.Explorer.Image!.Entries, e => e.Name == "NEW.BIN" && e.User == 15 && e.Size == 128);
            var reloaded = CpmFs.Open(Dsk.Load(File.ReadAllBytes(pathB)));
            Assert.Equal(new byte[] { 0x41, 0, 0x1a }, reloaded.Get("NEW.BIN", 15)[..3]);
            Assert.Equal(beforeA, File.ReadAllBytes(pathA));
            Select(window, "Alpha");
            Assert.Same(aImage, a.Explorer.Image);
            Assert.DoesNotContain(Texts(window), t => t.Contains("NEW.BIN"));
            Assert.Null(window.CopyExplorerOut(b, b.Explorer.Image!, entry, Path.Combine(domain.Root, "stale.bin")));
            Assert.False(window.CopyExplorerIn(b, b.Explorer.Image!, "LATE.BIN", 0, false, [1]));
            Assert.False(File.Exists(Path.Combine(domain.Root, "stale.bin")));
        }
        finally { window.Close(); }
        await window.ShutdownCompletion;
    }

    [AvaloniaFact]
    public async Task CopyInRequiresExplicitReplaceAndStaleImageCallbacksCannotWrite()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "source.dsk");
        File.WriteAllBytes(path, Disk("FILE.BIN").ToArray());
        var window = Window();
        try
        {
            var session = Add(window, new() { DiskA = new(path) });
            await window.BrowseExplorerDriveAsync(session, DiskSlot.A, new() { DiskA = new(path) });
            var first = session.Explorer.Image!;
            var before = File.ReadAllBytes(path);
            Assert.False(window.CopyExplorerIn(session, first, "FILE.BIN", 0, false, [9]));
            Assert.Contains("exists", session.Explorer.Notice);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(window.CopyExplorerIn(session, first, "BAD NAME", 0, true, [9]));
            Assert.False(window.CopyExplorerIn(session, first, "NEW.BIN", 16, false, [9]));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.True(window.CopyExplorerIn(session, first, "FILE.BIN", 0, true, [9]));
            Assert.NotSame(first, session.Explorer.Image);
            Assert.Equal(9, CpmFs.Open(Dsk.Load(File.ReadAllBytes(path))).Get("FILE.BIN")[0]);
            Assert.False(window.CopyExplorerIn(session, first, "LATE.BIN", 0, false, [8]));
            Assert.Single(CpmFs.Open(Dsk.Load(File.ReadAllBytes(path))).List());
            Assert.Empty(Directory.GetFiles(domain.Root, "*.tmp"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CopyOutUsesImmutableStoredRecordsAndNeverOverwritesHostOrImagePaths()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "source.dsk");
        var bytes = Disk("FILE.BIN");
        File.WriteAllBytes(path, bytes.ToArray());
        var window = Window();
        try
        {
            var a = Add(window, new());
            await Browse(window, a, "snapshot", bytes, path);
            var image = a.Explorer.Image!;
            var entry = Assert.Single(image.Entries);
            File.WriteAllBytes(path, Disk("CHANGED.BIN").ToArray());
            var changed = File.ReadAllBytes(path);
            var output = Path.Combine(domain.Root, "file.bin");
            var result = window.CopyExplorerOut(a, image, entry, output);
            Assert.NotNull(result);
            var stored = File.ReadAllBytes(output);
            Assert.Equal(128, stored.Length);
            Assert.Equal(new byte[] { 0x41, 0, 0x1a }, stored[..3]);
            Assert.All(stored[3..], b => Assert.Equal(0x1a, b));
            Assert.Contains("untrimmed", a.Explorer.Notice);
            Assert.Null(window.CopyExplorerOut(a, image, entry, output));
            Assert.Equal(stored, File.ReadAllBytes(output));
            Assert.Null(window.CopyExplorerOut(a, image, entry, path));
            Assert.Equal(changed, File.ReadAllBytes(path));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CopyInFromOneTabCannotEditImageMountedByAnotherTab()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "source.dsk");
        File.WriteAllBytes(path, Disk("MOUNTED.BIN").ToArray());
        var before = File.ReadAllBytes(path);
        var window = Window();
        try
        {
            var owner = Add(window, new() { Name = "Owner" });
            var host = new DiskWritebackHost(new DiskWritebackOwnerLedger(), new MediaBridge(owner.InputQueue), true);
            owner.WritebackHost = host;
            var operation = DiskReadReservation.Operation.ClaimStart([DiskSlot.A]);
            try
            {
                var read = await operation.ReadMemberAsync(DiskSlot.A, path);
                read.MarkDispatched();
                var drive = new MediaSession(new FloppyDrive(new()), new FloppyDrive(new()));
                var reply = drive.Apply(new(MediaAction.InsertDisk, Bytes: read.Bytes, RequestId: 1));
                Assert.Null(reply.Error);
                await host.NoteMounted(DiskSlot.A, path, read.Bytes, reply, read.CompleteAfterOwnerOutcome(reply));
            }
            finally { await operation.ReleaseOnceAsync("pane test accounted real mount"); }
            var editor = Add(window, new() { Name = "Editor", DiskA = new(path) });
            Button(window, "Browse A files").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.NotNull(editor.Explorer.Image);
            Assert.False(window.CopyExplorerIn(editor, editor.Explorer.Image!, "NEW.BIN", 0, false, [5]));
            Assert.Contains("mounted", editor.Explorer.Notice, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(domain.Root, "*.tmp"));
            Assert.Null(owner.Explorer.Image);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RawFloppyUsesItsExplicitDescriptorAndTheSameCopyLane()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var p = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Load(Dsk.Load(Disk("RAW.BIN").AsSpan()).ToRaw(), p.Geometry);
        var descriptor = MediaImageDescriptor.RawFloppy(p.Geometry);
        var path = Path.Combine(domain.Root, "raw.img");
        File.WriteAllBytes(path, RawFloppyAdapter.Save(disk, descriptor, new(80, 2)));
        var configuration = new MachineConfiguration
        {
            DiskA = new(path, Image: new("RawFloppy", p.Geometry.Cylinders, p.Geometry.Sides, p.Geometry.Sectors, p.Geometry.SectorSize))
        };
        var window = Window();
        try
        {
            var a = Add(window, configuration);
            Button(window, "Browse A files").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.Equal("RAW.BIN", Assert.Single(a.Explorer.Image!.Entries).Name);
            Assert.True(window.CopyExplorerIn(a, a.Explorer.Image, "RAWNEW.BIN", 2, false, [7]));
            Assert.Equal(p.Geometry.Length, new FileInfo(path).Length);
            Assert.Equal(7, CpmFs.Open(RawFloppyAdapter.Load(File.ReadAllBytes(path), descriptor, new(80, 2))).Get("RAWNEW.BIN", 2)[0]);
        }
        finally { window.Close(); }
        await window.ShutdownCompletion;
    }

    [AvaloniaFact]
    public async Task HddEntriesVisibleWithRefusedFloppyLaneAndFencedVolumeLane()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var raw = HddBytes();
        var path = Path.Combine(domain.Root, "volume.img");
        File.WriteAllBytes(path, raw);
        var window = Window();
        try
        {
            var a = Add(window, new() { Hdd = [new("C", path, WriteProtected: true)] });
            Button(window, "Browse C files").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var image = a.Explorer.Image!;
            Assert.Equal(2, image.Entries.Count);
            Assert.Contains(Texts(window), t => t.Contains("R/O") && t.Contains("SYS"));
            var refused = image.Entries.Single(e => e.Refused);
            Assert.Contains("REFUSED:", Texts(window).Single(t => t.Contains("FAR.BIN")));
            Assert.False(Button(window, "Copy out FAR.BIN…").IsEnabled);
            Assert.False(window.CopyExplorerIn(a, image, "NEW.BIN", 0, false, [1])); // floppy lane stays closed for hdd-raw
            Assert.Equal(raw, File.ReadAllBytes(path));
            Assert.Null(window.CopyExplorerOut(a, image, refused, Path.Combine(domain.Root, "refused.bin")));
            var good = image.Entries.Single(e => !e.Refused);
            var target = Path.Combine(domain.Root, "hdd.bin");
            Assert.NotNull(window.CopyExplorerOut(a, image, good, target));
            Assert.Equal(128, File.ReadAllBytes(target).Length);
            Assert.Equal(0x42, File.ReadAllBytes(target)[0]);
            // A configured-but-cold slot passes the existing fence: the volume lane opens for the opened file.
            Assert.True(image.CanImportVolume);
            Assert.True(Button(window, "Copy in host file…").IsEnabled);
            Assert.True(window.CopyExplorerVolumeIn(a, image, "NEWV.BIN", 0, false, [0x5c]));
            Assert.Equal(3, a.Explorer.Image!.Entries.Count);
            Assert.Contains(a.Explorer.Image.Entries, e => e.Name == "NEWV.BIN");
        }
        finally { window.Close(); }
        await window.ShutdownCompletion;
    }

    [AvaloniaFact]
    public async Task MalformedOrWrongGeometryImagesShowRefusalWithoutStaleFiles()
    {
        var window = Window();
        try
        {
            var a = Add(window, new());
            await Browse(window, a, "good", Disk("GOOD.BIN"));
            await Browse(window, a, "bad", [1, 2, 3]);
            Assert.Null(a.Explorer.Image);
            Assert.Contains("refused", a.Explorer.Notice);
            Assert.DoesNotContain(Texts(window), t => t.Contains("GOOD.BIN"));
            await window.BrowseExplorerImageAsync(a, "wrong HDD", null, MediaImageDescriptor.HddRaw8MiB, () => Task.FromResult(Disk("NO.BIN")));
            Assert.Null(a.Explorer.Image);
            Assert.Contains("refused", a.Explorer.Notice);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LiveOwnerSnapshotsAreReadOnlyAndHddBrowseCancelsWithoutAcknowledgingDirtyMedia()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var window = Window();
        var a = window.CurrentSession;
        var bridge = new MediaBridge(a.InputQueue);
        a.MediaBridge = bridge;
        InputController? input = null;
        var commands = new System.Collections.Concurrent.ConcurrentQueue<MediaCommand>();
        using var worker = new EmulationWorker(() =>
        {
            var wiring = HcsMachineFixture.Compose();
            wiring.Floppy.DriveA.Mount(Dsk.Load(Disk("LIVE.BIN").AsSpan()));
            var port = new BlockDiskPort(wiring.Memory, wiring.Floppy, hddEnabled: true);
            port.Restore(new(0, 0, [new(HddBytes(), 1, false, true),
                new([], 0, false, false), new([], 0, false, false), new([], 0, false, false)]));
            var media = new MediaSession(wiring.Floppy.DriveA, wiring.Floppy.DriveB, hdd: new(port));
            input = new(a.InputQueue, media: (command, _) =>
            {
                commands.Enqueue(command);
                bridge.Publish(media.Apply(command));
            });
            return wiring.Machine;
        }, context =>
        {
            context.Pause(); // Real worker boundary, no guest execution or UI-side media access.
            input!.ApplyPending(context.Machine.MasterTick, new KeyboardMatrix());
        }, cleanup: _ => bridge.Disconnect());
        window.AttachRuntime(worker);
        try
        {
            await window.BrowseExplorerDriveAsync(a, DiskSlot.A, null).WaitAsync(TimeSpan.FromSeconds(15));
            var floppy = a.Explorer.Image!;
            Assert.Null(floppy.Path); Assert.False(floppy.CanImport);
            Assert.Equal("LIVE.BIN", Assert.Single(floppy.Entries).Name);
            Assert.Contains(commands, c => c.Action == MediaAction.ExportDisk);
            Assert.False(window.CopyExplorerIn(a, floppy, "NO.BIN", 0, false, [1]));
            var replaced = await bridge.RequestAsync(new(MediaAction.InsertDisk, Bytes: Disk("LATER.BIN")))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(replaced.Error);
            Assert.NotNull(window.CopyExplorerOut(a, floppy, floppy.Entries[0], Path.Combine(domain.Root, "live.bin")));
            Assert.Equal("LIVE.BIN", Assert.Single(floppy.Entries).Name);

            await window.BrowseExplorerHddAsync(a, 2, null).WaitAsync(TimeSpan.FromSeconds(15));
            var hdd = a.Explorer.Image!;
            Assert.Null(hdd.Path); Assert.False(hdd.CanImport);
            Assert.Contains(hdd.Entries, e => e.Name == "HDD.BIN" && !e.Refused);
            var hddCommands = commands.Where(c => c.Hdd is not null).Select(c => c.Hdd!.Action).ToArray();
            Assert.Equal(new[] { HddAction.CaptureSave, HddAction.CancelSave }, hddCommands);
            var inspected = await bridge.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect)))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(inspected.Error);
            Assert.True(inspected.Hdd!.Drives[0].Dirty);
            Assert.Equal(1u, inspected.Hdd.Drives[0].Generation);
        }
        finally { window.Close(); }
        await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
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
    private static void Select(MainWindow window, string name)
        => window.FindControl<StackPanel>("MachineTabs")!.GetLogicalDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, name) || Equals(b.Content, "● " + name)).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static Button Button(MainWindow window, string name)
        => window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, name));
    private static T Field<T>(MainWindow window, string name) where T : Control
        => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static IEnumerable<string> Texts(MainWindow window)
        => window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");
    private static Task Browse(MainWindow window, MachineSession session, string name, ImmutableArray<byte> bytes, string? path = null)
        => window.BrowseExplorerImageAsync(session, name, path, MediaImageDescriptor.DskContainer, () => Task.FromResult(bytes));
    private static ImmutableArray<byte> Disk(string name, int user = 0)
    {
        var p = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(p.Geometry);
        new CpmFs(disk, p).Put(name, [0x41, 0, 0x1a], user);
        return ImmutableArray.CreateRange(disk.Save());
    }
    internal static byte[] HddBytes()
    {
        var p = Dpb.HddVolume();
        var raw = Enumerable.Repeat((byte)0xe5, p.Geometry.Length).ToArray();
        Array.Clear(raw, 0, 64);
        raw[0] = 3;
        Encoding.ASCII.GetBytes("HDD     BIN").CopyTo(raw, 1);
        raw[9] |= 0x80; raw[10] |= 0x80;
        raw[15] = 1;
        raw[16] = (byte)p.DirectoryBlocks;
        raw[p.DirectoryBlocks * p.BlockSize] = 0x42;
        raw[32] = 17;
        Encoding.ASCII.GetBytes("FAR     BIN").CopyTo(raw, 33);
        return raw;
    }
}
