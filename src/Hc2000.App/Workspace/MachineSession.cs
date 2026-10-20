using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Media;

namespace Hc2000.App.Workspace;

/// <summary>
/// Per-machine runtime ownership: one worker/generation, its media writeback host, its bounded
/// read-hold admission, its input queue/adapter, and its input-evidence identity. A tab's
/// runtime lifetime stays inside this session; closing or faulting it never touches another
/// session's queue, worker, bridge or evidence. The view (window) renders and boots; the
/// session owns the runtime state the boot and the close/drain both reason about.
/// </summary>
internal sealed class MachineSession
{
    private Runtime.EmulationWorker? worker;
    private long runtimeGeneration;
    private bool closed;

    // One bounded host read operation in flight (prestart configured A+B subset, or a single
    // panel insert): owns the single native cold-read fence for its frozen configured membership
    // until every expected member is accounted, or a named host drain at a lifecycle boundary
    // proves the owner callbacks can no longer land.
    private DiskReadReservation.Operation? heldOp;

    public MachineSession(KeyMappingMode mode)
    {
        InputQueue = new();
        KeyboardInput = new(InputQueue) { Mode = mode };
    }

    public InputQueue InputQueue { get; }
    public KeyboardInputAdapter KeyboardInput { get; }
    public bool Closed => closed;
    public Runtime.EmulationWorker? Worker => Volatile.Read(ref worker);
    public long RuntimeGeneration => Volatile.Read(ref runtimeGeneration);
    public DiskWritebackHost? WritebackHost { get; set; }
    public DiskReadReservation.Operation? HeldOperation => Volatile.Read(ref heldOp);
    public bool HasRuntime => worker is not null;
    public MachineConfiguration? StartingConfiguration { get; set; }
    public Task BasicReplacementCompletion { get; set; } = Task.CompletedTask;
    public bool BasicRuntimeActive { get; set; }
    public bool ReplacingRuntime { get; set; }
    public int MediaPollTicks { get; set; }
    public MediaBridge? MediaBridge { get; set; }
    public Snapshots.HcsBridge? SnapshotBridge { get; set; }
    public Explorer.ExplorerPaneState Explorer { get; } = new();
    public string InputEvidenceWindowId { get; } = Guid.NewGuid().ToString("N");
    public long ObservedWorkerSession { get; set; } = -1;
    public long ObservedRuntimeGeneration { get; set; } = -1;

