using System.Collections.Immutable;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.App.Snapshots;
using Hc2000.App.Workspace;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// Inline media listings in the machine UI (frozen app-tabs contract): per-tab selection
/// reaches only the bound session's media, missing providers show explicit rows, and the
/// existing guarded actions remain the only media mutation path.
/// </summary>
public sealed class InlineMediaPanelUiTests
{
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "inline-media-ui-" + Guid.NewGuid().ToString("N"));

    private static (string CatalogPath, DiskLibraryCatalog Catalog, string DiskPath) MakeCatalog(string diskName = "HELLO.DSK")
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        var diskPath = Path.Combine(root, diskName);
        File.WriteAllBytes(diskPath, MediaSessionTests.Disk().ToArray());
        var escaped = diskPath.Replace("\"", "\\");
        var length = new FileInfo(diskPath).Length;
        var entry = $$$"""
            {"id":"22222222-2222-4222-8222-222222222222","name":"Inline disk","sourcePath":"{{{escaped}}}","length":{{{length}}},"sha256":"1111111111111111111111111111111111111111111111111111111111111111","fileId":null,"format":"standard-dsk","provenance":{"source":"unit test","note":"fixture"},"contents":{"state":"unknown","profile":null,"reason":"not-scanned","files":[]}}
            """;
        var catalogPath = Path.Combine(root, "catalog.json");
        File.WriteAllText(catalogPath, "{\"version\":1,\"entries\":[" + entry + "],\"copies\":[]}");
        return (catalogPath, DiskLibraryCatalog.Load(catalogPath), diskPath);
    }

    private static Button TabButton(MainWindow window, string name)
    {
        var panel = window.FindControl<StackPanel>("MachineTabs")!;
        var button = panel.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => Equals(b.Content, name) || Equals(b.Content, "\u25CF " + name));
        Assert.NotNull(button);
        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return button;
    }

    [AvaloniaFact]
    public async Task FloppyInlineSelectionReachesOnlyTheBoundSession()
    {
        var (_, catalog, diskPath) = MakeCatalog();
        var media = new MediaPanel();
        var window = new Window { Content = media };
        window.Show();
        try
        {
            var queueA = new InputQueue(); var bridgeA = new MediaBridge(queueA);
            var sessionA = new MediaSession(new(new()), new(new()));
            var seenA = new List<MediaCommand>();
            var controllerA = new InputController(queueA, media: (c, _) => { seenA.Add(c); bridgeA.Publish(sessionA.Apply(c)); });
            var queueB = new InputQueue(); var bridgeB = new MediaBridge(queueB);
            var sessionB = new MediaSession(new(new()), new(new()));
            var seenB = new List<MediaCommand>();
            var controllerB = new InputController(queueB, media: (c, _) => { seenB.Add(c); bridgeB.Publish(sessionB.Apply(c)); });

            media.DiskLibrary = catalog;
            media.Attach(bridgeA);
            Assert.Single(media.DiskLibraryListed);
            Assert.Equal(InlineMediaStatus.Ready, media.DiskLibraryListed[0].Status);

            var rowInsert = media.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, "Insert A"));
            rowInsert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (seenA.Count == 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                controllerA.ApplyPending(0, new KeyboardMatrix());
                await Task.Delay(1, deadline.Token);
            }
            Assert.Equal(MediaAction.InsertDisk, seenA[0].Action);
            Assert.Empty(seenB);
            Assert.Contains(Path.GetFileName(diskPath),
                media.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("A:") == true).Text);

            // Rebind to session B, exactly the program tab-switch sequence; a second inline
            // selection must not change A's media.
            media.Disconnect();
            media.Attach(bridgeB);
            rowInsert = media.GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, "Insert A"));
            rowInsert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline2 = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (seenB.Count == 0)
            {
                deadline2.Token.ThrowIfCancellationRequested();
                controllerB.ApplyPending(0, new KeyboardMatrix());
                await Task.Delay(1, deadline2.Token);
            }
            Assert.Equal(MediaAction.InsertDisk, seenB[0].Action);
            Assert.Single(seenA); // A saw exactly its own selection; nothing crossed.
            // B's drive label now shows the selected disk (A's binding is gone).
            Assert.Contains(Path.GetFileName(diskPath),
                media.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("A:") == true).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SnapshotInlineLoadReachesOnlyTheBoundMachine()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        var alpha = Path.Combine(root, "alpha.hcs");
        var wiringA = HcsMachineFixture.Compose();
        var hcsA = new HcsSession(wiringA.Machine);
        var queueA = new InputQueue(); var bridgeA = new HcsBridge(queueA);
        var controllerA = new InputController(queueA, snapshot: (c, _) => bridgeA.Publish(hcsA.Apply(c)));
        var wiringB = HcsMachineFixture.Compose();
        var hcsB = new HcsSession(wiringB.Machine);
        var queueB = new InputQueue(); var bridgeB = new HcsBridge(queueB);
        var loadSeen = new List<HcsCommand>();
        var controllerB = new InputController(queueB, snapshot: (c, _) =>
        {
            if (c.Action == HcsAction.Load) loadSeen.Add(c);
            bridgeB.Publish(hcsB.Apply(c));
        });

        var snap = new HcsPanel();
        var window = new Window { Content = snap };
        window.Show();
        try
        {
            snap.SnapshotDirectory = root;
            snap.Attach(bridgeA);
            var emptyRow = Assert.Single(snap.SnapshotListed);
            Assert.Equal(InlineMediaStatus.Unavailable, emptyRow.Status);
            Assert.Contains("No .hcs snapshots", emptyRow.Reason!);

            var save = snap.SaveSnapshotAsync(alpha);
            while (!save.IsCompleted)
            {
                controllerA.ApplyPending(0, new KeyboardMatrix());
                await Task.Delay(5);
            }
            Assert.True(save.IsCompletedSuccessfully);
            Assert.True(File.Exists(alpha));

            snap.LastSnapshot = alpha;
            snap.RefreshSnapshotList();
            var listed = Assert.Single(snap.SnapshotListed);
            Assert.Equal(InlineMediaStatus.Ready, listed.Status);
            Assert.Contains("last selection", listed.Name);

            var tickA = wiringA.Machine.MasterTick;
            var tickB = wiringB.Machine.MasterTick;

            // Rebind to machine B (tab switch): the same row's guarded load goes to B only.
            snap.Disconnect();
            snap.Attach(bridgeB);
            var loadButton = snap.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Load"));
            loadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (loadSeen.Count == 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                controllerB.ApplyPending(0, new KeyboardMatrix());
                await Task.Delay(1, deadline.Token);
            }
            Assert.Equal(tickA, wiringB.Machine.MasterTick); // B restored A's captured frame
            Assert.Equal(tickA, wiringA.Machine.MasterTick); // A untouched
            Assert.Single(loadSeen);
            Assert.Contains("Snapshot loaded", snap.Notice!);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HddPanelShowsInlineEntriesWithoutASecondMediaModel()
    {
        var hdd = new HddPanel();
        var window = new Window { Content = hdd };
        window.Show();
        try
        {
            var off = Assert.Single(hdd.HddListed);
            Assert.Equal(InlineMediaStatus.Unavailable, off.Status);
            Assert.Contains("New machine", off.Reason!);
            Assert.Contains("hardware", off.Reason!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--hdd", off.Reason!);

            hdd.ConfigureCold(images => { Assert.Empty(images); return Task.CompletedTask; });
            var none = Assert.Single(hdd.HddListed);
            Assert.Equal(InlineMediaStatus.Unavailable, none.Status);
            Assert.Contains("No HDD media prepared or mounted", none.Reason!);

            var root = TempRoot();
            Directory.CreateDirectory(root);
            var raw = Path.Combine(root, "C.raw");
            await File.WriteAllBytesAsync(raw, new byte[HddMediaAdapter.ImageLength]);
            await hdd.PrepareImageAsync(raw, 2);
            var prepared = Assert.Single(hdd.HddListed);
            Assert.Equal(InlineMediaStatus.Ready, prepared.Status);
            Assert.Contains("writable", prepared.Detail); // a prepared raw image starts writable; protection is opt-in
            Assert.Contains("not attached", prepared.Detail);
            // The listing carries no guest bytes of its own: the row's size is the prepared image length.
            Assert.Equal(HddMediaAdapter.ImageLength, prepared.Size);

            // A runtime observation adds inspection rows for present drives.
            var actions = new List<HddAction>();
            hdd.Attach(command =>
            {
                if (command.Hdd is { } hddRequest) actions.Add(hddRequest.Action);
                var view = new HddDriveView(2, "C.raw", Present: true, Generation: 1, WriteProtected: true, Dirty: false);
                return Task.FromResult(new MediaReply(command.RequestId, new(null, false, false, 0, false, 0),
                    new(null, false, false, 0, false, 0), Hdd: new([view])));
            });
            hdd.Observe(new HddReply([new(2, "C.raw", Present: true, Generation: 7, WriteProtected: true, Dirty: false),
                new(3, null, Present: false, Generation: 1, WriteProtected: false, Dirty: false)]));
            // Attach consumed the cold candidate: only the two runtime rows remain.
            Assert.Equal(2, hdd.HddListed.Length);
            Assert.Equal(InlineMediaStatus.Ready, hdd.HddListed[0].Status);
            Assert.Contains("generation 7", hdd.HddListed[0].Detail);
            var inspect = hdd.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Inspect"));
            inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var inspectDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!actions.Contains(HddAction.Inspect))
            {
                inspectDeadline.Token.ThrowIfCancellationRequested();
                await Task.Delay(5, inspectDeadline.Token);
            }
            Assert.Equal([HddAction.Inspect], actions);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MainWindowTabsBindTheSharedMediaViewToTheSelectedMachine()
    {
        var (_, catalog, _) = MakeCatalog("TABBED.DSK");
        var root = TempRoot();
        Directory.CreateDirectory(root);
        var alpha = Path.Combine(root, "a.hcs");
        File.WriteAllBytes(alpha, new byte[4]);
        var configA = new MachineConfiguration { Name = "A", LastSnapshot = alpha };
        var configB = new MachineConfiguration { Name = "B" };
        var window = new MainWindow();
        window.Show();
        try
        {
            Assert.True(window.TryAddTabView(configA, out var tabA, out var okA));
            Assert.True(window.TryAddTabView(configB, out var tabB, out var okB));
            var media = window.FindControl<Media.MediaPanel>("MediaControls")!;
            var snap = window.FindControl<Snapshots.HcsPanel>("SnapshotControls")!;
            media.DiskLibrary = catalog;

            // Tab A (selected): A's snapshot scope lists its persisted last selection...
            TabButton(window, "A");
            var aRow = Assert.Single(snap.SnapshotListed, r => r.Name.Contains("a.hcs"));
            Assert.Contains("last selection", aRow.Name);
            // ...while A's disk row exists but is not actionable before A has a runtime.
            Assert.False(Assert.Single(media.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Insert A")).IsEnabled);

            // A acquires a runtime plus its own guarded bridges (the app's boot flow wires
            // these; the test mirrors the per-session contract). The shared view re-binds on
            // the tab-button path, not on the runtime attach itself.
            window.AttachRuntime(Hc2000.App.Runtime.Z80VisualDemo.CreateWorker());
            Assert.Same(tabA!.Session, window.CurrentSession);
            var a = tabA.Session;
            a.MediaBridge = new MediaBridge(a.InputQueue);
            a.WritebackHost = new DiskWritebackHost(new DiskWritebackOwnerLedger(), a.MediaBridge, false);
            a.SnapshotBridge = new HcsBridge(a.InputQueue);
            TabButton(window, "A");
            Assert.True(media.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Insert A")).IsEnabled);

            // Tab B: B's scope has no persisted last selection and no runtime yet; the same
            // shared view must not leak A's binding.
            TabButton(window, "B");
            var bRow = Assert.Single(snap.SnapshotListed);
            Assert.Equal(InlineMediaStatus.Unavailable, bRow.Status);
            Assert.Contains("No snapshot directory configured", bRow.Reason!);
            Assert.False(media.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Insert A")).IsEnabled);

            // B's own runtime makes B's row actionable; A keeps its two focus-out releases
            // (one when B was added, one when the view switched to B) and nothing else.
            window.AttachRuntime(Hc2000.App.Runtime.Z80VisualDemo.CreateWorker());
            Assert.Same(tabB!.Session, window.CurrentSession);
            var b = tabB.Session;
            b.MediaBridge = new MediaBridge(b.InputQueue);
            b.WritebackHost = new DiskWritebackHost(new DiskWritebackOwnerLedger(), b.MediaBridge, false);
            b.SnapshotBridge = new HcsBridge(b.InputQueue);
            TabButton(window, "B");
            Assert.True(media.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Insert A")).IsEnabled);
            Assert.Equal(2, tabA.Session.InputQueue.BoundaryCount);
            Assert.IsType<ReleaseAll>(tabA.Session.InputQueue.TakeNext().Command);
            Assert.IsType<ReleaseAll>(tabA.Session.InputQueue.TakeNext().Command);
        }
        finally { window.Close(); }
    }
}
