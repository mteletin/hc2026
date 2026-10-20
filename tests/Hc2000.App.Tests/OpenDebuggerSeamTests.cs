using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.RegularExpressions;
using Hc2000.App.Configuration;
using Hc2000.App.Debugging;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Settings;
using Hc2000.App.Snapshots;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// The existing Debugger… action is reachable without a selected machine and reports
/// unavailable state truthfully, while a running machine supplies its boundary snapshot.
/// </summary>
public sealed class OpenDebuggerSeamTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-open-debugger-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [AvaloniaTheory]
    [InlineData(1280, 900, false)]
    [InlineData(960, 720, false)]
    [InlineData(960, 720, true)]
    public async Task DebuggerWithoutASelectedMachineIsReachableAndShowsUnavailableState(int width, int height, bool closeLastMachine)
    {
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory))
        {
            Width = width, Height = height
        };
        try
        {
            window.Show();
            if (closeLastMachine)
            {
                Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk" }, out var tab, out _));
                await window.CloseTabAsync(tab!);
            }
            // With no tabs, the shell retains an implicit session but no selected machine.
            Assert.False(window.FindControl<Control>("MachineTabs")!.IsVisible);
            Assert.False(window.CurrentSession.HasRuntime);
            Assert.Null(window.CurrentRuntime);
            Assert.False(window.FindControl<Control>("RunningWorkspace")!.IsVisible);
            // Allow styles/layout and the rendered hit-test scene to catch up with Show.
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var button = Assert.Single(window.GetLogicalDescendants().OfType<Button>(),
                b => Equals(b.Content, "Debugger…"));
            // Finding a logical descendant is not reachability: the old button lived
            // under the hidden RunningWorkspace. Require a visible, clickable surface.
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
            Assert.NotNull(point);
            Assert.True(new Rect(window.ClientSize).Contains(point.Value));
            var hit = window.GetVisualAt(point.Value);
            Assert.NotNull(hit);
            Assert.Contains(button, hit.GetSelfAndVisualAncestors());
            window.MouseDown(point.Value, MouseButton.Left);
            window.MouseUp(point.Value, MouseButton.Left);

            var opened = await WaitFor<DebuggerWindow>(
                () => window.OwnedWindows.OfType<DebuggerWindow>().FirstOrDefault(),
                TimeSpan.FromSeconds(5));
            var controls = opened.GetLogicalDescendants().ToArray();
            Assert.Contains(controls.OfType<TextBlock>(), t => t.Text == "No machine selected.");
            Assert.DoesNotContain(controls.OfType<TextBlock>(), t => t.Text?.Contains("PC=") == true);
            Assert.Contains(controls.OfType<TextBox>(), t => t.Text == "Unavailable: no boundary snapshot.");
            Assert.False(Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Pause machine")).IsEnabled);
            Assert.False(Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Resume")).IsEnabled);
            Assert.False(Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Record")).IsEnabled);
            Assert.Null(window.CurrentRuntime);
            Assert.False(File.Exists(new SettingsStore(directory).FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task DebuggerOpenedOnARunningMachineShowsTheBoundarySnapshot()
    {
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory));
        try
        {
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk" }, out _, out _));
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            Assert.NotNull(window.CurrentRuntime);

            var button = Assert.Single(window.GetLogicalDescendants().OfType<Button>(),
                b => Equals(b.Content, "Debugger…"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var opened = await WaitFor<DebuggerWindow>(
                () => window.OwnedWindows.OfType<DebuggerWindow>().FirstOrDefault(),
                TimeSpan.FromSeconds(5));
            var status = await WaitFor<string>(
                () => opened.GetLogicalDescendants().OfType<TextBlock>()
                    .Select(t => t.Text)
                    .FirstOrDefault(t => t?.Contains("PC=") == true),
                TimeSpan.FromSeconds(10));

            Assert.DoesNotContain("No machine snapshot supplied", status);
            Assert.Matches("PC=[0-9A-F]{4}", status);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    /// <summary>A reset and then a pause must each produce a copy taken after that control was
    /// consumed. The one-shot publish at open keeps the retained pre-request copy, so its generation
    /// and sequence never move and this fails.</summary>
    [AvaloniaFact]
    public async Task DebuggerShowsACopyTakenAfterResetAndAfterPause()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(ProfileRomSelection.DefaultAssetRoot()));
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory));
        try
        {
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk" }, out var tab, out _));
            var session = tab!.Session;
            var media = session.MediaBridge = new MediaBridge(session.InputQueue);
            var hcs = session.SnapshotBridge = new HcsBridge(session.InputQueue);
            session.WritebackHost = new DiskWritebackHost(new DiskWritebackOwnerLedger(), media, false);
            window.AttachRuntime(BasicMachineComposition.CreateWorker(
                BasicMachineComposition.PrepareWholeChipV2(roms), session.InputQueue, media, hcs));
            session.BasicRuntimeActive = true;

            Assert.Single(window.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Debugger…"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var opened = await WaitFor<DebuggerWindow>(
                () => window.OwnedWindows.OfType<DebuggerWindow>().FirstOrDefault(), TimeSpan.FromSeconds(5));

            var (gen, seq, paused) = await WaitForStamp(opened, s => s.Sequence >= 0, TimeSpan.FromSeconds(15));
            Assert.False(paused);

            window.FindControl<Button>("MachineReset")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var afterReset = await WaitForStamp(opened, s => s.Generation > gen, TimeSpan.FromSeconds(15));

            Assert.Single(opened.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Pause machine"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var afterPause = await WaitForStamp(opened,
                s => s.Paused && s.Sequence > afterReset.Sequence, TimeSpan.FromSeconds(15));
            Assert.Equal(afterReset.Generation, afterPause.Generation);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    private static readonly Regex Stamp = new(@"gen=(\d+) seq=(\d+) paused=(yes|no)", RegexOptions.CultureInvariant);

    [AvaloniaFact]
    public async Task OpeningDebuggerDoesNotPresentARetainedPreRequestCopyAsFresh()
    {
        var memory = new Hc2000.Machine.Memory.HcMemory(new(), new byte[16384], new byte[16384], new byte[16384]);
        var allowCapture = 1;
        using var worker = new EmulationWorker(() => Z80VisualDemo.CreateMachine(out _, out _),
            context => context.Pause(), debugSnapshot: _ => Volatile.Read(ref allowCapture) == 0 ? null
                : new DebuggerSession("retained boundary fixture").Boundary(new(0x1234, 0xffff), memory, null, 42));
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory));
        try
        {
            worker.RequestDebugSnapshot();
            var retained = await WaitFor(() => worker.TakeLatestDebugEvidence(), TimeSpan.FromSeconds(10));
            Volatile.Write(ref allowCapture, 0);
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk" }, out _, out _));
            window.AttachRuntime(worker);
            Assert.Single(window.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Debugger…"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var opened = Assert.Single(window.OwnedWindows.OfType<DebuggerWindow>());
            Assert.Contains(opened.GetLogicalDescendants().OfType<TextBlock>(),
                t => t.Text == "Selected machine awaiting snapshot.");
            Assert.DoesNotContain(opened.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("PC=") == true);
            Volatile.Write(ref allowCapture, 1);
            var first = await WaitForStamp(opened, stamp => stamp.Sequence > retained.Sequence, TimeSpan.FromSeconds(10));
            var next = await WaitForStamp(opened, stamp => stamp.Sequence > first.Sequence, TimeSpan.FromSeconds(10));
            Assert.True(first.Paused && next.Paused); // T=42 is unchanged; sequence alone proves freshness.
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Other" }, out _, out _));
            await WaitFor(() => opened.GetLogicalDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Text == "No machine selected."), TimeSpan.FromSeconds(10));
            Assert.False(Assert.Single(opened.GetLogicalDescendants().OfType<Button>(),
                b => Equals(b.Content, "Pause machine")).IsEnabled);
            await AssertCapturesStop(worker);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task ClosingDebuggerStopsRefreshWithoutStoppingTheMachine()
    {
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory));
        try
        {
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk" }, out _, out _));
            var worker = Z80VisualDemo.CreateWorker();
            window.AttachRuntime(worker);
            Assert.Single(window.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Debugger…"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var opened = Assert.Single(window.OwnedWindows.OfType<DebuggerWindow>());
            await WaitForStamp(opened, stamp => stamp.Sequence > 0, TimeSpan.FromSeconds(10));
            opened.Close();
            await AssertCapturesStop(worker);
            Assert.False(worker.Completion.IsCompleted);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task FailedWorkerDetachesWithTheFailureRatherThanAwaitingASnapshot()
    {
        using var worker = new EmulationWorker(() => throw new InvalidOperationException("debugger fixture failure"), _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory));
        try
        {
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk" }, out _, out _));
            window.AttachRuntime(worker);
            Assert.Single(window.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Debugger…"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var opened = Assert.Single(window.OwnedWindows.OfType<DebuggerWindow>());
            Assert.Contains(opened.GetLogicalDescendants().OfType<TextBlock>(),
                t => t.Text == "Worker failure: debugger fixture failure");
            Assert.False(Assert.Single(opened.GetLogicalDescendants().OfType<Button>(),
                b => Equals(b.Content, "Pause machine")).IsEnabled);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    private static async Task AssertCapturesStop(EmulationWorker worker)
    {
        // Let the one demand already accepted before detach/close finish; none may follow it.
        await WaitFor(() => worker.DebugDemandPending ? null : new object(), TimeSpan.FromSeconds(10));
        var sequence = worker.TakeLatestDebugEvidence()!.Sequence;
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(sequence, worker.TakeLatestDebugEvidence()!.Sequence);
    }

    private readonly record struct DebugStamp(long Generation, long Sequence, bool Paused);

    /// <summary>The status line carries the stamps of the copy on display. Waiting on the predicate
    /// rather than the first line avoids reading the copy retained from before the control.</summary>
    private static async Task<DebugStamp> WaitForStamp(DebuggerWindow window, Func<DebugStamp, bool> until, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        DebugStamp last = default;
        while (DateTime.UtcNow < deadline)
        {
            var status = window.GetLogicalDescendants().OfType<TextBlock>()
                .Select(t => t.Text).FirstOrDefault(t => t?.Contains("gen=") == true);
            var match = status is null ? null : Stamp.Match(status);
            if (match is { Success: true })
            {
                last = new(long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value),
                    match.Groups[3].Value == "yes");
                if (until(last)) return last;
            }
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
        }
        throw new Xunit.Sdk.XunitException(
            $"No fresh debugger copy within {budget.TotalSeconds:0}s; last seen gen={last.Generation} seq={last.Sequence} paused={last.Paused}.");
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe, TimeSpan budget) where T : class
    {
        var until = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } hit) return hit;
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
        }
        return probe() ?? throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} within {budget.TotalSeconds:0}s.");
    }
}
