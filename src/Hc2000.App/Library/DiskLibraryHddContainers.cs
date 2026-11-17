using Hc2000.App.Media;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;
using Publication = Hc2000.HostFiles.HddContainerPublication;

namespace Hc2000.App.Library;

public enum DiskLibraryHddContainerStatus { Created, Reused, Refused, Cancelled, Unknown }
public sealed record DiskLibraryHddContainerResult(DiskLibraryHddContainerStatus Status, string Message,
    string? Path = null, HddVolumeIdentity? Identity = null, Publication.OfflineReceipt? Receipt = null);

public sealed partial class DiskLibraryCatalog
{
    private sealed record PendingHddContainer(string CopyId, string Path, Publication.OfflineAttempt Attempt);
    private PendingHddContainer? _pendingHddContainer;
    private const ulong LibraryHddContainerBytes = DiskLibrarySchema.HddBytes + 100UL;
    private static readonly Publication.PublicationLimits LibraryHddLimits = new(
        LibraryHddContainerBytes, 1, 36, 400, DiskLibrarySchema.HddBytes, 0, DiskLibrarySchema.HddBytes,
        3UL * DiskLibrarySchema.HddBytes + 32768, 5UL * DiskLibrarySchema.HddBytes,
        2 * LibraryHddContainerBytes, 8 * LibraryHddContainerBytes);

