using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Runtime;

namespace Hc2000.App.Media;

public enum WritebackOwnerOp { Begin, Commit, InspectCommit, Abort, Complete, CancelCompletion, BeginHdd, PublishHdd, ReconcileHdd, AbortHdd, InspectHdd }

public sealed record WritebackOwnerRequest(
    WritebackOwnerOp Op,
    string? Drive = null,
    WritebackIntent Intent = WritebackIntent.Shortcut,
    PersistToken? Token = null,
    IReadOnlyList<PersistenceReceipt>? Receipts = null,
    byte HddDrive = 2,
    long HddSaveId = 0);

public sealed record WritebackOwnerReply(bool Accepted, bool SessionFrozen, FrozenWriteback? Snapshot = null, string? Error = null,
    HddSaveCapture? HddSave = null, HddPublicationView? HddPublication = null);

/// <summary>Owner-thread lease and session fence. Begin refusal with a null snapshot creates no lease.
/// A throw after a freeze is unknown to the caller, not a fabricated abort.</summary>
internal sealed class DiskWritebackOwnerLedger
{
    private readonly object gate = new();
    private FrozenWriteback? lease;
    private HddSaveCapture? hddLease;
    private PersistenceReceipt? committed;
    private bool completionHold;
    private bool wasPaused;
    private bool pauseHeld;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public bool Frozen { get { lock (gate) return lease is not null || hddLease is not null || completionHold; } }
    public bool ShutdownPermitted { get { lock (gate) return lease is null && hddLease is null && completionHold; } }

    public WritebackOwnerReply Apply(WritebackOwnerRequest? request, MediaSession session, RuntimeContext? runtime,
        InputQueue queue)
    {
        if (request is null || !Enum.IsDefined(request.Op))
            return new(false, Frozen, Error: "Writeback owner request missing.");
        return request.Op switch
        {
            WritebackOwnerOp.Begin => Begin(request, session, runtime, queue),
            WritebackOwnerOp.Commit => Commit(request, session),
            WritebackOwnerOp.InspectCommit => Inspect(request),
            WritebackOwnerOp.Abort => Abort(request, runtime),
            WritebackOwnerOp.Complete => Complete(request, session, runtime),
            WritebackOwnerOp.CancelCompletion => Cancel(runtime),
            WritebackOwnerOp.BeginHdd => BeginHdd(request, session, runtime, queue),
            WritebackOwnerOp.PublishHdd or WritebackOwnerOp.ReconcileHdd => PublishHdd(request, session, runtime),
            WritebackOwnerOp.AbortHdd => AbortHdd(request, session, runtime),
            WritebackOwnerOp.InspectHdd => InspectHdd(),
            _ => new(false, Frozen, Error: "Unknown writeback owner op.")
        };
    }

    public void OnShutdown()
    {
        lock (gate)
        {
            if (lease is not null || hddLease is not null) return;
            completionHold = false;
        }
    }

    private WritebackOwnerReply Begin(WritebackOwnerRequest request, MediaSession session, RuntimeContext? runtime, InputQueue queue)
    {
        lock (gate)
        {
            if (lease is not null || hddLease is not null || completionHold)
                return new(false, true, Error: "Writeback fence already held.");
        }
        if (queue.BoundaryCount != 0)
            return new(false, false, Error: "Foreign FIFO work is pending.");
        if (runtime?.Stopping == true || runtime?.Resetting == true)
            return new(false, false, Error: "Lifecycle conflict.");
        if (request.Drive is not ("A" or "B") || !Enum.IsDefined(request.Intent))
            return new(false, false, Error: "Begin needs drive A or B.");
        var slot = request.Drive == "A" ? DiskSlot.A : DiskSlot.B;
        var viewed = session.Apply(new(MediaAction.Inspect));
        var view = slot == DiskSlot.A ? viewed.A : viewed.B;
        if (!view.Present || view.Busy)
            return new(false, false, Error: "Drive empty or busy.");
        HoldPause(runtime);
        try
        {
            var exported = session.Apply(new(MediaAction.ExportDisk, slot));
            if (exported.Error is not null || exported.Export.IsDefaultOrEmpty)
                throw new InvalidOperationException(exported.Error ?? "Export failed.");
            var token = new PersistToken(request.Drive, SessionId, view.Generation,
                DiskWritebackCoordinator.Hash(exported.Export), Guid.NewGuid().ToString("N"));
            var frozen = new FrozenWriteback(token, exported.Export, true, wasPaused);
            lock (gate) lease = frozen;
            return new(true, true, frozen);
        }
        catch
        {
            lock (gate) { if (lease is null) ReleasePause(runtime); }
            throw;
        }
    }

