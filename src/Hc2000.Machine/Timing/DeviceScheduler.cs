namespace Hc2000.Machine.Timing;

public enum EventPhase { ExternalInput = 0, SignalEdge = 1, Controller = 2, Video = 3, Frame = 4, Cpu = 5 }

/// <summary>Logical event data; callbacks belong to registered devices, not queue identity.</summary>
public sealed record DeviceEvent(long DueTick, EventPhase Phase, int DeviceId, int Kind,
    long Generation = 0, long Payload = 0, long InsertionSequence = 0, LogicalTime? DueTime = null);

/// <summary>Single emulation-thread scheduler. Device ids and generations are local to this machine.</summary>
public sealed class DeviceScheduler
{
    private sealed record Device(Action<DeviceEvent> Handler, long Generation);
    private readonly Dictionary<int, Device> devices = new();
    private readonly Dictionary<long, DeviceEvent> pending = new();
    private PriorityQueue<DeviceEvent, (LogicalTime, int, int, long)> queue = new();
    private LogicalTime logicalNow;
    private long sequence;
    private bool advancing;
    /// <summary>True only while an <see cref="AdvanceTo(long)"/> drain is in progress; a speed change is refused then.</summary>
    public bool Advancing => advancing;
    /// <summary>The floor of the exact logical time. Stays a long so integer callers are unchanged;
    /// the exact time is <see cref="LogicalNow"/>.</summary>
    public long Now => logicalNow.WholeTicks;
    /// <summary>The exact logical time the scheduler has advanced to.</summary>
    public LogicalTime LogicalNow => logicalNow;

