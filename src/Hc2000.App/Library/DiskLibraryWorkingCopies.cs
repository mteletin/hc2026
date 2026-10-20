using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Library;

public enum DiskLibraryCopyStatus { Created, Refused, Cancelled, Unknown }
public sealed record DiskLibraryCopyResult(DiskLibraryCopyStatus Status, DiskLibraryCopy? Copy,
    string Message, string? OrphanPath = null);

/// <summary>Fault/hold seam around production-owned copy steps. The writer is opened create-new by
/// production before WriteStage is called, and always closed/drained before verification or cleanup.
/// Wrappers must await their operation; these delegates are not live host admission authority.</summary>
public sealed class DiskLibraryWorkingCopyPublication
{
    public Func<FileStream, ImmutableArray<byte>, CancellationToken, Task> WriteStage { get; set; } = WriteAsync;
    public Func<string, CancellationToken, Task<ImmutableArray<byte>>> ReadBack { get; set; } = ReadAsync;
    public Action<string, string> Publish { get; set; } = (stage, final) => File.Move(stage, final, overwrite: false);
    public Action<string> Delete { get; set; } = File.Delete;

    private static async Task WriteAsync(FileStream stream, ImmutableArray<byte> bytes, CancellationToken cancellation)
    {
        for (var offset = 0; offset < bytes.Length; offset += 65536)
        {
            cancellation.ThrowIfCancellationRequested();
            await stream.WriteAsync(bytes.AsMemory().Slice(offset, Math.Min(65536, bytes.Length - offset)), cancellation);
        }
        await stream.FlushAsync(cancellation);
    }

    internal static async Task<ImmutableArray<byte>> ReadAsync(string path, CancellationToken cancellation)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return await ReadStreamAsync(input, cancellation);
    }

    internal static async Task<ImmutableArray<byte>> ReadStreamAsync(FileStream input, CancellationToken cancellation)
    {
        if (input.Length is < 1 or > DiskLibrarySchema.MaxContainerBytes) throw new InvalidDataException("image size is outside the copy budget");
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer, cancellation);
            if (count == 0) break;
            if (output.Length + count > DiskLibrarySchema.MaxContainerBytes) throw new InvalidDataException("image grew beyond the copy budget");
            output.Write(buffer, 0, count);
        }
        return ImmutableArray.CreateRange(output.ToArray());
    }
}

public sealed partial class DiskLibraryCatalog
{
    private sealed record PendingCopy(DiskLibraryDocument Prior, DiskLibraryDocument Candidate,
        DiskLibraryCopy Copy, string Stage, string Final, string OwnedId,
        NativeImageAdmission Admission, DiskLibraryWorkingCopyPublication Publication, bool CatalogAttempted,
        bool CatalogPublished = false);
    private PendingCopy? _pendingCopy;

