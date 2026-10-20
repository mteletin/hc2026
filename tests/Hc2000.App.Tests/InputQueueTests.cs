using System.Collections.Concurrent;
using Hc2000.App.Input;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class InputQueueTests
{
    private static bool Down(KeyboardMatrix matrix, HcKey key)
        => (matrix.Read((ushort)(0xffff ^ (1 << ((int)key / 5 + 8)))) & (1 << ((int)key % 5))) == 0;
    private static void Hold(InputQueue queue, string source, HcKey key, bool down = true)
        => queue.Enqueue(new KeyTransition(source, new KeyChord(key), down));

    [Fact]
    public void NoMatrixMutationBeforeBoundaryAndCommandsShareControlOrder()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix();
        var observed = new List<bool>();
        var controller = new InputController(queue, (_, _) => observed.Add(Down(matrix, HcKey.A)));
        Hold(queue, "a", HcKey.A);
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        Hold(queue, "a", HcKey.A, false);
        var last = queue.Enqueue(new RuntimeControl(InputControlKind.Resume));
        Assert.False(Down(matrix, HcKey.A));
        controller.ApplyPending(123, matrix);
        Assert.Equal(new[] { true, false }, observed);
        Assert.Equal(last, controller.LastAppliedSequence);
    }

    [Fact]
    public void LogicalHoldsFromOverlappingSourcesAndPasteAreReferenceCounted()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix();
        var controller = new InputController(queue);
        Hold(queue, "left", HcKey.Control); Hold(queue, "right", HcKey.Control);
        controller.ApplyPending(0, matrix);
        Hold(queue, "left", HcKey.Control, false);
        queue.Enqueue(new PasteText("\u0001", 10));
        controller.ApplyPending(1, matrix);
        Assert.True(Down(matrix, HcKey.CapsShift)); Assert.True(Down(matrix, HcKey.SymbolShift));
        Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(11, matrix);
        Assert.False(Down(matrix, HcKey.A)); Assert.True(Down(matrix, HcKey.SymbolShift));
        Hold(queue, "right", HcKey.Control, false); controller.ApplyPending(12, matrix);
        Assert.False(Down(matrix, HcKey.SymbolShift)); Assert.False(Down(matrix, HcKey.CapsShift));
    }

    [Fact]
    public void SourceReplacementAndRepeatCannotLeakHeldModifiers()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        queue.Enqueue(new KeyTransition("one", new KeyChord(HcKey.CapsShift, HcKey.A), true));
        queue.Enqueue(new KeyTransition("one", new KeyChord(HcKey.CapsShift, HcKey.A), true));
        Hold(queue, "one", HcKey.B); controller.ApplyPending(0, matrix);
        Assert.False(Down(matrix, HcKey.A)); Assert.False(Down(matrix, HcKey.CapsShift));
        Assert.True(Down(matrix, HcKey.B));
        Hold(queue, "one", HcKey.Space, false); controller.ApplyPending(1, matrix);
        Assert.False(Down(matrix, HcKey.B));
    }

    [Theory]
    [InlineData(InputControlKind.Reset)]
    [InlineData(InputControlKind.Load)]
    [InlineData(InputControlKind.Shutdown)]
    public void LifecycleReleaseCancelsPasteBeforeCallback(InputControlKind kind)
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix();
        var released = false;
        var controller = new InputController(queue, (_, _) => released = matrix.Read(0x00fe) == 31);
        Hold(queue, "a", HcKey.A); queue.Enqueue(new PasteText("bc", 10)); controller.ApplyPending(0, matrix);
        queue.Enqueue(new PasteText("de", 10)); queue.Enqueue(new RuntimeControl(kind)); controller.ApplyPending(1, matrix);
        Assert.True(released); Assert.False(controller.IsPasting);
        controller.ApplyPending(1000, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        // Cancelled active + queued paste reservations were returned.
        for (var i = 0; i < 4; i++) queue.Enqueue(new PasteText(new string('x', 4096)));
    }

    [Fact]
    public void FocusLossExplicitlyReleasesAllAndCancelsPaste()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        Hold(queue, "left", HcKey.CapsShift); queue.Enqueue(new PasteText("a", 10)); controller.ApplyPending(0, matrix);
        queue.Enqueue(new ReleaseAll()); controller.ApplyPending(1, matrix);
        Assert.Equal(31, matrix.Read(0x00fe)); Assert.False(controller.IsPasting);
    }

    [Fact]
    public void ConcurrentProducersHaveOneLinearizedSequenceAndNoLostControls()
    {
        var queue = new InputQueue(); var sequences = new ConcurrentDictionary<string, long>();
        Parallel.For(0, 1000, i =>
        {
            var name = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sequences[name] = queue.Enqueue(new RuntimeControl(InputControlKind.Pause, name));
        });
        var seen = new List<string>();
        var controller = new InputController(queue, (cmd, _) => seen.Add(cmd.Path!));
        controller.ApplyPending(0, new KeyboardMatrix());
        Assert.Equal(sequences.OrderBy(p => p.Value).Select(p => p.Key), seen);
        Assert.Equal(1000, controller.LastAppliedSequence);
    }

    [Fact]
    public void CommandsEnqueuedDuringBoundaryWaitForNextBoundary()
    {
        var queue = new InputQueue(); var seen = 0;
        var controller = new InputController(queue, (_, _) => { seen++; queue.Enqueue(new RuntimeControl(InputControlKind.Pause)); });
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        controller.ApplyPending(0, new KeyboardMatrix()); Assert.Equal(1, seen);
        controller.ApplyPending(0, new KeyboardMatrix()); Assert.Equal(2, seen);
    }

    [Fact]
    public void CallbackFailureDoesNotDropRemainingKeyRelease()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix();
        var controller = new InputController(queue, (_, _) => throw new InvalidOperationException("fake control failure"));
        Hold(queue, "a", HcKey.A); queue.Enqueue(new RuntimeControl(InputControlKind.Pause)); Hold(queue, "a", HcKey.A, false);
        Assert.Throws<InvalidOperationException>(() => controller.ApplyPending(0, matrix));
        Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(0, matrix); Assert.False(Down(matrix, HcKey.A));
    }

    [Fact]
    public void ConsumptionBindsToOneOwnerThread()
    {
        var controller = new InputController(new InputQueue()); var matrix = new KeyboardMatrix();
        controller.ApplyPending(0, matrix);
        Exception? error = null;
        var thread = new Thread(() => { try { controller.ApplyPending(0, matrix); } catch (Exception ex) { error = ex; } });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void PastePressReleaseAndCrLfOrderUsePositiveMasterTickInterval()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        queue.Enqueue(new PasteText("a\r\nb", 10));
        controller.ApplyPending(100, matrix); Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(109, matrix); Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(110, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        controller.ApplyPending(119, matrix); Assert.False(Down(matrix, HcKey.Enter));
        controller.ApplyPending(120, matrix); Assert.True(Down(matrix, HcKey.Enter));
        controller.ApplyPending(130, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        controller.ApplyPending(140, matrix); Assert.True(Down(matrix, HcKey.B));
        controller.ApplyPending(150, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        Assert.False(controller.IsPasting);
    }

    [Fact]
    public void MultiplePasteRequestsKeepReleaseGapAndFifo()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        queue.Enqueue(new PasteText("a", 10)); queue.Enqueue(new PasteText("b", 20));
        controller.ApplyPending(0, matrix); Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(10, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        controller.ApplyPending(11, matrix); Assert.Equal(31, matrix.Read(0x00fe));
        controller.ApplyPending(20, matrix); Assert.True(Down(matrix, HcKey.B));
        controller.ApplyPending(39, matrix); Assert.True(Down(matrix, HcKey.B));
        controller.ApplyPending(40, matrix); Assert.False(controller.IsPasting);
    }

    [Fact]
    public void LateBoundariesDoNotBurstOrTypeWhilePausedAndRestoreRebases()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        queue.Enqueue(new PasteText("aa", 10)); controller.ApplyPending(100, matrix);
        for (var i = 0; i < 100; i++) controller.ApplyPending(100, matrix);
        Assert.True(Down(matrix, HcKey.A)); // Paused: no wall-clock progress.
        controller.ApplyPending(10000, matrix); Assert.False(Down(matrix, HcKey.A));
        controller.ApplyPending(10001, matrix); Assert.False(Down(matrix, HcKey.A));
        controller.ApplyPending(10010, matrix); Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(0, matrix); Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(9, matrix); Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(10, matrix); Assert.False(Down(matrix, HcKey.A));
    }

    [Fact]
    public void PasteValidationAndAggregateCapacityAreExplicitAndReusable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasteText("a", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasteText("a", -1));
        Assert.Throws<ArgumentException>(() => new PasteText("€"));
        Assert.Throws<ArgumentException>(() => new PasteText(new string('a', 4097)));
        var queue = new InputQueue();
        for (var i = 0; i < 4; i++) queue.Enqueue(new PasteText(new string('a', 4096)));
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(new PasteText("a")));
        queue.Enqueue(new ReleaseAll()); new InputController(queue).ApplyPending(0, new KeyboardMatrix());
        queue.Enqueue(new PasteText(new string('a', 4096)));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: 300)));
    }

    [Fact]
    public void WorkerCleanupReleasesEvenWhenQueuedFocusReleaseHasNotBeenConsumed()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        Hold(queue, "shift", HcKey.CapsShift); queue.Enqueue(new PasteText("ab", 10)); controller.ApplyPending(0, matrix);
        queue.Enqueue(new ReleaseAll()); // Cancellation beat the next paused/running boundary.
        controller.ReleaseOwned(matrix);
        Assert.Equal(31, matrix.Read(0x00fe)); Assert.False(controller.IsPasting);
        Exception? error = null;
        var thread = new Thread(() => { try { controller.ReleaseOwned(matrix); } catch (Exception ex) { error = ex; } });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void MissingLifecycleConsumerFailsExplicitly()
    {
        var queue = new InputQueue(); queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        Assert.Throws<InvalidOperationException>(() => new InputController(queue).ApplyPending(0, new KeyboardMatrix()));
    }

    [Fact]
    public void UiArrivalDuringInstructionDoesNotApplyUntilCompletedBoundary()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        controller.ApplyPending(0, matrix);
        // A fake instruction's bus callback receives an external UI transition, but never consumes it.
        Hold(queue, "a", HcKey.A);
        Assert.False(Down(matrix, HcKey.A));
        var completedInstructionTick = 35L;
        controller.ApplyPending(completedInstructionTick, matrix);
        Assert.True(Down(matrix, HcKey.A));
        Assert.Equal(1, controller.LastAppliedSequence);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(0)]
    public void HostSpeedControlsDoNotChangePasteMasterTickSchedule(int speed)
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var chosenSpeed = -1;
        var controller = new InputController(queue, (cmd, _) => chosenSpeed = cmd.SpeedPercent);
        queue.Enqueue(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: speed));
        queue.Enqueue(new PasteText("a", 10)); controller.ApplyPending(100, matrix);
        Assert.Equal(speed, chosenSpeed);
        controller.ApplyPending(109, matrix); Assert.True(Down(matrix, HcKey.A));
        controller.ApplyPending(110, matrix); Assert.False(Down(matrix, HcKey.A));
    }

    [Fact]
    public void EmptyPasteCompletesWithoutAKeyOrReservation()
    {
        var queue = new InputQueue(); var matrix = new KeyboardMatrix(); var controller = new InputController(queue);
        queue.Enqueue(new PasteText("")); controller.ApplyPending(0, matrix);
        Assert.False(controller.IsPasting); Assert.Equal(31, matrix.Read(0x00fe));
    }
}
