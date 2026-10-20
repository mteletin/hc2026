using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Library;

/// <summary>Admitted content observation of one registered working copy: the bounded bytes read under the
/// live host cold-read fence plus the current content hash and container format. Original/initial
/// provenance stays in the referenced records; this record grants no dispatch permission.</summary>
public sealed record DiskLibraryCopyContentSnapshot(DiskLibraryEntry Original, DiskLibraryCopy Copy,
    ImmutableArray<byte> Bytes, string CurrentSha256, DiskLibraryFormat Format);

internal sealed record DiskLibraryCopyContentResult(DiskLibraryCopyContentSnapshot? Snapshot,
    DiskLibraryRefusal Reason, string Message);

/// <summary>Prepared carries the admitted content snapshot; the rest are closed refusals, not mounts,
/// writebacks or dispatch receipts.</summary>
public enum DiskLibraryCopySnapshotStatus { Prepared, Refused, OwnershipUnknown, UnknownCopy, Cancelled }

/// <summary>Fresh admitted identity of the copy file at the moment of the read: the canonical path, the
/// Linux file identity as the catalog records it, and the length the native admission measured.</summary>
public sealed record DiskLibraryCopyFileIdentity(string CanonicalPath, string? FileId, long Length);

public sealed record DiskLibraryCopySnapshotResult(DiskLibraryCopySnapshotStatus Status, string Message,
    DiskLibraryCopyContentSnapshot? Snapshot = null, DiskLibraryCopyFileIdentity? Identity = null,
    DiskLibraryRefusal? Refusal = null);

/// <summary>
/// Current managed-copy preparation under the authoritative host-held read admission. The copy is
/// resolved from the existing catalog metadata, the library root and copy path are bounded and checked
/// for links and aliases of the protected original, the single native cold-read fence is claimed (a
/// refused or busy claim refuses ownership-unknown before any file I/O), the bytes are read through the
/// existing admission/DSK surface (a guarded native open or the plain bounded read on legacy hosts), a
/// fresh native same-file check closes the identity window, and the immutable bytes are validated.
/// Initial length/hash remain provenance; a legitimately modified working copy may differ from
/// InitialSha256. The result is content and identity only: it never mounts, writes back or dispatches.
/// </summary>
public static class DiskLibraryCopySnapshot
{
    /// <summary>The membership marker slot a copy read freezes: a copy is not a machine disk, so one slot
    /// stands in for the frozen membership of this bounded read operation; no machine media is touched.</summary>
    private static readonly DiskSlot CopySlot = DiskSlot.A;

