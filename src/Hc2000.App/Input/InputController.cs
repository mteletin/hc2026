using Hc2000.Machine.Input;

namespace Hc2000.App.Input;

/// <summary>Sole emulation-owner consumer. Call only at completed-instruction/paused boundaries.</summary>
public sealed class InputController(InputQueue queue, Action<RuntimeControl, long>? control = null,
    Action<Media.MediaCommand, long>? media = null, Action<Snapshots.HcsCommand, long>? snapshot = null,
    Action<Network.CpNetCommand, long>? network = null, Action<Network.CpNetServerCommand, long>? networkServer = null,
    long minimumHoldTicks = 0)
{
    private readonly Dictionary<string, KeyChord> held = new(StringComparer.Ordinal);
    private readonly List<string> pressedThisBoundary = new();
    private readonly Dictionary<string, long> deferredReleases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> releaseNotBefore = new(StringComparer.Ordinal);
    private readonly List<string> dueReleases = new();
    private readonly long minimumHold = minimumHoldTicks >= 0 ? minimumHoldTicks
        : throw new ArgumentOutOfRangeException(nameof(minimumHoldTicks));
    private readonly int[] counts = new int[Enum.GetValues<HcKey>().Length];
    private readonly Queue<PasteText> pastes = new();
    private PasteText? active;
    private KeyChord? pasteHold;
    private int position;
    private long due;
    private long lastTick;
    private int? owner;
    public long LastAppliedSequence { get; private set; }
    public bool IsPasting => active is not null || pastes.Count != 0;

    private void VerifyOwner()
    {
        owner ??= Environment.CurrentManagedThreadId;
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Input must be consumed by the emulation owner.");
    }

    /// <summary>Worker finally/cleanup: release live holds even if cancellation beat queued UI releases.</summary>
    public void ReleaseOwned(KeyboardMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        VerifyOwner();
        Release(matrix);
    }

    public void ApplyPending(long masterTick, KeyboardMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (masterTick < 0) throw new ArgumentOutOfRangeException(nameof(masterTick));
        VerifyOwner();
        // A restore/reset can move master time backwards: rebase, never infer wall-time catch-up.
        if (masterTick < lastTick) due = checked(masterTick + (active?.IntervalTicks ?? 0));
        lastTick = masterTick;
        // Boundaries occur every CPU instruction, not every video frame. A batched tap
        // must survive until its emulated-time deadline so the ROM keyboard scan can see it.
        if (deferredReleases.Count > 0)
        {
            dueReleases.Clear();
            foreach (var pair in deferredReleases)
                if (pair.Value <= masterTick) dueReleases.Add(pair.Key);
            foreach (var source in dueReleases)
            {
                if (held.Remove(source, out var chord)) Change(chord, -1, matrix);
                deferredReleases.Remove(source);
                releaseNotBefore.Remove(source);
            }
        }
        pressedThisBoundary.Clear();
        var batchSize = queue.BoundaryCount;
        for (var i = 0; i < batchSize; i++)
        {
            var (sequence, command) = queue.TakeNext();
            switch (command)
            {
                case KeyTransition key:
                    if (key.Down)
                    {
                        // A fresh press supersedes any pending deferred release of the same
                        // source: the remove path below already releases the old chord.
                        deferredReleases.Remove(key.Source);
                        if (held.Remove(key.Source, out var old)) Change(old, -1, matrix);
                        held.Add(key.Source, key.Chord);
                        releaseNotBefore[key.Source] = checked(masterTick + minimumHold);
                        Change(key.Chord, 1, matrix);
                        if (!pressedThisBoundary.Contains(key.Source)) pressedThisBoundary.Add(key.Source);
                    }
                    else if (held.Remove(key.Source, out var old))
                    {
                        if (pressedThisBoundary.Contains(key.Source) || releaseNotBefore[key.Source] > masterTick)
                        {
                            held.Add(key.Source, old);
                            pressedThisBoundary.Remove(key.Source);
                            deferredReleases[key.Source] = Math.Max(releaseNotBefore[key.Source], checked(masterTick + 1));
                        }
                        else
                        {
                            Change(old, -1, matrix);
                            releaseNotBefore.Remove(key.Source);
                            deferredReleases.Remove(key.Source);
                        }
                    }
                    break;
                case PasteText paste:
                    pastes.Enqueue(paste);
                    break;
                case ReleaseAll:
                    Release(matrix);
                    break;
                case RuntimeControl lifecycle:
                    if (lifecycle.Kind is InputControlKind.Reset or InputControlKind.Load or InputControlKind.Shutdown)
                        Release(matrix);
                    if (control is null)
                        throw new InvalidOperationException("Lifecycle command requires an owner-side control handler.");
                    control(lifecycle, masterTick);
                    break;
                case Media.MediaCommand request:
                    if (media is null)
                        throw new InvalidOperationException("Media command requires an owner-side handler.");
                    media(request, masterTick);
                    break;
                case Snapshots.HcsCommand hcs:
                    if (snapshot is null)
                        throw new InvalidOperationException("Snapshot command requires an owner-side handler.");
                    snapshot(hcs, masterTick);
                    break;
                case Network.CpNetCommand cpnet:
                    if (network is null)
                        throw new InvalidOperationException("Network command requires an owner-side handler.");
                    network(cpnet, masterTick);
                    break;
                case Network.CpNetServerCommand server:
                    if (networkServer is null)
                        throw new InvalidOperationException("Network server command requires an owner-side handler.");
                    networkServer(server, masterTick);
                    break;
                default:
                    throw new ArgumentException("Unknown input command.");
            }
            LastAppliedSequence = sequence;
            // Reset retires this machine at the boundary. Later FIFO requests belong to the
            // replacement owner epoch, not the old CPU/keyboard/media observation.
            if (command is RuntimeControl { Kind: InputControlKind.Reset or InputControlKind.Shutdown }
                or Snapshots.HcsCommand { Action: Snapshots.HcsAction.Load }) break;
        }
        AdvancePaste(masterTick, matrix);
    }

    private void AdvancePaste(long tick, KeyboardMatrix matrix)
    {
        if (active is null)
        {
            while (pastes.TryDequeue(out var next))
            {
                if (next.Text.Length == 0) { queue.CompletePaste(next); continue; }
                active = next; position = 0; due = Math.Max(due, tick); break;
            }
        }
        if (active is null || tick < due) return;
        // At most one edge per boundary, with a fresh interval from the actual tick.
        // Late boundaries must not burst missed characters or collapse down/up on one Step.
        // A character with a shift (a capital, a symbol, a control code) is typed as a person types it: the shift
        // goes down one frame before its key and comes up one frame after it. Both at the same instant can land
        // in the middle of the guest's keyboard scan, which then reads the key without its shift (PRINT typed as
        // letters came out as "Rint").
        if (pasteHold is not null)
        {
            var shifted = pasteHold.Keys.Length > 1;
            if (shifted && !pasteKeyUp)
            {
                ChangeKeys(pasteHold.Keys.Skip(pasteHold.Keys.Length - 1), -1, matrix); pasteKeyUp = true;
                due = checked(tick + PasteShiftLeadTicks);
                return;
            }
            if (shifted) ChangeKeys(pasteHold.Keys.Take(pasteHold.Keys.Length - 1), -1, matrix);
            else Change(pasteHold, -1, matrix);
            pasteHold = null; pasteKeyUp = false;
            if (++position == active.Text.Length)
            {
                due = checked(tick + active.IntervalTicks);
                queue.CompletePaste(active); active = null;
                // Preserve the release gap even across successive requests.
                return;
            }
        }
        else if (pasteShift is not null)
        {
            pasteHold = pasteShift; pasteShift = null;
            ChangeKeys(pasteHold.Keys.Skip(pasteHold.Keys.Length - 1), 1, matrix);
        }
        else
        {
            var chord = KeyMap.Character(active.Text[position])!;
            if (chord.Keys.Length > 1)
            {
                pasteShift = chord;
                ChangeKeys(chord.Keys.Take(chord.Keys.Length - 1), 1, matrix);
                due = checked(tick + PasteShiftLeadTicks);
                return;
            }
            pasteHold = chord;
            Change(pasteHold, 1, matrix);
        }
        due = checked(tick + active.IntervalTicks);
    }

    // One frame.
    private const long PasteShiftLeadTicks = PasteText.DefaultIntervalTicks / 6;
    private KeyChord? pasteShift;
    private bool pasteKeyUp;

    private void ChangeKeys(IEnumerable<HcKey> keys, int delta, KeyboardMatrix matrix)
    {
        foreach (var key in keys)
        {
            counts[(int)key] += delta;
            matrix.SetKey(key, counts[(int)key] != 0);
        }
    }

    private void Change(KeyChord chord, int delta, KeyboardMatrix matrix)
    {
        foreach (var key in chord.Keys)
        {
            counts[(int)key] += delta;
            matrix.SetKey(key, counts[(int)key] != 0);
        }
    }

    private void Release(KeyboardMatrix matrix)
    {
        foreach (var chord in held.Values) Change(chord, -1, matrix);
        held.Clear();
        deferredReleases.Clear();
        releaseNotBefore.Clear();
        if (pasteShift is not null) { ChangeKeys(pasteShift.Keys.Take(pasteShift.Keys.Length - 1), -1, matrix); pasteShift = null; }
        if (pasteHold is not null)
        {
            if (pasteKeyUp) ChangeKeys(pasteHold.Keys.Take(pasteHold.Keys.Length - 1), -1, matrix);
            else Change(pasteHold, -1, matrix);
            pasteHold = null; pasteKeyUp = false;
        }
        if (active is not null) { queue.CompletePaste(active); active = null; }
        while (pastes.TryDequeue(out var paste)) queue.CompletePaste(paste);
        due = lastTick;
    }
}
