namespace Hc2000.HostFiles;

internal enum ImageAdmissionRefusal { Busy, Quarantined, Unsupported, InvalidDomain, Cancelled, IoFailure }
internal enum ImageAdmissionUse { ColdRead, Publication }
internal enum ImageAdmissionPlatformMode { GuardedLinux, LegacyOnly }
internal enum KnownImagePublication { Unpublished, KnownOld, KnownNew }
internal readonly record struct NativeImageFileId(uint DeviceMajor, uint DeviceMinor, ulong Inode);
internal sealed record AdmittedImageFacts(string CanonicalPath, NativeImageFileId FileId, long Length);
internal sealed record ImageAdmissionResult(NativeImageAdmission? Admission, ImageAdmissionRefusal? Refusal)
{
    // Error compatibility only, never an alternate admission or permission to run a writer.
    internal bool MissingTargetParent { get; init; }
}

internal sealed class NativeImageAdmission : IDisposable
{
    // Test configuration is internal, flows only in the fixture execution context, and is never a CLI option.
    private sealed record TestDomain(string Root, ImageAdmissionPlatformMode Mode);
    private static readonly AsyncLocal<TestDomain?> testDomain = new();
    internal static ImageAdmissionPlatformMode PlatformMode => testDomain.Value?.Mode ??
        (OperatingSystem.IsLinux() ? ImageAdmissionPlatformMode.GuardedLinux : ImageAdmissionPlatformMode.LegacyOnly);
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

    private NativeImageAdmission(FileStream gate, ImageAdmissionRecord record, ImageAdmissionUse use, bool privateTest)
    { this.gate = gate; this.record = record; Use = use; this.privateTest = privateTest; }

    internal static IDisposable EnterPrivateTestDomain(string root,
        ImageAdmissionPlatformMode? mode = null)
    {
        var previous = testDomain.Value;
        var physical = OperatingSystem.IsLinux() ? ImageAdmissionPlatformMode.GuardedLinux : ImageAdmissionPlatformMode.LegacyOnly;
        testDomain.Value = new(Path.GetFullPath(root), mode ?? physical);
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
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsLinux() || PlatformMode == ImageAdmissionPlatformMode.LegacyOnly)
                return new(null, ImageAdmissionRefusal.Unsupported);
            if (use == ImageAdmissionUse.Publication && (attempt == Guid.Empty || string.IsNullOrWhiteSpace(target) || target.Length > 4096))
                return new(null, ImageAdmissionRefusal.InvalidDomain);
            var isTest = testDomain.Value is not null;
            var root = testDomain.Value?.Root ?? Path.Combine(LinuxImageAdmissionNative.AccountHome(), ".local/state/hc2000/native-image-admission");
            using var directory = LinuxImageAdmissionNative.Directory(root, true, isTest);
            var df = LinuxImageAdmissionNative.Probe(directory);
            if (df.Uid != LinuxImageAdmissionNative.Uid || (df.Mode & 0x1ff) != 0x1c0)
                return new(null, ImageAdmissionRefusal.InvalidDomain);
            var (handle, created) = LinuxImageAdmissionNative.Gate(directory);
            stream = new FileStream(handle, FileAccess.ReadWrite, 1, false);
            ImageAdmissionRecord record;
            if (created)
            {
                record = ImageAdmissionRecord.Clean(Guid.NewGuid());
                stream.Write(record.Encode()); stream.Flush(true);
                LinuxImageAdmissionNative.FlushDirectory(directory);
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
                var path = Path.GetFullPath(target!);
                checkingTargetParent = true;
                using var parent = LinuxImageAdmissionNative.Directory(Path.GetDirectoryName(path)!, false, isTest);
                checkingTargetParent = false;
                // A nonexistent name is allowed only beneath a validated parent. Existing sources are
                // checked again via their actual opened handle before the publisher may stage anything.
                admission.publicationPath = path;
                if (TargetExists(path))
                {
                    var facts = LinuxImageAdmissionNative.FileAt(path, isTest);
                    if (facts.Links != 1) throw new AdmissionException(ImageAdmissionRefusal.Unsupported);
                    admission.publicationSource = new(path, facts.Id, checked((long)facts.Length));
                    record = record.Intent(attempt, path).WithFile(facts.Id);
                }
                else record = record.Intent(attempt, path);
                admission.record = record;
                admission.Persist();
            }
            stream = null;
            return new(admission, null);
        }
        catch (OperationCanceledException) { return new(null, ImageAdmissionRefusal.Cancelled); }
        catch (AdmissionException ex)
        { return new(null, ex.Refusal) { MissingTargetParent = checkingTargetParent && ex.MissingDirectory }; }
        catch (InvalidDataException) { return new(null, ImageAdmissionRefusal.Quarantined); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return new(null, ImageAdmissionRefusal.IoFailure); }
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
        return result.Admission ?? throw new AdmissionException(result.Refusal!.Value);
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
    internal void ValidatePublicationTargetStillCurrent()
    {
        lock (sync)
        {
            Active(ImageAdmissionUse.Publication);
            if (record.State != 1) throw new InvalidOperationException("Publication is unresolved, not an active writer.");
            using var parent = LinuxImageAdmissionNative.Directory(Path.GetDirectoryName(publicationPath!)!, false, privateTest);
            var exists = TargetExists(publicationPath!);
            if (publicationSource is null)
            {
                if (exists) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
                return;
            }
            if (!exists) throw new AdmissionException(ImageAdmissionRefusal.InvalidDomain);
            var current = LinuxImageAdmissionNative.FileAt(publicationPath!, privateTest);
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
            var handle = LinuxImageAdmissionNative.OpenSource(canonical, privateTest);
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
            var byPath = LinuxImageAdmissionNative.FileAt(canonical, privateTest);
            var byHandle = LinuxImageAdmissionNative.Probe(source.SafeFileHandle);
            LinuxImageAdmissionNative.Supported(source.SafeFileHandle);
            if (byPath.Id != byHandle.Id || byPath.Length != byHandle.Length || byPath.Links != byHandle.Links ||
                byHandle.Uid != LinuxImageAdmissionNative.Uid || (byHandle.Mode & 0xf000) != 0x8000 ||
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
