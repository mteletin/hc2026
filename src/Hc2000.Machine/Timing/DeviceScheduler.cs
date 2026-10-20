namespace Hc2000.Machine.Timing;

public enum EventPhase { ExternalInput = 0, SignalEdge = 1, Controller = 2, Video = 3, Frame = 4, Cpu = 5 }

/// <summary>Logical event data; callbacks belong to registered devices, not queue identity.</summary>
public sealed record DeviceEvent(long DueTick, EventPhase Phase, int DeviceId, int Kind,
    long Generation = 0, long Payload = 0, long InsertionSequence = 0);

/// <summary>Single emulation-thread scheduler. Device ids and generations are local to this machine.</summary>
public sealed class DeviceScheduler
{
    private sealed record Device(Action<DeviceEvent> Handler, long Generation);
    private readonly Dictionary<int, Device> devices = new();
    private readonly Dictionary<long, DeviceEvent> pending = new();
    private PriorityQueue<DeviceEvent, (long, int, int, long)> queue = new();
    private long sequence;
    private bool advancing;
    public long Now { get; private set; }

    public void RegisterDevice(int stableDeviceId, Action<DeviceEvent> handler)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stableDeviceId);
        ArgumentNullException.ThrowIfNull(handler);
        devices.Add(stableDeviceId, new(handler, 0));
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
        if (item.DueTick <= Now) throw new ArgumentOutOfRangeException(nameof(item), "Schedule strictly after Now.");
        if (!Enum.IsDefined(item.Phase) || item.InsertionSequence != 0)
            throw new ArgumentException("Invalid phase or caller-supplied sequence.", nameof(item));
        if (item.Generation != Generation(item.DeviceId))
            throw new ArgumentException("Stale device generation.", nameof(item));
        var next = checked(sequence + 1);
        item = item with { InsertionSequence = next };
        pending.Add(next, item);
        queue.Enqueue(item, (item.DueTick, (int)item.Phase, item.DeviceId, next));
        sequence = next;
        return next;
    }

    public bool Cancel(long token) => pending.Remove(token);

    public IReadOnlyList<DeviceEvent> PendingEvents => pending.Values
        .Where(e => e.Generation == Generation(e.DeviceId))
        .OrderBy(e => e.DueTick).ThenBy(e => e.Phase).ThenBy(e => e.DeviceId)
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
        var gens = generations.ToDictionary(g => g.Device, g => g.Generation);
        if (gens.Count != devices.Count || gens.Any(g => g.Value < 0 || !devices.ContainsKey(g.Key)))
            throw new ArgumentException("Restored device roster.", nameof(generations));
        var sequences = new HashSet<long>();
        foreach (var e in events)
            if (e.DueTick <= now || !Enum.IsDefined(e.Phase) || e.InsertionSequence < 1 || e.InsertionSequence >= nextSequence
                || !sequences.Add(e.InsertionSequence) || !gens.TryGetValue(e.DeviceId, out var gen) || e.Generation != gen)
                throw new ArgumentException("Restored event invariant.", nameof(events));
    }
    internal void RestoreEvents(long now, IReadOnlyList<DeviceEvent> events, long nextSequence,
        IReadOnlyList<(int Device, long Generation)> generations)
    {
        ValidateRestore(now, events, nextSequence, generations);
        // Build the queue before adoption. Generation counters are values, not a replay loop.
        var restoredQueue = new PriorityQueue<DeviceEvent, (long, int, int, long)>();
        foreach (var e in events) restoredQueue.Enqueue(e, (e.DueTick, (int)e.Phase, e.DeviceId, e.InsertionSequence));
        foreach (var (device, gen) in generations) devices[device] = devices[device] with { Generation = gen };
        pending.Clear(); foreach (var e in events) pending.Add(e.InsertionSequence, e);
        queue = restoredQueue; sequence = nextSequence - 1; Now = now;
    }

    public void AdvanceTo(long target)
    {
        if (advancing) throw new InvalidOperationException("Reentrant AdvanceTo is forbidden.");
        if (target < Now) throw new ArgumentOutOfRangeException(nameof(target), "Master time cannot regress.");
        advancing = true;
        try
        {
            while (queue.TryPeek(out var item, out _) && item.DueTick <= target)
            {
                queue.Dequeue();
                if (!pending.Remove(item.InsertionSequence)) continue;
                var device = devices[item.DeviceId];
                if (device.Generation != item.Generation) continue;
                Now = item.DueTick;
                device.Handler(item);
            }
            Now = target;
        }
        finally { advancing = false; }
    }
}
