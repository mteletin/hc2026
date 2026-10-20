using Hc2000.Machine.Input;

namespace Hc2000.App.Input;

/// <summary>Sole emulation-owner consumer. Call only at completed-instruction/paused boundaries.</summary>
public sealed class InputController(InputQueue queue, Action<RuntimeControl, long>? control = null,
    Action<Media.MediaCommand, long>? media = null, Action<Snapshots.HcsCommand, long>? snapshot = null)
{
    private readonly Dictionary<string, KeyChord> held = new(StringComparer.Ordinal);
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
        var batchSize = queue.BoundaryCount;
        for (var i = 0; i < batchSize; i++)
        {
            var (sequence, command) = queue.TakeNext();
            switch (command)
            {
                case KeyTransition key:
                    if (held.Remove(key.Source, out var old)) Change(old, -1, matrix);
                    if (key.Down) { held.Add(key.Source, key.Chord); Change(key.Chord, 1, matrix); }
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
        if (pasteHold is not null)
        {
            Change(pasteHold, -1, matrix); pasteHold = null;
            if (++position == active.Text.Length)
            {
                due = checked(tick + active.IntervalTicks);
                queue.CompletePaste(active); active = null;
                // Preserve the release gap even across successive requests.
                return;
            }
        }
        else
        {
            pasteHold = KeyMap.Character(active.Text[position])!;
            Change(pasteHold, 1, matrix);
        }
        due = checked(tick + active.IntervalTicks);
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
        if (pasteHold is not null) { Change(pasteHold, -1, matrix); pasteHold = null; }
        if (active is not null) { queue.CompletePaste(active); active = null; }
        while (pastes.TryDequeue(out var paste)) queue.CompletePaste(paste);
        due = lastTick;
    }
}
