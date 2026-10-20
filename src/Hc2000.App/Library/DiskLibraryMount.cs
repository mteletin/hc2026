using System.Collections.Immutable;
using Hc2000.App.Media;

namespace Hc2000.App.Library;

/// <summary>Outcome of a one-action catalog mount. Mounted reports the prepared copy content and the
/// target status at both boundaries. Everything else is a closed refusal: nothing was dispatched or the
/// dispatch was rejected, and the active session is preserved exactly by the existing media path.</summary>
public enum DiskLibraryMountOutcome { Mounted, Refused, Cancelled, OwnershipUnknown, UnknownCopy, Busy, DispatchFailed }

/// <summary>Displayed status of a mount attempt: the registered original (never dispatched), the working
/// copy that was prepared and dispatched, the protection fact, and the target drive views before and
/// after the dispatch.</summary>
public sealed record DiskLibraryMountStatus(
    string OriginalId, string OriginalSha256, string OriginalSourcePath,
    string CopyId, string CopyRelativePath, string CopyCurrentSha256, DiskLibraryFormat CopyFormat, long CopyLength,
    bool OriginalDispatchedAsTarget,
    DriveView TargetBefore, DriveView TargetAfter);

public sealed record DiskLibraryMountResult(DiskLibraryMountOutcome Outcome, string Message,
    DiskLibraryCopyContentSnapshot? Prepared = null, DiskLibraryMountStatus? Status = null,
    DiskLibraryRefusal? Refusal = null);

/// <summary>
/// One-action mount of a registered working copy into A: or B:. Reuses the accepted surfaces end to end:
/// the copy is resolved from the existing catalog, its current content is prepared through the admitted
/// copy snapshot (host-held cold-read fence, fresh identity, DSK validation) and those exact bytes are
/// dispatched through the existing App media path (the same InsertDisk boundary the media panel
/// dispatches), so the accepted dirty/writeback discipline applies unchanged. The original is never
/// touched: only the prepared copy bytes are dispatched, so library originals never become writable
/// runtime targets. A refusal, cancellation or rejected dispatch preserves the active session: the
/// existing path refuses before the old medium is ejected. This class is not a mount or session engine.
/// </summary>
public sealed class DiskLibraryMount(DiskLibraryCatalog catalog, string libraryRoot)
{
    private readonly object gate = new();
    private bool _inFlight;

    /// <summary>Observe the current media boundary; the existing App media path's Inspect dispatch.
    /// Null means the media path is not wired and the mount refuses fail-closed before any read.</summary>
    public Func<Task<MediaReply>>? Inspect { get; set; }

    /// <summary>Dispatch the prepared copy bytes into the slot through the existing App media path (the
    /// media panel insert path without the file pick/read). Existing dirty/writeback semantics apply: a
    /// dirty unsaved target refuses before the old medium is ejected. Null = not wired.</summary>
    public Func<DiskSlot, ImmutableArray<byte>, string, Task<MediaReply>>? DispatchInsert { get; set; }

