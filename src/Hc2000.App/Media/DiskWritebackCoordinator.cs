using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Media;

public enum WritebackIntent { Shortcut, Eject, Close }
public enum WritebackPublication { UnpublishedAbort, Published, Unknown }
public sealed record WritebackBinding(string Drive, string AbsoluteRegularPath, string MountHash,
    string SessionId, long MountIdentity, string? FileId = null);
public sealed record PersistToken(string Drive, string SessionId, long MountIdentity,
    string SnapshotHash, string AttemptId);
public sealed record PersistenceReceipt(PersistToken Token, string PublishedHash);
public sealed record OwnerMountReceipt(string Drive, string SessionId, long MountIdentity,
    string MountedHash, bool Succeeded);
public sealed record FrozenWriteback(PersistToken Token, ImmutableArray<byte> Bytes,
    bool SessionFrozen, bool PreviouslyPaused);
public sealed record WritebackBeginResult(FrozenWriteback? Snapshot, string? Refusal = null);
public sealed record WritebackOwnerResult(bool Accepted, bool SessionFrozen, string? Error = null);
public sealed record WritebackTargetInfo(bool Regular, bool Link, bool Writable,
    ImmutableArray<byte> Bytes, string? FileId = null);
public sealed record WritebackPublishResult(WritebackPublication Outcome, bool WriterDrained,
    string? PublishedHash = null, string? Error = null);
public sealed record WritebackResult(WritebackPublication Outcome, PersistenceReceipt? Receipt = null,
    string? Error = null);
public sealed record WritebackBindResult(WritebackBinding? Binding, string? Error = null, string? Warning = null);

/// <summary>Adapter to the EXISTING owner FIFO, not a new worker. Begin excludes its own pending
/// request, refuses foreign media/FDC/lifecycle work, and freezes the entire session. Drive/session/
/// mount/AttemptId are owner identities, not dirty counts. Commit consumes a token once; abort retains
/// dirty marks. Close/eject intent keeps the freeze through guarded completion/cancellation. Neither
/// completion releases an unresolved writer. Releasing the last fence restores the prior pause state.</summary>
public interface IDiskWritebackOwner
{
    Task<WritebackBeginResult> BeginAsync(string drive, WritebackIntent intent);
    Task<WritebackOwnerResult> CommitAsync(PersistToken token);
    // Query the already-issued commit/receipt; never consume the token a second time.
    Task<WritebackOwnerResult> InspectCommitAsync(PersistToken token);
    Task<WritebackOwnerResult> AbortAsync(PersistToken token);
    Task<WritebackOwnerResult> CompleteAsync(WritebackIntent intent, string? drive,
        IReadOnlyList<PersistenceReceipt> receipts);
    Task<WritebackOwnerResult> CancelCompletionAsync();
}

/// <summary>Injected host capabilities. Inspect uses bounded existing media reading; Publish calls
/// existing MediaFiles.SaveAsync only, with no alternative writer. Reconcile OBSERVES/drains that
/// attempt and reads back; it must never start a replacement. A returned task's cancellation alone
/// cannot establish WriterDrained. The exclusive cooperating-writer assumption is NOT a lock/CAS.</summary>
public interface IDiskWritebackPersistence
{
    Task<WritebackTargetInfo> InspectAsync(string absolutePath);
    Task<WritebackPublishResult> PublishAsync(string absolutePath, ImmutableArray<byte> bytes, PersistToken token);
    Task<WritebackPublishResult> ReconcileAsync(string absolutePath, PersistToken token);
}

