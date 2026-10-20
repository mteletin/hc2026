using System.Collections.Immutable;

namespace Hc2000.App.Media;

/// <summary>Binds a successful mount receipt to the existing coordinator. File identity comes only from Linux lstat.
/// Save completion is the helper task result, not a fabricated phase receipt.
/// MountObservation keeps the latest confirmed mount receipt per slot (receipt-accounting-split v1): a
/// partial post-receipt fact, NOT held-read authority, complete occupancy or stopped-host truth; absence
/// never means empty ownership, and PathFactsAtReceipt is a probe at the callback, not identity held during read.</summary>
internal sealed class DiskWritebackHost
{
    internal enum MountBindingState { Off, Pending, Bound, UnboundError }
    internal sealed record MountObservation(
        DiskSlot Slot, string SessionId, long RequestId, long Generation,
        string? CanonicalSourcePath, string MountedHash,
        LinuxFileFacts.Fact PathFactsAtReceipt,
        MountBindingState BindingState, bool Invalidated, string? Error,
        DiskReadReservation.HostFileIdentity? ReadIdentity = null,
        MediaImageDescriptor? Descriptor = null);

    private readonly MediaBridge bridge;
    private readonly DiskWritebackCoordinator coordinator;
    private readonly Dictionary<string, long> generations = new();
    private readonly Dictionary<string, string> paths = new();
    private readonly Dictionary<DiskSlot, MountObservation> observations = new();
    private string? lastError;

    public DiskWritebackHost(DiskWritebackOwnerLedger ledger, MediaBridge bridge, bool enabled)
    {
        Ledger = ledger;
        Enabled = enabled;
        this.bridge = bridge;
        coordinator = new(new FifoDiskWritebackOwner(bridge), new DiskWritebackPersistence());
    }

    public DiskWritebackOwnerLedger Ledger { get; }
    public bool Enabled { get; }
    public bool Frozen => Ledger.Frozen;
    public bool ReportsTarget => paths.Count != 0 || lastError is not null;

    /// <summary>Binding-preserving reset guard: true when the same-media reset may proceed with the
    /// writeback bindings retained. A false refusal means no reset and no dropped binding; invalidation
    /// remains reserved for confirmed changed or retired media.</summary>
    public bool ResetGuardClear() => coordinator.ResetGuardClear();

    /// <summary>Latest confirmed mount receipts per A/B slot as an immutable snapshot; a refused, stale or
    /// absent observation is never recorded and never erases the predecessor.</summary>
    internal ImmutableArray<MountObservation> CaptureMountObservations()
    {
        var snapshot = new List<MountObservation>(2);
        foreach (var slot in new[] { DiskSlot.A, DiskSlot.B })
            if (observations.TryGetValue(slot, out var observation)) snapshot.Add(observation);
        return snapshot.ToImmutableArray();
    }

    public async Task NoteMounted(DiskSlot slot, string path, ImmutableArray<byte> bytes, MediaReply reply, DiskReadReservation.HostFileIdentity? readIdentity = null,
        MediaImageDescriptor? descriptor = null)
    {
        var drive = slot == DiskSlot.A ? "A" : "B";
        var view = slot == DiskSlot.A ? reply.A : reply.B;
        // A refused insert (error or absent view) is never recorded and never erases the predecessor;
        // a confirmed mount records its observation even with writeback OFF (Off state, no binding).
        if (reply.Error is not null || !view.Present) return;
        string? canonical;
        try { canonical = Path.GetFullPath(path); }
        catch { canonical = null; }
        var mountedHash = DiskWritebackCoordinator.Hash(bytes);
        // A late or stale confirmed observation cannot overwrite newer slot state; the same request is idempotent.
        if (observations.TryGetValue(slot, out var current) &&
            (current.RequestId == reply.RequestId || current.Generation >= view.Generation)) return;
        var observation = new MountObservation(slot, Ledger.SessionId, reply.RequestId, view.Generation,
            canonical, mountedHash,
            canonical is null
                ? new LinuxFileFacts.Fact(false, false, false, false, null)
                : LinuxFileFacts.Inspect(canonical),
            Enabled ? MountBindingState.Pending : MountBindingState.Off, false, null,
            readIdentity, descriptor);
        observations[slot] = observation;
        if (!Enabled) return;
        // F9: the current writeback protocol validates containers and cannot validate a raw sector
        // image: refuse the writable binding explicitly. The confirmed mount is retained as
        // UnboundError; nothing is bound and no predecessor state is cleared.
        if (descriptor is { Kind: MediaImageKind.RawFloppy })
        {
            var rawRefusal = $"{drive}: raw sector-image writeback is not supported by the current protocol; the mount is retained without a writable binding.";
            lastError = rawRefusal;
            if (IsCurrent(slot, observation) && !observations[slot].Invalidated)
                observations[slot] = observations[slot] with { BindingState = MountBindingState.UnboundError, Error = rawRefusal };
            return;
        }
        // Clearing runs only after a confirmed successful mount: a refused insert must not erase the
        // predecessor's writeback state or observation (no clearing-before-validation).
        generations.Remove(drive);
        paths.Remove(drive);
        var mounted = new OwnerMountReceipt(drive, Ledger.SessionId, view.Generation, mountedHash, true);
        var bound = await coordinator.BindAsync(mounted, path, bytes, true);
        if (bound.Binding is null)
        {
            lastError = bound.Error ?? "Writeback bind refused.";
            // Binding failure keeps the ACTUAL new mount visible as UnboundError: not a fake owner refusal,
            // not a return to the predecessor. A late bind never undoes a newer generation or an
            // invalidation: it updates only a still-current, not-invalidated entry, and from that entry.
            if (IsCurrent(slot, observation) && !observations[slot].Invalidated)
                observations[slot] = observations[slot] with { BindingState = MountBindingState.UnboundError,
                    Error = bound.Error ?? "Writeback bind refused." };
            return;
        }
        generations[drive] = view.Generation;
        paths[drive] = bound.Binding.AbsoluteRegularPath;
        lastError = bound.Warning;
        if (IsCurrent(slot, observation) && !observations[slot].Invalidated)
            observations[slot] = observations[slot] with { BindingState = MountBindingState.Bound };
    }