    /// <summary>Mount one registered copy into one slot in one action. One mount at a time; a concurrent
    /// attempt refuses as Busy rather than queueing a second dispatch.</summary>
    public async Task<DiskLibraryMountResult> MountAsync(string copyId, DiskSlot slot, CancellationToken cancellation = default)
    {
        bool began;
        lock (gate) { began = !_inFlight; _inFlight = true; }
        if (!began) return new(DiskLibraryMountOutcome.Busy, "another mount is already in flight; one action at a time");
        try
        {
            var copy = catalog.Copies.FirstOrDefault(c => string.Equals(c.Id, copyId, StringComparison.Ordinal));
            if (copy is null) return new(DiskLibraryMountOutcome.UnknownCopy, $"no registered working copy with id {copyId}");
            var original = catalog.Entries.FirstOrDefault(e => string.Equals(e.Id, copy.EntryId, StringComparison.Ordinal));
            if (original is null) return new(DiskLibraryMountOutcome.UnknownCopy, $"copy {copy.Id} references missing entry {copy.EntryId}");

            var inspect = Inspect; var dispatch = DispatchInsert;
            if (inspect is null || dispatch is null)
                return new(DiskLibraryMountOutcome.OwnershipUnknown,
                    "the existing App media path is not wired (inspect/dispatch seams absent); nothing was prepared or dispatched");

            var prepared = await DiskLibraryCopySnapshot.PrepareAsync(catalog, copy.Id, libraryRoot, cancellation);
            if (prepared.Status is not DiskLibraryCopySnapshotStatus.Prepared || prepared.Snapshot is not { } snapshot)
            {
                var (outcome, refusal) = prepared.Status switch
                {
                    DiskLibraryCopySnapshotStatus.UnknownCopy => (DiskLibraryMountOutcome.UnknownCopy, prepared.Refusal),
                    DiskLibraryCopySnapshotStatus.OwnershipUnknown => (DiskLibraryMountOutcome.OwnershipUnknown, prepared.Refusal),
                    DiskLibraryCopySnapshotStatus.Cancelled => (DiskLibraryMountOutcome.Cancelled, null),
                    DiskLibraryCopySnapshotStatus.Refused => (DiskLibraryMountOutcome.Refused, prepared.Refusal),
                    _ => (DiskLibraryMountOutcome.DispatchFailed, prepared.Refusal)
                };
                return new(outcome, prepared.Message, Refusal: refusal);
            }

            var before = await inspect();
            var beforeView = View(before, slot);
            var name = Path.GetFileName(copy.RelativePath);
            MediaReply reply;
            try
            {
                reply = await dispatch(slot, snapshot.Bytes, name);
            }
            catch (OperationCanceledException)
            {
                return new(DiskLibraryMountOutcome.Cancelled, "cancelled during the media path dispatch; the active session is preserved", Prepared: snapshot, Status: Status(original, copy, snapshot, beforeView));
            }
            catch (Exception ex)
            {
                DriveView after;
                try { after = View(await inspect(), slot); }
                catch { after = beforeView; } // the dispatch returned no owner outcome; the pre-dispatch view stays the honest observation
                return new(DiskLibraryMountOutcome.DispatchFailed, "the media path dispatch threw: " + ex.Message, Prepared: snapshot, Status: Status(original, copy, snapshot, beforeView, after));
            }

            var afterView = View(reply, slot);
            if (reply.Error is not null)
                return new(DiskLibraryMountOutcome.DispatchFailed, reply.Error, Prepared: snapshot, Status: Status(original, copy, snapshot, beforeView, afterView));
            if (!afterView.Present || !string.Equals(afterView.Name, name, StringComparison.Ordinal) || afterView.Generation <= beforeView.Generation)
                return new(DiskLibraryMountOutcome.DispatchFailed,
                    $"the dispatch reported success but the {slot} view did not mount {name} (generation {beforeView.Generation} -> {afterView.Generation}); the active session is preserved",
                    Prepared: snapshot, Status: Status(original, copy, snapshot, beforeView, afterView));

            return new(DiskLibraryMountOutcome.Mounted,
                $"working copy {copy.Id} ({name}) is mounted into {slot}; original {original.Id} is never a writable runtime target",
                Prepared: snapshot, Status: Status(original, copy, snapshot, beforeView, afterView));
        }
        finally
        {
            lock (gate) _inFlight = false;
        }
    }

    private static DriveView View(MediaReply reply, DiskSlot slot) => slot == DiskSlot.A ? reply.A : reply.B;

    private static DiskLibraryMountStatus Status(DiskLibraryEntry original, DiskLibraryCopy copy,
        DiskLibraryCopyContentSnapshot snapshot, DriveView before, DriveView? after = null) =>
        new(original.Id, original.Sha256, original.SourcePath, copy.Id, copy.RelativePath,
            snapshot.CurrentSha256, snapshot.Format, snapshot.Bytes.Length,
            OriginalDispatchedAsTarget: false, before, after ?? before);
}
