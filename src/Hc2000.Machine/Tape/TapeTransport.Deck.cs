using Hc2000.Machine.Tape.Snapshots;

namespace Hc2000.Machine.Tape;

public sealed partial class TapeTransport
{
    private TapeDeckCore? deck;
    internal Func<TapeCassetteImage, TapeFailure?>? PersistenceCheck { get; set; }
    private TapeFailure? CheckPersistence(TapeCassetteImage candidate) => PersistenceCheck is { } check ? check(candidate)
        : candidate.SourceAssets.Any(s => s.Bytes.Length > new TapeLimits().InputBytes)
            ? new(TapeError.BoundsExceeded, "No composed snapshot admission for a large original.") : null;
    internal event Action<TapeDeckView>? DeckStateChanged;
    private void NotifyDeckStateChanged(TapeDeckView view) => DeckStateChanged?.Invoke(view);
    public ITapeDeck Deck => this;
    private TapeDeckCore Core
    {
        get
        {
            VerifyOwner();
            return deck ??= new(scheduler, ula, deviceId, VerifyOwner, CheckPersistence) { StateChanged = NotifyDeckStateChanged };
        }
    }
    private TapeResult<T> Command<T>(Func<TapeResult<T>> command) where T : notnull
    {
        VerifyOwner();
        try { return command(); }
        catch (OverflowException e) { return TapeResult<T>.Fail(TapeError.BoundsExceeded, e.Message); }
    }
    TapeDeckView ITapeDeck.Observe() => Core.Observe();
    internal TapeResult<TapeDeckView> WindTo(bool reverse, TapeTime target) => Command(() => Core.WindTo(reverse, target));
    /// <summary>The active side's signal, read-only (spec 14 gap and block scan).</summary>
    public ITapeSignalIndex? ActiveIndex => Core.ActiveIndex;
    TapeResult<TapeDeckView> ITapeDeck.Insert(TapeCassetteImage cassette, bool discardDirty) => Command(() => Core.Insert(cassette, discardDirty));
    TapeResult<TapeDeckView> ITapeDeck.Blank(TapeCapacity capacity, bool discardDirty) => Command(() => Core.Blank(capacity, discardDirty));
    TapeResult<TapeDeckView> ITapeDeck.Flip() => Command(() => Core.Flip());
    TapeResult<TapeDeckView> ITapeDeck.SetTab(TapeSide side, bool present) => Command(() => Core.SetTab(side, present));
    TapeResult<TapeDeckView> ITapeDeck.Play() => Command(() => Core.Play());
    TapeResult<TapeDeckView> ITapeDeck.RecordPlay() => Command(() => Core.RecordPlay());
    TapeResult<TapeDeckView> ITapeDeck.Rewind() => Command(() => Core.Rewind());
    TapeResult<TapeDeckView> ITapeDeck.FastForward() => Command(() => Core.FastForward());
    TapeResult<TapeDeckView> ITapeDeck.StopEject(bool discardDirty) => Command(() => Core.StopEject(discardDirty));
    TapeResult<TapeDeckView> ITapeDeck.Pause() => Command(() => Core.Pause());
    TapeResult<TapeDeckView> ITapeDeck.ResetCounter() => Command(() => Core.ResetCounter());
    TapeResult<TapeDeckView> ITapeDeck.SetFastWind(bool enabled) => Command(() => Core.SetFastWind(enabled));
    public void OnMicChanged(long masterTick, bool microphoneLevel)
    {
        VerifyOwner();
        // Observing the pin must not convert a legacy transport or manufacture REC capability.
        if (deck is not null) deck.OnMicChanged(masterTick, microphoneLevel);
    }
    TapeResult<TapeExportSnapshot> ITapeDeck.Export(TapeSide side) => Command(() => Core.Export(side));
    TapeResult<TapeDeckView> ITapeDeck.AcknowledgeSave(TapeSaveAcknowledgement acknowledgement) => Command(() => Core.AcknowledgeSave(acknowledgement));
}