    private bool IsCurrent(DiskSlot slot, MountObservation recorded)
        => observations.TryGetValue(slot, out var latest)
           && latest.RequestId == recorded.RequestId
           && latest.Generation == recorded.Generation;

    public async Task<bool> TryEjectAsync(DiskSlot slot)
    {
        var drive = slot == DiskSlot.A ? "A" : "B";
        if (!paths.ContainsKey(drive)) return false;
        // Capture the selected receipt and its matching writeback bookkeeping BEFORE the first await:
        // only the unchanged captured record may be forgotten (receipt-eject-retention-631571).
        var captured = observations.TryGetValue(slot, out var selected) ? selected : null;
        var capturedGeneration = generations[drive];
        var capturedPath = paths[drive];
        var capturedSession = Ledger.SessionId;
        var published = await coordinator.PersistAsync(drive, WritebackIntent.Eject);
        if (published.Outcome != WritebackPublication.Published)
        {
            lastError = published.Error ?? "Writeback eject refused.";
            return true;
        }
        var done = await coordinator.CompleteAsync(WritebackIntent.Eject, drive: drive);
        if (!done.Accepted)
        {
            lastError = done.Error ?? "Eject completion refused.";
            return true;
        }
        // Accepted is not eject evidence: the current ledger accepts a clean completion while the owner
        // still holds the disk. Forget the captured receipt only after exactly one read-only owner
        // readback through the existing bridge proves the covered eject: the target absent at the
        // captured generation +1, with the receipt still current. This is observation, not another eject.
        MediaReply? inspected = null;
        try { inspected = await bridge.RequestAsync(new MediaCommand(MediaAction.Inspect)); }
        catch (InvalidOperationException) { }
        var view = inspected is null ? null : slot == DiskSlot.A ? inspected.A : inspected.B;
        var covered = inspected is not null && inspected.Error is null && view is not null
            && !view.Present && view.Generation == capturedGeneration + 1
            && captured is not null && Ledger.SessionId == capturedSession
            && observations.TryGetValue(slot, out var current)
            && current.RequestId == captured.RequestId && current.Generation == captured.Generation
            && current.Invalidated == captured.Invalidated
            && generations.TryGetValue(drive, out var liveGeneration) && liveGeneration == capturedGeneration
            && paths.TryGetValue(drive, out var livePath) && livePath == capturedPath;
        if (covered)
        {
            // A genuine dirty covered-eject success removes only its matching captured entry.
            observations.Remove(slot);
            paths.Remove(drive);
            generations.Remove(drive);
            lastError = null;
        }
        else
        {
            // Accepted + Present or an unknown/stale readback retains the mount and its writeback
            // bookkeeping with a diagnostic; never label it ejected or clear from uncertainty.
            lastError = inspected is null
                ? $"{drive}: eject readback unavailable; mount retained."
                : inspected.Error is not null
                    ? $"{drive}: eject readback failed ({inspected.Error}); mount retained."
                    : view is { Present: true }
                        ? $"{drive}: writeback accepted but owner still present; mount retained."
                        : $"{drive}: eject readback generation mismatch; mount retained.";
        }
        return true;
    }

    public async Task ShortcutAsync()
    {
        foreach (var drive in paths.Keys.ToArray())
        {
            var published = await coordinator.PersistAsync(drive, WritebackIntent.Shortcut);
            if (published.Outcome != WritebackPublication.Published)
                lastError = published.Error ?? $"{drive}: writeback shortcut refused.";
        }
    }

    public async Task<string?> BeforeCloseAsync()
    {
        if (paths.Count == 0) return null;
        foreach (var drive in paths.Keys.ToArray())
        {
            var published = await coordinator.PersistAsync(drive, WritebackIntent.Close);
            if (published.Outcome != WritebackPublication.Published)
                return published.Error ?? $"{drive}: writeback close refused.";
        }
        var done = await coordinator.CompleteAsync(WritebackIntent.Close);
        if (!done.Accepted) return done.Error ?? "Close completion refused.";
        return null;
    }

    public async Task InvalidateAsync()
    {
        // Mark at entry and retain conservatively: clearing writeback bindings does not prove disk eject or
        // runtime retirement, so the observations survive even a successful binding invalidation.
        foreach (var slot in observations.Keys.ToArray())
            observations[slot] = observations[slot] with { Invalidated = true };
        if (!await coordinator.InvalidateAsync()) lastError = "Writeback bindings held by an unresolved fence.";
        else
        {
            paths.Clear();
            generations.Clear();
        }
    }

    public string StatusLine()
    {
        var parts = new[] { "A", "B" }.Select(drive =>
            paths.TryGetValue(drive, out var path) ? $"{drive}: writeback {path}" : $"{drive}: writeback off");
        var line = string.Join("; ", parts);
        return lastError is null ? line : line + "; " + lastError;
    }
}
