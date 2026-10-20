using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.App.Media;
using Hc2000.Machine.Storage;
using System.Linq;

namespace Hc2000.App.Library;

/// <summary>One accounted host file (mount or writeback target) the host reports before register/verify.</summary>
public sealed record AccountedHostFile(string FullPath, string? FileId);

/// <summary>Stopped-host admission snapshot: the files the host has accounted (mounted or writeback-owned) and whether any cooperating writer is known. Tests and the host inject this; an empty one is the stopped host.</summary>
public sealed class DiskLibraryHostAdmission
{
    public DiskLibraryHostAdmission(ImmutableArray<AccountedHostFile> accounted, bool knownWriters)
        => (Accounted, KnownWriters) = (accounted, knownWriters);
    public ImmutableArray<AccountedHostFile> Accounted { get; }
    public bool KnownWriters { get; }
    public static DiskLibraryHostAdmission StoppedHost { get; } = new(ImmutableArray<AccountedHostFile>.Empty, false);
}

/// <summary>Register outcome: Accepted carries the entry; the rest are closed, honest refusals (no guessed content).</summary>
public enum DiskLibraryRefusal { Accepted, NotRegularFile, FinalLink, RepeatOrAlias, OwnershipUnknown, ActiveWriter, Unreadable, TooLarge, GrewDuringRead, UnsupportedContainer, Budget, ReconciliationNeeded }
public sealed record DiskLibraryRegisterResult(DiskLibraryEntry? Entry, DiskLibraryRefusal Reason, string Message);

/// <summary>Verify outcome for one entry: only Available is content-proven (hash matches); nothing else is reported as clean.</summary>
public enum DiskLibraryAvailability { Available, Missing, Unreadable, Changed, ActiveWriter, UnsafeAlias, Unsupported, OwnershipUnknown }
public sealed record DiskLibraryVerifyResult(DiskLibraryAvailability Status, string Message);

public enum DiskLibrarySaveStatus { Published, FailedBeforePublish, Unknown }
public sealed record DiskLibrarySaveResult(DiskLibrarySaveStatus Status, string Message);

/// <summary>Tracked publication seam: every step is a production-owned delegate the caller may wrap to hold, fail or observe the attempt; in-flight attempts are counted, and <see cref="DrainAsync"/> completes once the tracked attempts settle. Defaults are the real file operations. Nothing is deleted or the gate released before the tracked attempt settles.</summary>
public sealed class DiskLibraryPublication
{
    private readonly object _gate = new();
    private int _inFlight;
    private TaskCompletionSource? _drained;

    public Func<string, byte[], Task> WriteStage { get; set; } = (stage, bytes) => File.WriteAllBytesAsync(stage, bytes);
    public Func<string, Task<ImmutableArray<byte>>> ReadBack { get; set; } = stage => MediaFiles.ReadAsync(stage, DiskLibrarySchema.MaxCatalogBytes);
    public Action<string, string> Publish { get; set; } = (stage, target) => File.Move(stage, target, overwrite: true);

    /// <summary>Tracked attempts currently settling; a held step stays here until the production path settles it.</summary>
    public int InFlight { get { lock (_gate) return _inFlight; } }

    /// <summary>Completions once every tracked attempt has settled; immediate when none are in flight.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            if (_inFlight == 0) return Task.CompletedTask;
            if (_drained is null) _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    internal void Begin() { lock (_gate) _inFlight++; }

    internal void End()
    {
        TaskCompletionSource? done = null;
        lock (_gate)
        {
            if (--_inFlight == 0) { done = _drained; _drained = null; }
        }
        done?.TrySetResult();
    }
}

/// <summary>Host store for the disk library: register/verify real .dsk files (bounded reads via MediaFiles, facts via LinuxFileFacts, parsing via the existing Dsk/CpmFs readers) and publish the catalog by sibling stage + rename + strict read-back. No mounting, no image mutation.</summary>
public sealed partial class DiskLibraryCatalog
{
    private volatile DiskLibraryHostAdmission? _admission;
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private DiskLibraryDocument _document;