    private WritebackOwnerReply Commit(WritebackOwnerRequest request, MediaSession session)
    {
        lock (gate)
        {
            if (lease is null || request.Token != lease.Token) return new(false, Frozen, Error: "Commit token is not the active lease.");
            var slot = lease.Token.Drive == "A" ? DiskSlot.A : DiskSlot.B;
            var exported = session.Apply(new(MediaAction.ExportDisk, slot));
            if (exported.Error is not null || DiskWritebackCoordinator.Hash(exported.Export) != lease.Token.SnapshotHash)
                return new(false, true, Error: "Frozen image is no longer current.");
            committed = new(lease.Token, lease.Token.SnapshotHash);
            lease = null;
            return new(true, completionHold);
        }
    }

    private WritebackOwnerReply Inspect(WritebackOwnerRequest request)
    {
        lock (gate)
        {
            if (request.Token is null || committed is null || request.Token != committed.Token
                || request.Token.SessionId != SessionId)
                return new(false, Frozen, Error: "No current-epoch commit for that token.");
            return new(true, Frozen);
        }
    }

    private WritebackOwnerReply Abort(WritebackOwnerRequest request, RuntimeContext? runtime)
    {
        lock (gate)
        {
            if (lease is null || request.Token != lease.Token) return new(false, Frozen, Error: "Abort token is not the active lease.");
            lease = null;
            committed = null;
            if (!completionHold) ReleasePause(runtime);
            return new(true, completionHold);
        }
    }

    private WritebackOwnerReply Complete(WritebackOwnerRequest request, MediaSession session, RuntimeContext? runtime)
    {
        lock (gate)
        {
            if (lease is not null || hddLease is not null) return new(false, true, Error: "Unresolved lease.");
            if (!Enum.IsDefined(request.Intent)) return new(false, Frozen, Error: "Invalid completion intent.");
            var receipts = request.Receipts ?? Array.Empty<PersistenceReceipt>();
            if (request.Intent == WritebackIntent.Eject)
            {
                if (request.Drive is not ("A" or "B")) return new(false, Frozen, Error: "Eject needs an exact drive.");
                var slot = request.Drive == "A" ? DiskSlot.A : DiskSlot.B;
                var view = session.Apply(new(MediaAction.Inspect));
                var drive = slot == DiskSlot.A ? view.A : view.B;
                if (drive.Busy) return new(false, Frozen, Error: "Drive busy.");
                if (drive.DirtySectors != 0)
                {
                    var exported = session.Apply(new(MediaAction.ExportDisk, slot));
                    var hash = exported.Error is null && !exported.Export.IsDefaultOrEmpty
                        ? DiskWritebackCoordinator.Hash(exported.Export) : null;
                    if (hash is null || !receipts.Any(r => r.Token.Drive == request.Drive && r.Token.SessionId == SessionId
                        && r.Token.MountIdentity == drive.Generation && r.PublishedHash == hash))
                        return new(false, Frozen, Error: "Eject receipt does not cover the current image.");
                }
                if (drive.Present)
                {
                    var ejected = session.EjectCovered(slot);
                    if (ejected.Error is not null) return new(false, true, Error: ejected.Error);
                }
                completionHold = false;
                ReleasePause(runtime);
                return new(true, false);
            }
            var dirty = DirtyWithoutReceipt(session, receipts);
            if (dirty is not null) return new(false, Frozen, Error: dirty);
            completionHold = true;
            HoldPause(runtime);
            return new(true, true);
        }
    }

    private WritebackOwnerReply Cancel(RuntimeContext? runtime)
    {
        lock (gate)
        {
            if (lease is not null || hddLease is not null) return new(false, true, Error: "Cannot cancel completion while a lease is open.");
            completionHold = false;
            ReleasePause(runtime);
            return new(true, false);
        }
    }