/// <summary>Host-only bindings and persistence orchestration. No mount/export/runtime/prefs implementation,
/// host paths in Machine/HCS, dirty clearing, DiscardChanges, backup writer or forced guest resume.</summary>
public sealed class DiskWritebackCoordinator(IDiskWritebackOwner owner, IDiskWritebackPersistence persistence)
{
    private sealed record Target(WritebackBinding Binding, string ExpectedHash);
    private sealed class Pending(Target target, FrozenWriteback snapshot)
    {
        public Target Target { get; } = target;
        public FrozenWriteback Snapshot { get; } = snapshot;
        public Task<WritebackPublishResult>? Writer { get; set; }
        public bool WriterStarted { get; set; }
        public bool CommitInvoked { get; set; }
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Target> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistenceReceipt> receipts = new(StringComparer.Ordinal);
    private readonly HashSet<string> attempts = new(StringComparer.Ordinal);
    private bool ownerUnknown;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Hash(ImmutableArray<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
    private static bool Drive(string drive) => drive is "A" or "B";
    private static WritebackResult Refuse(string reason) => new(WritebackPublication.UnpublishedAbort, Error: reason);
    private static WritebackResult Unknown(string reason) => new(WritebackPublication.Unknown, Error: reason);

    public async Task<WritebackBindResult> BindAsync(OwnerMountReceipt mount, string sourcePath,
        ImmutableArray<byte> mountedBytes, bool enabled)
    {
        if (!await gate.WaitAsync(0)) return new(null, "Writeback request pending.");
        try
        {
            if (ownerUnknown || pending.Count != 0) return new(null, "Unresolved owner/writer fence.");
            if (!enabled)
            {
                targets.Remove(mount.Drive); receipts.Remove(mount.Drive);
                return new(null, "Writeback off.");
            }
            if (!Drive(mount.Drive) || !mount.Succeeded || string.IsNullOrWhiteSpace(mount.SessionId)
                || mount.MountedHash != Hash(mountedBytes)) return new(null, "No matching successful mount receipt.");
            // A confirmed replacement cannot inherit its predecessor's overwrite capability.
            targets.Remove(mount.Drive); receipts.Remove(mount.Drive);
            var full = Path.GetFullPath(sourcePath);
            var info = await persistence.InspectAsync(full);
            Validate(info.Bytes);
            if (!info.Regular || info.Link || Hash(info.Bytes) != mount.MountedHash)
                return new(null, "Source is not the unchanged mounted regular file.");
            if (targets.Any(t => t.Key != mount.Drive &&
                (string.Equals(t.Value.Binding.AbsoluteRegularPath, full, PathComparison)
                 || info.FileId is not null && info.FileId == t.Value.Binding.FileId)))
                return new(null, "A/B writeback target collision.");
            var binding = new WritebackBinding(mount.Drive, full, mount.MountedHash,
                mount.SessionId, mount.MountIdentity, info.FileId);
            targets[mount.Drive] = new(binding, mount.MountedHash);
            receipts.Remove(mount.Drive);
            return new(binding, Warning: info.FileId is null
                ? "File identity unavailable: unreported hardlink alias remains a residual." : null);
        }
        catch (Exception ex) { return new(null, "Binding refused: " + ex.Message); }
        finally { gate.Release(); }
    }

    /// <summary>Binding-preserving reset guard: true when a same-media machine reset may proceed with the
    /// writeback bindings retained. A false refusal drops no binding: it means the reset may not run yet
    /// (a host request holds the gate, an owner/writer fence is pending, or the owner outcome is unknown),
    /// never that a binding was invalidated. Invalidation remains reserved for confirmed changed or
    /// retired media.</summary>
    public bool ResetGuardClear()
    {
        if (!gate.Wait(0)) return false;
        try { return pending.Count == 0 && !ownerUnknown; }
        finally { gate.Release(); }
    }

    /// <summary>Called only for confirmed picker/drag/replacement/eject/HCS invalidation.
    /// A pending freeze must be resolved first, never silently removed.</summary>
    public async Task<bool> InvalidateAsync(string? drive = null)
    {
        if (!await gate.WaitAsync(0)) return false;
        try
        {
            if (ownerUnknown || pending.Count != 0) return false;
            if (drive is null) { targets.Clear(); receipts.Clear(); }
            else { targets.Remove(drive); receipts.Remove(drive); }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<WritebackResult> PersistAsync(string drive, WritebackIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0)) return Refuse("Writeback request pending.");
        Pending? active = null;
        var beginInvoked = false;
        try
        {
            if (ownerUnknown || pending.Count != 0) return Unknown("Unresolved owner/writer fence.");
            if (!Drive(drive) || !Enum.IsDefined(intent) || !targets.TryGetValue(drive, out var target))
                return Refuse("Writeback off or unbound.");
            cancellationToken.ThrowIfCancellationRequested();
            beginInvoked = true;
            var begin = await owner.BeginAsync(drive, intent);
            if (begin.Snapshot is null) return Refuse(begin.Refusal ?? "Owner refused Begin.");
            active = new(target, begin.Snapshot);
            pending[drive] = active;
            var token = active.Snapshot.Token;
            if (!active.Snapshot.SessionFrozen || token.Drive != drive
                || token.SessionId != target.Binding.SessionId || token.MountIdentity != target.Binding.MountIdentity
                || string.IsNullOrWhiteSpace(token.AttemptId) || !attempts.Add(token.AttemptId)
                || token.SnapshotHash != Hash(active.Snapshot.Bytes))
                return Unknown("Invalid frozen mount/token; no writer started, owner fence needs reconciliation.");
            Validate(active.Snapshot.Bytes);
            var info = await persistence.InspectAsync(target.Binding.AbsoluteRegularPath);
            Validate(info.Bytes);
            if (!info.Regular || info.Link || !info.Writable || Hash(info.Bytes) != target.ExpectedHash
                || target.Binding.FileId is not null && info.FileId != target.Binding.FileId)
                return await AbortBeforeWriter(active, "Target missing, changed, corrupt or unwritable.");
            cancellationToken.ThrowIfCancellationRequested();
            active.WriterStarted = true; // Set BEFORE invoking: even a synchronous throw may hide a live writer.
            active.Writer = persistence.PublishAsync(target.Binding.AbsoluteRegularPath, active.Snapshot.Bytes, token);
            var publication = await active.Writer.WaitAsync(cancellationToken);
            return await Settle(active, publication);
        }
        catch (Exception ex)
        {
            // Once publication was invoked, even a cancelled/faulted await may hide a live rename.
            if (active is not null && !active.WriterStarted)
                return await AbortBeforeWriter(active, "Pre-publication refusal: " + ex.Message);
            if (!beginInvoked) return Refuse("Cancelled before Begin: " + ex.Message);
            if (active is null) ownerUnknown = true;
            return Unknown("Owner/writer completion unknown: " + ex.Message);
        }
        finally { gate.Release(); }
    }

    public async Task<WritebackResult> ReconcileAsync(string drive)
    {
        if (!await gate.WaitAsync(0)) return Unknown("Writeback request pending.");
        try
        {
            if (!pending.TryGetValue(drive, out var active)) return Refuse("No unresolved attempt.");
            if (!active.WriterStarted) return Unknown("Owner-only fence unresolved; no publication to reconcile.");
            var result = active.Writer is { IsCompletedSuccessfully: true } writer ? await writer : null;
            if (result is null || !result.WriterDrained || result.Outcome == WritebackPublication.Unknown)
                result = await persistence.ReconcileAsync(active.Target.Binding.AbsoluteRegularPath, active.Snapshot.Token);
            return await Settle(active, result);
        }
        catch (Exception ex) { return Unknown("Reconciliation unknown: " + ex.Message); }
        finally { gate.Release(); }
    }

    public async Task<WritebackOwnerResult> CompleteAsync(WritebackIntent intent, bool cancel = false, string? drive = null)
    {
        if (!await gate.WaitAsync(0)) return new(false, true, "Writeback request pending.");
        try
        {
            if (ownerUnknown || pending.Count != 0) return new(false, true, "Unresolved owner/writer fence.");
            if (!Enum.IsDefined(intent) || intent == WritebackIntent.Eject && (drive is null || !Drive(drive)))
                return new(false, true, "Completion needs a valid intent and exact eject drive.");
            // Owner checks receipt freshness AND existing HDD/tape/dirty/close/eject guards while frozen.
            var result = cancel ? await owner.CancelCompletionAsync()
                : await owner.CompleteAsync(intent, drive, receipts.Values.ToArray());
            if (result.Accepted) receipts.Clear();
            return result;
        }
        catch (Exception ex) { ownerUnknown = true; return new(false, true, ex.Message); }
        finally { gate.Release(); }
    }

    private async Task<WritebackResult> AbortBeforeWriter(Pending active, string reason)
    {
        try
        {
            var abort = await owner.AbortAsync(active.Snapshot.Token);
            if (!abort.Accepted) return Unknown("Owner abort unresolved: " + (abort.Error ?? reason));
            pending.Remove(active.Snapshot.Token.Drive);
            return Refuse(reason);
        }
        catch (Exception ex) { return Unknown("Owner abort unknown: " + ex.Message); }
    }

    private async Task<WritebackResult> Settle(Pending active, WritebackPublishResult publication)
    {
        var token = active.Snapshot.Token;
        if (!publication.WriterDrained || publication.Outcome == WritebackPublication.Unknown)
            return Unknown(publication.Error ?? "Writer can still rename; fence retained.");
        if (publication.Outcome == WritebackPublication.UnpublishedAbort)
            return await AbortBeforeWriter(active, publication.Error ?? "Unpublished writer abort.");
        if (publication.Outcome != WritebackPublication.Published || publication.PublishedHash != token.SnapshotHash)
            return Unknown("Publication receipt mismatch.");
        var info = await persistence.InspectAsync(active.Target.Binding.AbsoluteRegularPath);
        Validate(info.Bytes);
        if (!info.Regular || info.Link || Hash(info.Bytes) != token.SnapshotHash)
            return Unknown("Published image readback mismatch; fence retained.");
        WritebackOwnerResult commit;
        if (active.CommitInvoked) commit = await owner.InspectCommitAsync(token);
        else
        {
            active.CommitInvoked = true;
            commit = await owner.CommitAsync(token);
        }
        if (!commit.Accepted) return Unknown("Published but owner acknowledgement unknown: " + commit.Error);
        var receipt = new PersistenceReceipt(token, token.SnapshotHash);
        receipts[token.Drive] = receipt;
        targets[token.Drive] = active.Target with
        {
            ExpectedHash = token.SnapshotHash,
            Binding = active.Target.Binding with { FileId = info.FileId }
        };
        pending.Remove(token.Drive);
        return new(WritebackPublication.Published, receipt);
    }

    private static void Validate(ImmutableArray<byte> bytes)
    {
        if (bytes.IsDefaultOrEmpty || bytes.Length > MediaFiles.MaxDiskBytes)
            throw new InvalidDataException("Invalid or oversized disk image.");
        _ = Dsk.Load(bytes.AsSpan());
    }
}