    /// <summary>Settlement of an unknown publication that retained its stage; register/save refuse until <see cref="ReconcileAsync"/> clears it from fresh facts. At most one, because the mutation gate serializes attempts.</summary>
    private (string Stage, byte[] Intended, bool PriorAbsent, byte[]? Prior)? _pendingUnknown;

    public string CatalogPath { get; }
    public DiskLibraryHostAdmission? Admission { get; }
    public IReadOnlyList<DiskLibraryEntry> Entries => _document.Entries;
    public IReadOnlyList<DiskLibraryCopy> Copies => _document.Copies;

    private DiskLibraryCatalog(string path, DiskLibraryDocument document, DiskLibraryHostAdmission? admission)
        => (CatalogPath, _document, _admission, Admission) = (path, document, admission, admission);

    /// <summary>Load the catalog file (missing file = empty in-memory catalog); a file that violates the schema throws DiskLibrarySchemaException. admission may stay null: without it register refuses ownership-unknown and verify reports ownership unknown; the stopped host must be injected explicitly (DiskLibraryHostAdmission.StoppedHost).</summary>
    public static DiskLibraryCatalog Load(string catalogPath, DiskLibraryHostAdmission? admission = null)
    {
        var path = Path.GetFullPath(catalogPath);
        DiskLibraryDocument document;
        try
        {
            document = File.Exists(path)
                ? DiskLibrarySchema.Load(File.ReadAllBytes(path))
                : new(DiskLibrarySchema.Version, ImmutableArray<DiskLibraryEntry>.Empty, ImmutableArray<DiskLibraryCopy>.Empty);
        }
        catch (IOException ex) { throw new DiskLibrarySchemaException("catalog path is not a readable file: " + ex.Message); }
        return new(path, document, admission);
    }

    /// <summary>Replace the host admission snapshot for subsequent register/verify calls (ownership changes after load).</summary>
    public void SetAdmission(DiskLibraryHostAdmission? admission) => _admission = admission;

    /// <summary>True when no host admission snapshot is present (load-time admission was null and no SetAdmission since).
    /// Register and working-copy refuse ownership-unknown without it; the UI reports the refusal instead of guessing.</summary>
    public bool AdmissionMissing => _admission is null;

