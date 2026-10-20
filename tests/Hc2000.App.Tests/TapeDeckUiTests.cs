using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class TapeDeckUiTests
{
    [AvaloniaFact]
    public async Task ActualControlsExposeCapabilitiesAndRequireExplicitManualMotion()
    {
        var f = new RuntimeTests.Fixture(); var tape = f.Machine.EnableTape(3);
        var session = new MediaSession(new(new()), new(new()), tape, manualDeck: true);
        var panel = new TapeDeckPanel();
        panel.Attach(command => Task.FromResult(session.Apply(command)));
        panel.Observe(session.Apply(new(MediaAction.Inspect)).Tape);
        Button Button(string text) => Assert.Single(panel.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, text));
        Assert.True(Button("Insert blank").IsEnabled);
        Assert.False(Button("PLAY").IsEnabled);
        await panel.CommandAsync(MediaAction.BlankTape);
        Assert.True(Button("PLAY").IsEnabled);
        Assert.True(Button("REC + PLAY").IsEnabled); // blank side, tab in: the recorder may arm
        Assert.True(Button("Export active side…").IsEnabled); // every writer is installed
        Assert.True(Button("Save cassette A + B as two TZX files…").IsEnabled);
        Assert.Contains("000", panel.Status); Assert.Contains("00:00", panel.Status);
        Assert.Contains("source Blank", panel.Status);
        Assert.DoesNotContain("Recording capability is not installed", panel.Status, StringComparison.OrdinalIgnoreCase);
        await panel.CommandAsync(MediaAction.PlayTape);
        Assert.False(Button("REW").IsEnabled); Assert.False(Button("Flip side").IsEnabled);
        Assert.True(Button("PAUSE").IsEnabled);
        await panel.CommandAsync(MediaAction.StopEjectTape);
        Assert.True(tape.Deck.Observe().Present);
        Assert.True(Button("Flip side").IsEnabled);
        await panel.CommandAsync(MediaAction.StopEjectTape);
        Assert.False(tape.Deck.Observe().Present);
    }

    [AvaloniaFact]
    public async Task DirtyCloseAndEjectCancellationKeepCassetteUntilExplicitDiscard()
    {
        var f = new RuntimeTests.Fixture(); var tape = f.Machine.EnableTape(3);
        var session = new MediaSession(new(new()), new(new()), tape, manualDeck: true);
        session.Apply(new(MediaAction.BlankTape));
        var initial = tape.Capture().Value;
        Assert.True(tape.Restore(initial with { Component = initial.Component with
            { Dirty = [true, false], EditRevision = ["1", "0"], SavedRevision = ["0", "0"] } }).Success);
        var panel = new TapeDeckPanel(); panel.Attach(command => Task.FromResult(session.Apply(command)));
        panel.Observe(session.Apply(new(MediaAction.Inspect)).Tape);
        Assert.False(panel.CanCloseSafely);
        await panel.CommandAsync(MediaAction.StopEjectTape);
        Assert.Contains("DirtyConfirmationRequired", panel.Notice);
        Assert.True(tape.Deck.Observe().Present); Assert.True(tape.Deck.Observe().A.Dirty);
        var discard = Assert.Single(panel.GetLogicalDescendants().OfType<CheckBox>(), c => Equals(c.Content, "Explicitly discard dirty cassette on replacement/eject"));
        discard.IsChecked = true;
        Assert.True(panel.CanCloseSafely);
        await panel.CommandAsync(MediaAction.StopEjectTape);
        Assert.False(tape.Deck.Observe().Present);
    }

    [Fact]
    public void RouteChoicesAreExplicitAndInvalidChoicesNeverReachOwner()
    {
        Assert.Empty(TapeDeckPanel.ParseSelections(null));
        Assert.Equal(new[] { new Hc2000.Machine.Tape.TapeSelection(1, 2), new(3, 0) }, TapeDeckPanel.ParseSelections("1:2, 3:0"));
        Assert.Throws<InvalidDataException>(() => TapeDeckPanel.ParseSelections("1:bad"));
        Assert.Throws<InvalidDataException>(() => TapeDeckPanel.ParseSelections("-1:0"));
    }
}
