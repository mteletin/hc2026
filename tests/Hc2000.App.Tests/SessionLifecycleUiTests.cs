using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class SessionLifecycleUiTests
{
    [AvaloniaFact]
    public async Task LegacyPanelCannotReplaceTheAutoBootedV2Machine()
    {
        var window = new MainWindow(new LaunchOptions(null, null, true, false));
        window.Show();
        try
        {
            using var booted = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (window.BootedIf1Length == 0) await Task.Delay(10, booted.Token);
            var runtime = Assert.IsType<EmulationWorker>(window.CurrentRuntime);
            var panel = window.FindControl<ProfileRomPanel>("ProfileRomControls")!;
            Assert.False(panel.IsVisible);
            Assert.False(panel.IsEnabled);
            // Even a programmatic raise of the obsolete panel action has no window handler.
            await panel.SelectAsync("hc2000-v1", RomSelectionFixture.RepositoryRoot());
            panel.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Start BASIC"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            Assert.Same(runtime, window.CurrentRuntime);
            Assert.False(runtime.Completion.IsCompleted);
            Assert.Equal("Real hc2000-v2 whole-chip machine running",
                window.FindControl<TextBlock>("RuntimeStatus")!.Text);
        }
        finally
        {
            window.Close();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ResetRunsCleanupBeforeFreshFactoryOnTheSameOwner()
    {
        var queue = new InputQueue();
        RuntimeContext? current = null;
        var input = new InputController(queue, (control, _) =>
        {
            if (control.Kind == InputControlKind.Reset) current!.Reset();
        });
        var booted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0, cleanups = 0, owner = 0;
        queue.Enqueue(new KeyTransition("held", new KeyChord(Hc2000.Machine.Input.HcKey.P), true));
        queue.Enqueue(new PasteText(new string('a', PasteText.MaxLength)));
        using var worker = new EmulationWorker(() =>
        {
            if (++factoryCalls == 1) owner = Environment.CurrentManagedThreadId;
            else
            {
                Assert.Equal(owner, Environment.CurrentManagedThreadId);
                Assert.Equal(1, cleanups);
            }
            return new RuntimeTests.Fixture().Machine;
        }, context =>
        {
            current = context;
            input.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard);
            if (factoryCalls == 1) booted.TrySetResult();
            else if (!reset.Task.IsCompleted)
            {
                Assert.Equal(31, context.Machine.Keyboard.Read(0));
                Assert.False(input.IsPasting);
                context.Pause();
                reset.TrySetResult();
            }
        }, cleanup: context =>
        {
            input.ReleaseOwned(context.Machine.Keyboard);
            Assert.False(input.IsPasting);
            cleanups++;
        });
        try
        {
            await booted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Enqueue(new RuntimeControl(InputControlKind.Reset));
            await reset.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, factoryCalls);
            EnqueueFullPasteBudget(queue);
        }
        finally
        {
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            queue.DiscardPending();
        }
    }

    [AvaloniaFact]
    public async Task CloseWaitsForOwnerCleanupAndDrainsPendingInput()
    {
        var window = new MainWindow();
        using var releaseCleanup = new ManualResetEventSlim();
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new InputController(window.InputQueue);
        window.FindControl<ComboBox>("InputModeBox")!.SelectedIndex = 0; // Exercise symbolic text explicitly.
        Assert.False(window.KeyboardInput.KeyDown(Key.P)); // Symbolic text must reach the platform.
        Assert.True(window.KeyboardInput.TextInput("p"));
        EnqueueFullPasteBudget(window.InputQueue);
        var applied = false;
        var worker = new EmulationWorker(() => new RuntimeTests.Fixture().Machine, context =>
        {
            if (applied) return;
            input.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard);
            context.Pause();
            applied = true;
            consumed.SetResult();
        }, cleanup: context =>
        {
            cleanupEntered.SetResult();
            Assert.True(releaseCleanup.Wait(TimeSpan.FromSeconds(10)));
            input.ReleaseOwned(context.Machine.Keyboard);
            Assert.Equal(31, context.Machine.Keyboard.Read(0));
            Assert.False(input.IsPasting);
        });
        window.AttachRuntime(worker);
        window.Show();
        try
        {
            await consumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.ShutdownCompletion.IsCompleted);
            releaseCleanup.Set();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(worker.Completion.IsCompletedSuccessfully);
            Assert.Null(window.CurrentRuntime);
            Assert.Equal(0, window.InputQueue.BoundaryCount);
            EnqueueFullPasteBudget(window.InputQueue);
        }
        finally
        {
            releaseCleanup.Set();
            window.Close();
            worker.Dispose();
            await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void EnqueueFullPasteBudget(InputQueue queue)
    {
        for (var i = 0; i < InputQueue.MaxOutstandingPasteCharacters / PasteText.MaxLength; i++)
            queue.Enqueue(new PasteText(new string('a', PasteText.MaxLength)));
    }
}