    /// <summary>Composition root attaches the worker; the session throws if a runtime already owns it.</summary>
    public void AttachWorker(Runtime.EmulationWorker runtime)
    {
        if (worker is not null || closed) throw new InvalidOperationException("Runtime already attached.");
        worker = runtime;
        Interlocked.Increment(ref runtimeGeneration);
        runtime.SessionRetired += KeyboardInput.Evidence.EndSession;
        // Retire owner bridges even when their tab is not selected. Capture this worker's
        // bridges: its delayed completion must never retire a replacement worker's bridges.
        var media = MediaBridge;
        var snapshots = SnapshotBridge;
        _ = runtime.Completion.ContinueWith(_ =>
        {
            media?.Disconnect();
            snapshots?.Disconnect();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Retire the session: generation bump, focus release, evidence end, shutdown enqueued.</summary>
    public void Retire()
    {
        if (closed) return;
        closed = true;
        Interlocked.Increment(ref runtimeGeneration);
        KeyboardInput.ReleaseFocus(InputReleaseOrigin.SessionEnd);
        KeyboardInput.Evidence.EndSession();
        InputQueue.Enqueue(new RuntimeControl(InputControlKind.Shutdown));
    }

    internal void MarkClosed() => closed = true;
    internal void BumpGeneration() => Interlocked.Increment(ref runtimeGeneration);
    internal void DetachWorker() => worker = null;

    // Host-held disk read admission (panel insert): one bounded operation that owns the single
    // native cold-read fence for this one frozen member. The operation is acknowledged by the actual
    // owner outcome or released by a named host drain at a lifecycle boundary; cancellation of an
    // await alone never releases pending work.
    public async Task<DiskReadReservation.Reservation> ReserveHeldReadAsync(DiskSlot slot, string path, CancellationToken cancellationToken)
    {
        var operation = DiskReadReservation.Operation.ClaimStart(new[] { slot }, cancellationToken);
        var previous = Interlocked.Exchange(ref heldOp, operation);
        if (previous is not null)
        {
            // A second insert while the fence is held: restore the parent (or clear an already
            // released one), release this refused claim, and refuse before any read.
            if (previous.Released) Interlocked.Exchange(ref heldOp, null);
            else Interlocked.Exchange(ref heldOp, previous);
            // The refused claim owns no member read, so the release reaches the fence synchronously.
            await operation.ReleaseOnceAsync("disk read already held");
            throw new InvalidOperationException("Disk read already held; wait for it to complete.");
        }
        try
        {
            return await operation.ReadMemberAsync(slot, path, cancellationToken);
        }
        catch
        {
            // A member read that failed before any enqueue left no pending owner work: the started
            // I/O was drained by the member read, so the operation ends and the claim is cleared.
            if (ReferenceEquals(Interlocked.CompareExchange(ref heldOp, null, operation), operation))
                await operation.ReleaseOnceAsync("member read failed before enqueue");
            throw;
        }
    }

    // One bounded configured start (prestart): claim the single native cold-read fence before any
    // file I/O and freeze the configured A/B membership (1-2 disks). The whole subset is then read
    // under the same live fence by PrestartMedia; a read or parse failure aborts the operation
    // before any enqueue. No configured disks means no operation: the legacy read path is unchanged.
    public DiskReadReservation.Operation? ClaimHeldConfiguredStart(MachineConfiguration configuration)
    {
        var slots = new List<DiskSlot>(2);
        if (configuration.DiskA is not null) slots.Add(DiskSlot.A);
        if (configuration.DiskB is not null) slots.Add(DiskSlot.B);
        if (slots.Count == 0) return null;
        var operation = DiskReadReservation.Operation.ClaimStart(slots);
        var previous = Interlocked.Exchange(ref heldOp, operation);
        if (previous is not null)
        {
            if (previous.Released) Interlocked.Exchange(ref heldOp, null);
            else Interlocked.Exchange(ref heldOp, previous);
            // The refused claim owns no members, so the release reaches the fence synchronously.
            _ = operation.ReleaseOnceAsync("configured start already held");
            throw new InvalidOperationException("Disk read already held; wait for it to complete.");
        }
        return operation;
    }

    // Named host-lifecycle drain: the held operation releases its single native fence here instead
    // of waiting for an owner reply that will never come. A released operation is a no-op.
    public void ReleaseHeldRead(string reason)
    {
        var operation = Interlocked.Exchange(ref heldOp, null);
        // The release is exact-once and self-draining: it awaits any in-flight member read before the
        // fence goes away, so the void wrapper need not wait for the quiescence.
        _ = operation?.ReleaseOnceAsync(reason);
    }

    // Release exactly this operation identity: the caller verified this operation's accounting across
    // an await (the mount hook), and a concurrent named drain must not be released here — a different
    // held operation keeps its own fence.
    public Task ReleaseHeldReadAsync(DiskReadReservation.Operation operation, string reason)
    {
        if (Interlocked.CompareExchange(ref heldOp, null, operation) is null)
            return Task.CompletedTask;
        return operation.ReleaseOnceAsync(reason);
    }

    // Protected original paths: a confirmed non-invalidated mount source or the file a held
    // reservation reads. Save targets that resolve to one of them are refused.
    public bool PathIsProtectedOriginal(string path)
    {
        string canonical;
        try { canonical = Path.GetFullPath(path); }
        catch { return false; }
        if (Volatile.Read(ref heldOp) is { } operation)
            foreach (var member in operation.Members)
                if (string.Equals(member.CanonicalPath, canonical, StringComparison.Ordinal)) return true;
        foreach (var observation in WritebackHost?.CaptureMountObservations() ?? [])
            if (string.Equals(observation.CanonicalSourcePath, canonical, StringComparison.Ordinal) && !observation.Invalidated) return true;
        return false;
    }
}
