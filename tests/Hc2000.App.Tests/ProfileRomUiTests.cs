using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class ProfileRomUiTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public void ShellListsOnlyRegisteredFaithfulRomProfilesWithV2Default()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var profiles = window.GetLogicalDescendants().OfType<ComboBox>()
                .Single(c => c.Name == "RomProfileChoice");
            Assert.Equal(new[] { "hc2000-v2", "hc2000-v1" }, profiles.Items.Cast<string>());
            Assert.Equal("hc2000-v2", profiles.SelectedItem);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task V2CandidateShowsCanonicalUpperPlacementAndNeverSelectsAnIncompleteSet()
    {
        var panel = new ProfileRomPanel(); var window = new Window { Content = panel }; window.Show();
        try
        {
            var text = panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");
            Assert.Contains(text, t => t.Contains("cpm-hc2kzip.bin") && t.Contains("0x6000"));
            Assert.Contains(text, t => t.Contains("missing lower half"));
            await panel.SelectAsync("hc2000-v2", RomSelectionFixture.RepositoryRoot());
            Assert.Null(panel.SelectedRomSet);
            Assert.Contains("lower half", panel.Notice);
            Assert.Contains("not selected", panel.Notice);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MissingAndUnknownCandidatesLeaveNoPartialSelectionAndControlsRecover()
    {
        using var fixture = new RomSelectionFixture();
        var panel = new ProfileRomPanel(); var window = new Window { Content = panel }; window.Show();
        try
        {
            await panel.SelectAsync("hc2000-v1", fixture.Root);
            Assert.Null(panel.SelectedRomSet);
            Assert.Contains("basic.rom", panel.Notice);
            await panel.SelectAsync("HC2000-V1", fixture.Root);
            Assert.Contains("Unknown faithful profile", panel.Notice);
            Assert.Null(panel.SelectedRomSet);
            var buttons = panel.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.All(buttons.Where(b => Equals(b.Content, "Choose ROM asset root…")
                || Equals(b.Content, "Validate and select ROM set")), b => Assert.True(b.IsEnabled));
            Assert.All(buttons.Where(b => Equals(b.Content, "Start BASIC")
                || Equals(b.Content, "Reset BASIC")), b => Assert.False(b.IsEnabled));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SelectButtonStagesV1AndFailedLateRegionOrV2CandidateRetainsPreviousVerifiedBytes()
    {
        Assert.Null(RomSelectionFixture.MissingAssets()); // This integration test requires materialized named ROMs.
        using var fixture = new RomSelectionFixture(copyV1: true);
        var window = new MainWindow(); window.Show();
        try
        {
            var panel = window.FindControl<ProfileRomPanel>("ProfileRomControls")!;
            var choice = panel.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "RomProfileChoice");
            choice.SelectedItem = "hc2000-v1";
            panel.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "RomAssetRoot").Text = fixture.Root;
            var button = panel.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Validate and select ROM set"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!button.IsEnabled) await Task.Delay(1, deadline.Token);
            var selected = Assert.IsType<SelectedProfileRomSet>(panel.SelectedRomSet);
            Assert.Equal("hc2000-v1", selected.ProfileId);
            Assert.Equal(3, selected.Regions.Length);
            Assert.Contains("profile selection alone never starts a runtime", panel.Notice);
            // The last region fails after the first two passed. Selection is still atomic.
            await File.WriteAllBytesAsync(fixture.V1Path("if1.rom"), new byte[0x4000]);
            await panel.SelectAsync("hc2000-v1", fixture.Root);
            Assert.Same(selected, panel.SelectedRomSet);
            Assert.Contains("identity mismatch: roms/hc2000/v1/if1.rom", panel.Notice);
            choice.SelectedItem = "hc2000-v2";
            await panel.SelectAsync("hc2000-v2", fixture.Root);
            Assert.Same(selected, panel.SelectedRomSet);
            Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Selected ROM set: hc2000-v1") == true);
            Assert.Contains("Previous selection unchanged", panel.Notice);
            var controller = new Hc2000.App.Input.InputController(window.InputQueue,
                control: (_, _) => Assert.Fail("ROM selection must not send runtime controls."),
                media: (_, _) => Assert.Fail("ROM selection must not send media commands."));
            controller.ApplyPending(0, new Hc2000.Machine.Input.KeyboardMatrix());
            Assert.Equal(0, controller.LastAppliedSequence); // No input/reset/ROM swap.
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task StartAndResetAreExplicitActionsAfterSuccessfulValidation()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var panel = new ProfileRomPanel(); var window = new Window { Content = panel }; window.Show();
        try
        {
            await panel.SelectAsync("hc2000-v1", RomSelectionFixture.RepositoryRoot());
            SelectedProfileRomSet? started = null;
            var resets = 0;
            panel.StartRequested += value => started = value;
            panel.ResetRequested += () => resets++;
            var buttons = panel.GetLogicalDescendants().OfType<Button>().ToArray();
            var start = Assert.Single(buttons, b => Equals(b.Content, "Start BASIC"));
            var reset = Assert.Single(buttons, b => Equals(b.Content, "Reset BASIC"));
            Assert.True(start.IsEnabled);
            Assert.False(reset.IsEnabled);
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(panel.SelectedRomSet, started);
            panel.SetBasicRuntimeActive(true);
            Assert.False(start.IsEnabled);
            Assert.True(reset.IsEnabled);
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, resets);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task NormalMainWindowStartsOnlyWholeChipRuntime()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var window = new MainWindow(new LaunchOptions(null, null, true, false));
        window.Show();
        try
        {
            using var started = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (window.BootedIf1Length == 0) await Task.Delay(10, started.Token);
            Assert.False(window.FindControl<ProfileRomPanel>("ProfileRomControls")!.IsVisible);
            Assert.Contains("Real hc2000-v2 whole-chip machine running",
                window.FindControl<TextBlock>("RuntimeStatus")!.Text);
            var screen = window.FindControl<Hc2000.App.Runtime.VideoSurface>("Screen")!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (screen.PresentedMasterTick is null) await Task.Delay(10, deadline.Token);
            Assert.True(screen.PresentedMasterTick > 0);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    [AvaloniaFact]
    public async Task ResetAndCloseReleaseHeldInputAndPasteCapacity()
    {
        Assert.Null(RomSelectionFixture.MissingAssets());
        var window = new MainWindow(new LaunchOptions(null, null, true, false));
        var closed = false;
        var actualClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler closedWitness = (_, _) =>
        {
            closed = true;
            actualClosed.TrySetResult();
        };
        window.Closed += closedWitness;
        try
        {
            window.Show();
            using var booted = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (window.BootedIf1Length == 0) await Task.Delay(10, booted.Token);
            var old = Assert.IsType<Hc2000.App.Runtime.EmulationWorker>(window.CurrentRuntime);
            var screen = window.FindControl<Hc2000.App.Runtime.VideoSurface>("Screen")!;
            var oldTick = await WaitForPresentedTick(screen, TimeSpan.FromSeconds(5), atLeast: 1_000_000);

            var afterResetKey = oldTick + 71_136;
            window.FindControl<ComboBox>("InputModeBox")!.SelectedIndex = 0; // Exercise symbolic text explicitly.
            Assert.False(window.KeyboardInput.KeyDown(Key.P)); // Native TextInput follows an unhandled press.
            Assert.True(window.KeyboardInput.TextInput("p"));
            await WaitForPresentedTick(screen, TimeSpan.FromSeconds(5), afterResetKey);
            for (var i = 0; i < 4; i++)
                Assert.True(window.KeyboardInput.Paste(new string('a', PasteText.MaxLength)));

            window.FindControl<Button>("MachineReset")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (screen.PresentedMasterTick is null || screen.PresentedMasterTick >= oldTick)
                    await Task.Delay(10, deadline.Token);
            }
            var fresh = Assert.IsType<Hc2000.App.Runtime.EmulationWorker>(window.CurrentRuntime);
            Assert.Same(old, fresh); // Reset retains the owner; machine/CPU/time are fresh.
            Assert.False(fresh.Completion.IsCompleted);

            EnqueueFullPasteBudget(window.InputQueue);
            var beforeClose = screen.PresentedMasterTick!.Value;
            Assert.False(window.KeyboardInput.KeyDown(Key.P)); // Native TextInput follows an unhandled press.
            Assert.True(window.KeyboardInput.TextInput("p"));
            await WaitForPresentedTick(screen, TimeSpan.FromSeconds(5), beforeClose + 71_136);
            // Fixture-only observations: do not change close acceptance or wait semantics.
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            var mediaBusyField = typeof(MediaPanel).GetField("busy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var mediaBridgeField = typeof(MediaPanel).GetField("bridge", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var closeObservation = 0;
            var shutdownAtEntry = window.ShutdownCompletion;
            void ObserveClose(string phase)
            {
                if (++closeObservation > 12) return;
                output.WriteLine($"close#{closeObservation} hostStopwatch={System.Diagnostics.Stopwatch.GetTimestamp()} " +
                    $"phase={phase} visible={window.IsVisible} currentOwnerSame={ReferenceEquals(fresh, window.CurrentRuntime)} " +
                    $"shutdownTask={window.ShutdownCompletion.Id}/{window.ShutdownCompletion.Status} " +
                    $"sameAsEntry={ReferenceEquals(shutdownAtEntry, window.ShutdownCompletion)} " +
                    $"workerTask={fresh.Completion.Id}/{fresh.Completion.Status} actualClosed={actualClosed.Task.Status} " +
                    $"mediaBusy={mediaBusyField?.GetValue(media) ?? "unavailable"} " +
                    $"mediaPending={(mediaBridgeField?.GetValue(media) as MediaBridge)?.HasPending.ToString() ?? "unavailable"} " +
                    $"deckBusy={media.Deck.IsBusy} hddBusy={media.Hdd.IsBusy} mediaNotice={media.Notice}");
            }
            EventHandler<WindowClosingEventArgs> closingObserver = (_, e) => ObserveClose($"Closing cancel={e.Cancel}");
            EventHandler closedObserver = (_, _) => ObserveClose("Closed after existing shutdown handler");
            window.Closing += closingObserver;
            window.Closed += closedObserver;
            try
            {
                // ONE original five-second success budget covers accepted Closed AND owner shutdown.
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                ObserveClose("before Close");
                window.Close();
                ObserveClose("after Close");
                try
                {
                    await actualClosed.Task.WaitAsync(closeDeadline.Token);
                    var shutdownAwaited = window.ShutdownCompletion;
                    output.WriteLine($"close awaiting post-Closed shutdownTask={shutdownAwaited.Id}/{shutdownAwaited.Status} " +
                        $"sameAsEntry={ReferenceEquals(shutdownAtEntry, shutdownAwaited)}");
                    await shutdownAwaited.WaitAsync(closeDeadline.Token);
                }
                finally { ObserveClose("after original close/shutdown budget"); }
                Assert.True(fresh.Completion.IsCompleted);
                EnqueueFullPasteBudget(window.InputQueue);
            }
            finally
            {
                window.Closing -= closingObserver;
                window.Closed -= closedObserver;
            }
        }
        finally
        {
            // Teardown is NOT evidence of successful user close. Record state before stopping
            // only this fixture's owner; never force consent or clear media/refusal state.
            output.WriteLine($"teardown entry actualClosed={closed} visible={window.IsVisible} " +
                $"workerTask={window.CurrentRuntime?.Completion.Status} shutdownTask={window.ShutdownCompletion.Status}");
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var owned = window.CurrentRuntime;
            try
            {
                owned?.Dispose();
                if (owned is not null) await owned.Completion.WaitAsync(cleanupDeadline.Token);
            }
            finally
            {
                try
                {
                    if (!closed) window.Close();
                    await window.ShutdownCompletion.WaitAsync(cleanupDeadline.Token);
                    output.WriteLine($"teardown end actualClosed={closed} visible={window.IsVisible} " +
                        $"ownedTask={owned?.Completion.Status} shutdownTask={window.ShutdownCompletion.Status}");
                }
                finally { window.Closed -= closedWitness; }
            }
        }
    }

    private static async Task<long> WaitForPresentedTick(Hc2000.App.Runtime.VideoSurface screen, TimeSpan timeout,
        long atLeast = 0)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (screen.PresentedMasterTick is null || screen.PresentedMasterTick < atLeast)
            await Task.Delay(10, deadline.Token);
        return screen.PresentedMasterTick!.Value;
    }

    private static void EnqueueFullPasteBudget(Hc2000.App.Input.InputQueue queue)
    {
        for (var i = 0; i < 4; i++) queue.Enqueue(new PasteText(new string('a', PasteText.MaxLength)));
    }
}
