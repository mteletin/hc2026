using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;

namespace Hc2000.Machine.Tape;

public enum TapeState { Unloaded, Stopped, Playing, AtEnd }
public enum TapeStage { Pilot, Sync1, Sync2, Data, TerminalHold, Pause, AtEnd }

/// <summary>Copied legacy TAP observation, not an HCS schema. Numerators use ReferenceHz.</summary>
public sealed record TapeCursor(TapeState State, int BlockIndex, TapeStage Stage,
    int PilotConsumed, int NextBit, int Half, bool WaveformLevel,
    Int128 IdealTargetNumerator, Int128 RemainingNumerator, long MediaIdentity, long Generation);

/// <summary>The single Machine-owned cassette device. Strict legacy TAP calls/cursor project
/// onto the SAME compact signal/deck implementation from the first PLAY; no parallel TAP
/// waveform engine, CPU clock or scheduler registration is created.</summary>
public sealed partial class TapeTransport : ITapeDeck
{
    public const long ReferenceHz = 3_500_000;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly DeviceScheduler scheduler;
    private readonly Ula ula;
    private readonly int deviceId;
    private Int128 target; // legacy stopped-cursor observation only, never a signal clock

    public TapeTransport(DeviceScheduler scheduler, Ula ula, int stableDeviceId)
    {
        this.scheduler = scheduler; this.ula = ula; deviceId = stableDeviceId;
        scheduler.RegisterDevice(deviceId, item => { VerifyOwner(); deck?.OnEvent(item); });
        ula.TapeIn = false;
    }
    internal int DeviceId => deviceId;
    public TapeCursor Cursor
    {
        get
        {
            VerifyOwner();
            return deck is not null ? DeckCursor() : new(TapeState.Unloaded, 0, TapeStage.AtEnd, 0, 0, 0,
                false, 0, 0, 0, scheduler.Generation(deviceId));
        }
    }
    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != owner)
            throw new InvalidOperationException("Tape commands belong to the emulation owner at a drained boundary.");
    }

    /// <summary>Strict legacy checksum-valid selection. All validation/copy/conversion finishes
    /// before adopting the candidate. Legacy clean PLAY replacement remains compatible;
    /// the new deck's insertion/button API enforces its explicit stopped interlocks.</summary>
    public void LoadTap(ReadOnlySpan<byte> bytes, int maxBytes = 64 * 1024 * 1024, int maxBlocks = 4096)
    {
        VerifyOwner();
        if (maxBytes > 64 * 1024 * 1024 || maxBlocks > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Legacy limits may only lower the v1 bounds.");
        var media = Tap.Load(bytes, maxBytes, maxBlocks).CopyRawBlocks();
        long oldIdentity = 0;
        if (deck is not null)
        {
            var view = deck.Observe();
            bool legacyPlaying = compatibilityFrames is not null && view.Mode == TapeDeckMode.Playing && !view.Paused;
            if (view.Mode != TapeDeckMode.Stopped && !legacyPlaying || view.Paused || view.A.Dirty || view.B.Dirty)
                throw new InvalidOperationException("STOP/save or explicitly discard cassette edits before legacy replacement.");
            oldIdentity = checked((long)view.MediaIdentity);
        }
        var identity = checked(oldIdentity + 1); var generation = scheduler.Generation(deviceId);
        var cursor = new TapeCursor(media.Count == 0 ? TapeState.AtEnd : TapeState.Stopped, 0,
            media.Count == 0 ? TapeStage.AtEnd : TapeStage.Pilot, 0, 0, 0, false, 0,
            media.Count == 0 ? 0 : (Int128)2168 * FrameTiming.MasterHz, identity, generation);
        var migrated = Migrate(new(cursor, [new("tape-source-0", TapeFormat.Tap, TapeLegacyMigration.RawTap(media))],
            scheduler.Now, deviceId, generation, null), new());
        if (!migrated.Success) throw new InvalidDataException(migrated.Failure!.Detail);
        var candidate = migrated.Value with { Component = migrated.Value.Component with { MicLevel = ula.Beeper.Microphone } };
        var restored = Restore(candidate);
        if (!restored.Success) throw new InvalidDataException(restored.Failure!.Detail);
        target = 0;
    }
    public void Play()
    {
        VerifyOwner(); if (deck is null) return;
        if (compatibilityFrames is not null && DeckCursor().State == TapeState.AtEnd) return;
        ((ITapeDeck)this).Play();
    }
    public void Stop()
    {
        VerifyOwner();
        if (deck is not null) { target = DeckCursor().IdealTargetNumerator; deck.StopOnly(); }
        else ula.TapeIn = false;
    }
    public void Eject()
    {
        VerifyOwner(); if (deck is null) return;
        var view = deck.Observe();
        if (view.A.Dirty || view.B.Dirty) throw new InvalidOperationException("Confirm discarding dirty cassette edits via the deck API.");
        deck.StopOnly(); deck.StopEject(false); compatibilityFrames = null;
    }
    /// <summary>CPU reset preserves external head/latches and exact residual. A replacement
    /// worker uses the same detached Capture/Restore contract and applies its current MIC pin.</summary>
    public void Reset()
    {
        VerifyOwner(); if (deck is null) return;
        var captured = Capture();
        if (!captured.Success) throw new InvalidOperationException(captured.Failure!.Detail);
        var restored = Restore(captured.Value);
        if (!restored.Success) throw new InvalidOperationException(restored.Failure!.Detail);
    }
}
