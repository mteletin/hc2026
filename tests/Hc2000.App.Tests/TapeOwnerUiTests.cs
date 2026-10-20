using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class TapeOwnerUiTests
{
    [AvaloniaFact]
    public void ParsedTapeRequiresExplicitOwnerQueueControl()
    {
        var panel = new MediaPanel();
        Assert.Single(panel.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Queue selected TAP"));
    }

    [AvaloniaFact]
    public async Task RefreshShowsSchedulerCompletionRatherThanStickyPlaying()
    {
        var fixture = new RuntimeTests.Fixture(); var tape = fixture.Machine.EnableTape(3);
        var session = new MediaSession(new(new()), new(new()), tape);
        var queue = new InputQueue(); var bridge = new MediaBridge(queue);
        var controller = new InputController(queue, media: (command, _) => bridge.Publish(session.Apply(command)));
        var panel = new MediaPanel(); var window = new Window { Content = panel }; window.Show();
        async Task Pump(Task request)
        {
            controller.ApplyPending(fixture.Scheduler.Now, fixture.Machine.Keyboard);
            await request.WaitAsync(TimeSpan.FromSeconds(10));
        }
        try
        {
            panel.Attach(bridge); await Pump(panel.InspectAsync());
            await panel.ValidateTapeAsync(TapeOwnerTests.PathFor("raw-data.tap"));
            await Pump(panel.QueueSelectedTapeAsync()); await Pump(panel.PlayTapeAsync());
            Assert.Contains("Playing", panel.TapeStatus);
            fixture.Scheduler.AdvanceTo(53_424_275);
            await Pump(panel.InspectAsync());
            Assert.Contains("AtEnd", panel.TapeStatus);
            Assert.False(Assert.Single(panel.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Play")).IsEnabled);
            Assert.False(Assert.Single(panel.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Stop")).IsEnabled);
            panel.Disconnect();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SelectionAndButtonsReachRealTransportOnWorkerOwner()
    {
        var queue = new InputQueue(); var bridge = new MediaBridge(queue);
        InputController? controller = null;
        using var worker = new EmulationWorker(() =>
        {
            var fixture = new RuntimeTests.Fixture();
            var session = new MediaSession(new(new()), new(new()), fixture.Machine.EnableTape(3));
            controller = new(queue, media: (command, _) => bridge.Publish(session.Apply(command)));
            return fixture.Machine;
        }, context =>
        {
            context.Pause(); // Queue/control pumping remains live, but no master-tick advancement.
            controller!.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard);
        }, cleanup: _ => bridge.Disconnect());
        var panel = new MediaPanel(); var window = new Window { Content = panel }; window.Show();
        Button Button(string text) => Assert.Single(panel.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, text));
        async Task Click(string text, string expected)
        {
            Assert.True(Button(text).IsEnabled);
            Button(text).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (panel.TapeStatus?.Contains(expected) != true || !Button("Refresh media state").IsEnabled)
                await Task.Delay(1, deadline.Token);
        }
        try
        {
            panel.Attach(bridge);
            await panel.InspectAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await panel.ValidateTapeAsync(TapeOwnerTests.PathFor("raw-data.tap"));
            Assert.Contains("Not loaded into a machine", panel.Notice);
            Assert.Contains("Unloaded", panel.TapeStatus);
            Assert.False(Button("Play").IsEnabled);
            await Click("Queue selected TAP", "Stopped");
            Assert.Contains("raw-data.tap", panel.TapeStatus);
            await Click("Play", "Playing");
            Assert.False(Button("Play").IsEnabled);
            await Click("Stop", "Stopped");
            Assert.False(Button("Stop").IsEnabled);
            await Click("Play", "Playing");
            await Click("Eject TAP", "Unloaded");
            Assert.False(Button("Play").IsEnabled);
            await panel.ValidateTapeAsync(TapeOwnerTests.PathFor("bad-checksum.tap"));
            Assert.Contains("Invalid TAP checksum", panel.Notice);
            Assert.False(Button("Queue selected TAP").IsEnabled);
            Assert.Contains("Unloaded", panel.TapeStatus);
            panel.Disconnect();
            Assert.Contains("unavailable", panel.TapeStatus);
            Assert.False(Button("Stop").IsEnabled);
            Assert.False(Button("Queue selected TAP").IsEnabled);
        }
        finally
        {
            window.Close(); worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