    /// <summary>Creates an independent private single-volume container from an admitted current seed
    /// snapshot. Source read admission is drained before publication admission is acquired: the receipt
    /// names those captured bytes, not a claim that the seed was frozen until publication. Once present,
    /// the container is authoritative and is never rebuilt from the seed, including after guest saves.
    /// Neither a preparation receipt nor the returned identity grants cold/live owner authority.</summary>
    public async Task<DiskLibraryHddContainerResult> PrepareHddContainerAsync(string copyId, string libraryRoot,
        CancellationToken cancellation = default)
    {
        try { await _mutation.WaitAsync(cancellation); }
        catch (OperationCanceledException) { return new(DiskLibraryHddContainerStatus.Cancelled, "cancelled before admission"); }
        try
        {
            if (_pendingHddContainer is not null || _pendingCopy is not null || _pendingUnknown is not null)
                return new(DiskLibraryHddContainerStatus.Unknown, "reconciliation required before preparation");
            RequireCopyAdmission();
            if (!NativeImageAdmission.IsGuarded)
                return new(DiskLibraryHddContainerStatus.Refused, "guarded native file identity required");
            // Strict wire validation bounds count, copy id/path and kind before deriving a destination.
            _ = DiskLibrarySchema.Load(DiskLibrarySchema.Encode(_document));
            var copy = _document.Copies.SingleOrDefault(c => c.Id == copyId);
            var entry = _document.Entries.SingleOrDefault(e => e.Id == copy?.EntryId);
            if (copy is null || entry is not { Kind: DiskLibraryKind.Hdd, Format: DiskLibraryFormat.Flat512 })
                return new(DiskLibraryHddContainerStatus.Refused, "registered HDD copy required");
            var root = System.IO.Path.GetFullPath(libraryRoot);
            if (System.IO.Path.GetDirectoryName(CatalogPath) != root)
                return new(DiskLibraryHddContainerStatus.Refused, "library root must be the catalog parent");
            using var rootHandle = ImageAdmissionNative.Directory(root, create: false);
            RequirePrivateDirectory(rootHandle);
            var directory = System.IO.Path.Combine(root, "containers");
            using var containerDirectory = ImageAdmissionNative.Directory(directory, create: true);
            RequirePrivateDirectory(containerDirectory);
            var target = System.IO.Path.Combine(directory, copy.Id + ".hdd");
            if (System.IO.Path.Exists(target))
                return AdmitLibraryHddContainer(copy.Id, target, DiskLibraryHddContainerStatus.Reused);
            RequireVacant(target);
            // PrepareAsync releases its actual native read fence on every return. No nested gate.
            var seed = await DiskLibraryCopySnapshot.PrepareAsync(this, copy.Id, root, cancellation);
            if (seed.Status != DiskLibraryCopySnapshotStatus.Prepared)
                return new(seed.Status == DiskLibraryCopySnapshotStatus.Cancelled
                    ? DiskLibraryHddContainerStatus.Cancelled : DiskLibraryHddContainerStatus.Refused, seed.Message);
            RequireCopyAdmission();
            RequireVacant(target);
            var request = new Publication.OfflineCreateRequest(target, Guid.NewGuid(), Guid.NewGuid(),
                new(true, true, true, true), LibraryHddLimits, new(1, 0, DiskLibrarySchema.HddBytes, 0));
            var attempt = Publication.CreateOffline(request, seed.Snapshot!.Bytes.AsSpan(), cancellation);
            var receipt = attempt.Receipt;
            if (receipt.Outcome == Publication.OfflineOutcome.Unknown)
            {
                _pendingHddContainer = new(copy.Id, target, attempt);
                return new(DiskLibraryHddContainerStatus.Unknown, "publication retained for reconciliation; no selection made", target, Receipt: receipt);
            }
            attempt.Dispose();
            if (receipt.Outcome == Publication.OfflineOutcome.KnownNew)
                return AdmitLibraryHddContainer(copy.Id, target, DiskLibraryHddContainerStatus.Created) with { Receipt = receipt };
            return new(receipt.Error == Publication.OfflineError.Cancelled
                ? DiskLibraryHddContainerStatus.Cancelled : DiskLibraryHddContainerStatus.Refused,
                "container publication: " + receipt.Error, Receipt: receipt);
        }
        catch (OperationCanceledException) { return new(DiskLibraryHddContainerStatus.Cancelled, "cancelled before selection"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or DiskLibrarySchemaException)
        { return new(DiskLibraryHddContainerStatus.Refused, ex.Message); }
        finally { _mutation.Release(); }
    }

    public async Task<DiskLibraryHddContainerResult> ReconcileHddContainerAsync()
    {
        await _mutation.WaitAsync();
        try
        {
            var pending = _pendingHddContainer;
            if (pending is null) return new(DiskLibraryHddContainerStatus.Refused, "no container reconciliation pending");
            var receipt = pending.Attempt.Reconcile();
            if (receipt.Outcome == Publication.OfflineOutcome.Unknown)
                return new(DiskLibraryHddContainerStatus.Unknown, "container publication remains unknown", pending.Path, Receipt: receipt);
            _pendingHddContainer = null;
            pending.Attempt.Dispose();
            if (receipt.Outcome == Publication.OfflineOutcome.KnownNew)
                return AdmitLibraryHddContainer(pending.CopyId, pending.Path, DiskLibraryHddContainerStatus.Created) with { Receipt = receipt };
            return new(DiskLibraryHddContainerStatus.Refused, "publication reconciled: " + receipt.Outcome, Receipt: receipt);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return new(DiskLibraryHddContainerStatus.Refused, ex.Message); }
        finally { _mutation.Release(); }
    }

    /// <summary>Read-only intended-target inspection for guest-safe remap. Never materializes
    /// media or claims a stopped host; the owner must reopen under the guest ACK hold.</summary>
    public async Task<DiskLibraryHddContainerResult> InspectPreparedHddContainerAsync(string copyId, string libraryRoot,
        CancellationToken cancellation = default)
    {
        try { await _mutation.WaitAsync(cancellation); }
        catch (OperationCanceledException) { return new(DiskLibraryHddContainerStatus.Cancelled, "target inspection cancelled"); }
        try
        {
            if (_pendingHddContainer is not null || _pendingCopy is not null || _pendingUnknown is not null)
                return new(DiskLibraryHddContainerStatus.Unknown, "reconciliation required before target inspection");
            if (!NativeImageAdmission.IsGuarded)
                return new(DiskLibraryHddContainerStatus.Refused, "guarded native file identity required");
            _ = DiskLibrarySchema.Load(DiskLibrarySchema.Encode(_document));
            var copy = _document.Copies.SingleOrDefault(c => c.Id == copyId);
            var entry = _document.Entries.SingleOrDefault(e => e.Id == copy?.EntryId);
            if (copy is null || entry is not { Kind: DiskLibraryKind.Hdd, Format: DiskLibraryFormat.Flat512 })
                return new(DiskLibraryHddContainerStatus.Refused, "registered HDD copy required");
            var root = System.IO.Path.GetFullPath(libraryRoot);
            if (System.IO.Path.GetDirectoryName(CatalogPath) != root)
                return new(DiskLibraryHddContainerStatus.Refused, "library root must be the catalog parent");
            using var rootHandle = ImageAdmissionNative.Directory(root, create: false);
            RequirePrivateDirectory(rootHandle);
            var directory = System.IO.Path.Combine(root, "containers");
            using var containerDirectory = ImageAdmissionNative.Directory(directory, create: false);
            RequirePrivateDirectory(containerDirectory);
            var target = System.IO.Path.Combine(directory, copy.Id + ".hdd");
            using var admitted = HddColdVolume.Open(target, "LIBRARY.DSK", writable: true);
            var facts = ImageAdmissionNative.FileAt(target, false);
            if (facts.Links != 1 || facts.Length != LibraryHddContainerBytes)
                throw new IOException("private container must be single-volume ordinary non-aliased media");
            var id = FileId(target);
            if (id is null || _document.Entries.Any(e => FileId(e.SourcePath) == id)
                || _document.Copies.Any(c => FileId(System.IO.Path.Combine(root, c.RelativePath)) == id))
                throw new IOException("container identity unavailable or aliases a protected original/raw seed");
            cancellation.ThrowIfCancellationRequested();
            return new(DiskLibraryHddContainerStatus.Reused, "intended target inspected only; guest ACK and fresh owner admission still required", target, admitted.Identity);
        }
        catch (OperationCanceledException) { return new(DiskLibraryHddContainerStatus.Cancelled, "target inspection cancelled"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or DiskLibrarySchemaException)
        { return new(DiskLibraryHddContainerStatus.Refused, ex.Message); }
        finally { _mutation.Release(); }
    }

    private DiskLibraryHddContainerResult AdmitLibraryHddContainer(string copyId, string target, DiskLibraryHddContainerStatus status)
    {
        RequireCopyAdmission();
        // Fresh D3 admission, not a filename/receipt/seed-hash shortcut. It checks metadata and payload CRC.
        using var admitted = HddColdVolume.Open(target, "LIBRARY.DSK", writable: true);
        var facts = ImageAdmissionNative.FileAt(target, false);
        if (facts.Links != 1 || facts.Length != LibraryHddContainerBytes)
            throw new IOException("private container must be a single-volume ordinary non-aliased file");
        var id = FileId(target);
        RequireUnaccounted(target, id);
        var root = System.IO.Path.GetDirectoryName(CatalogPath)!;
        if (_document.Entries.Any(e => FileId(e.SourcePath) == id)
            || _document.Copies.Any(c => FileId(System.IO.Path.Combine(root, c.RelativePath)) == id))
            throw new IOException("container aliases a protected original or raw seed");
        return new(status, $"copy {copyId}: private container is the working medium; raw .img remains seed/provenance only; not mounted",
            target, admitted.Identity);
    }
}