    /// <summary>Create one verified independent image and then publish its catalog reference. The root
    /// must be the catalog's parent, owned by this user and private (0700); new directories are 0700.
    /// No source permission changes, mount, or writeback occurs. Missing admission refuses. The host
    /// must supply stopped/quiescent ownership and exclusive cooperating access, not merely paused CPU.
    /// Cancellation requests drain, never abandons an in-flight delegate. Unknown retains native admission
    /// and hides the candidate until ReconcileWorkingCopyAsync settles it.</summary>
    public async Task<DiskLibraryCopyResult> CreateWorkingCopyAsync(string entryId, string libraryRoot,
        DiskLibraryWorkingCopyPublication? publication = null, DiskLibraryPublication? catalogPublication = null,
        CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try { await _mutation.WaitAsync(token); }
        catch (OperationCanceledException) { return CopyResult(DiskLibraryCopyStatus.Cancelled, "cancelled before admission"); }
        NativeImageAdmission? native = null;
        string? stage = null, final = null, ownedId = null;
        var pub = publication ?? new DiskLibraryWorkingCopyPublication();
        var prior = _document;
        var catalogAttempted = false;
        DiskLibraryCopy? copy = null;
        DiskLibraryDocument? candidate = null;
        try
        {
            if (_pendingUnknown is not null || _pendingCopy is not null)
                return CopyResult(DiskLibraryCopyStatus.Unknown, "reconciliation is required before another mutation");
            var entry = prior.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return CopyResult(DiskLibraryCopyStatus.Refused, "unknown entry id");
            if (prior.Copies.Length >= DiskLibrarySchema.MaxCopies || prior.Copies.Count(c => c.EntryId == entryId) >= DiskLibrarySchema.MaxCopiesPerEntry)
                return CopyResult(DiskLibraryCopyStatus.Refused, "copy count budget exceeded");
            RequireCopyAdmission();
            if (!OperatingSystem.IsLinux()) return CopyResult(DiskLibraryCopyStatus.Refused, "trustworthy Linux identity is unavailable");
            var acquired = NativeImageAdmission.TryAcquireColdRead(token);
            native = acquired.Admission;
            if (native is null) return CopyResult(DiskLibraryCopyStatus.Refused, "native admission refused: " + acquired.Refusal);
            var root = Path.GetFullPath(libraryRoot);
            if (Path.GetDirectoryName(CatalogPath) != root)
                return CopyResult(DiskLibraryCopyStatus.Refused, "library root must be the catalog parent directory");
            using var rootHandle = LinuxImageAdmissionNative.Directory(root, create: true);
            RequirePrivateDirectory(rootHandle);
            var copies = Path.Combine(root, "copies");
            using var copiesHandle = LinuxImageAdmissionNative.Directory(copies, create: true);
            RequirePrivateDirectory(copiesHandle);
            RequireSafeCatalogPath();

            using var source = native.OpenColdSource(entry.SourcePath);
            var before = native.ValidateOpenedSource(source, entry.SourcePath);
            var sourceId = FileId(entry.SourcePath);
            RequireUnaccounted(entry.SourcePath, sourceId);
            if (prior.Entries.Any(e => e.Id != entryId && (e.SourcePath == entry.SourcePath || FileId(e.SourcePath) == sourceId)) ||
                prior.Copies.Any(c => FileId(Path.Combine(root, c.RelativePath)) == sourceId))
                throw new IOException("source aliases another original or managed copy");
            var bytes = await DiskLibraryWorkingCopyPublication.ReadStreamAsync(source, token);
            native.ValidateSourceStillCurrent(source, before);
            if (bytes.Length != entry.Length || Sha256Hex(bytes) != entry.Sha256)
                throw new IOException("original changed since registration");
            var disk = Dsk.Load(bytes.AsSpan());
            if (disk.Format == DiskFormat.Raw || (disk.Format == DiskFormat.Extended ? DiskLibraryFormat.ExtendedDsk : DiskLibraryFormat.StandardDsk) != entry.Format)
                throw new InvalidDataException("source container no longer matches the registered DSK format");
            var id = Guid.NewGuid().ToString("d");
            copy = new(id, entryId, "copies/" + id + ".dsk", entry.Sha256, entry.Sha256, entry.Length);
            candidate = prior with { Copies = prior.Copies.Add(copy) };
            _ = DiskLibrarySchema.Encode(candidate); // Validate all byte/count/schema bounds before creating a file.
            final = Path.Combine(root, copy.RelativePath);
            var stageCandidate = final + ".part";
            RequireVacant(final);
            token.ThrowIfCancellationRequested();
            await using (var writer = new FileStream(stageCandidate, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous, BufferSize = 65536,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                stage = stageCandidate;
                ownedId = FileId(stage) ?? throw new IOException("stage identity unavailable");
                if (ownedId == sourceId) throw new IOException("stage is not independent storage");
                await pub.WriteStage(writer, bytes, token);
            }
            var verified = await pub.ReadBack(stage, token);
            if (verified.Length != bytes.Length || Sha256Hex(verified) != entry.Sha256 || FileId(stage) != ownedId)
                throw new IOException("copy verification failed");
            native.ValidateSourceStillCurrent(source, before);
            RequireCopyAdmission();
            RequireUnaccounted(stage, ownedId);
            RequireVacant(final);
            token.ThrowIfCancellationRequested();
            try { pub.Publish(stage, final); }
            catch (Exception ex)
            {
                // Even a throwing rename may have landed. Never infer untouched from the exception.
                _pendingCopy = new(prior, candidate, copy, stage, final, ownedId, native, pub, false);
                native = null;
                return CopyResult(DiskLibraryCopyStatus.Unknown, "copy publication requires reconciliation: " + ex.Message, final);
            }
            if (FileId(final) != ownedId || LinuxFileFacts.Inspect(stage).Known)
                throw new IOException("copy rename outcome is not confirmed by owned identity");
            token.ThrowIfCancellationRequested();
            RequireSafeCatalogPath();
            catalogAttempted = true;
            var catalogPub = catalogPublication ?? new DiskLibraryPublication();
            var cancellableCatalog = new DiskLibraryPublication
            {
                WriteStage = async (path, data) =>
                {
                    token.ThrowIfCancellationRequested();
                    await catalogPub.WriteStage(path, data);
                },
                ReadBack = catalogPub.ReadBack,
                Publish = (path, target) =>
                {
                    token.ThrowIfCancellationRequested();
                    catalogPub.Publish(path, target);
                }
            };
            DiskLibrarySaveResult saved;
            catalogPub.Begin();
            try { saved = await SaveUnderGateAsync(cancellableCatalog, candidate); }
            finally { catalogPub.End(); }
            if (saved.Status == DiskLibrarySaveStatus.Published)
            {
                _document = candidate;
                return new(DiskLibraryCopyStatus.Created, copy, "verified independent copy and catalog reference published");
            }
            if (saved.Status == DiskLibrarySaveStatus.Unknown)
            {
                _pendingCopy = new(prior, candidate, copy, stage, final, ownedId, native, pub, true);
                native = null;
                return CopyResult(DiskLibraryCopyStatus.Unknown, saved.Message, final);
            }
            return CleanupCopy(pub, stage, final, ownedId,
                token.IsCancellationRequested ? DiskLibraryCopyStatus.Cancelled : DiskLibraryCopyStatus.Refused, saved.Message);
        }
        catch (Exception ex)
        {
            _document = prior;
            if (catalogAttempted && _pendingUnknown is not null && copy is not null && candidate is not null && native is not null && ownedId is not null)
            {
                _pendingCopy = new(prior, candidate, copy, stage!, final!, ownedId, native, pub, true);
                native = null;
                return CopyResult(DiskLibraryCopyStatus.Unknown, ex.Message, final);
            }
            return CleanupCopy(pub, stage, final, ownedId,
                ex is OperationCanceledException ? DiskLibraryCopyStatus.Cancelled : DiskLibraryCopyStatus.Refused, ex.Message);
        }
        finally { native?.Dispose(); _mutation.Release(); }
    }

    /// <summary>Drain is implicit in the awaited attempt. A pending catalog is reconciled through the
    /// existing strict read-back; an image-only unknown is never promoted to success by equal content.
    /// Cleanup requires fresh nonreference and the exact owned inode. No suffix-based scavenging.</summary>
    public async Task<DiskLibraryCopyResult> ReconcileWorkingCopyAsync()
    {
        await _mutation.WaitAsync();
        try
        {
            var pending = _pendingCopy;
            if (pending is null) return CopyResult(DiskLibraryCopyStatus.Refused, "no working-copy reconciliation pending");
            RequireCopyAdmission();
            RequireSafeCatalogPath();
            if (pending.CatalogAttempted && _pendingUnknown is not null)
            {
                var saved = await ReconcileUnderGateAsync();
                if (saved.Status == DiskLibrarySaveStatus.Unknown) return CopyResult(DiskLibraryCopyStatus.Unknown, saved.Message, pending.Final);
                if (saved.Status == DiskLibrarySaveStatus.Published)
                {
                    pending = pending with { CatalogPublished = true };
                    _pendingCopy = pending;
                }
            }
            if (pending.CatalogPublished)
            {
                var catalogBytes = await MediaFiles.ReadAsync(CatalogPath, DiskLibrarySchema.MaxCatalogBytes);
                if (!catalogBytes.AsSpan().SequenceEqual(DiskLibrarySchema.Encode(pending.Candidate)))
                    return CopyResult(DiskLibraryCopyStatus.Unknown, "catalog changed after publication reconciliation", pending.Final);
                if (FileId(pending.Final) != pending.OwnedId)
                    return CopyResult(DiskLibraryCopyStatus.Unknown, "catalog references a copy whose identity changed", pending.Final);
                _document = pending.Candidate;
                _pendingCopy = null;
                pending.Admission.Dispose();
                return new(DiskLibraryCopyStatus.Created, pending.Copy, "catalog reference reconciled; independent copy identity confirmed");
            }
            // A fresh strict catalog read must prove this attempt's files are unreferenced before deletion.
            if (File.Exists(CatalogPath))
            {
                var fresh = DiskLibrarySchema.Load((await MediaFiles.ReadAsync(CatalogPath, DiskLibrarySchema.MaxCatalogBytes)).AsSpan());
                if (fresh.Copies.Any(c => c.Id == pending.Copy.Id || c.RelativePath == pending.Copy.RelativePath))
                    return CopyResult(DiskLibraryCopyStatus.Unknown, "catalog may reference this copy; cleanup refused", pending.Final);
            }
            var result = CleanupCopy(pending.Publication, pending.Stage, pending.Final, pending.OwnedId,
                DiskLibraryCopyStatus.Refused, "unreferenced copy attempt reconciled without promotion to success");
            _document = pending.Prior;
            _pendingCopy = null;
            pending.Admission.Dispose();
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DiskLibrarySchemaException)
        { return CopyResult(DiskLibraryCopyStatus.Unknown, ex.Message, _pendingCopy?.Final); }
        finally { _mutation.Release(); }
    }

    private void RequireCopyAdmission()
    {
        if (_admission is null || _admission.Accounted.IsDefault) throw new IOException("ownership-unknown: stopped-host admission required");
        if (_admission.KnownWriters) throw new IOException("active cooperating writer");
    }

    private void RequireUnaccounted(string path, string? id)
    {
        RequireCopyAdmission();
        if (id is null) throw new IOException("ownership-unknown: file identity unavailable");
        foreach (var accounted in _admission!.Accounted)
        {
            var freshId = FileId(accounted.FullPath);
            if (freshId is null || accounted.FileId != freshId)
                throw new IOException("ownership-unknown: accounted identity missing or stale");
            if (Path.GetFullPath(accounted.FullPath) == path || freshId == id)
                throw new IOException("active mounted or writeback-owned file");
        }
    }

    private static void RequirePrivateDirectory(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var facts = LinuxImageAdmissionNative.Probe(handle);
        if (facts.Uid != LinuxImageAdmissionNative.Uid || (facts.Mode & 0x1ff) != 0x1c0)
            throw new IOException("managed directory must be user-owned and mode 0700");
    }

    private static string? FileId(string path)
    {
        var facts = LinuxFileFacts.Inspect(path);
        return facts.Known && facts.Regular && !facts.Link && FirstLinkedAncestor(path) is null ? facts.FileId : null;
    }

    private void RequireSafeCatalogPath()
    {
        if (FirstLinkedAncestor(CatalogPath) is not null) throw new IOException("linked catalog ancestor");
        var facts = LinuxFileFacts.Inspect(CatalogPath);
        if (facts.Known && (!facts.Regular || facts.Link || facts.FileId is null)) throw new IOException("unsafe catalog target");
        if (_document.Entries.Any(e => e.SourcePath == CatalogPath || (facts.FileId is not null && FileId(e.SourcePath) == facts.FileId)))
            throw new IOException("catalog aliases a protected original");
        var root = Path.GetDirectoryName(CatalogPath)!;
        if (_document.Copies.Any(c => Path.Combine(root, c.RelativePath) == CatalogPath ||
            (facts.FileId is not null && FileId(Path.Combine(root, c.RelativePath)) == facts.FileId)))
            throw new IOException("catalog aliases a managed copy");
        if (facts.FileId is not null) RequireUnaccounted(CatalogPath, facts.FileId);
    }

    private void RequireVacant(string path)
    {
        if (LinuxFileFacts.Inspect(path).Known || File.Exists(path) || Directory.Exists(path)) throw new IOException("copy target already exists");
        if (_admission!.Accounted.Any(a => a.FullPath == path)) throw new IOException("copy target is accounted by the host");
    }

    private DiskLibraryCopyResult CleanupCopy(DiskLibraryWorkingCopyPublication pub, string? stage, string? final,
        string? ownedId, DiskLibraryCopyStatus status, string message)
    {
        string? orphan = null;
        foreach (var path in new[] { stage, final })
        {
            if (path is null) continue;
            if (ownedId is null)
            {
                if (path == stage) orphan = path; // Created here, but never delete without trustworthy identity.
                continue;
            }
            var fact = LinuxFileFacts.Inspect(path);
            if (!fact.Known)
            {
                if (File.Exists(path) || Directory.Exists(path)) orphan = path;
                continue;
            }
            try
            {
                if (FileId(path) != ownedId) throw new IOException("cleanup identity changed");
                RequireUnaccounted(path, ownedId);
                pub.Delete(path);
                if (LinuxFileFacts.Inspect(path).Known) throw new IOException("cleanup did not remove owned file");
            }
            catch (Exception) { orphan = path; }
        }
        return CopyResult(status, message + (orphan is null ? "" : "; inert orphan retained"), orphan);
    }

    private static DiskLibraryCopyResult CopyResult(DiskLibraryCopyStatus status, string message, string? orphan = null)
        => new(status, null, message, orphan);
}
