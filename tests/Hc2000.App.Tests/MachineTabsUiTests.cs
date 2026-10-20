using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Snapshots;
using Hc2000.App.Workspace;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Multi-machine tabs: per-machine queue/worker/bridge/evidence isolation on the shared window view.</summary>
public sealed class MachineTabsUiTests
{
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
    public void FifthMachineRefusedAtTheWorkspaceCap()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var first = new MachineConfiguration { Name = "M1" };
            Assert.True(window.TryAddTabView(first, out var tab1, out var r1));
            Assert.NotNull(tab1); Assert.Null(r1);
            for (int i = 2; i <= 4; i++)
                Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "M" + i }, out _, out var ok));
            Assert.False(window.TryAddTabView(new MachineConfiguration { Name = "M5" }, out var fifth, out var refusal));
            Assert.Null(fifth);
            Assert.False(string.IsNullOrWhiteSpace(refusal));
            var panel = window.FindControl<StackPanel>("MachineTabs")!;
            Assert.Equal(4, panel.Children.Count);
            // Duplicate configuration identity is refused even under the cap.
            Assert.False(window.TryAddTabView(first with { Name = "again" }, out _, out var dupRefusal));
            Assert.False(string.IsNullOrWhiteSpace(dupRefusal));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TwoTabsOwnDistinctQueuesAndInputRoutesToTheSelectedMachine()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "A" }, out var tabA, out _));
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "B" }, out var tabB, out _));
            var a = tabA!.Session; var b = tabB!.Session;
            Assert.NotSame(a.InputQueue, b.InputQueue);
            Assert.NotEqual(a.InputEvidenceWindowId, b.InputEvidenceWindowId);

            // B is selected after the add; its runtime attaches to B only.
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            Assert.Same(b.Worker, window.CurrentRuntime);
            Assert.Null(a.Worker);
            Assert.True(window.KeyboardInput.KeyDown(Key.P));
            Assert.Equal(1, b.InputQueue.BoundaryCount);
            // A's only pending command is the focus-out release enqueued in its own FIFO when
            // B was selected; the P key transition never crossed into A's queue.
            Assert.Equal(1, a.InputQueue.BoundaryCount);
            Assert.IsType<ReleaseAll>(a.InputQueue.TakeNext().Command);

            TabButton(window, "A");
            Assert.Same(a, window.CurrentSession);
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            Assert.NotNull(a.Worker);
            Assert.True(window.KeyboardInput.KeyDown(Key.Q));
            // A holds exactly the Q key transition; B holds P plus the focus-out release
            // from switching away from it.
            Assert.Equal(1, a.InputQueue.BoundaryCount);
            Assert.IsType<KeyTransition>(a.InputQueue.TakeNext().Command);
            Assert.Equal(2, b.InputQueue.BoundaryCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void KeyPressedOnOneTabStaysInThatMachinesQueue()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "A" }, out var tabA, out _));
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "B" }, out var tabB, out _));
            TabButton(window, "A");
            Assert.True(window.KeyboardInput.KeyDown(Key.P));
            // A's FIFO holds the focus-out release (from selecting A) then the P key transition;
            // B's FIFO holds only the focus-out release from selecting A. The key never crossed.
            Assert.Equal(2, tabA!.Session.InputQueue.BoundaryCount);
            Assert.IsType<ReleaseAll>(tabA.Session.InputQueue.TakeNext().Command);
            Assert.IsType<KeyTransition>(tabA.Session.InputQueue.TakeNext().Command);
            Assert.Equal(1, tabB!.Session.InputQueue.BoundaryCount);
            // Selecting the other tab re-binds the view and enqueues its own focus-out release
            // on A's FIFO (already drained above), but never re-routes the held key into B's.
            TabButton(window, "B");
            Assert.Equal(1, tabA.Session.InputQueue.BoundaryCount);
            Assert.IsType<ReleaseAll>(tabA.Session.InputQueue.TakeNext().Command);
            Assert.Equal(1, tabB.Session.InputQueue.BoundaryCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SavingFromTheSelectedTabRefusesAnotherLiveTabsHeldSourceBeforeMutation()
    {
        // Tab A holds a real read of its own temporary source. The shared save path, bound to the
        // selected tab (B), must refuse to target A's held source before any mutation, including a
        // same-file alias, leaving A's bytes, binding and hold intact. Temporary fixture file: no
        // ROMs, no install state; the member read is a bounded byte read.
        var dir = Directory.CreateTempSubdirectory("hc2000-tabs-xtab-");
        var sourceA = Path.Combine(dir.FullName, "A-source.dsk");
        var sourceBytes = new byte[16384];
        new Random(20261016).NextBytes(sourceBytes);
        File.WriteAllBytes(sourceA, sourceBytes);

        var window = new MainWindow();
        window.Show();
        try
        {
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "A" }, out var tabA, out var refusalA));
            Assert.Null(refusalA);
            var a = tabA!.Session;
            a.AttachWorker(Z80VisualDemo.CreateWorker());
            a.MediaBridge = new MediaBridge(a.InputQueue);
            a.WritebackHost = new DiskWritebackHost(new DiskWritebackOwnerLedger(), a.MediaBridge, false);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await a.ReserveHeldReadAsync(DiskSlot.A, sourceA, deadline.Token);
            // Existing per-session contract: the held read makes its exact source a protected
            // original on the owning session (green at the retained SHA).
            Assert.True(a.PathIsProtectedOriginal(sourceA), "the owning session must keep protecting its held source");

            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "B" }, out var tabB, out var refusalB));
            Assert.Null(refusalB);
            var b = tabB!.Session;
            b.AttachWorker(Z80VisualDemo.CreateWorker());
            b.MediaBridge = new MediaBridge(b.InputQueue);
            b.WritebackHost = new DiskWritebackHost(new DiskWritebackOwnerLedger(), b.MediaBridge, false);

            // B was selected at add time before its runtime was live, so the shared view is not
            // bound to it yet; the real tab-button path re-binds it now.
            TabButton(window, "B");
            var media = window.FindControl<MediaPanel>("MediaControls");
            Assert.NotNull(media);
            Assert.True(media!.PathIsProtectedOriginal is not null, "the shared save guard must be bound to the selected session");

            // The refusal is enforced by the production-wired guard on the shared panel before any
            // save mutation: tab A's held source, reached from tab B, is protected, including the
            // same-file alias the existing contract normalizes.
            Assert.True(media.PathIsProtectedOriginal!(sourceA), "tab B's save must refuse tab A's held source");
            Assert.True(media.PathIsProtectedOriginal!(Path.Combine(dir.FullName, ".", "A-source.dsk")), "the same-file alias must be refused under the existing contract");
            Assert.True(sourceBytes.SequenceEqual(File.ReadAllBytes(sourceA)), "A's bytes must be untouched by a refused save");
            Assert.False(a.Closed, "A's tab stays open");
            Assert.True(a.PathIsProtectedOriginal(sourceA), "A's hold must stay active on A after B's refused save");

            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            window.Close();
            try { dir.Delete(recursive: true); } catch { /* best effort cleanup of the temp fixture */ }
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveTabRoundTripPreservesMediaAndSnapshotBridges(bool snapshot)
    {
        using var domain = new HddColdAdmissionTests.PrivateScopedDomain();
        var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(ProfileRomSelection.DefaultAssetRoot()));
        var window = new MainWindow();
        window.Show();
        try
        {
            MachineSession StartTab(string name)
            {
                Assert.True(window.TryAddTabView(new MachineConfiguration { Name = name }, out var tab, out _));
                var session = tab!.Session;
                var media = session.MediaBridge = new MediaBridge(session.InputQueue);
                var hcs = session.SnapshotBridge = new HcsBridge(session.InputQueue);
                session.WritebackHost = new DiskWritebackHost(new DiskWritebackOwnerLedger(), media, false);
                window.AttachRuntime(BasicMachineComposition.CreateWorker(
                    BasicMachineComposition.PrepareWholeChipV2(roms), session.InputQueue, media, hcs));
                TabButton(window, name); // Bind the shared panels through the real selection path.
                return session;
            }

            async Task CheckSelected(MachineSession session, string name)
            {
                Assert.Same(session.Worker, window.CurrentRuntime);
                Assert.False(session.Worker!.Completion.IsCompleted);
                if (snapshot)
                {
                    var panel = window.FindControl<HcsPanel>("SnapshotControls")!;
                    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".hcs");
                    try
                    {
                        await panel.SaveSnapshotAsync(path).WaitAsync(TimeSpan.FromSeconds(10));
                        Assert.Equal($"Snapshot saved to {Path.GetFileName(path)}.", panel.Notice);
                        Assert.True(new FileInfo(path).Length > 0, name);
                    }
                    finally { File.Delete(path); }
                }
                else
                {
                    var panel = window.FindControl<MediaPanel>("MediaControls")!;
                    await panel.InspectAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.StartsWith("Media command applied at an emulation boundary.", panel.Notice);
                }
            }

            var a = StartTab("A");
            await CheckSelected(a, "A");
            var b = StartTab("B");
            await CheckSelected(b, "B");
            TabButton(window, "A");
            await CheckSelected(a, "A after B");
            TabButton(window, "B");
            await CheckSelected(b, "B after A");

            // A's actual owner exit still retires its bridges while B owns the shared view.
            a.Worker!.Dispose();
            await a.Worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await a.MediaBridge!.RequestAsync(new(MediaAction.Inspect)).WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await a.SnapshotBridge!.RequestAsync(new(HcsAction.Save)).WaitAsync(TimeSpan.FromSeconds(5)));
            await CheckSelected(b, "B after A exited");
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [AvaloniaFact]
    public async Task ClosingOneTabLeavesTheOtherMachineTicking()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "A" }, out var tabA, out _));
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "B" }, out var tabB, out _));
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            var a = tabA!.Session; var b = tabB!.Session;
            Assert.NotNull(a.Worker); Assert.NotNull(b.Worker);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (window.LatestPresentedFrame is null)
                await Task.Delay(20, deadline.Token);
            var before = window.LatestPresentedFrame!;

            await window.CloseTabAsync(tabB);
            Assert.True(b.Closed);
            Assert.Null(b.Worker);
            Assert.NotNull(a.Worker);
            Assert.Same(a.Worker, window.CurrentRuntime);

            // The surviving machine keeps presenting: the presented frame advances past the one B rendered.
            using var advance = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ReferenceEquals(before, window.LatestPresentedFrame) && !advance.Token.IsCancellationRequested)
                await Task.Delay(20, advance.Token);
            Assert.NotSame(before, window.LatestPresentedFrame);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosedTabRefusesNewRuntimes()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "A" }, out var tabA, out _));
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "B" }, out var tabB, out _));
            await window.CloseTabAsync(tabB!);
            var b = tabB!.Session;
            Assert.True(b.Closed);
            // Retire enqueued the focus-out release plus the shutdown control; no worker was
            // attached to drain them.
            Assert.Equal(2, b.InputQueue.BoundaryCount);
            Assert.Throws<InvalidOperationException>(() => b.AttachWorker(Z80VisualDemo.CreateWorker()));
            // The remaining tab is untouched.
            var a = tabA!.Session;
            Assert.False(a.Closed);
            Assert.Null(a.Worker);
        }
        finally { window.Close(); }
    }
}
