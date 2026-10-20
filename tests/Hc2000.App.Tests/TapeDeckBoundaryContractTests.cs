using Hc2000.App.Media;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Uses existing constructors and declared foundation types, so this runs against
/// the compiling core artifact BEFORE App dispatch exists (not a compile-only red).</summary>
public sealed class TapeDeckBoundaryContractTests
{
    [Theory]
    [InlineData("StopEjectTape")]
    [InlineData("BlankTape")]
    public void DirtyCancelRoutesThroughOwnerWithoutDiscardingSignal(string actionName)
    {
        var f = new RuntimeTests.Fixture(); var tape = f.Machine.EnableTape(3);
        var deck = Assert.IsAssignableFrom<ITapeDeck>(tape);
        Assert.True(deck.Blank(TapeCapacity.C60, false).Success);
        var transfer = Assert.IsAssignableFrom<Hc2000.Machine.Tape.Snapshots.ITapeDeckStateTransfer>(tape);
        var saved = transfer.Capture().Value;
        Assert.True(transfer.Restore(saved with { Component = saved.Component with
            { Dirty = [true, false], EditRevision = ["1", "0"], SavedRevision = ["0", "0"] } }).Success);
        var before = deck.Observe();
        var session = new MediaSession(new(new()), new(new()), tape);
        Assert.True(Enum.TryParse<MediaAction>(actionName, out var action), $"Missing owner dispatch: {actionName}");
        var reply = session.Apply(new(action));
        Assert.Contains("DirtyConfirmationRequired", reply.Error);
        Assert.Equal(before, deck.Observe());
        Assert.True(deck.Observe().Present);
    }

    [Fact]
    public void StoppedExportCarriesIdentitySideAndRevisionButDoesNotAcknowledgeSave()
    {
        var f = new RuntimeTests.Fixture(); var tape = f.Machine.EnableTape(3);
        var deck = Assert.IsAssignableFrom<ITapeDeck>(tape); deck.Blank(TapeCapacity.C60, false);
        var transfer = Assert.IsAssignableFrom<Hc2000.Machine.Tape.Snapshots.ITapeDeckStateTransfer>(tape);
        var saved = transfer.Capture().Value;
        Assert.True(transfer.Restore(saved with { Component = saved.Component with
            { Dirty = [true, false], EditRevision = ["1", "0"], SavedRevision = ["0", "0"] } }).Success);
        var session = new MediaSession(new(new()), new(new()), tape);
        Assert.True(Enum.TryParse<MediaAction>("ExportTape", out var action), "Missing export request; host cancellation must not mark a side saved.");
        var reply = session.Apply(new(action));
        var property = typeof(MediaReply).GetProperty("TapeExport"); Assert.NotNull(property);
        var snapshot = Assert.IsType<TapeExportSnapshot>(property.GetValue(reply));
        Assert.Equal(deck.Observe().MediaIdentity, snapshot.MediaIdentity);
        Assert.Equal(TapeSide.A, snapshot.Side); Assert.Equal(1ul, snapshot.EditRevision);
        // User cancels the host save picker/write here: no acknowledgement is sent.
        Assert.True(deck.Observe().A.Dirty); Assert.Equal(0ul, deck.Observe().A.SavedRevision);
    }
}
