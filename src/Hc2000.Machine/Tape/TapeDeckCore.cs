using System.Collections.Immutable;
using System.Numerics;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;

namespace Hc2000.Machine.Tape;

/// <summary>Mechanics for the existing transport. It registers no device and owns no second clock.
/// Recording edits the same indexed signal from the actual MIC pin.</summary>
internal sealed partial class TapeDeckCore : ITapeDeck
{
    private readonly DeviceScheduler scheduler;
    private readonly Ula ula;
    private readonly int deviceId;
    private readonly Action verifyOwner;
    private readonly Func<TapeCassetteImage, TapeFailure?>? persistenceCheck;
    private readonly TapeLimits limits = new();
    private TapeCassetteImage? cassette;
    private ITapeSignalIndex[] indexes = [];
    private TapeSide active;
    private TapeDeckMode mode;
    private TapeHoldReason hold;
    private bool paused, fastWind, mic;
    private TapeTime position, anchorPosition;
    private long anchorTick, token;
    private ulong identity;
    private uint? consumedStop;
    private TapeTime? nextPosition, idealDue;
    private int? pendingKind;
    /// <summary>Skip (spec 14 commands 07/08): wind stops here with no hold. Pending kind 103, so it rides the snapshot.</summary>
    private TapeTime? windTarget;
    internal ITapeSignalIndex? ActiveIndex => cassette is null ? null : indexes[(int)active];
    private TapeSideStatus[] sides = [new(true, false, 0, 0, 0), new(true, false, 0, 0, 0)];
    private TapeTime Duration => cassette is null ? default : indexes[(int)active].Image.Duration;
    internal TapeCassetteImage? Cassette => cassette;
    internal Action<TapeDeckView>? StateChanged { get; set; }
    internal TapeDeckCore(DeviceScheduler scheduler, Ula ula, int deviceId, Action verifyOwner,
        Func<TapeCassetteImage, TapeFailure?>? persistenceCheck = null)
    { this.scheduler = scheduler; this.ula = ula; this.deviceId = deviceId; this.verifyOwner = verifyOwner; this.persistenceCheck = persistenceCheck; anchorTick = scheduler.Now; mic = ula.Beeper.Microphone; }
    private int Rate => mode is TapeDeckMode.Stopped || paused || hold != TapeHoldReason.None ? 0
        : mode == TapeDeckMode.Rewinding ? -(fastWind ? 120 : 10)
        : mode == TapeDeckMode.FastForwarding ? fastWind ? 120 : 10 : 1;
    private TapeTime CurrentPosition()
    {
        var elapsed = new TapeTime(scheduler.Now - anchorTick, FrameTiming.MasterHz) * (ulong)Math.Abs(Rate);
        if (Rate < 0) return elapsed >= anchorPosition ? default : anchorPosition - elapsed;
        var value = anchorPosition + elapsed; return value > Duration ? Duration : value;
    }
    private void Sync() { verifyOwner(); position = CurrentPosition(); }
    private void Anchor() { anchorTick = scheduler.Now; anchorPosition = position; }
    private void Cancel()
    {
        _ = checked(scheduler.Generation(deviceId) + 1);
        if (token != 0) scheduler.Cancel(token);
        token = 0; nextPosition = idealDue = null; pendingKind = null; windTarget = null;
        scheduler.InvalidateDevice(deviceId);
    }
    public TapeDeckView Observe()
    {
        Sync();
        var n = Turns(position, cassette?.Capacity ?? TapeCapacity.C60);
        var zero = sides[(int)active].CounterZeroTurns;
        uint counter = (uint)((n % 1000 + 1000 - zero % 1000) % 1000);
        return new(cassette is not null, active, cassette?.Capacity, mode, paused, hold, position, Duration,
            counter, position.Floor(), sides[0], sides[1], fastWind, ula.TapeIn, mic, identity,
            new(true, true, true, true, recordFailure?.Detail));
    }
    internal static ulong Turns(TapeTime p, TapeCapacity capacity)
    {
        // Multiply 2*pi*r0*n + pi*h*n^2 <= v*p by a common denominator.
        BigInteger den = (BigInteger)p.Denominator;
        BigInteger right = (BigInteger)p.Numerator * 476 * 113 * 1_000_000;
        BigInteger left(ulong n) => (710 * 11000 * (BigInteger)n + 355 * (capacity == TapeCapacity.C60 ? 18 : 12) * (BigInteger)n * n) * 10000 * den;
        ulong lo = 0, hi = 1;
        while (left(hi) <= right) hi = checked(hi * 2);
        while (lo + 1 < hi) { ulong mid = lo + (hi - lo) / 2; if (left(mid) <= right) lo = mid; else hi = mid; }
        return lo;
    }
    private void NotifyStateChanged() => StateChanged?.Invoke(Observe());
    private TapeResult<TapeDeckView> Ok()
    {
        var view = Observe(); StateChanged?.Invoke(view);
        return TapeResult<TapeDeckView>.Ok(view);
    }
    private static TapeResult<TapeDeckView> Fail(TapeError error, string detail) => TapeResult<TapeDeckView>.Fail(error, detail);
    private bool Idle => mode == TapeDeckMode.Stopped && !paused;
    private bool Dirty => sides.Any(s => s.Dirty);
    public TapeResult<TapeDeckView> Insert(TapeCassetteImage candidate, bool discardDirty)
    {
        Sync();
        if (!Idle) return Fail(TapeError.Interlock, "STOP before inserting.");
        if (Dirty && !discardDirty) return Fail(TapeError.DirtyConfirmationRequired, "Current cassette has unsaved edits.");
        if (!Enum.IsDefined(candidate.Capacity)) return Fail(TapeError.InvalidFormat, "Unknown capacity.");
        var duration = new TapeTime(candidate.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1);
        if (candidate.A.Duration != duration || candidate.B.Duration != duration) return Fail(TapeError.InvalidFormat, "Both sides must match capacity.");
        if (TapeInputPolicy.ValidateSources(candidate, limits, TapeInputPolicy.DefaultLimits(TapeFormat.Wav)) is { } sourceFailure)
            return Fail(sourceFailure.Code, sourceFailure.Detail);
        var factory = new TapeSignalIndexFactory();
        var a = factory.Build(candidate.A, limits); var b = factory.Build(candidate.B, limits);
        if (!a.Success) return Fail(a.Failure!.Code, a.Failure.Detail);
        if (!b.Success) return Fail(b.Failure!.Code, b.Failure.Detail);
        if (candidate.SourceAssets.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != candidate.SourceAssets.Length)
            return Fail(TapeError.InvalidFormat, "Duplicate source asset id.");
        var sources = candidate.SourceAssets.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var references = candidate.A.SourceRoute.Select(r => r.SourceId).Concat(candidate.B.SourceRoute.Select(r => r.SourceId))
            .Concat(candidate.A.Markers.Select(m => m.SourceId)).Concat(candidate.B.Markers.Select(m => m.SourceId))
            .Concat(candidate.A.Annotations.Select(m => m.SourceId)).Concat(candidate.B.Annotations.Select(m => m.SourceId)).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        if (references.Count != sources.Count || references.Any(id => !sources.ContainsKey(id!))) return Fail(TapeError.InvalidFormat, "Source references must match embedded assets.");
        if (sources.Values.Any(s => s.Format == TapeFormat.Wav))
        {
            var persistenceFailure = persistenceCheck?.Invoke(candidate);
            if (persistenceFailure is not null) return Fail(persistenceFailure.Code, persistenceFailure.Detail);
            if (persistenceCheck is null && sources.Values.Any(s => s.Bytes.Length > limits.InputBytes))
                return Fail(TapeError.BoundsExceeded, "Large original requires composed snapshot persistability admission.");
        }
        ulong newIdentity = checked(identity + 1);
        Cancel(); cassette = candidate; indexes = [a.Value, b.Value]; identity = newIdentity;
        active = TapeSide.A; position = default; Anchor(); hold = TapeHoldReason.None; consumedStop = null;
        sides = [new(true, false, 0, 0, 0), new(true, false, 0, 0, 0)]; ula.TapeIn = false;
        return Ok();
    }
    public TapeResult<TapeDeckView> Blank(TapeCapacity capacity, bool discardDirty)
    {
        if (!Enum.IsDefined(capacity)) return Fail(TapeError.InvalidFormat, "Unknown capacity.");
        var duration = new TapeTime(capacity == TapeCapacity.C60 ? 1800 : 2700, 1);
        var side = new TapeSideImage(duration, default, [new(1, [new(false, duration)])], [], [], [], new(TapeFormat.Blank, null, true, [], null));
        return Insert(new(capacity, side, side, []), discardDirty);
    }
    public TapeResult<TapeDeckView> Flip()
    {
        Sync(); if (!Idle || cassette is null) return Fail(TapeError.Interlock, "Flip requires stopped cassette.");
        Cancel(); position = Duration - position; active = active == TapeSide.A ? TapeSide.B : TapeSide.A;
        hold = TapeHoldReason.None; consumedStop = null; Anchor(); ula.TapeIn = false; return Ok();
    }
    public TapeResult<TapeDeckView> SetTab(TapeSide side, bool present)
    {
        Sync(); if (!Idle || cassette is null || !Enum.IsDefined(side)) return Fail(TapeError.Interlock, "Tab change requires stopped cassette.");
        sides[(int)side] = sides[(int)side] with { TabPresent = present }; return Ok();
    }
    public TapeResult<TapeDeckView> Play()
    {
        Sync();
        if (mode == TapeDeckMode.Recording) return Ok();
        if (mode == TapeDeckMode.Playing && hold == TapeHoldReason.FormatStop)
        {
            PreflightMotion(TapeDeckMode.Playing, false);
            hold = TapeHoldReason.None;
            if (position == Duration) { mode = TapeDeckMode.Stopped; hold = TapeHoldReason.PhysicalEnd; Anchor(); return Ok(); }
            Anchor(); NotifyStateChanged(); ScheduleNext(); return Ok();
        }
        if (!Idle || cassette is null || position == Duration) return Fail(TapeError.Interlock, "PLAY requires stopped cassette before end.");
        PreflightMotion(TapeDeckMode.Playing, false);
        Cancel(); mode = TapeDeckMode.Playing; hold = TapeHoldReason.None; Anchor(); NotifyStateChanged(); ScheduleNext(); return Ok();
    }
    public TapeResult<TapeDeckView> RecordPlay()
    {
        Sync(); if (!Idle || cassette is null || position == Duration) return Fail(TapeError.Interlock, "REC requires stopped cassette before end.");
        if (!sides[(int)active].TabPresent) return Fail(TapeError.WriteProtected, "Active tab is absent.");
        PreflightMotion(TapeDeckMode.Recording, false);
        Cancel(); mode = TapeDeckMode.Recording; hold = TapeHoldReason.None; BeginRecording();
        Anchor(); NotifyStateChanged(); ScheduleNext(); return Ok();
    }
    public TapeResult<TapeDeckView> Rewind() => Wind(true, null);
    public TapeResult<TapeDeckView> FastForward() => Wind(false, null);
    /// <summary>The same REW/FF, stopping at <paramref name="target"/> (strictly inside the wind) with no hold.</summary>
    internal TapeResult<TapeDeckView> WindTo(bool reverse, TapeTime target) => Wind(reverse, target);
    private TapeResult<TapeDeckView> Wind(bool reverse, TapeTime? target)
    {
        Sync(); if (!Idle || cassette is null || (reverse ? position == default : position == Duration)) return Fail(TapeError.Interlock, "Winding requires stopped cassette away from boundary.");
        if (target is { } t && (reverse ? t >= position || t == default : t <= position || t >= Duration))
            return Fail(TapeError.Interlock, "Wind target must lie strictly inside the wind.");
        PreflightMotion(reverse ? TapeDeckMode.Rewinding : TapeDeckMode.FastForwarding, false);
        Cancel(); mode = reverse ? TapeDeckMode.Rewinding : TapeDeckMode.FastForwarding; windTarget = target;
        hold = TapeHoldReason.None; if (reverse) consumedStop = null; Anchor(); NotifyStateChanged(); ScheduleNext(); return Ok();
    }
    public TapeResult<TapeDeckView> StopEject(bool discardDirty)
    {
        Sync();
        if (mode != TapeDeckMode.Stopped || paused)
        {
            if (!FlushRecording()) return Fail(recordFailure!.Code, recordFailure.Detail);
            EndRecording(); Cancel(); mode = TapeDeckMode.Stopped; paused = false; hold = TapeHoldReason.None;
            Anchor(); ula.TapeIn = false; return Ok();
        }
        if (Dirty && !discardDirty) return Fail(TapeError.DirtyConfirmationRequired, "Confirm discarding cassette edits.");
        Cancel(); cassette = null; indexes = []; active = TapeSide.A; position = default;
        sides = [new(true, false, 0, 0, 0), new(true, false, 0, 0, 0)]; hold = TapeHoldReason.None; consumedStop = null;
        Anchor(); ula.TapeIn = false; return Ok();
    }
    internal void StopOnly()
    {
        Sync(); if (!FlushRecording()) return;
        EndRecording(); Cancel(); mode = TapeDeckMode.Stopped; paused = false;
        hold = TapeHoldReason.None; Anchor(); ula.TapeIn = false; NotifyStateChanged();
    }
    public TapeResult<TapeDeckView> Pause()
    {
        Sync(); if (mode is not (TapeDeckMode.Playing or TapeDeckMode.Recording) || hold != TapeHoldReason.None)
            return Fail(TapeError.Interlock, "PAUSE applies only to active PLAY/REC.");
        PreflightMotion(mode, !paused);
        if (!FlushRecording()) return Fail(recordFailure!.Code, recordFailure.Detail);
        Cancel(); paused = !paused;
        if (mode == TapeDeckMode.Recording && !paused) ResumeRecording();
        Anchor(); NotifyStateChanged(); ScheduleNext(); return Ok();
    }
    public TapeResult<TapeDeckView> ResetCounter()
    {
        Sync(); sides[(int)active] = sides[(int)active] with { CounterZeroTurns = Turns(position, cassette?.Capacity ?? TapeCapacity.C60) }; return Ok();
    }
    public TapeResult<TapeDeckView> SetFastWind(bool enabled)
    { Sync(); if (!Idle) return Fail(TapeError.Interlock, "STOP before changing wind speed."); fastWind = enabled; return Ok(); }
    public void OnMicChanged(long masterTick, bool microphoneLevel)
    {
        verifyOwner(); if (masterTick != scheduler.Now) throw new ArgumentException("MIC ingress must use the drained bus timestamp.", nameof(masterTick));
        RecordMicBoundary(microphoneLevel);
    }
    public TapeResult<TapeExportSnapshot> Export(TapeSide side)
    {
        Sync(); if (!Idle || cassette is null || !Enum.IsDefined(side)) return TapeResult<TapeExportSnapshot>.Fail(TapeError.Interlock, "Export requires stopped cassette.");
        return TapeResult<TapeExportSnapshot>.Ok(new(identity, side, sides[(int)side].EditRevision, indexes[(int)side].Image, cassette.SourceAssets));
    }
    public TapeResult<TapeDeckView> AcknowledgeSave(TapeSaveAcknowledgement acknowledgement)
    {
        Sync(); if (cassette is null || !Enum.IsDefined(acknowledgement.Side)) return Fail(TapeError.Interlock, "No saved side.");
        int side = (int)acknowledgement.Side;
        if (acknowledgement.MediaIdentity == identity && acknowledgement.EditRevision == sides[side].EditRevision
            && (acknowledgement.Target == TapeFormat.Tzx && acknowledgement.SignalFidelity == TapeSignalFidelity.ExactSignal
                || acknowledgement.ByteFidelity == TapeByteFidelity.OriginalBytes && sides[side].EditRevision == 0))
            sides[side] = sides[side] with { Dirty = false, SavedRevision = acknowledgement.EditRevision };
        return Ok();
    }
    private void PreflightMotion(TapeDeckMode desired, bool nextPaused)
    {
        _ = checked(scheduler.Generation(deviceId) + 1);
        if (nextPaused || desired == TapeDeckMode.Stopped || position == Duration && desired == TapeDeckMode.Playing) return;
        int rate = desired == TapeDeckMode.Rewinding ? -(fastWind ? 120 : 10) : desired == TapeDeckMode.FastForwarding ? fastWind ? 120 : 10 : 1;
        TapeTime next = rate < 0 ? default : Duration;
        if (desired == TapeDeckMode.Playing)
        {
            var marker = NextMarker(position);
            if (marker?.Position == position) return; // synchronous hold, no recursive Schedule(Now)
            next = indexes[(int)active].NextEdge(position) ?? Duration;
            if (marker is not null && marker.Position < next) next = marker.Position;
        }
        var distance = rate < 0 ? position - next : next - position;
        _ = (new TapeTime(scheduler.Now, 1) + distance * (ulong)FrameTiming.MasterHz / (ulong)Math.Abs(rate)).Ceiling();
        _ = scheduler.NextSequence;
    }
    private TapeStopMarker? NextMarker(TapeTime from) => indexes[(int)active].Image.Markers.FirstOrDefault(m =>
        m.Position >= from && (m.Position != from || consumedStop is null || m.Ordinal > consumedStop.Value));
    private void ScheduleNext()
    {
        if (Rate == 0 || cassette is null) { ula.TapeIn = false; return; }
        if (mode == TapeDeckMode.Playing)
        {
            var marker = NextMarker(position);
            if (marker?.Position == position) { Hold(marker); return; }
            ula.TapeIn = indexes[(int)active].Sample(position);
            nextPosition = indexes[(int)active].NextEdge(position) ?? Duration; pendingKind = nextPosition == Duration ? 102 : 100;
            if (marker is not null && marker.Position < nextPosition) { nextPosition = marker.Position; pendingKind = 101; }
        }
        else { ula.TapeIn = false; nextPosition = windTarget ?? (Rate < 0 ? default(TapeTime) : Duration); pendingKind = windTarget is null ? 102 : 103; }
        var distance = Rate < 0 ? anchorPosition - nextPosition!.Value : nextPosition!.Value - anchorPosition;
        idealDue = new TapeTime(anchorTick, 1) + distance * (ulong)FrameTiming.MasterHz / (ulong)Math.Abs(Rate);
        var due = idealDue.Value.Ceiling();
        token = scheduler.Schedule(new(due, EventPhase.SignalEdge, deviceId, pendingKind.Value, scheduler.Generation(deviceId)));
    }
    private void Hold(TapeStopMarker marker)
    {
        position = marker.Position; hold = TapeHoldReason.FormatStop; consumedStop = marker.Ordinal; Anchor();
        ula.TapeIn = false; token = 0; nextPosition = idealDue = null; pendingKind = null; NotifyStateChanged();
    }
    internal void OnEvent(DeviceEvent item)
    {
        verifyOwner(); if (item.Generation != scheduler.Generation(deviceId) || item.InsertionSequence != token) return;
        var boundary = nextPosition!.Value; var kind = pendingKind; token = 0; nextPosition = idealDue = null; pendingKind = null;
        position = CurrentPosition();
        if (kind == 103)
        {
            position = boundary; windTarget = null; mode = TapeDeckMode.Stopped; hold = TapeHoldReason.None;
            Anchor(); ula.TapeIn = false; NotifyStateChanged(); return;
        }
        if (mode == TapeDeckMode.Playing)
        {
            // Sample at exact current rational position batches all sub-tick edges in source order.
            // A marker at/before that coordinate arrests motion at its ideal coordinate instead.
            var marker = NextMarker(boundary);
            if (marker is not null && marker.Position <= position) { Hold(marker); return; }
        }
        if (position == Duration || Rate < 0 && position == default)
        {
            if (!FlushRecording()) return;
            EndRecording(); mode = TapeDeckMode.Stopped; paused = false; hold = TapeHoldReason.PhysicalEnd;
            Anchor(); ula.TapeIn = false; NotifyStateChanged(); return;
        }
        ScheduleNext();
    }
}
