namespace Hc2000.App.Input;

public abstract record InputCommand;
public sealed record KeyTransition : InputCommand
{
    public string Source { get; }
    public KeyChord Chord { get; }
    public bool Down { get; }
    public KeyTransition(string source, KeyChord chord, bool down)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 128)
            throw new ArgumentException("A bounded physical-source name is required.", nameof(source));
        Source = source; Chord = chord ?? throw new ArgumentNullException(nameof(chord)); Down = down;
    }
}
public sealed record ReleaseAll : InputCommand;
public enum InputControlKind { Pause, Resume, Step, Reset, Load, Shutdown, SetSpeed }
public sealed record RuntimeControl(InputControlKind Kind, string? Path = null, int SpeedPercent = 100) : InputCommand;

public sealed record PasteText : InputCommand
{
    public const int MaxLength = 4096;
    // Six scan frames per edge: exceeds the ROM's five-tick debounce, below typematic's 35 ticks.
    // It is conservative policy, not a claim of booted-ROM acceptance.
    public const long DefaultIntervalTicks = 6 * 355680;
    public string Text { get; }
    public long IntervalTicks { get; }
    public PasteText(string text, long intervalTicks = DefaultIntervalTicks)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLength) throw new ArgumentException("Paste exceeds 4096 characters.", nameof(text));
        if (intervalTicks <= 0) throw new ArgumentOutOfRangeException(nameof(intervalTicks));
        Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (Text.Any(c => KeyMap.Character(c) is null))
            throw new ArgumentException("Paste contains an unmappable character.", nameof(text));
        IntervalTicks = intervalTicks;
    }
}

/// <summary>One linearized reliable FIFO for input and lifecycle controls. No coalescing or lost releases.</summary>
public sealed class InputQueue
{
    private readonly object gate = new();
    private readonly Queue<(long Sequence, InputCommand Command)> pending = new();
    private long sequence;
    private int outstandingPasteCharacters;
    public const int MaxOutstandingPasteCharacters = 16384;

    // Commands are reliable/unbounded; paste data (including active/queued requests) is bounded.
    // No synchronous wait on the UI and no native/device/clock operations in this producer.
    public long Enqueue(InputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not (KeyTransition or PasteText or ReleaseAll or RuntimeControl or Media.MediaCommand or Snapshots.HcsCommand))
            throw new ArgumentException("Unknown input command.", nameof(command));
        if (command is RuntimeControl control && (!Enum.IsDefined(control.Kind)
            || control.Kind == InputControlKind.SetSpeed && control.SpeedPercent is not (0 or 100 or 200 or 400)))
            throw new ArgumentOutOfRangeException(nameof(command));
        if (command is Media.MediaCommand media && (!Enum.IsDefined(media.Action) || !Enum.IsDefined(media.Slot)
            || media.Action == Media.MediaAction.InsertDisk && (media.Bytes.IsDefaultOrEmpty || media.Bytes.Length > Media.MediaFiles.MaxDiskBytes)))
            throw new ArgumentException("Invalid or oversized media command.", nameof(command));
        if (command is Snapshots.HcsCommand hcs && (!Enum.IsDefined(hcs.Action)
            || hcs.Action == Snapshots.HcsAction.Save && !hcs.Bytes.IsDefault
            || hcs.Action == Snapshots.HcsAction.Load && (hcs.Bytes.IsDefaultOrEmpty || hcs.Bytes.Length > Media.MediaFiles.MaxSnapshotBytes)))
            throw new ArgumentException("Invalid or oversized snapshot command.", nameof(command));
        lock (gate)
        {
            if (command is PasteText paste)
            {
                if (outstandingPasteCharacters + paste.Text.Length > MaxOutstandingPasteCharacters)
                    throw new InvalidOperationException("Paste queue is full; request was not enqueued.");
                outstandingPasteCharacters += paste.Text.Length;
            }
            var id = checked(++sequence);
            pending.Enqueue((id, command));
            return id;
        }
    }

    internal int BoundaryCount { get { lock (gate) return pending.Count; } }

    internal (long Sequence, InputCommand Command) TakeNext()
    {
        lock (gate) return pending.Dequeue();
    }

    /// <summary>Discard commands not yet consumed by a session being retired.</summary>
    internal void DiscardPending()
    {
        lock (gate)
        {
            foreach (var (_, command) in pending)
                if (command is PasteText paste) outstandingPasteCharacters -= paste.Text.Length;
            pending.Clear();
        }
    }

    internal void CompletePaste(PasteText paste)
    {
        lock (gate) outstandingPasteCharacters -= paste.Text.Length;
    }
}
