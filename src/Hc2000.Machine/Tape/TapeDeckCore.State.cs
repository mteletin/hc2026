using System.Collections.Immutable;
using System.Globalization;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Tape;

internal sealed partial class TapeDeckCore
{
    private static readonly string[] Modes = ["stopped", "playing", "recording", "rewinding", "fast-forwarding"];
    private static readonly string[] Holds = ["none", "format-stop", "physical-end", "fault"];
    internal TapeDeckState CaptureState()
    {
        var failure = RecorderFlushForCapture();
        if (failure is not null) throw new InvalidOperationException(failure.Detail);
        Sync(); Anchor();
        var recorder = RecorderState;
        var component = new TapeComponent2Dto(cassette is null ? null : TapeSnapshotContract.CassetteAssetId,
            (uint)active, TapeNativeCodec.Rational(position), Modes[(int)mode], paused, Holds[(int)hold], consumedStop,
            sides.Select(s => TapeNativeCodec.Decimal(s.CounterZeroTurns)).ToImmutableArray(),
            sides.Select(s => s.TabPresent).ToImmutableArray(), sides.Select(s => s.Dirty).ToImmutableArray(),
            sides.Select(s => TapeNativeCodec.Decimal(s.EditRevision)).ToImmutableArray(), sides.Select(s => TapeNativeCodec.Decimal(s.SavedRevision)).ToImmutableArray(),
            fastWind, TapeNativeCodec.Decimal(identity), anchorTick.ToString(CultureInfo.InvariantCulture), TapeNativeCodec.Rational(anchorPosition),
            ula.TapeIn, mic, recorder.Start is { } rs ? TapeNativeCodec.Rational(rs) : null,
            recorder.Head is { } rh ? TapeNativeCodec.Rational(rh) : null, recorder.Level,
            nextPosition is { } np ? TapeNativeCodec.Rational(np) : null, idealDue is { } due ? TapeNativeCodec.Rational(due) : null,
            (uint?)pendingKind, token.ToString(CultureInfo.InvariantCulture));
        return new(component, cassette);
    }
    internal TapeResult<TapeDeckCore> ValidateState(TapeDeckState state, long? now = null, DeviceEvent? pending = null, long? generation = null)
    {
        try
        {
            var c = state.Component;
            var candidate = new TapeDeckCore(scheduler, ula, deviceId, verifyOwner, persistenceCheck)
            {
                cassette = state.Cassette, position = TapeNativeCodec.Rational(c.Position), anchorPosition = TapeNativeCodec.Rational(c.AnchorPosition),
                anchorTick = checked((long)TapeNativeCodec.Unsigned(c.AnchorTick)), identity = TapeNativeCodec.Unsigned(c.MediaIdentity),
                active = (TapeSide)c.ActiveSide, paused = c.Paused, fastWind = c.FastWind, mic = c.MicLevel,
                consumedStop = c.ConsumedStop, token = checked((long)TapeNativeCodec.Unsigned(c.PendingToken)),
                nextPosition = c.NextPosition is null ? null : TapeNativeCodec.Rational(c.NextPosition),
                idealDue = c.IdealDue is null ? null : TapeNativeCodec.Rational(c.IdealDue), pendingKind = checked((int?)c.PendingKind)
            };
            int modeIndex = Array.IndexOf(Modes, c.Mode), holdIndex = Array.IndexOf(Holds, c.HoldReason);
            if (modeIndex < 0 || holdIndex < 0 || c.ActiveSide > 1) throw new ArgumentException("Deck mode/hold/side.");
            candidate.mode = (TapeDeckMode)modeIndex; candidate.hold = (TapeHoldReason)holdIndex;
            if (candidate.pendingKind == 103) candidate.windTarget = candidate.nextPosition;
            if (c.CounterZeroTurns.Length != 2 || c.Tabs.Length != 2 || c.Dirty.Length != 2 || c.EditRevision.Length != 2 || c.SavedRevision.Length != 2)
                throw new ArgumentException("Per-side state roster.");
            candidate.sides = [Side(0), Side(1)];
            TapeSideStatus Side(int i)
            {
                ulong edit = TapeNativeCodec.Unsigned(c.EditRevision[i]), saved = TapeNativeCodec.Unsigned(c.SavedRevision[i]);
                if (saved > edit || c.Dirty[i] != (edit != saved)) throw new ArgumentException("Dirty revision invariant.");
                return new(c.Tabs[i], c.Dirty[i], TapeNativeCodec.Unsigned(c.CounterZeroTurns[i]), edit, saved);
            }
            if (candidate.cassette is null)
            {
                if (c.CassetteAssetId is not null || candidate.position != default || candidate.active != TapeSide.A || c.Mode != "stopped" || c.Paused || c.HoldReason != "none"
                    || c.ConsumedStop is not null || c.WaveformLevel || candidate.sides.Any(s => s != new TapeSideStatus(true, false, 0, 0, 0))) throw new ArgumentException("Empty deck state.");
            }
            else
            {
                if (c.CassetteAssetId != TapeSnapshotContract.CassetteAssetId) throw new ArgumentException("Cassette reference.");
                // Native validation is detached and includes exact source extent/coverage/bounds.
                var encoded = TapeNativeCodec.Write(candidate.cassette, limits, TapeInputPolicy.DefaultLimits(TapeFormat.Wav));
                if (!encoded.Success) throw new ArgumentException(encoded.Failure!.Detail);
                var a = new TapeSignalIndexFactory().Build(candidate.cassette.A, limits);
                var b = new TapeSignalIndexFactory().Build(candidate.cassette.B, limits);
                if (!a.Success || !b.Success) throw new ArgumentException("Side index.");
                candidate.indexes = [a.Value, b.Value];
            }
            if (candidate.position > candidate.Duration || candidate.anchorPosition != candidate.position || now is { } n && candidate.anchorTick != n)
                throw new ArgumentException("Captured position/anchor invariant.");
            if (candidate.cassette is not null && candidate.sides.Any(s => s.CounterZeroTurns > Turns(candidate.Duration, candidate.cassette.Capacity)))
                throw new ArgumentException("Counter zero exceeds reel geometry.");
            if (candidate.mode == TapeDeckMode.Recording && !candidate.sides[(int)candidate.active].TabPresent)
                throw new ArgumentException("Recording with missing active tab.");
            if (!candidate.RecorderAdoptState(c.RecordStart is null ? null : TapeNativeCodec.Rational(c.RecordStart),
                    c.RecordHead is null ? null : TapeNativeCodec.Rational(c.RecordHead), c.RecordLevel))
                throw new ArgumentException("Recorder state invariant.");
            if (candidate.paused && candidate.mode is not (TapeDeckMode.Playing or TapeDeckMode.Recording) || candidate.hold == TapeHoldReason.PhysicalEnd && (candidate.mode != TapeDeckMode.Stopped || candidate.position != default && candidate.position != candidate.Duration)
                || candidate.hold == TapeHoldReason.FormatStop && candidate.mode != TapeDeckMode.Playing
                || candidate.hold == TapeHoldReason.Fault && candidate.mode != TapeDeckMode.Stopped
                || candidate.hold != TapeHoldReason.None && candidate.paused)
                throw new ArgumentException("Hold/pause interlock.");
            if (candidate.consumedStop is { } ordinal && (candidate.cassette is null || !candidate.indexes[(int)candidate.active].Image.Markers.Any(m => m.Ordinal == ordinal && m.Position <= candidate.position)))
                throw new ArgumentException("Consumed marker invariant.");
            if (candidate.hold == TapeHoldReason.FormatStop && !candidate.indexes[(int)candidate.active].Image.Markers.Any(m => m.Ordinal == candidate.consumedStop && m.Position == candidate.position))
                throw new ArgumentException("Format stop invariant.");
            bool motion = candidate.Rate != 0;
            bool expectedLevel = motion && candidate.mode == TapeDeckMode.Playing && candidate.position < candidate.Duration && candidate.indexes[(int)candidate.active].Sample(candidate.position);
            if (c.WaveformLevel != expectedLevel) throw new ArgumentException("Waveform invariant.");
            if (!motion)
            {
                if (candidate.token != 0 || c.NextPosition is not null || c.IdealDue is not null || c.PendingKind is not null || pending is not null)
                    throw new ArgumentException("Inactive deck owns no event.");
            }
            else
            {
                var plan = candidate.Plan();
                if (candidate.token <= 0 || candidate.nextPosition != plan.Position || candidate.pendingKind != plan.Kind || candidate.idealDue != plan.Due
                    || plan.Due.Ceiling() <= candidate.anchorTick) throw new ArgumentException("Pending logical boundary invariant.");
                if (pending is not null && (pending.DeviceId != deviceId || pending.Phase != EventPhase.SignalEdge || pending.Kind != plan.Kind
                    || pending.Payload != 0 || pending.DueTick != plan.Due.Ceiling() || pending.InsertionSequence != candidate.token || pending.Generation != generation))
                    throw new ArgumentException("Pending scheduler pairing.");
                if (now is not null && pending is null) throw new ArgumentException("Missing scheduler event.");
            }
            return TapeResult<TapeDeckCore>.Ok(candidate);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or InvalidOperationException or NullReferenceException)
        { return TapeResult<TapeDeckCore>.Fail(TapeError.SnapshotUnsupported, e.Message); }
    }
    private (TapeTime Position, int Kind, TapeTime Due) Plan()
    {
        TapeTime next = windTarget ?? (Rate < 0 ? default : Duration); int kind = windTarget is null ? 102 : 103;
        if (mode == TapeDeckMode.Playing)
        {
            next = indexes[(int)active].NextEdge(position) ?? Duration; kind = next == Duration ? 102 : 100;
            var marker = NextMarker(position);
            if (marker is not null && marker.Position == position) throw new ArgumentException("Unconsumed synchronous marker.");
            if (marker is not null && marker.Position < next) { next = marker.Position; kind = 101; }
        }
        var distance = Rate < 0 ? anchorPosition - next : next - anchorPosition;
        var due = new TapeTime(anchorTick, 1) + distance * (ulong)FrameTiming.MasterHz / (ulong)Math.Abs(Rate);
        return (next, kind, due);
    }
    internal void PreflightRebind()
    {
        Anchor(); if (Rate != 0) _ = Plan().Due.Ceiling();
    }
    internal void Rebind()
    {
        // Called only on a validated detached candidate. Preflight the new absolute deadline.
        Anchor(); if (Rate != 0) _ = Plan().Due.Ceiling();
        token = 0; nextPosition = idealDue = null; pendingKind = null;
        ScheduleNext();
    }
    internal void AdoptOutput() => ula.TapeIn = Rate != 0 && mode == TapeDeckMode.Playing && position < Duration && indexes[(int)active].Sample(position);
}