    /// <summary>
    /// Prepare one registered copy for current-content use. Refusals are closed and honest: a missing
    /// writer admission, a busy or refused cold-read fence, a linked or escaping path, an alias of the
    /// protected original or another managed copy, an active writer, or a changed identity refuses with
    /// the exact gap. No authority is fabricated; absent a required real surface the result is
    /// ownership-unknown.
    /// </summary>
    public static async Task<DiskLibraryCopySnapshotResult> PrepareAsync(DiskLibraryCatalog catalog,
        string copyId, string libraryRoot, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(copyId);
        ArgumentNullException.ThrowIfNull(libraryRoot);
        if (cancellation.IsCancellationRequested)
            return Cancelled("cancelled before host admission");

        // Registered metadata only, no I/O.
        var copy = catalog.Copies.FirstOrDefault(c => string.Equals(c.Id, copyId, StringComparison.Ordinal));
        if (copy is null)
            return new DiskLibraryCopySnapshotResult(DiskLibraryCopySnapshotStatus.UnknownCopy, "copy id is not registered");
        var entry = catalog.Entries.FirstOrDefault(e => string.Equals(e.Id, copy.EntryId, StringComparison.Ordinal));
        if (entry is null)
            return new DiskLibraryCopySnapshotResult(DiskLibraryCopySnapshotStatus.UnknownCopy,
                $"copy {copy.Id} names an unregistered original entry {copy.EntryId}");

        // Incomplete live accounting refuses: without the writer admission snapshot an active writer
        // cannot be ruled out.
        var admission = catalog.Admission;
        if (admission is null)
            return Ownership("the catalog host admission (writer accounting) is not injected; an active writer cannot be ruled out");

        // Bounded root/path algebra before any I/O: the copy path must stay inside the library root.
        string root, full;
        try
        {
            root = Path.GetFullPath(libraryRoot);
            if (Path.IsPathRooted(copy.RelativePath))
                return Ownership($"copy path {copy.RelativePath} is absolute and escapes the library root");
            full = Path.GetFullPath(Path.Combine(root, copy.RelativePath));
            if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return Ownership($"copy path {copy.RelativePath} escapes the library root");
        }
        catch (Exception ex)
        {
            return Refused(DiskLibraryRefusal.Unreadable, "library root path is invalid: " + ex.Message);
        }

        // The guarded native surface is mandatory: legacy hosts keep no native identity, so their bytes
        // cannot be bound to a fresh file identity; report the exact gap and stay fail-closed.
        if (NativeImageAdmission.PlatformMode != ImageAdmissionPlatformMode.GuardedLinux)
            return Ownership("the host has no guarded native image admission (legacy platform); a fresh file identity and read exclusion are unavailable");

        // Claim the single host cold-read fence: the authoritative host-held admission for this read.
        // A refused or busy claim means other live accounting holds the gate; refuse before any file I/O.
        DiskReadReservation.Operation operation;
        try
        {
            operation = DiskReadReservation.Operation.ClaimStart([CopySlot], cancellation);
        }
        catch (OperationCanceledException)
        {
            return Cancelled("cancelled while claiming the host cold-read fence");
        }
        catch (AdmissionException ex)
        {
            if (ex.Refusal == ImageAdmissionRefusal.Cancelled)
                return Cancelled("cancelled while claiming the host cold-read fence");
            return Ownership($"the host cold-read fence is held or refused ({ex.Refusal}); other live accounting is present, no copy image was read");
        }

        try
        {
            // Fresh bounded validation under the live fence: the root, every linked ancestor, the file
            // itself, its identity and size, and aliases of the protected original and managed copies.
            var rootFacts = LinuxFileFacts.Inspect(root);
            if (!rootFacts.Known)
                return Refused(DiskLibraryRefusal.Unreadable, "library root is missing or unstatable");
            if (rootFacts.Link)
                return Refused(DiskLibraryRefusal.FinalLink, "library root is a link");

            for (var dir = Path.GetDirectoryName(full); !string.Equals(dir, root, StringComparison.Ordinal); dir = Path.GetDirectoryName(dir)!)
            {
                if (dir is null)
                    break;
                var ancestor = LinuxFileFacts.Inspect(dir);
                if (!ancestor.Known || ancestor.Link)
                    return Refused(DiskLibraryRefusal.FinalLink, "a directory under the library root is missing or a link");
            }

            var facts = LinuxFileFacts.Inspect(full);
            if (!facts.Known)
                return Refused(DiskLibraryRefusal.Unreadable, "copy file is missing or unstatable");
            if (facts.Link)
                return Refused(DiskLibraryRefusal.FinalLink, "copy path is a link");
            if (!facts.Regular)
                return Refused(DiskLibraryRefusal.NotRegularFile, "copy path is not a regular file");
            if (facts.FileId is null)
                return Ownership("a fresh file identity is unavailable for the copy file");

            LinuxImageAdmissionNative.Facts native;
            try
            {
                native = LinuxImageAdmissionNative.FileAt(full, false);
            }
            catch (AdmissionException ex)
            {
                return Refused(DiskLibraryRefusal.Unreadable, "native admission refused the copy file: " + ex.Message);
            }
            if (native.Length > (ulong)DiskLibrarySchema.MaxContainerBytes)
                return Refused(DiskLibraryRefusal.Budget, "copy file exceeds the container budget");

            string original;
            try
            {
                original = Path.GetFullPath(entry.SourcePath);
            }
            catch
            {
                original = entry.SourcePath;
            }
            if (string.Equals(original, full, StringComparison.Ordinal))
                return Refused(DiskLibraryRefusal.RepeatOrAlias, "copy path is the protected original path");
            var originalFacts = LinuxFileFacts.Inspect(original);
            if (originalFacts.Known && string.Equals(originalFacts.FileId, facts.FileId, StringComparison.Ordinal))
                return Refused(DiskLibraryRefusal.RepeatOrAlias, "copy path aliases the protected original (same file identity)");
            foreach (var other in catalog.Copies.Where(c => !string.Equals(c.Id, copy.Id, StringComparison.Ordinal)))
            {
                var otherPath = Path.Combine(root, other.RelativePath);
                if (string.Equals(Path.GetFullPath(otherPath), full, StringComparison.Ordinal))
                    return Refused(DiskLibraryRefusal.RepeatOrAlias, $"copy path aliases managed copy {other.Id}");
                var otherFacts = LinuxFileFacts.Inspect(otherPath);
                if (otherFacts.Known && string.Equals(otherFacts.FileId, facts.FileId, StringComparison.Ordinal))
                    return Refused(DiskLibraryRefusal.RepeatOrAlias, $"copy path aliases managed copy {other.Id}");
            }

            foreach (var accounted in admission.Accounted)
            {
                if (string.Equals(accounted.FullPath, full, StringComparison.Ordinal) ||
                    string.Equals(accounted.FileId, facts.FileId, StringComparison.Ordinal))
                    return Refused(DiskLibraryRefusal.ActiveWriter, "copy file is mounted or writeback-owned by the host accounting");
            }

            // The admitted bounded read through the existing surface: a guarded native open plus bounded
            // read, or the plain bounded read on legacy hosts (unreachable here: the guarded platform is
            // required above).
            var reservation = await operation.ReadMemberAsync(CopySlot, full, cancellation);
            var bytes = reservation.Bytes;
            var identity = reservation.Identity!;

            // Fresh native same-file check closing the identity window, with the same comparison the
            // host admission uses for stale identity: the bytes must still be bound to the file opened.
            LinuxImageAdmissionNative.Facts current;
            try
            {
                current = LinuxImageAdmissionNative.FileAt(reservation.CanonicalPath, false);
            }
            catch (AdmissionException ex)
            {
                return Refused(DiskLibraryRefusal.Unreadable, "post-read identity check refused: " + ex.Message);
            }
            if (current.Id != identity.FileId || current.Length != (ulong)identity.Length)
                return Ownership("the copy identity changed between the bounded read and the post-read check; the bytes cannot be bound to an identity");

            // Current content: the existing DSK reader plus the pure owned-bytes validation.
            var content = ValidateOwnedBytes(entry, copy, bytes);
            if (content.Reason != DiskLibraryRefusal.Accepted || content.Snapshot is null)
                return Refused(content.Reason, content.Message);

            return new DiskLibraryCopySnapshotResult(DiskLibraryCopySnapshotStatus.Prepared,
                $"copy {copy.Id} prepared under the live host cold-read fence; original {entry.Id} sha {entry.Sha256} and initial sha {copy.InitialSha256} retained as provenance; current sha {content.Snapshot.CurrentSha256}, {content.Snapshot.Format}",
                content.Snapshot,
                new DiskLibraryCopyFileIdentity(reservation.CanonicalPath, facts.FileId, identity.Length),
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled("cancelled during the admitted copy read");
        }
        catch (InvalidDataException ex)
        {
            return Refused(DiskLibraryRefusal.Budget, "the copy read failed its budget: " + ex.Message);
        }
        catch (IOException ex)
        {
            return Refused(DiskLibraryRefusal.Unreadable, "the copy read failed: " + ex.Message);
        }
        finally
        {
            // Exact-once fence release, whatever the outcome: this read never dispatches, so its member
            // accounting is the only accounting of the operation.
            await operation.ReleaseOnceAsync("disk library copy snapshot " + copyId);
        }
    }

    /// <summary>
    /// Validate already-owned immutable bytes, not a file. Positive results cannot satisfy PrepareAsync's
    /// missing host admission, establish fresh identity or authorize dispatch. Initial length/hash remain
    /// provenance; the current working content may legitimately have changed since copy creation.
    /// </summary>
    internal static DiskLibraryCopyContentResult ValidateOwnedBytes(DiskLibraryEntry original,
        DiskLibraryCopy copy, ImmutableArray<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(copy);
        if (bytes.IsDefaultOrEmpty || bytes.Length > DiskLibrarySchema.MaxContainerBytes)
            return new(null, DiskLibraryRefusal.Budget, "current bytes are empty or exceed the container budget");

        // Reuse the strict schema's metadata/provenance rules rather than another catalog validator.
        try
        {
            var metadata = new DiskLibraryDocument(DiskLibrarySchema.Version, [original], [copy]);
            _ = DiskLibrarySchema.Load(DiskLibrarySchema.Encode(metadata));
        }
        catch (DiskLibrarySchemaException ex)
        { return new(null, DiskLibraryRefusal.ReconciliationNeeded, ex.Message); }

        Dsk disk;
        try { disk = Dsk.Load(bytes.AsSpan()); }
        catch (InvalidDataException ex)
        { return new(null, DiskLibraryRefusal.UnsupportedContainer, ex.Message); }
        if (disk.Format == DiskFormat.Raw)
            return new(null, DiskLibraryRefusal.UnsupportedContainer, "raw images require geometry");
        var format = disk.Format == DiskFormat.Extended ? DiskLibraryFormat.ExtendedDsk : DiskLibraryFormat.StandardDsk;
        var currentHash = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
        return new(new(original, copy, bytes, currentHash, format), DiskLibraryRefusal.Accepted,
            "owned bytes validated only; no host identity or admission established");
    }

    private static DiskLibraryCopySnapshotResult Refused(DiskLibraryRefusal refusal, string message)
        => new(DiskLibraryCopySnapshotStatus.Refused, message, null, null, refusal);

    private static DiskLibraryCopySnapshotResult Ownership(string message)
        => new(DiskLibraryCopySnapshotStatus.OwnershipUnknown, message, null, null, DiskLibraryRefusal.OwnershipUnknown);

    private static DiskLibraryCopySnapshotResult Cancelled(string message)
        => new(DiskLibraryCopySnapshotStatus.Cancelled, message);
}