    private WritebackOwnerReply BeginHdd(WritebackOwnerRequest request, MediaSession session, RuntimeContext? runtime, InputQueue queue)
    {
        lock (gate)
        {
            if (Frozen) return new(false, true, Error: "Writeback fence already held.");
            if (queue.BoundaryCount != 0 || runtime?.Stopping == true || runtime?.Resetting == true)
                return new(false, false, Error: "Foreign FIFO work or lifecycle conflict.");
            HoldPause(runtime);
            HddSaveCapture? capture = null;
            try
            {
                capture = session.CaptureHddSave(request.HddDrive);
                session.FreezeHddSave(capture);
                hddLease = capture; // Set before replying: a lost Begin reply never releases the fence.
                return new(true, true, HddSave: capture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                ReleasePause(runtime);
                return new(false, false, Error: ex.Message, HddSave: capture);
            }
        }
    }

    private WritebackOwnerReply InspectHdd()
    {
        lock (gate) return new(true, Frozen, HddSave: hddLease);
    }

    private WritebackOwnerReply PublishHdd(WritebackOwnerRequest request, MediaSession session, RuntimeContext? runtime)
    {
        lock (gate)
        {
            if (hddLease?.Id != request.HddSaveId) return new(false, Frozen, Error: "HDD capture is not the active owner lease.");
            var result = session.PublishHddSave(request.HddSaveId, request.Op == WritebackOwnerOp.ReconcileHdd);
            if (!result.RequiresFence) { hddLease = null; ReleasePause(runtime); }
            return new(result.Acknowledged, Frozen, Error: result.Error, HddPublication: result);
        }
    }

    private WritebackOwnerReply AbortHdd(WritebackOwnerRequest request, MediaSession session, RuntimeContext? runtime)
    {
        lock (gate)
        {
            if (hddLease?.Id != request.HddSaveId || !session.AbortHddSave(request.HddSaveId))
                return new(false, Frozen, Error: "HDD publication is unresolved; reconcile the same capture.");
            hddLease = null;
            ReleasePause(runtime);
            return new(true, Frozen);
        }
    }

    private static string? DirtyWithoutReceipt(MediaSession session, IReadOnlyList<PersistenceReceipt> receipts)
    {
        var view = session.Apply(new(MediaAction.Inspect));
        foreach (var (drive, slot) in new[] { ("A", DiskSlot.A), ("B", DiskSlot.B) })
        {
            var current = slot == DiskSlot.A ? view.A : view.B;
            if (current.DirtySectors == 0) continue;
            var exported = session.Apply(new(MediaAction.ExportDisk, slot));
            if (exported.Error is not null) return exported.Error;
            var hash = DiskWritebackCoordinator.Hash(exported.Export);
            if (!receipts.Any(r => r.Token.Drive == drive && r.PublishedHash == hash && r.Token.MountIdentity == current.Generation))
                return $"Close refused: {drive}: has no covering receipt.";
        }
        return null;
    }

    private void HoldPause(RuntimeContext? runtime)
    {
        if (pauseHeld || runtime is null) return;
        wasPaused = runtime.Paused;
        runtime.Pause();
        pauseHeld = true;
    }

    private void ReleasePause(RuntimeContext? runtime)
    {
        if (!pauseHeld) return;
        if (runtime is not null && !wasPaused) runtime.Resume();
        pauseHeld = false;
    }
}

/// <summary>Host side of the existing media FIFO. It does not export, save or invent a lease.</summary>
internal sealed class FifoDiskWritebackOwner(MediaBridge bridge) : IDiskWritebackOwner
{
    public Task<WritebackBeginResult> BeginAsync(string drive, WritebackIntent intent) =>
        Send(new(WritebackOwnerOp.Begin, drive, intent), static reply =>
            new WritebackBeginResult(reply.Snapshot, reply.Snapshot is null ? reply.Error : null));

    public Task<WritebackOwnerResult> CommitAsync(PersistToken token) => Owner(new(WritebackOwnerOp.Commit, Token: token));
    public Task<WritebackOwnerResult> InspectCommitAsync(PersistToken token) => Owner(new(WritebackOwnerOp.InspectCommit, Token: token));
    public Task<WritebackOwnerResult> AbortAsync(PersistToken token) => Owner(new(WritebackOwnerOp.Abort, Token: token));
    public Task<WritebackOwnerResult> CompleteAsync(WritebackIntent intent, string? drive, IReadOnlyList<PersistenceReceipt> receipts) =>
        Owner(new(WritebackOwnerOp.Complete, drive, intent, Receipts: receipts));
    public Task<WritebackOwnerResult> CancelCompletionAsync() => Owner(new(WritebackOwnerOp.CancelCompletion));

    private async Task<WritebackOwnerResult> Owner(WritebackOwnerRequest request)
    {
        var reply = await Send(request, static value => value);
        return new(reply.Accepted, reply.SessionFrozen, reply.Error);
    }

    private async Task<T> Send<T>(WritebackOwnerRequest request, Func<WritebackOwnerReply, T> map)
    {
        var reply = await bridge.RequestAsync(new(MediaAction.Writeback, Writeback: request));
        if (reply.Writeback is null) throw new InvalidOperationException(reply.Error ?? "Writeback owner reply missing.");
        return map(reply.Writeback);
    }
}
