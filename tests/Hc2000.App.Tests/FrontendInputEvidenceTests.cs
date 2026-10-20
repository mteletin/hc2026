using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Hc2000.App.Input;
using Hc2000.App.Runtime;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class FrontendInputEvidenceTests
{
    private sealed class Clock : IHostClock
    {
        public long Frequency => 1000;
        public long Now => 42;
        public void WaitUntil(long deadline, CancellationToken cancellation)
            => throw new InvalidOperationException("Observation must not pace input.");
    }

    private sealed class BrokenClock : IHostClock
    {
        public long Frequency => throw new InvalidOperationException("clock failure");
        public long Now => throw new InvalidOperationException("clock failure");
        public void WaitUntil(long deadline, CancellationToken cancellation) => throw new NotSupportedException();
    }

    [AvaloniaFact]
    public void ExistingWindowRoutingRecordsOnlyDeliveredAdapterEvents()
    {
        var window = new MainWindow(); // Not shown: no auto-boot or competing queue consumer.
        try
        {
            window.KeyboardInput.Evidence.BeginSession("routed-fixture", new Clock());
            window.KeyboardInput.Evidence.Enable();
            var editor = window.FindControl<TextBox>("PasteTextBox")!;
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });
            Assert.Empty(window.KeyboardInput.Evidence.Read().Records);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.B });
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.B });
            Assert.Equal(new[] { "key-down", "key-up" }, window.KeyboardInput.Evidence.Read().Records
                .Where(r => r.Stage == "receipt").Select(r => r.Origin));
        }
        finally { window.KeyboardInput.Evidence.EndSession(); window.Close(); }
    }

    [Fact]
    public void RetiredSessionCannotBeReenabledWithStaleClockIdentity()
    {
        var input = new KeyboardInputAdapter(new());
        var current = true;
        input.Evidence.BeginSession("race", new Clock(), () => current);
        input.Evidence.Enable(); input.KeyDown(Key.A);
        current = false; // Owner retires/reset/load-adopts while UI has old session identity.
        Assert.False(input.Evidence.Enable());
        Assert.Empty(input.Evidence.Read().Records);
        Assert.Null(input.Evidence.Read().Session);
        input.KeyUp(Key.A);
        Assert.Empty(input.Evidence.Read().Records);
    }

    [AvaloniaFact]
    public void DisconnectedWindowRefusesCaptureAndDoesNotManufactureClock()
    {
        var window = new MainWindow();
        try
        {
            Assert.False(window.SetInputEvidenceEnabled(true));
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });
            var snapshot = window.ReadWindowInputEvidence();
            Assert.False(snapshot.Enabled); Assert.Null(snapshot.Session); Assert.Empty(snapshot.Records);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WindowUsesAttachedWorkersExactClockAndRetirementClearsWithoutUiPump()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock();
        var runtime = new EmulationWorker(() => new RuntimeTests.Fixture().Machine,
            context => { context.Pause(); ready.TrySetResult(); }, hostClock: clock);
        var window = new MainWindow();
        try
        {
            window.AttachRuntime(runtime);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(clock, runtime.HostClock);
            Assert.True(window.SetInputEvidenceEnabled(true));
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });
            Assert.All(window.ReadWindowInputEvidence().Records, r =>
            { Assert.Equal(42, r.HostTimestamp); Assert.Equal(1000, r.HostFrequency); });
            Assert.NotEmpty(window.KeyboardInput.Evidence.Read().Records);
            runtime.Dispose();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            // Owner callback clears even before any presentation tick/read refresh.
            Assert.Empty(window.KeyboardInput.Evidence.Read().Records);
            Assert.False(window.SetInputEvidenceEnabled(true));
        }
        finally { window.Close(); runtime.Dispose(); }
    }

    [Fact]
    public void ObserverClockFailureCannotDropGuestCommand()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue);
        var controller = new InputController(queue); var matrix = new KeyboardMatrix();
        input.Evidence.BeginSession("fault", new BrokenClock()); input.Evidence.Enable();
        Assert.True(input.KeyDown(Key.A));
        Assert.False(input.Evidence.Enabled);
        controller.ApplyPending(0, matrix);
        Assert.Equal(30, matrix.Read(0xfdfe));
        Assert.Equal(1, controller.LastAppliedSequence);
    }

    [Fact]
    public void EnabledAndDisabledPathsHaveIdenticalMatrixAndSequenceAtEveryBoundary()
    {
        var offQueue = new InputQueue(); var onQueue = new InputQueue();
        var off = new KeyboardInputAdapter(offQueue); var on = new KeyboardInputAdapter(onQueue);
        on.Evidence.BeginSession("comparison", new Clock()); on.Evidence.Enable();
        var offController = new InputController(offQueue); var onController = new InputController(onQueue);
        var offMatrix = new KeyboardMatrix(); var onMatrix = new KeyboardMatrix();
        Action<KeyboardInputAdapter>[] actions =
        [
            a => a.KeyDown(Key.LeftShift), a => a.KeyDown(Key.A), a => a.KeyDown(Key.A),
            a => a.KeyUp(Key.A), a => a.ReleaseFocus(InputReleaseOrigin.Deactivated),
            a => a.Mode = KeyMappingMode.Symbolic, a => a.KeyDown(Key.D2),
            a => a.TextInput("\""), a => a.KeyUp(Key.D2), a => a.Paste("€"),
            a => a.Paste("ab"), a => a.ReleaseFocus()
        ];
        for (var i = 0; i < actions.Length; i++)
        {
            actions[i](off); actions[i](on);
            offController.ApplyPending(i, offMatrix); onController.ApplyPending(i, onMatrix);
            for (var row = 0; row < 8; row++)
            {
                var address = (ushort)(0xffff ^ (1 << (row + 8)));
                Assert.Equal(offMatrix.Read(address), onMatrix.Read(address));
            }
            Assert.Equal(offController.LastAppliedSequence, onController.LastAppliedSequence);
            Assert.Equal(offController.IsPasting, onController.IsPasting);
        }
        Assert.Contains(on.Evidence.Read().Records, r => r.Outcome == "window-deactivated");
        Assert.Contains(on.Evidence.Read().Records, r => r.Outcome == "mapping-mode-change");
    }

    [Fact]
    public void DefaultOffAndExplicitSessionRequired()
    {
        var input = new KeyboardInputAdapter(new());
        Assert.False(input.Evidence.Enable());
        input.KeyDown(Key.A);
        Assert.Empty(input.Evidence.Read().Records);
        input.Evidence.BeginSession("isolated-fixture", new Clock());
        Assert.False(input.Evidence.Enabled);
        Assert.True(input.Evidence.Enable());
        input.KeyUp(Key.A);
        input.Evidence.Disable();
        Assert.Empty(input.Evidence.Read().Records);
        input.KeyDown(Key.B);
        Assert.Empty(input.Evidence.Read().Records);
    }

    [Fact]
    public void ActualAdapterReportsIndependentPhysicalMappingAndQueueOrder()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue);
        input.Evidence.BeginSession("test", new Clock()); input.Evidence.Enable();
        input.KeyDown(Key.LeftShift, KeyModifiers.Shift);
        input.KeyDown(Key.A, KeyModifiers.Shift);
        input.KeyDown(Key.A, KeyModifiers.Shift); // repeat: no second closure
        input.KeyUp(Key.A);
        var records = input.Evidence.Read().Records;
        Assert.Equal(new[] { "receipt", "mapping", "queue-handoff", "receipt", "mapping",
            "queue-handoff", "receipt", "mapping", "receipt", "queue-handoff" }, records.Select(r => r.Stage));
        Assert.Equal(new[] { HcKey.CapsShift }, records[1].Chord);
        Assert.Equal(new[] { HcKey.A }, records[4].Chord); // shift is an independent held source
        Assert.Equal("repeat-suppressed", records[7].Outcome);
        Assert.Equal(new long?[] { 1, 2, 3 }, records.Where(r => r.Stage == "queue-handoff").Select(r => r.QueueSequence));
        Assert.All(records, r => { Assert.Equal(42, r.HostTimestamp); Assert.Equal(1000, r.HostFrequency); });
        Assert.Equal(Enumerable.Range(1, records.Length).Select(i => (long)i), records.Select(r => r.Order));
        Assert.Contains("No guest consumption", input.Evidence.Read().Coverage);
    }

    [Fact]
    public void ObservationDoesNotApplyKeysAndClearDoesNotReleaseThem()
    {
        var queue = new InputQueue(); var input = new KeyboardInputAdapter(queue);
        var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        input.Evidence.BeginSession("test", new Clock()); input.Evidence.Enable();
        input.KeyDown(Key.A);
        Assert.Equal(31, matrix.Read(0x00fe));
        input.Evidence.Clear();
        controller.ApplyPending(0, matrix);
        Assert.Equal(30, matrix.Read(0xfdfe)); // A is bit0 of row1
        input.Evidence.Disable();
        Assert.Equal(30, matrix.Read(0xfdfe));
        input.KeyUp(Key.A); controller.ApplyPending(1, matrix);
        Assert.Equal(31, matrix.Read(0x00fe));
    }

    [Fact]
    public void BoundedRetentionAndSessionEndAreExplicit()
    {
        var input = new KeyboardInputAdapter(new());
        input.Evidence.BeginSession("first", new Clock()); input.Evidence.Enable();
        for (var i = 0; i < 300; i++) input.KeyUp(Key.A);
        var snapshot = input.Evidence.Read();
        Assert.Equal(256, snapshot.Records.Length);
        Assert.Equal(344, snapshot.Dropped);
        Assert.Equal(345, snapshot.Records[0].Order);
        input.Evidence.EndSession();
        Assert.Empty(input.Evidence.Read().Records);
        Assert.Null(input.Evidence.Read().Session);
        Assert.False(input.Evidence.Enable());
        input.Evidence.BeginSession("second", new Clock());
        Assert.False(input.Evidence.Enabled);
        Assert.Empty(input.Evidence.Read().Records);
    }

    [Fact]
    public void RefusalAndSymbolicTextDoNotInventPhysicalDeliveryOrGuestEffect()
    {
        var input = new KeyboardInputAdapter(new()) { Mode = KeyMappingMode.Symbolic };
        input.Evidence.BeginSession("test", new Clock()); input.Evidence.Enable();
        Assert.False(input.KeyDown(Key.D2, KeyModifiers.Shift));
        Assert.True(input.TextInput("\""));
        Assert.False(input.Paste("€"));
        var records = input.Evidence.Read().Records;
        Assert.Equal("await-host-text", records[1].Outcome);
        var mapped = Assert.Single(records.Where(r => r.Origin == "text-input" && r.Stage == "mapping"));
        Assert.Equal(new[] { HcKey.SymbolShift, HcKey.P }, mapped.Chord);
        Assert.Single(records.Where(r => r.Stage == "refusal" && r.Outcome == "invalid-paste"));
        Assert.Single(records.Where(r => r.Stage == "queue-handoff"));
        input.ReleaseFocus();
        Assert.Contains(input.Evidence.Read().Records, r => r.Origin == "release-request" && r.Outcome == "cause-unavailable");
    }
}
