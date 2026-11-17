using System.Buffers.Binary;

namespace Hc2000.Machine.Expansion;

/// <summary>
/// X0 main-CPU guest-service dispatcher: ONE dispatcher for all main-CPU card services. One
/// outstanding service per card, Busy backpressure, nested entry refused (pending guard),
/// bounded owner-thread handlers, explicit completion, cancel and too-late results. An ambiguous
/// outcome is never replayed: the receipt retains result-or-timeout.
/// </summary>

public enum X0ServiceState { Idle = 0, Pending = 1, Done = 2, Cancelled = 3, TooLate = 4, Busy = 5, InvalidState = 6 }

public sealed record X0ServiceCall(ushort CardId, byte ServiceId, byte[] Args, long SubmitTick);

public abstract class X0ServiceOutcome
{
    public static X0ServiceOutcomeDone Completed(byte[] result) => new(result);
    public static X0ServiceOutcomePending WorkDue(long deadlineTicks) => new(deadlineTicks);
}

/// <summary>Handler finished within the owner-thread call; the bounded result is retained.</summary>
public sealed class X0ServiceOutcomeDone(byte[] result) : X0ServiceOutcome
{
    public byte[] Result { get; } = result;
}

/// <summary>
/// The service does host-session work (a gateway, a DMA channel) that completes later. The
/// deadline is in master ticks from submission; external completion arrives through
/// <see cref="ExpansionServiceDispatcher.Complete"/> before the deadline. Polling or cancelling
/// at/after the deadline without completion is TooLate.
/// </summary>
public sealed class X0ServiceOutcomePending(long deadlineTicks) : X0ServiceOutcome
{
    public long DeadlineTicks { get; } = deadlineTicks;
}

/// <summary>Bounded owner-thread handler. Must not retain the call after returning.</summary>
public delegate X0ServiceOutcome X0ServiceHandler(in X0ServiceCall call, long masterTick);

public sealed record X0ServiceStatus(X0ServiceState State, byte ServiceId, long Deadline, byte[]? Result);

public sealed class ExpansionServiceDispatcher
{
    public const int MaxServiceBytes = 65535;
    public const int MaxCards = 256;

    private sealed class Card
    {
        public ushort Id;
        public readonly Dictionary<byte, X0ServiceHandler> Services = new();
        public X0ServiceState State = X0ServiceState.Idle;
        public byte ServiceId;
        public byte[] Result = [];
        public long Deadline, SubmitTick;
        public bool Completed;
    }

    private readonly List<Card> cards = new();
    private bool dispatching;
    internal bool MainMapBusy => dispatching || cards.Any(c => c.State == X0ServiceState.Pending);
    internal bool CanRegisterCard(ushort cardId) =>
        !dispatching && cardId != 0 && cards.Count < MaxCards && cards.All(c => c.Id != cardId);

    public bool RegisterCard(ushort cardId)
    {
        if (!CanRegisterCard(cardId)) return false;
        cards.Add(new Card { Id = cardId });
        return true;
    }

    public bool RegisterService(ushort cardId, byte serviceId, X0ServiceHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var c = Find(cardId);
        return !dispatching && c is not null && c.Services.TryAdd(serviceId, handler);
    }

    public X0ServiceStatus Status(ushort cardId)
    {
        var c = Find(cardId);
        if (c is null) return new(X0ServiceState.InvalidState, 0, 0, null);
        return Snapshot(c);
    }