    /// <summary>Register one host .dsk. listContents observes the CP/M directory read-only; otherwise contents stay unknown/not-scanned. Refusals are closed and honest; the in-memory document changes only on Accepted.</summary>
    public async Task<DiskLibraryRegisterResult> RegisterAsync(string path, string provenanceSource, string? provenanceNote = null, bool listContents = false)
    {
        if (path is null || path.Length == 0) throw new ArgumentException("path is required", nameof(path));
        if (provenanceSource is null || provenanceSource.Length == 0 || provenanceSource.Length > 2048 || HasControl(provenanceSource))
            throw new ArgumentException("provenance source must be 1..2048 characters without control characters", nameof(provenanceSource));
        if (provenanceNote is { Length: > 2048 } || (provenanceNote is not null && HasControl(provenanceNote)))
            throw new ArgumentException("provenance note must be 0..2048 characters without control characters", nameof(provenanceNote));

        if (_pendingUnknown is not null || _pendingCopy is not null)
            return Refuse(DiskLibraryRefusal.ReconciliationNeeded, "an unknown catalog publication is pending; reconcile it before registering another mutation");
        if (!await _mutation.WaitAsync(TimeSpan.FromSeconds(30)))
            return Refuse(DiskLibraryRefusal.ActiveWriter, "a catalog mutation is still in flight after the 30 second gate timeout");
        try
        {
            if (_pendingUnknown is not null || _pendingCopy is not null)
                return Refuse(DiskLibraryRefusal.ReconciliationNeeded, "reconciliation is required before another mutation");
            var full = Path.GetFullPath(path);
            if (full.Length > 4096) return Refuse(DiskLibraryRefusal.Budget, "source path exceeds the 4096 character schema bound");
            var admission = _admission;
            if (admission is null) return Refuse(DiskLibraryRefusal.OwnershipUnknown, "no host admission was provided; inject the host admission snapshot before registering");
            if (admission.KnownWriters) return Refuse(DiskLibraryRefusal.ActiveWriter, "host admission reports a known cooperating writer");

            var pre = LinuxFileFacts.Inspect(full);
            if (!pre.Known) return Refuse(DiskLibraryRefusal.OwnershipUnknown, File.Exists(full) ? "host file facts are unknown on this platform" : "source file is missing; host file facts are unavailable");
            if (pre.Link) return Refuse(DiskLibraryRefusal.FinalLink, "final path component is a symbolic link");
            var linkedAncestor = FirstLinkedAncestor(full);
            if (linkedAncestor is not null) return Refuse(DiskLibraryRefusal.FinalLink, "path component " + linkedAncestor + " is a symbolic link");
            if (!pre.Regular) return Refuse(DiskLibraryRefusal.NotRegularFile, "path is not a regular file");
            if (_document.Entries.Any(e => e.SourcePath == full)) return Refuse(DiskLibraryRefusal.RepeatOrAlias, "path is already registered");
            if (pre.FileId is not null && _document.Entries.Any(e => e.FileId == pre.FileId))
                return Refuse(DiskLibraryRefusal.RepeatOrAlias, "file id is already registered under another path");
            if (Accounted(admission, full, pre.FileId)) return Refuse(DiskLibraryRefusal.ActiveWriter, "path or file id is an accounted mount or writeback target");

            long preSize;
            try { preSize = new FileInfo(full).Length; }
            catch (IOException) { return Refuse(DiskLibraryRefusal.Unreadable, "file size is unreadable"); }
            if (preSize > MediaFiles.MaxDiskBytes) return Refuse(DiskLibraryRefusal.TooLarge, "file exceeds the 4 MiB container limit");

            ImmutableArray<byte> bytes;
            try { bytes = await MediaFiles.ReadAsync(full, MediaFiles.MaxDiskBytes); }
            catch (InvalidDataException) { return Refuse(DiskLibraryRefusal.TooLarge, "file exceeded the 4 MiB container limit while reading"); }
            catch (FileNotFoundException) { return Refuse(DiskLibraryRefusal.Unreadable, "file disappeared before the read completed"); }
            catch (IOException) { return Refuse(DiskLibraryRefusal.Unreadable, "file is unreadable"); }

            var post = LinuxFileFacts.Inspect(full);
            long postSize;
            try { postSize = new FileInfo(full).Length; }
            catch (IOException) { return Refuse(DiskLibraryRefusal.Unreadable, "file size is unreadable after the read"); }
            if (!post.Known || !post.Regular || post.Link || post.FileId != pre.FileId || preSize != postSize || postSize != bytes.Length)
                return Refuse(DiskLibraryRefusal.GrewDuringRead, "file identity or size changed during the read");

            Dsk disk;
            try { disk = Dsk.Load(bytes.AsSpan()); }
            catch (InvalidDataException ex) { return Refuse(DiskLibraryRefusal.UnsupportedContainer, ex.Message); }
            if (disk.Format == DiskFormat.Raw) return Refuse(DiskLibraryRefusal.UnsupportedContainer, "raw images require geometry and are not registered");
            var format = disk.Format == DiskFormat.Extended ? DiskLibraryFormat.ExtendedDsk : DiskLibraryFormat.StandardDsk;

            var name = Path.GetFileName(full);
            if (name.Length is < 1 or > 256 || HasControl(name) || name.Trim().Length == 0)
                return Refuse(DiskLibraryRefusal.Budget, "file name is not representable as an entry name");

            var contents = listContents
                ? ObserveContents(disk)
                : new DiskLibraryContents(DiskLibraryContentState.Unknown, null, ImmutableArray<DiskLibraryFileRow>.Empty, "not-scanned");
            if (_document.Entries.Length == DiskLibrarySchema.MaxEntries)
                return Refuse(DiskLibraryRefusal.Budget, $"entries reach the {DiskLibrarySchema.MaxEntries} bound");

            var entry = new DiskLibraryEntry(
                Guid.NewGuid().ToString("d"), name, full, bytes.Length, Sha256Hex(bytes), pre.FileId, format,
                new DiskLibraryProvenance(provenanceSource, provenanceNote ?? string.Empty), contents);
            var candidate = _document with { Entries = _document.Entries.Add(entry) };
            try
            {
                if (DiskLibrarySchema.Encode(candidate).Length > DiskLibrarySchema.MaxCatalogBytes)
                    return Refuse(DiskLibraryRefusal.Budget, "encoded catalog would exceed the 8 MiB budget");
            }
            catch (DiskLibrarySchemaException) { return Refuse(DiskLibraryRefusal.Budget, "encoded catalog exceeds its budget"); }

            _document = candidate;
            return new DiskLibraryRegisterResult(entry, DiskLibraryRefusal.Accepted, "registered");
        }
        finally { _mutation.Release(); }
    }

