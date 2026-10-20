namespace Hc2000.Machine.Tape;

internal sealed partial class TapeDeckCore
{
    // This is an interval writer on the core's indexed signal, not another transport/clock.
    // Keep a bounded batch of real MIC runs; every capture/stop/pause commits its partial tail.
    private const int RecordBatchRuns = 512;
    private TapeTime? recordStart, recordHead;
    private TapeTime recordCommitted;
    private bool recordLevel;
    private readonly List<TapeRun> recordRuns = [];
    private TapeFailure? recordFailure;

    private void BeginRecording()
    {
        recordStart = recordHead = recordCommitted = position;
        recordLevel = !mic; recordRuns.Clear(); recordFailure = null;
    }
    private void ResumeRecording()
    {
        recordHead = recordCommitted = position;
        recordLevel = !mic;
    }
    private void EndRecording()
    {
        recordStart = recordHead = null; recordLevel = false; recordRuns.Clear();
    }
    private bool AppendRecording()
    {
        if (mode != TapeDeckMode.Recording || paused || hold != TapeHoldReason.None) return true;
        var head = recordHead!.Value;
        if (position > head)
        {
            var duration = position - head;
            if (recordRuns.Count != 0 && recordRuns[^1].Level == recordLevel)
                recordRuns[^1] = new(recordLevel, recordRuns[^1].Duration + duration);
            else recordRuns.Add(new(recordLevel, duration));
            recordHead = position;
        }
        return true;
    }
    private bool FlushRecording()
    {
        if (mode != TapeDeckMode.Recording) return true;
        try
        {
            AppendRecording();
            if (recordRuns.Count == 0) return true;
            var end = recordHead!.Value;
            ulong revision = checked(sides[(int)active].EditRevision + 1);
            var image = indexes[(int)active].Image;
            uint ordinal = image.Annotations.Where(a => a.Position == recordCommitted)
                .Select(a => a.Ordinal).DefaultIfEmpty(0u).Max();
            ordinal = checked(ordinal + 1);
            var replacement = new TapeSideImage(end - recordCommitted, default,
                [new(1, recordRuns)], [],
                [new(default, ordinal, TapeAnnotationKind.Overwrite,
                    $"MIC overwrite [{recordCommitted},{end})", null, null)], [], image.ImportOptions);
            var edited = indexes[(int)active].Replace(recordCommitted, end, replacement, limits);
            if (!edited.Success) return RecordingFault(edited.Failure!);
            var indexed = new TapeSignalIndexFactory().Build(edited.Value, limits);
            if (!indexed.Success) return RecordingFault(indexed.Failure!);
            // Nothing live is changed until both replacement and revision preflight succeed.
            indexes[(int)active] = indexed.Value;
            cassette = active == TapeSide.A ? cassette! with { A = edited.Value } : cassette! with { B = edited.Value };
            sides[(int)active] = sides[(int)active] with { EditRevision = revision, Dirty = true };
            if (consumedStop is { } stop && !edited.Value.Markers.Any(m => m.Ordinal == stop && m.Position <= position)) consumedStop = null;
            recordCommitted = end; recordRuns.Clear();
            return true;
        }
        catch (Exception e) when (e is OverflowException or ArgumentException)
        { return RecordingFault(new(TapeError.BoundsExceeded, e.Message)); }
    }
    private bool RecordingFault(TapeFailure failure)
    {
        // Reject the uncommitted batch atomically. Previously committed edits remain dirty.
        position = recordCommitted; recordFailure = failure;
        Cancel(); mode = TapeDeckMode.Stopped; paused = false; hold = TapeHoldReason.Fault;
        EndRecording(); Anchor(); ula.TapeIn = false; NotifyStateChanged();
        return false;
    }
    private void RecordMicBoundary(bool microphoneLevel)
    {
        if (mic == microphoneLevel) return; // BEL-only notifications cannot split MIC runs.
        if (mode == TapeDeckMode.Recording && !paused && hold == TapeHoldReason.None)
        {
            Sync();
            try
            {
                AppendRecording();
                if (recordRuns.Count >= RecordBatchRuns && !FlushRecording()) { mic = microphoneLevel; return; }
                recordLevel = !microphoneLevel;
            }
            catch (Exception e) when (e is OverflowException or ArgumentException)
            { RecordingFault(new(TapeError.BoundsExceeded, e.Message)); }
        }
        mic = microphoneLevel;
    }

    // Existing native DTO shapes are owned by core. These hooks let core serialize/adopt the
    // committed recorder state without serializing the batching implementation or callbacks.
    internal TapeFailure? RecorderFlushForCapture()
    {
        Sync(); return FlushRecording() ? null : recordFailure;
    }
    internal (TapeTime? Start, TapeTime? Head, bool Level) RecorderState => (recordStart, recordHead, recordLevel);
    internal bool RecorderAdoptState(TapeTime? start, TapeTime? head, bool level)
    {
        if (mode == TapeDeckMode.Recording)
        {
            if (cassette is null || !sides[(int)active].TabPresent || start is null || head != position
                || start > head || hold != TapeHoldReason.None || !paused && level != !mic) return false;
            recordStart = start; recordHead = head; recordCommitted = position; recordLevel = level;
        }
        else
        {
            if (start is not null || head is not null || level) return false;
            EndRecording();
        }
        recordRuns.Clear(); recordFailure = null; return true;
    }
}