    /// <summary>
    /// Submit one bounded guest service. One outstanding service per card; a nested entry is
    /// refused with Busy and changes nothing.
    /// </summary>
    public X0ServiceStatus Submit(ushort cardId, byte serviceId, ReadOnlySpan<byte> args, long masterTick)
    {
        var c = Find(cardId);
        if (c is null || !c.Services.TryGetValue(serviceId, out var handler))
            return new(X0ServiceState.InvalidState, serviceId, 0, null);
        // Pending guard: a nested entry while one service is outstanding is refused with Busy.
        if (dispatching || c.State == X0ServiceState.Pending)
            return new(X0ServiceState.Busy, c.ServiceId, c.Deadline, null);
        if (masterTick < 0 || args.Length is 0 or > MaxServiceBytes)
            return new(X0ServiceState.InvalidState, serviceId, 0, null);
        var call = new X0ServiceCall(cardId, serviceId, args.ToArray(), masterTick);
        X0ServiceOutcome outcome;
        dispatching = true;
        try { outcome = handler(in call, masterTick); }
        finally { dispatching = false; }
        c.ServiceId = serviceId;
        c.SubmitTick = masterTick;
        c.Completed = false;
        c.Result = [];
        switch (outcome)
        {
            case X0ServiceOutcomeDone done when done.Result is { Length: > 0 and <= MaxServiceBytes }:
                c.State = X0ServiceState.Done;
                c.Result = (byte[])done.Result.Clone();
                c.Deadline = masterTick;
                c.Completed = true;
                break;
            case X0ServiceOutcomePending pending when pending.DeadlineTicks > 0:
                if (!TryAdd(masterTick, pending.DeadlineTicks, out var deadline))
                {
                    c.State = X0ServiceState.InvalidState;
                    c.Deadline = 0;
                    break;
                }
                c.State = X0ServiceState.Pending;
                c.Deadline = deadline;
                break;
            default:
                c.State = X0ServiceState.InvalidState;
                c.Deadline = 0;
                break;
        }
        return Snapshot(c);
    }

    /// <summary>
    /// Host-session external completion (the gateway, the DMA channel), on the owner thread,
    /// before the deadline. A completion after the deadline or after cancellation is TooLate
    /// and is not replayed.
    /// </summary>
    public X0ServiceStatus Complete(ushort cardId, byte serviceId, ReadOnlySpan<byte> result, long masterTick)
    {
        var c = Find(cardId);
        if (c is null || result.Length is 0 or > MaxServiceBytes)
            return new(X0ServiceState.InvalidState, serviceId, 0, null);
        if (dispatching) return new(X0ServiceState.Busy, c.ServiceId, c.Deadline, null);
        if (c.State != X0ServiceState.Pending || c.ServiceId != serviceId)
            return new(X0ServiceState.InvalidState, serviceId, 0, null);
        if (masterTick < c.SubmitTick) return new(X0ServiceState.InvalidState, serviceId, 0, null);
        if (!c.Completed && masterTick >= c.Deadline)
        {
            c.State = X0ServiceState.TooLate;
            return Snapshot(c);
        }
        if (!c.Completed)
        {
            c.Completed = true;
            c.Result = result.ToArray();
        }
        return Snapshot(c);
    }

    /// <summary>Observe the current receipt. A completed pending service is collected as Done;
    /// at/after the deadline an uncompleted one times out.</summary>
    public X0ServiceStatus Poll(ushort cardId, long masterTick)
    {
        var c = Find(cardId);
        if (c is null) return new(X0ServiceState.InvalidState, 0, 0, null);
        if (dispatching) return new(X0ServiceState.Busy, c.ServiceId, c.Deadline, null);
        if (masterTick < c.SubmitTick) return new(X0ServiceState.InvalidState, c.ServiceId, c.Deadline, null);
        if (c.State == X0ServiceState.Pending)
        {
            if (c.Completed) c.State = X0ServiceState.Done;
            else if (masterTick >= c.Deadline) { c.State = X0ServiceState.TooLate; c.Result = []; }
        }
        return Snapshot(c);
    }

