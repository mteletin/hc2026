using System.Collections.Immutable;

namespace Hc2000.Machine.Tape;

public enum TapeDeckMode { Stopped, Playing, Recording, Rewinding, FastForwarding }
public enum TapeHoldReason { None, FormatStop, PhysicalEnd, Fault }
public sealed record TapeDeckCapabilities(bool CanPlay, bool CanRecord, bool CanWind,
    bool CanCaptureRestore, string? UnavailableReason);
public sealed record TapeSideStatus(bool TabPresent, bool Dirty, ulong CounterZeroTurns,
    ulong EditRevision, ulong SavedRevision);

/// <summary>Detached observation, never a live Machine/ULA reference. Core computes display values.</summary>
public sealed record TapeDeckView(bool Present, TapeSide ActiveSide, TapeCapacity? Capacity,
    TapeDeckMode Mode, bool Paused, TapeHoldReason HoldReason, TapeTime Position, TapeTime Duration,
    uint Counter, ulong ElapsedSeconds, TapeSideStatus A, TapeSideStatus B, bool FastWind,
    bool EarLevel, bool MicLevel, ulong MediaIdentity, TapeDeckCapabilities Capabilities);
public sealed record TapeExportSnapshot(ulong MediaIdentity, TapeSide Side, ulong EditRevision,
    TapeSideImage Image, ImmutableArray<TapeSourceAsset> SourceAssets);
public sealed record TapeSaveAcknowledgement(ulong MediaIdentity, TapeSide Side, ulong EditRevision,
    TapeSignalFidelity SignalFidelity, TapeByteFidelity ByteFidelity, TapeFormat Target);

/// <summary>Owner-boundary contract for the existing transport's future deck extension.
/// No implementation, scheduler registration, composition, capture/restore or MIC behavior is
/// introduced here. Absent capability must be disabled/refused, never reported as success.</summary>
public interface ITapeDeck
{
    TapeDeckView Observe();
    TapeResult<TapeDeckView> Insert(TapeCassetteImage cassette, bool discardDirty);
    TapeResult<TapeDeckView> Blank(TapeCapacity capacity, bool discardDirty);
    TapeResult<TapeDeckView> Flip();
    TapeResult<TapeDeckView> SetTab(TapeSide side, bool present);
    TapeResult<TapeDeckView> Play();
    TapeResult<TapeDeckView> RecordPlay();
    TapeResult<TapeDeckView> Rewind();
    TapeResult<TapeDeckView> FastForward();
    TapeResult<TapeDeckView> StopEject(bool discardDirty);
    TapeResult<TapeDeckView> Pause();
    TapeResult<TapeDeckView> ResetCounter();
    TapeResult<TapeDeckView> SetFastWind(bool enabled);
    void OnMicChanged(long masterTick, bool microphoneLevel);
    TapeResult<TapeExportSnapshot> Export(TapeSide side);
    TapeResult<TapeDeckView> AcknowledgeSave(TapeSaveAcknowledgement acknowledgement);
}