    public void RegisterDevice(int stableDeviceId, Action<DeviceEvent> handler)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stableDeviceId);
        ArgumentNullException.ThrowIfNull(handler);
        devices.Add(stableDeviceId, new(handler, 0));
    }

    /// <summary>Detach a host-session-only device after cancelling all of its events.</summary>
    public void UnregisterDevice(int stableDeviceId)
    {
        if (pending.Values.Any(item => item.DeviceId == stableDeviceId))
            throw new InvalidOperationException("A device with pending events cannot be detached.");
        if (!devices.Remove(stableDeviceId)) throw new ArgumentException("Unknown device.", nameof(stableDeviceId));
    }

    public long Generation(int deviceId) => devices[deviceId].Generation;

    public void InvalidateDevice(int deviceId)
    {
        var device = devices[deviceId];
        devices[deviceId] = device with { Generation = checked(device.Generation + 1) };
    }

    /// <returns>A machine-local cancellation token (the assigned insertion sequence).</returns>
    public long Schedule(DeviceEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var due = TimeOf(item);
        if (due <= logicalNow) throw new ArgumentOutOfRangeException(nameof(item), "Schedule strictly after Now.");
        if (!Enum.IsDefined(item.Phase) || item.InsertionSequence != 0)
            throw new ArgumentException("Invalid phase or caller-supplied sequence.", nameof(item));
        if (item.DueTime is { } exact && exact.WholeTicks != item.DueTick)
            throw new ArgumentException("DueTick must be the floor of DueTime.", nameof(item));
        if (item.Generation != Generation(item.DeviceId))
            throw new ArgumentException("Stale device generation.", nameof(item));
        var next = checked(sequence + 1);
        item = item with { InsertionSequence = next };
        pending.Add(next, item);
        queue.Enqueue(item, (due, (int)item.Phase, item.DeviceId, next));
        sequence = next;
        return next;
    }

    public bool Cancel(long token) => pending.Remove(token);

    public IReadOnlyList<DeviceEvent> PendingEvents => pending.Values
        .Where(e => e.Generation == Generation(e.DeviceId))
        .OrderBy(e => TimeOf(e)).ThenBy(e => e.Phase).ThenBy(e => e.DeviceId)
        .ThenBy(e => e.InsertionSequence).ToArray();

    /// <summary>The insertion sequence the next Schedule call will assign.</summary>
    public long NextSequence => checked(sequence + 1);

    /// <summary>Stable device ids with their current generation, in registration order.</summary>
    public IReadOnlyList<(int DeviceId, long Generation)> DeviceGenerations =>
        devices.OrderBy(d => d.Key).Select(d => (d.Key, d.Value.Generation)).ToArray();

    /// <summary>Replaces the pending set with a captured one (snapshot restore). Insertion
    /// sequences keep their captured values; the caller must have restored Now and all
    /// device generations to the captured values first. Throws on a generation or sequence
    /// mismatch instead of partially mutating.</summary>
    internal void ValidateRestore(long now, IReadOnlyList<DeviceEvent> events, long nextSequence,
        IReadOnlyList<(int Device, long Generation)> generations)
    {
        if (advancing || now < Now || sequence >= nextSequence) throw new InvalidOperationException("Restore requires a quiescent fresh scheduler.");
        ValidateRestore(LogicalTime.Tick(now), events, nextSequence, generations);
    }

    /// <summary>Validates a captured event set at an exact (possibly fractional) boundary. Events may
    /// share the boundary tick only when their exact due is strictly later; the legacy integer path
    /// keeps its <c>DueTick &gt; now</c> rule exactly.</summary>
    internal void ValidateRestore(LogicalTime now, IReadOnlyList<DeviceEvent> events, long nextSequence,
        IReadOnlyList<(int Device, long Generation)> generations)
    {
        if (advancing || now < LogicalTime.Tick(Now) || sequence >= nextSequence) throw new InvalidOperationException("Restore requires a quiescent fresh scheduler.");
        var gens = generations.ToDictionary(g => g.Device, g => g.Generation);
        if (gens.Count != devices.Count || gens.Any(g => g.Value < 0 || !devices.ContainsKey(g.Key)))
            throw new ArgumentException("Restored device roster.", nameof(generations));
        var sequences = new HashSet<long>();
        foreach (var e in events)
            if (TimeOf(e) <= now || !Enum.IsDefined(e.Phase) || e.InsertionSequence < 1 || e.InsertionSequence >= nextSequence
                || !sequences.Add(e.InsertionSequence) || !gens.TryGetValue(e.DeviceId, out var gen) || e.Generation != gen)
                throw new ArgumentException("Restored event invariant.", nameof(events));
    }
    internal void RestoreEvents(long now, IReadOnlyList<DeviceEvent> events, long nextSequence,
        IReadOnlyList<(int Device, long Generation)> generations)
    {
        RestoreEvents(LogicalTime.Tick(now), events, nextSequence, generations);
    }
    /// <summary>Adopts the captured queue at an exact boundary; the scheduler lands on the exact time
    /// (legacy documents land on their integer tick, so the trace is unchanged).</summary>
    internal void RestoreEvents(LogicalTime now, IReadOnlyList<DeviceEvent> events, long nextSequence,
        IReadOnlyList<(int Device, long Generation)> generations)
    {
        ValidateRestore(now, events, nextSequence, generations);
        // Build the queue before adoption. Generation counters are values, not a replay loop.
        var restoredQueue = new PriorityQueue<DeviceEvent, (LogicalTime, int, int, long)>();
        foreach (var e in events) restoredQueue.Enqueue(e, (TimeOf(e), (int)e.Phase, e.DeviceId, e.InsertionSequence));
        foreach (var (device, gen) in generations) devices[device] = devices[device] with { Generation = gen };
        pending.Clear(); foreach (var e in events) pending.Add(e.InsertionSequence, e);
        queue = restoredQueue; sequence = nextSequence - 1; logicalNow = now;
    }

    /// <summary>Advance to an integer master tick, comparing on whole ticks so integer-exact
    /// callers pay no fraction bookkeeping. Drains and lands exactly as
    /// <see cref="AdvanceTo(LogicalTime)"/> does at <see cref="LogicalTime.Tick"/>: an event is
    /// due when its exact time is at or below the integer target (a fractional event in the
    /// target's own tick is not due at the tick).</summary>
    public void AdvanceTo(long target)
    {
        if (advancing) throw new InvalidOperationException("Reentrant AdvanceTo is forbidden.");
        if (target < logicalNow.WholeTicks || (target == logicalNow.WholeTicks && !logicalNow.IsInteger))
            throw new ArgumentOutOfRangeException(nameof(target), "Master time cannot regress.");
        advancing = true;
        try
        {
            while (queue.TryPeek(out var item, out _) &&
                   (item.DueTick < target || (item.DueTick == target && (item.DueTime is null || item.DueTime.Value.IsInteger))))
            {
                queue.Dequeue();
                if (!pending.Remove(item.InsertionSequence)) continue;
                var device = devices[item.DeviceId];
                if (device.Generation != item.Generation) continue;
                logicalNow = TimeOf(item);
                device.Handler(item);
            }
            logicalNow = LogicalTime.Tick(target);
        }
        finally { advancing = false; }
    }

    /// <summary>Drain every pending event at or before the exact logical time, then land on it.
    /// Ordering is exact time, then phase, device id and insertion sequence; phase breaks ties
    /// only at exact equality. <see cref="Now"/> stays the floor.</summary>
    public void AdvanceTo(LogicalTime target)
    {
        if (advancing) throw new InvalidOperationException("Reentrant AdvanceTo is forbidden.");
        if (target < logicalNow) throw new ArgumentOutOfRangeException(nameof(target), "Master time cannot regress.");
        advancing = true;
        try
        {
            while (queue.TryPeek(out var item, out _) && TimeOf(item) <= target)
            {
                queue.Dequeue();
                if (!pending.Remove(item.InsertionSequence)) continue;
                var device = devices[item.DeviceId];
                if (device.Generation != item.Generation) continue;
                logicalNow = TimeOf(item);
                device.Handler(item);
            }
            logicalNow = target;
        }
        finally { advancing = false; }
    }

    /// <summary>The event's exact due time: an explicit fraction, or its integer tick.</summary>
    private static LogicalTime TimeOf(DeviceEvent item) =>
        item.DueTime is { } exact ? exact : LogicalTime.Tick(item.DueTick);
}