    /// <summary>Verify one entry now: facts and writer checks first, then a bounded re-read and sha256 compare. Available is the only content-proven status.</summary>
    public async Task<DiskLibraryVerifyResult> VerifyAsync(string entryId)
    {
        var entry = _document.Entries.FirstOrDefault(e => e.Id == entryId)
            ?? throw new KeyNotFoundException("unknown entry id " + entryId);
        var admission = _admission;
        if (admission is null) return new DiskLibraryVerifyResult(DiskLibraryAvailability.OwnershipUnknown, "no host admission was provided; verify reports ownership unknown");
        if (admission.KnownWriters) return new DiskLibraryVerifyResult(DiskLibraryAvailability.ActiveWriter, "host admission reports a known cooperating writer");
        if (admission.Accounted.Any(a => a.FullPath == entry.SourcePath))
            return new DiskLibraryVerifyResult(DiskLibraryAvailability.ActiveWriter, "entry path is an accounted mount or writeback target");
        if (!File.Exists(entry.SourcePath)) return new DiskLibraryVerifyResult(DiskLibraryAvailability.Missing, "source file is missing");

        var fact = LinuxFileFacts.Inspect(entry.SourcePath);
        if (!fact.Known) return new DiskLibraryVerifyResult(DiskLibraryAvailability.OwnershipUnknown, "host file facts are unknown on this platform");
        if (fact.Link) return new DiskLibraryVerifyResult(DiskLibraryAvailability.UnsafeAlias, "final path component is a symbolic link");
        var linkedAncestor = FirstLinkedAncestor(entry.SourcePath);
        if (linkedAncestor is not null) return new DiskLibraryVerifyResult(DiskLibraryAvailability.UnsafeAlias, "path component " + linkedAncestor + " is a symbolic link");
        if (!fact.Regular) return new DiskLibraryVerifyResult(DiskLibraryAvailability.Unsupported, "path is not a regular file");
        if (fact.FileId is not null)
        {
            if (admission.Accounted.Any(a => a.FileId is not null && a.FileId == fact.FileId))
                return new DiskLibraryVerifyResult(DiskLibraryAvailability.ActiveWriter, "file id is an accounted mount or writeback target");
            if (_document.Entries.Any(e => e.Id != entry.Id && e.FileId == fact.FileId))
                return new DiskLibraryVerifyResult(DiskLibraryAvailability.UnsafeAlias, "file id is registered under another entry");
        }

        try
        {
            var bytes = await MediaFiles.ReadAsync(entry.SourcePath, MediaFiles.MaxDiskBytes);
            return Sha256Hex(bytes) == entry.Sha256
                ? new DiskLibraryVerifyResult(DiskLibraryAvailability.Available, "content hash matches the registered sha256")
                : new DiskLibraryVerifyResult(DiskLibraryAvailability.Changed, "content hash differs from the registered sha256");
        }
        catch (FileNotFoundException) { return new DiskLibraryVerifyResult(DiskLibraryAvailability.Missing, "file is missing"); }
        catch (InvalidDataException) { return new DiskLibraryVerifyResult(DiskLibraryAvailability.Unreadable, "file exceeds the 4 MiB container limit"); }
        catch (IOException) { return new DiskLibraryVerifyResult(DiskLibraryAvailability.Unreadable, "file is unreadable"); }
    }

    public DiskLibrarySearchResult Search(string query) => DiskLibrarySchema.Search(_document, query);

