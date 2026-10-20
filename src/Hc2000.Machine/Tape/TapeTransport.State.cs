using Hc2000.Machine.Tape.Snapshots;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Tape;

public sealed partial class TapeTransport : ITapeDeckStateTransfer
{
    public ITapeDeckStateTransfer StateTransfer => this;
    internal bool CanAdoptHcs => deck is null || !deck.Observe().Present;
    public TapeResult<TapeDeckState> Capture()
    {
        VerifyOwner();
        try
        {
            var state = Core.CaptureState();
            if (state.Component.MicLevel != ula.Beeper.Microphone || state.Component.MicLevel != ((ula.FeLatch & 8) != 0))
                return TapeResult<TapeDeckState>.Fail(TapeError.SnapshotUnsupported, "MIC ingress disagrees with the ULA pin.");
            var validated = Core.ValidateState(state, scheduler.Now,
                scheduler.PendingEvents.SingleOrDefault(e => e.DeviceId == deviceId), scheduler.Generation(deviceId));
            return validated.Success ? TapeResult<TapeDeckState>.Ok(state)
                : TapeResult<TapeDeckState>.Fail(validated.Failure!.Code, validated.Failure.Detail);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or InvalidOperationException)
        { return TapeResult<TapeDeckState>.Fail(TapeError.SnapshotUnsupported, e.Message); }
    }
    public TapeResult<TapeDeckView> Restore(TapeDeckState state)
    {
        VerifyOwner();
        var validated = ValidateRestoredState(state);
        if (!validated.Success) return TapeResult<TapeDeckView>.Fail(validated.Failure!.Code, validated.Failure.Detail);
        var candidate = validated.Value;
        if (state.Cassette is { } cassette && cassette.SourceAssets.Any(s => s.Format == TapeFormat.Wav)
            && CheckPersistence(cassette) is { } persistenceFailure)
            return TapeResult<TapeDeckView>.Fail(persistenceFailure.Code, persistenceFailure.Detail);
        var frames = LegacyFrames(candidate);
        try { candidate.PreflightRebind(); _ = scheduler.NextSequence; _ = checked(scheduler.Generation(deviceId) + 1); }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        { return TapeResult<TapeDeckView>.Fail(TapeError.BoundsExceeded, e.Message); }
        // All validation/preflight finishes before invalidating the one live device.
        CancelAllTapeEvents(); deck = candidate; compatibilityFrames = frames;
        candidate.StateChanged = NotifyDeckStateChanged;
        candidate.Rebind(); candidate.AdoptOutput(); NotifyDeckStateChanged(candidate.Observe());
        return TapeResult<TapeDeckView>.Ok(candidate.Observe());
    }
    internal TapeResult<TapeDeckCore> ValidateRestoredState(TapeDeckState state, long? now = null, DeviceEvent? pending = null, long? generation = null)
    {
        VerifyOwner();
        return new TapeDeckCore(scheduler, ula, deviceId, VerifyOwner, CheckPersistence).ValidateState(state, now, pending, generation);
    }
    internal void AdoptValidatedState(TapeDeckCore candidate)
    {
        VerifyOwner(); deck = candidate; compatibilityFrames = LegacyFrames(candidate);
        candidate.StateChanged = NotifyDeckStateChanged;
        candidate.AdoptOutput(); NotifyDeckStateChanged(candidate.Observe());
    }
    private void CancelAllTapeEvents()
    {
        _ = checked(scheduler.Generation(deviceId) + 1);
        foreach (var item in scheduler.PendingEvents.Where(e => e.DeviceId == deviceId)) scheduler.Cancel(item.InsertionSequence);
        scheduler.InvalidateDevice(deviceId);
    }
    public TapeResult<TapeDeckState> Migrate(Tape1MigrationInput input, TapeLimits limits)
    {
        VerifyOwner(); return TapeLegacyMigration.Migrate(input, limits);
    }
}