    public X0ServiceStatus Cancel(ushort cardId, long masterTick)
    {
        var c = Find(cardId);
        if (c is null) return new(X0ServiceState.InvalidState, 0, 0, null);
        if (dispatching) return new(X0ServiceState.Busy, c.ServiceId, c.Deadline, null);
        if (masterTick < c.SubmitTick) return new(X0ServiceState.InvalidState, c.ServiceId, c.Deadline, null);
        switch (c.State)
        {
            case X0ServiceState.Pending when c.Completed:
                c.State = X0ServiceState.Done;
                break;
            case X0ServiceState.Pending when !c.Completed:
                if (masterTick >= c.Deadline)
                {
                    c.State = X0ServiceState.TooLate;
                    c.Result = [];
                }
                else
                {
                    c.State = X0ServiceState.Cancelled;
                    c.Result = [];
                }
                break;
            case X0ServiceState.Done or X0ServiceState.TooLate or X0ServiceState.Cancelled:
                // Terminal receipt is retained: cancelling it changes nothing and replays nothing.
                break;
            default:
                return new(X0ServiceState.InvalidState, 0, 0, null);
        }
        return Snapshot(c);
    }

    internal ExpansionX0.X0ServiceCardState[] CaptureCards()
    {
        if (dispatching) throw new InvalidOperationException("Cannot snapshot a running service handler.");
        return cards.Select(c => new ExpansionX0.X0ServiceCardState(c.Id, (byte)c.State, c.ServiceId,
            c.Deadline, c.SubmitTick, c.Completed, (byte[])c.Result.Clone())).ToArray();
    }

    internal void ValidateCards(ExpansionX0.X0ServiceCardState[] states)
    {
        if (dispatching) throw new InvalidOperationException("Cannot restore during a service handler.");
        if (states.Length != cards.Count) throw new ArgumentException("Service card roster changed.");
        for (var index = 0; index < cards.Count; index++)
        {
            var s = states[index]; var c = cards[index];
            if (s.CardId != c.Id) throw new ArgumentException("Service card roster.");
            if (!Enum.IsDefined((X0ServiceState)s.State) || s.Result.Length is > MaxServiceBytes) throw new ArgumentException("Service card state.");
            var state = (X0ServiceState)s.State;
            switch (state)
            {
                case X0ServiceState.Idle:
                    if (s.Deadline != 0 || s.Completed || s.Result.Length != 0) throw new ArgumentException("Service idle state.");
                    break;
                case X0ServiceState.Pending:
                    if (s.Deadline <= s.SubmitTick || s.Completed != (s.Result.Length > 0))
                        throw new ArgumentException("Service pending state.");
                    break;
                case X0ServiceState.Done:
                    if (!s.Completed || s.Result.Length == 0 || s.Deadline < s.SubmitTick)
                        throw new ArgumentException("Service done state.");
                    break;
                case X0ServiceState.Cancelled or X0ServiceState.TooLate:
                    if (s.Deadline <= s.SubmitTick || s.Result.Length != 0 || s.Completed) throw new ArgumentException("Service terminal state.");
                    break;
                case X0ServiceState.InvalidState:
                    if (s.Deadline != 0 || s.Completed || s.Result.Length != 0) throw new ArgumentException("Service invalid state.");
                    break;
                default:
                    throw new ArgumentException("Service card state.");
            }
        }
    }

    internal void AdoptCards(ExpansionX0.X0ServiceCardState[] states)
    {
        ValidateCards(states);
        for (var index = 0; index < cards.Count; index++)
        {
            var s = states[index]; var c = cards[index];
            var state = (X0ServiceState)s.State;
            c.State = state; c.ServiceId = s.ServiceId; c.Deadline = s.Deadline;
            c.SubmitTick = s.SubmitTick; c.Completed = s.Completed; c.Result = (byte[])s.Result.Clone();
        }
    }

    private X0ServiceStatus Snapshot(Card c)
    {
        var result = c.State == X0ServiceState.Done && c.Completed ? (byte[])c.Result.Clone() : null;
        return new(c.State, c.ServiceId, c.Deadline, result);
    }

    private Card? Find(ushort cardId) => cards.FirstOrDefault(c => c.Id == cardId);

    private static bool TryAdd(long a, long b, out long sum)
    {
        if (a < 0 || b <= 0 || a > long.MaxValue - b) { sum = 0; return false; }
        sum = a + b;
        return true;
    }
}