    /// <summary>Publish through the tracked publication seam. Success is settled only from fresh facts taken after the tracked steps: Published when every step completed and the fresh catalog bytes equal the intended bytes; a step failure is FailedBeforePublish only when a fresh read-back proves the prior bytes untouched; anything else is Unknown and the stage is left in place. The mutation gate is held until the tracked attempt settles and the stage is never deleted before settlement.</summary>
    public async Task<DiskLibrarySaveResult> SaveAsync(DiskLibraryPublication? publication = null)
    {
        if (_pendingUnknown is not null || _pendingCopy is not null)
            return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Unknown, "an unknown catalog publication is pending; reconcile it before another mutation");
        if (!await _mutation.WaitAsync(TimeSpan.FromSeconds(30)))
            return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Unknown, "a catalog mutation is still in flight after the 30 second gate timeout");
        try
        {
            if (_pendingUnknown is not null || _pendingCopy is not null)
                return new(DiskLibrarySaveStatus.Unknown, "reconciliation required before saving");
            return await SaveUnderGateAsync(publication);
        }
        finally { _mutation.Release(); }
    }

    private async Task<DiskLibrarySaveResult> SaveUnderGateAsync(DiskLibraryPublication? publication, DiskLibraryDocument? candidate = null)
    {
        var pub = publication ?? new DiskLibraryPublication();
        pub.Begin();
        try
        {
            byte[] encoded;
            try { encoded = DiskLibrarySchema.Encode(candidate ?? _document); }
            catch (DiskLibrarySchemaException ex) { return new DiskLibrarySaveResult(DiskLibrarySaveStatus.FailedBeforePublish, "catalog could not be encoded: " + ex.Message + "; nothing written"); }

            bool priorAbsent = false, priorUnreadable = false;
            byte[]? prior = null;
            if (File.Exists(CatalogPath))
            {
                try { prior = (await pub.ReadBack(CatalogPath)).ToArray(); }
                catch (Exception) { priorUnreadable = true; }
            }
            else priorAbsent = true;

            var stage = CatalogPath + ".stage-" + Guid.NewGuid().ToString("N");
            string? stepFailure = null;
            var publishFailed = false;
            try
            {
                await pub.WriteStage(stage, encoded);
                var readBack = await pub.ReadBack(stage);
                var reloaded = DiskLibrarySchema.Load(readBack.AsSpan());
                if (!DiskLibrarySchema.Encode(reloaded).AsSpan().SequenceEqual(encoded))
                    throw new DiskLibrarySchemaException("stage read-back does not round-trip through the schema");
                try { pub.Publish(stage, CatalogPath); }
                catch (Exception ex) { stepFailure = ex.Message; publishFailed = true; }
            }
            catch (Exception ex) { stepFailure = ex.Message; publishFailed = false; }

            // Settle only from fresh facts taken after the tracked steps.
            bool freshAbsent = false, freshUnreadable = false;
            byte[]? fresh = null;
            if (File.Exists(CatalogPath))
            {
                try { fresh = (await pub.ReadBack(CatalogPath)).ToArray(); }
                catch (Exception) { freshUnreadable = true; }
            }
            else freshAbsent = true;
            var fact = LinuxFileFacts.Inspect(CatalogPath);
            var catalogSafe = fact.Known && fact.Regular && !fact.Link;
            bool stageExists;
            try { stageExists = File.Exists(stage); }
            catch (Exception) { stageExists = true; }

            if (stepFailure is null)
            {
                if (fresh is not null && catalogSafe && fresh.AsSpan().SequenceEqual(encoded))
                {
                    if (stageExists) DeleteBestEffort(stage);
                    _pendingUnknown = null;
                    return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Published, "published by rename and confirmed by a fresh catalog read-back");
                }
                // The steps completed but the fresh facts do not confirm the intended bytes: keep the stage and the attempt for reconciliation.
                _pendingUnknown = (stage, encoded, priorAbsent, prior);
                return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Unknown,
                    "publication steps completed but the fresh catalog facts do not confirm the intended bytes; success is never inferred from equal content alone; stage " + stage + " left in place for reconciliation");
            }

            if (!publishFailed)
            {
                // A pre-publication step failed, so the publish step never ran and the target was structurally untouched.
                var deleted = stageExists ? DeleteBestEffort(stage) : true;
                _pendingUnknown = null;
                return new DiskLibrarySaveResult(DiskLibrarySaveStatus.FailedBeforePublish,
                    "publication failed before publish (" + stepFailure + "); the catalog was never touched; stage " + (deleted ? "deleted" : "may still exist at " + stage));
            }

            // The publish step failed. The target is provably untouched only when the stage still exists (the rename never happened)
            // and the fresh target facts equal the prior snapshot: absent for absent, or byte-equal and safe.
            var priorUntouched = !freshUnreadable && stageExists
                && (priorAbsent ? freshAbsent : !priorUnreadable && fresh is not null && catalogSafe && fresh.AsSpan().SequenceEqual(prior));
            if (priorUntouched)
            {
                var deleted = DeleteBestEffort(stage);
                _pendingUnknown = null;
                return new DiskLibrarySaveResult(DiskLibrarySaveStatus.FailedBeforePublish,
                    "publish failed (" + stepFailure + ") but the fresh facts prove the prior catalog untouched and the stage still in place; stage " + (deleted ? "deleted" : "may still exist at " + stage));
            }

            // The rename may have landed, or the target changed: keep the stage and the attempt for reconciliation.
            _pendingUnknown = (stage, encoded, priorAbsent, prior);
            return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Unknown,
                "publish failed (" + stepFailure + ") and the fresh catalog facts do not prove the prior bytes untouched; stage " + stage + " left in place for reconciliation; success is never inferred from equal content alone");
        }
        finally
        {
            pub.End();
        }
    }

    /// <summary>Reconcile the settled unknown publication from fresh facts: the target is read back through the production read path under the mutation gate, and the unknown clears only when the fresh bytes equal the intended bytes (Published) or the prior bytes (FailedBeforePublish). Drain completion is not reconciliation. With nothing pending it reports Unknown and touches nothing.</summary>
    public async Task<DiskLibrarySaveResult> ReconcileAsync()
    {
        if (_pendingCopy is not null)
            return new(DiskLibrarySaveStatus.Unknown, "use ReconcileWorkingCopyAsync for the pending working copy");
        if (_pendingUnknown is null)
            return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Unknown, "no unknown catalog publication is pending; nothing to reconcile");
        if (!await _mutation.WaitAsync(TimeSpan.FromSeconds(30)))
            return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Unknown, "a catalog mutation is still in flight after the 30 second gate timeout; reconcile again");
        try
        {
            if (_pendingCopy is not null || _pendingUnknown is null)
                return new(DiskLibrarySaveStatus.Unknown, "pending state changed; use working-copy reconciliation if applicable");
            return await ReconcileUnderGateAsync();
        }
        finally { _mutation.Release(); }
    }

    private async Task<DiskLibrarySaveResult> ReconcileUnderGateAsync()
    {
            var pending = _pendingUnknown!.Value;
            var pub = new DiskLibraryPublication();
            bool freshAbsent = false, freshUnreadable = false;
            byte[]? fresh = null;
            if (File.Exists(CatalogPath))
            {
                try { fresh = (await pub.ReadBack(CatalogPath)).ToArray(); }
                catch (Exception) { freshUnreadable = true; }
            }
            else freshAbsent = true;
            var fact = LinuxFileFacts.Inspect(CatalogPath);
            var catalogSafe = !freshAbsent && fact.Known && fact.Regular && !fact.Link;
            if (pending.PriorAbsent && freshAbsent && !fact.Known)
            {
                var deleted = File.Exists(pending.Stage) ? DeleteBestEffort(pending.Stage) : true;
                _pendingUnknown = null;
                return new(DiskLibrarySaveStatus.FailedBeforePublish,
                    "reconciled: prior and current catalog are absent; stage " + (deleted ? "deleted" : "may still exist at " + pending.Stage));
            }
            if (!freshUnreadable && fresh is not null)
            {
                if (catalogSafe && fresh.AsSpan().SequenceEqual(pending.Intended))
                {
                    var deleted = File.Exists(pending.Stage) ? DeleteBestEffort(pending.Stage) : true;
                    _pendingUnknown = null;
                    return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Published, "reconciled: the fresh catalog bytes are the intended bytes; stage " + (deleted ? "deleted" : "may still exist at " + pending.Stage));
                }
                if (!pending.PriorAbsent && catalogSafe && fresh.AsSpan().SequenceEqual(pending.Prior))
                {
                    var deleted = File.Exists(pending.Stage) ? DeleteBestEffort(pending.Stage) : true;
                    _pendingUnknown = null;
                    return new DiskLibrarySaveResult(DiskLibrarySaveStatus.FailedBeforePublish, "reconciled: the fresh catalog bytes are still the prior bytes; nothing was published; stage " + (deleted ? "deleted" : "may still exist at " + pending.Stage));
                }
            }
            return new DiskLibrarySaveResult(DiskLibrarySaveStatus.Unknown,
                "reconcile: the fresh catalog facts " + (freshUnreadable ? "are unreadable" : freshAbsent ? "are an absent file" : "match neither the prior nor the intended bytes")
                + " and the unknown publication stays pending; stage " + pending.Stage + " retained; reconcile again once the host state is settled");
    }

    /// <summary>The nearest ancestor directory of <paramref name="full"/> that is a symbolic link, or null: every path component is checked, not only the final one.</summary>
    private static string? FirstLinkedAncestor(string full)
    {
        var dir = Path.GetDirectoryName(full);
        var guard = 0;
        while (!string.IsNullOrEmpty(dir) && guard++ < 512)
        {
            var fact = LinuxFileFacts.Inspect(dir);
            if (fact.Link) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static bool DeleteBestEffort(string stage)
    {
        try { File.Delete(stage); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>Read-only CP/M directory observation; every failure maps to an honest unknown reason, never guessed rows.</summary>
    private static DiskLibraryContents ObserveContents(Dsk disk)
    {
        CpmFs fs;
        try { fs = CpmFs.Open(disk); }
        catch (InvalidDataException ex)
        {
            var reason = ex.Message.StartsWith("Ambiguous", StringComparison.Ordinal) ? "ambiguous"
                : ex.Message.StartsWith("No valid directory/DPB preset", StringComparison.Ordinal) ? "no-preset"
                : "unsupported-directory";
            return new DiskLibraryContents(DiskLibraryContentState.Unknown, null, ImmutableArray<DiskLibraryFileRow>.Empty, reason);
        }
        try
        {
            var rows = fs.List();
            if (rows.Count > DiskLibrarySchema.MaxDirectoryRows)
                return new DiskLibraryContents(DiskLibraryContentState.Unknown, fs.Parameters.Name, ImmutableArray<DiskLibraryFileRow>.Empty, "budget");
            if (rows.Any(r => r.User > 15))
                return new DiskLibraryContents(DiskLibraryContentState.Unknown, fs.Parameters.Name, ImmutableArray<DiskLibraryFileRow>.Empty, "unsupported-directory");
            var files = rows.Select(r => new DiskLibraryFileRow(r.User, r.Name, r.ReadOnly, r.System, r.Records, r.AllocatedBytes, r.DirectoryIndex)).ToImmutableArray();
            return new DiskLibraryContents(DiskLibraryContentState.DirectoryListed, fs.Parameters.Name, files, "listing-only");
        }
        catch (Exception ex) when (ex is not DiskLibrarySchemaException)
        {
            return new DiskLibraryContents(DiskLibraryContentState.Unknown, fs.Parameters.Name, ImmutableArray<DiskLibraryFileRow>.Empty, "reader-error");
        }
    }

    private static bool Accounted(DiskLibraryHostAdmission admission, string full, string? fileId)
        => admission.Accounted.Any(a => (a.FileId is not null && fileId is not null && a.FileId == fileId) || a.FullPath == full);

    private static string Sha256Hex(ImmutableArray<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));

    private static DiskLibraryRegisterResult Refuse(DiskLibraryRefusal reason, string message) => new(null, reason, message);

    private static bool HasControl(string value)
    {
        foreach (var c in value) if (c < 0x20 || c == 0x7F) return true;
        return false;
    }
}
