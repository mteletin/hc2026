namespace Hc2000.HostFiles;

internal enum ImageAdmissionRefusal { Busy, Quarantined, Unsupported, InvalidDomain, Cancelled, IoFailure }
internal enum ImageAdmissionUse { ColdRead, Publication }
internal enum ImageAdmissionPlatformMode { GuardedLinux, GuardedWindows, LegacyOnly }
internal enum KnownImagePublication { Unpublished, KnownOld, KnownNew }
internal readonly record struct NativeImageFileId(uint DeviceMajor, uint DeviceMinor, ulong Inode);
internal sealed record AdmittedImageFacts(string CanonicalPath, NativeImageFileId FileId, long Length);
internal sealed record ImageAdmissionResult(NativeImageAdmission? Admission, ImageAdmissionRefusal? Refusal)
{
    // Error compatibility only, never an alternate admission or permission to run a writer.
    internal bool MissingTargetParent { get; init; }
    internal string? Diagnostic { get; init; }
    internal NativeImageAdmission RequireAdmission() => Admission ??
        throw new AdmissionException(Refusal ?? ImageAdmissionRefusal.IoFailure, diagnostic: Diagnostic);
}

internal sealed class NativeImageAdmission : IDisposable
{
    // Test configuration is internal, flows only in the fixture execution context, and is never a CLI option.
    private sealed record TestDomain(string Root, ImageAdmissionPlatformMode Mode, bool AccountStateRoot);
    private static readonly AsyncLocal<TestDomain?> testDomain = new();
    internal static bool HasPrivateTestDomain => testDomain.Value is not null;
    internal static ImageAdmissionPlatformMode PlatformMode => testDomain.Value?.Mode ??
        (OperatingSystem.IsLinux() ? ImageAdmissionPlatformMode.GuardedLinux : OperatingSystem.IsWindows() ? ImageAdmissionPlatformMode.GuardedWindows : ImageAdmissionPlatformMode.LegacyOnly);
    internal static bool IsGuarded => PlatformMode != ImageAdmissionPlatformMode.LegacyOnly;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NativeImageAdmission, byte> retained = new();
    private readonly object sync = new();
    private readonly FileStream gate;
    private readonly bool privateTest;
    private ImageAdmissionRecord record;
    private Guid? candidate;
    private string? publicationPath;
    private AdmittedImageFacts? publicationSource;
    private bool terminal;
    internal ImageAdmissionUse Use { get; }
    // The state directory was account-owned but not 0700 when this admission opened it; admission
    // tightened it to 0700 and carries the path so the caller can say that it did.
    internal string? TightenedStateDirectory { get; private set; }

    private NativeImageAdmission(FileStream gate, ImageAdmissionRecord record, ImageAdmissionUse use, bool privateTest)
    { this.gate = gate; this.record = record; Use = use; this.privateTest = privateTest; }

    internal static IDisposable EnterPrivateTestDomain(string root,
        ImageAdmissionPlatformMode? mode = null,
        bool accountStateRoot = false)
    {
        var previous = testDomain.Value;
        var physical = OperatingSystem.IsLinux() ? ImageAdmissionPlatformMode.GuardedLinux : OperatingSystem.IsWindows() ? ImageAdmissionPlatformMode.GuardedWindows : ImageAdmissionPlatformMode.LegacyOnly;
        testDomain.Value = new(Path.GetFullPath(root), mode ?? physical, accountStateRoot);
        return new TestScope(() => testDomain.Value = previous);
    }
    private sealed class TestScope(Action restore) : IDisposable
    {
        private Action? action = restore;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
    internal static ImageAdmissionResult TryAcquireColdRead(CancellationToken cancellation) => Acquire(ImageAdmissionUse.ColdRead, null, Guid.Empty, cancellation);
    internal static ImageAdmissionResult TryBeginPublication(string targetPath, Guid attemptId, CancellationToken cancellation) => Acquire(ImageAdmissionUse.Publication, targetPath, attemptId, cancellation);

    private static ImageAdmissionResult Acquire(ImageAdmissionUse use, string? target, Guid attempt, CancellationToken cancellation)
    {
        FileStream? stream = null;
        NativeImageAdmission? admission = null;
        bool checkingTargetParent = false;
        var stage = "platform selection";
        string? stateRoot = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) || !IsGuarded)
                return new(null, ImageAdmissionRefusal.Unsupported);
            if (use == ImageAdmissionUse.Publication && (attempt == Guid.Empty || string.IsNullOrWhiteSpace(target) || target.Length > 4096))
                return new(null, ImageAdmissionRefusal.InvalidDomain);
            var domain = testDomain.Value;
            var isTest = domain is not null;
            var root = domain?.Root ?? ImageAdmissionNative.StateRoot;
            stateRoot = root;
            stage = "state directory traversal";
            // In production the root is the account's own state directory, so the strict walk below
            // is allowed to see a loose home chain: tighten the account-owned components of it first.
            // Test domains and disk-library namespaces never run this walk; a loose parent there is
            // still refused and untouched.
            string? tightened = null;
            if (domain is null && OperatingSystem.IsLinux())
            {
                var home = ImageAdmissionNative.AccountHome();
                tightened = ImageAdmissionNative.TightenStateChain(home, root);
            }
            using var directory = ImageAdmissionNative.Directory(root, true, domain is not null);
            stage = "state directory identity and permissions";
            var df = ImageAdmissionNative.Probe(directory);
            if (OperatingSystem.IsLinux() && df.Uid != ImageAdmissionNative.Uid)
                return new(null, ImageAdmissionRefusal.InvalidDomain);
            // A state directory left loose (0755) by an older installer still belongs to the account
            // that is running: tighten it to 0700 and carry on instead of refusing. Every domain root
            // (disk library namespaces, test domains) keeps the strict rule: loose means refused and
            // untouched. A directory the account does not own, and any parent the walk above cannot
            // clear, still refuses as before.
            if (OperatingSystem.IsWindows()) ImageAdmissionNative.RequirePrivateDirectory(directory);
            if (OperatingSystem.IsLinux() && (df.Mode & 0x1ff) != 0x1c0)
            {
                if (domain is { AccountStateRoot: false })
                    return new(null, ImageAdmissionRefusal.InvalidDomain);
                tightened = root;
                ImageAdmissionNative.TightenDirectory(directory);
                var after = ImageAdmissionNative.Probe(directory);
                if (after.Uid != ImageAdmissionNative.Uid || (after.Mode & 0x1ff) != 0x1c0)
                    return new(null, ImageAdmissionRefusal.InvalidDomain);
            }
            stage = "native gate open";
            var (handle, created) = ImageAdmissionNative.Gate(directory);
            stream = new FileStream(handle, FileAccess.ReadWrite, 1, false);
            stage = "native gate record";
            ImageAdmissionRecord record;
            if (created)
            {
                record = ImageAdmissionRecord.Clean(Guid.NewGuid());
                stream.Write(record.Encode()); stream.Flush(true);
                ImageAdmissionNative.FlushDirectory(directory);
            }
            else
            {
                if (stream.Length != ImageAdmissionRecord.Size) throw new InvalidDataException("Invalid admission record length.");
                var bytes = new byte[ImageAdmissionRecord.Size]; stream.ReadExactly(bytes);
                record = ImageAdmissionRecord.Decode(bytes);
            }
            if (record.State != 0) return new(null, ImageAdmissionRefusal.Quarantined);
            cancellation.ThrowIfCancellationRequested();
            admission = new(stream, record, use, isTest);
            if (use == ImageAdmissionUse.Publication)
            {
                stage = "publication target validation";
                var path = ImageAdmissionNative.CanonicalPath(target!);
                checkingTargetParent = true;
                using var parent = ImageAdmissionNative.Directory(Path.GetDirectoryName(path)!, false, isTest);
                checkingTargetParent = false;
                // A nonexistent name is allowed only beneath a validated parent. Existing sources are
                // checked again via their actual opened handle before the publisher may stage anything.
                admission.publicationPath = path;
                if (TargetExists(path))
                {
                    var facts = ImageAdmissionNative.FileAt(path, isTest);
                    if (facts.Links != 1) throw new AdmissionException(ImageAdmissionRefusal.Unsupported);
                    admission.publicationSource = new(path, facts.Id, checked((long)facts.Length));
                    record = record.Intent(attempt, path).WithFile(facts.Id);
                }
                else record = record.Intent(attempt, path);
                admission.record = record;
                admission.Persist();
            }
            stream = null;
            if (tightened is not null) admission.TightenedStateDirectory = tightened;
            return new(admission, null);
        }
        catch (OperationCanceledException) { return new(null, ImageAdmissionRefusal.Cancelled); }
        catch (AdmissionException ex)
        { return new(null, ex.Refusal) { MissingTargetParent = checkingTargetParent && ex.MissingDirectory,
            Diagnostic = $"stage={stage}; stateRoot={stateRoot}; {ex.Message}" }; }
        catch (InvalidDataException ex) { return new(null, ImageAdmissionRefusal.Quarantined) { Diagnostic = $"stage={stage}; stateRoot={stateRoot}; {ex.Message}" }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return new(null, ImageAdmissionRefusal.IoFailure) { Diagnostic = $"stage={stage}; stateRoot={stateRoot}; {ex.GetType().Name}; HRESULT=0x{ex.HResult:X8}; {ex.Message}" }; }
        finally { stream?.Dispose(); }
    }
    // Existing publisher policy is selected before starting work, never after a native refusal.
    internal static NativeImageAdmission? BeginExistingPublication(string target, CancellationToken cancellation = default)
    {
        if (PlatformMode == ImageAdmissionPlatformMode.LegacyOnly) return null;
        var result = TryBeginPublication(target, Guid.NewGuid(), cancellation);
        if (result.Refusal == ImageAdmissionRefusal.Cancelled) cancellation.ThrowIfCancellationRequested();
        if (result.Refusal == ImageAdmissionRefusal.InvalidDomain && result.MissingTargetParent)
            throw new DirectoryNotFoundException("Native target parent does not exist.");
        return result.RequireAdmission();
    }
    private static bool TargetExists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new AdmissionException(ImageAdmissionRefusal.Unsupported);
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }
    // Positive absence evidence for a drained creation, including retained quarantine reconciliation.
    internal void ValidateCreationTargetAbsent()
    {
        lock (sync)
        {
            Active(ImageAdmissionUse.Publication);
            if (publicationSource is not null) throw new InvalidOperationException("Not an absent-target publication.");
            using var parent = ImageAdmissionNative.Directory(Path.GetDirectoryName(publicationPath!)!, false, privateTest);
            if (TargetExists(publicationPath!)) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        }
    }
    internal void ValidatePublicationTargetStillCurrent()
    {
        lock (sync)
        {
            Active(ImageAdmissionUse.Publication);
            if (record.State != 1) throw new InvalidOperationException("Publication is unresolved, not an active writer.");
            using var parent = ImageAdmissionNative.Directory(Path.GetDirectoryName(publicationPath!)!, false, privateTest);
            var exists = TargetExists(publicationPath!);
            if (publicationSource is null)
            {
                if (exists) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
                return;
            }
            if (!exists) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            var current = ImageAdmissionNative.FileAt(publicationPath!, privateTest);
            if (current.Id != publicationSource.FileId || current.Length != (ulong)publicationSource.Length || current.Links != 1)
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
        }
    }
    private void Persist()
    { gate.Position = 0; gate.Write(record.Encode()); gate.Flush(true); }
    private void Active(ImageAdmissionUse use)
    { if (terminal || Use != use) throw new InvalidOperationException("Stale or wrong-use native admission."); }

    internal FileStream OpenColdSource(string path) => OpenSource(path, ImageAdmissionUse.ColdRead, 65536);
    internal FileStream OpenPublicationSource(string path) => OpenSource(path, ImageAdmissionUse.Publication, 1);
    private FileStream OpenSource(string path, ImageAdmissionUse use, int bufferBytes)
    {
        lock (sync)
        {
            Active(use);
            if (path.Length > 4096) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            var canonical = Path.GetFullPath(path);
            if (use == ImageAdmissionUse.Publication && !string.Equals(publicationPath, canonical, StringComparison.Ordinal))
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            var handle = ImageAdmissionNative.OpenSource(canonical, privateTest);
            try { return new FileStream(handle, FileAccess.Read, bufferBytes, false); }
            catch { handle.Dispose(); throw; }
        }
    }
    internal AdmittedImageFacts ValidateOpenedSource(FileStream source, string path)
    {
        lock (sync)
        {
            if (terminal) throw new InvalidOperationException("Stale admission.");
            var canonical = Path.GetFullPath(path);
            if (Use == ImageAdmissionUse.Publication && !string.Equals(publicationPath, canonical, StringComparison.Ordinal))
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            var byPath = ImageAdmissionNative.FileAt(canonical, privateTest);
            var byHandle = ImageAdmissionNative.Probe(source.SafeFileHandle);
            ImageAdmissionNative.Supported(source.SafeFileHandle);
            if (byPath.Id != byHandle.Id || byPath.Length != byHandle.Length || byPath.Links != byHandle.Links ||
                (OperatingSystem.IsLinux() && byHandle.Uid != ImageAdmissionNative.Uid) || (byHandle.Mode & 0xf000) != 0x8000 ||
                (Use == ImageAdmissionUse.Publication && byHandle.Links != 1))
                throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            return new(canonical, byHandle.Id, checked((long)byHandle.Length));
        }
    }
    internal void ValidateSourceStillCurrent(FileStream source, AdmittedImageFacts expected)
    {
        if (ValidateOpenedSource(source, expected.CanonicalPath) != expected)
            throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
    }
    internal void MarkColdOwnerPending(Guid candidateId)
    {
        lock (sync)
        {
            Active(ImageAdmissionUse.ColdRead);
            if (candidate.HasValue || candidateId == Guid.Empty) throw new InvalidOperationException("Candidate already pending or invalid.");
            candidate = candidateId; retained.TryAdd(this, 0);
        }
    }
    internal void ValidateColdOwner(Guid candidateId, FileStream source, AdmittedImageFacts expected)
    {
        lock (sync)
        {
            Active(ImageAdmissionUse.ColdRead);
            if (candidate != candidateId) throw new InvalidOperationException("Wrong cold owner.");
            ValidateSourceStillCurrent(source, expected);
        }
    }
    internal void CompleteColdOwnerAfterDrain(Guid candidateId)
    {
        lock (sync)
        {
            Active(ImageAdmissionUse.ColdRead);
            if (candidate != candidateId) throw new InvalidOperationException("Wrong cold owner.");
            Release();
        }
    }
    internal void CompleteKnownPublicationAfterDrain(KnownImagePublication outcome)
    {
        lock (sync)
        {
            Active(ImageAdmissionUse.Publication);
            if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
            record = ImageAdmissionRecord.Clean(record.Domain);
            try { Persist(); }
            catch { retained.TryAdd(this, 0); throw; }
            Release();
        }
    }
    internal void RetainUnknownPublication()
    {
        lock (sync)
        {
            Active(ImageAdmissionUse.Publication);
            retained.TryAdd(this, 0); record.Quarantine(); Persist();
        }
    }
    private void Release()
    { terminal = true; gate.Dispose(); retained.TryRemove(this, out _); }
    public void Dispose()
    {
        lock (sync)
        {
            if (terminal) return;
            if (Use == ImageAdmissionUse.ColdRead && !candidate.HasValue) Release();
            else if (Use == ImageAdmissionUse.Publication)
            {
                // RetainUnknown roots the actual FD before its best-effort durable quarantine write;
                // an existing WriteIntent already refuses owner-loss recovery if that write fails.
                try { RetainUnknownPublication(); }
                catch (IOException) { }
            }
        }
    }
}
