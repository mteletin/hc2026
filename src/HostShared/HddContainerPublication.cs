using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

/// <summary>Host-only bounded whole-file replacement, shared by App and Tools. Public inputs and receipts
/// describe host evidence, not machine authority; a live caller must retain its independent FIFO fence.</summary>
public static class HddContainerPublication
{
    public sealed record StoreIdentity(ImmutableArray<byte> ContainerId, ulong Revision, ImmutableArray<byte> MetadataSha256);
    public sealed record VolumeIdentity(StoreIdentity Store, string Name, ImmutableArray<byte> PayloadSha256);
    public sealed record PublicationLimits(ulong MaxFileBytes, ulong MaxEntries, ulong MaxIndexBytes,
        ulong MaxMetadataBytes, ulong MaxSelectedBytes, ulong MaxResidentBytes, ulong MaxSnapshotBytes,
        ulong MaxCopyWorkBytes, ulong MaxTotalLiveBytes, ulong MaxTransactionBytes, ulong MaxIoBytes);
    public sealed record PublicationUsage(uint ConcurrentSelectedImages, ulong ResidentBytes, ulong SnapshotBytes,
        ulong TransferBufferBytes);

    // Shared synchronous whole-container publication engine. CLI argument parsing is not part of this seam.
    public enum OfflineOutcome { Refused, KnownOld, KnownNew, Unknown }
    public enum OfflineError {
        None, InvalidRequest, UnsupportedStore, Busy, AbandonedLease,
        ReadOnly, Protected, StaleIdentity, InvalidImageSize,
        InvalidContainer, ResourceLimit, Cancelled, IoFailure,
        VerificationFailed, ReconciliationFailed,
        AlreadyInFlight, OwnerAbandoned, QuarantinePoisoned, ReconcileCapExhausted
    }
    public enum OfflineIoPoint {
        SourceOpen, FooterRead, IndexRead, SourceValidationRead,
        TempCreate, TempWrite, TempVerifyRead, SourceRecheckRead,
        BeforeMove, AfterMove, TargetReadback, TempDelete
    }
    public readonly record struct OfflineIoEvent(OfflineIoPoint Point, ulong Offset, int Count);
    public sealed record OfflineOwnership(bool LocalDisposable, bool ExclusivelyOwned, bool Unbound,
        bool SameHostSecurityDomain);
    public sealed record OfflineReplaceRequest(string TargetPath, VolumeIdentity ExpectedVolume,
        Guid OwnerEpoch, Guid AttemptId, OfflineOwnership Ownership, PublicationLimits Limits, PublicationUsage Usage);
    public sealed record OfflineCharge(ulong MetadataBytes, ulong CopyWorkBytes, ulong TotalLiveBytes,
        ulong TransactionBytes, ulong ReservedIoBytes, ulong ActualIoBytes, ulong TransferBufferBytes);
    public sealed record OfflineReceipt(string TargetPath, Guid OwnerEpoch, Guid AttemptId,
        VolumeIdentity ExpectedVolume, VolumeIdentity? IntendedVolume,
        ImmutableArray<byte> ExpectedFileSha256, ImmutableArray<byte> IntendedFileSha256,
        bool RenameMayHaveStarted, bool WriterDrained, OfflineOutcome Outcome, OfflineError Error,
        OfflineCharge Charge, Guid? BlockingAttemptId);
    public sealed record OfflineAbandonReceipt(Guid AttemptId, bool Abandoned, OfflineError Fault, DateTimeOffset Utc);

    private const int OfflineBufferBytes = 32768;
    private const int MaxQuarantined = 4;
    // Before bounded shape admission there is no validated identity snapshot to report.
    private static readonly VolumeIdentity OfflineUnvalidatedIdentity = new(new([], 0, []), "", []);
    private static OfflineAttempt? offlineAdmission;
    private static readonly object offlineState = new();
    private static readonly OfflineAttempt?[] offlineQuarantine = new OfflineAttempt?[MaxQuarantined];
    private static bool offlineTerminalPoison;
    private static long offlineSequence;
    private static long offlineUtcTicks;

    private static DateTimeOffset OfflineStamp()
    {
        // Called under offlineState. Wall time is evidence only, never lease expiry.
        offlineUtcTicks = Math.Max(DateTimeOffset.UtcNow.Ticks, checked(offlineUtcTicks + 1));
        return new DateTimeOffset(offlineUtcTicks, TimeSpan.Zero);
    }
    private sealed class OfflineRefusal(OfflineError error) : Exception
    { public OfflineError Error => error; }
    private static void OfflineRequire([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, OfflineError error)
    { if (!condition) throw new OfflineRefusal(error); }
    private static bool OfflineHash(ImmutableArray<byte> value) => !value.IsDefault && value.Length == 32;
    private static bool OfflineId(ImmutableArray<byte> value) => !value.IsDefault && value.Length == 16 && value.Any(b => b != 0);
    private static ImmutableArray<byte> OfflineDigest(ReadOnlySpan<byte> value) => ImmutableArray.CreateRange(SHA256.HashData(value));
    private static bool OfflineEqual(ImmutableArray<byte> a, ImmutableArray<byte> b) => a.AsSpan().SequenceEqual(b.AsSpan());

    public sealed class OfflineAttempt : IDisposable
    {
        internal readonly OfflineReplaceRequest Request;
        internal readonly Action<OfflineIoEvent>? Fault;
        internal readonly long Sequence;
        internal byte[] Buffer = [];
        internal ulong FileBytes;
        internal bool OwnsAdmission;
        internal bool Retained;
        internal bool Completed;
        internal bool WriterActive = true;
        internal NativeImageAdmission? NativeAdmission;
        internal OfflineAbandonReceipt? Abandonment;
        private OfflineReceipt receipt;
        public OfflineReceipt Receipt { get { lock (offlineState) return receipt; } }

        internal OfflineAttempt(OfflineReplaceRequest request, Action<OfflineIoEvent>? fault)
        {
            Request = request;
            Fault = fault;
            Sequence = Interlocked.Increment(ref offlineSequence);
            receipt = new(request?.TargetPath ?? "", request?.OwnerEpoch ?? Guid.Empty, request?.AttemptId ?? Guid.Empty,
                OfflineUnvalidatedIdentity, null, [], [], false, true, OfflineOutcome.Refused, OfflineError.None,
                new(0, 0, 0, 0, 0, 0, 0), null);
        }
        internal FileStream OpenSource(string path) => NativeAdmission?.OpenPublicationSource(path) ?? OfflineOpen(path);
        internal void Set(OfflineReceipt value) { lock (offlineState) receipt = value; }
        internal void Observe(OfflineIoPoint point, ulong offset = 0, int count = 0) => Fault?.Invoke(new(point, offset, count));
        internal void Charge(int count)
        {
            var r = Receipt;
            var actual = checked(r.Charge.ActualIoBytes + (ulong)count);
            OfflineRequire(actual <= r.Charge.ReservedIoBytes, OfflineError.ResourceLimit);
            Set(r with { Charge = r.Charge with { ActualIoBytes = actual } });
        }
        internal void Read(FileStream stream, Span<byte> bytes, OfflineIoPoint point)
        {
            Observe(point, (ulong)stream.Position, bytes.Length);
            Charge(bytes.Length); // Partial/uncertain reads conservatively charge the requested amount.
            stream.ReadExactly(bytes);
        }
        internal void Write(FileStream stream, ReadOnlySpan<byte> bytes)
        {
            Observe(OfflineIoPoint.TempWrite, (ulong)stream.Position, bytes.Length);
            Charge(bytes.Length);
            stream.Write(bytes);
        }
        internal void Finish(OfflineOutcome outcome, OfflineError error)
        {
            lock (offlineState)
            {
                receipt = receipt with { Outcome = outcome, Error = error, WriterDrained = true };
                if (outcome == OfflineOutcome.Unknown)
                {
                    Retained = true;
                    if (!offlineQuarantine.Contains(this))
                    {
                        var slot = Array.IndexOf(offlineQuarantine, null);
                        if (slot < 0)
                        {
                            // Defensive guard, unreachable by supported count<=1 transitions.
                            offlineTerminalPoison = true;
                            receipt = receipt with { Error = OfflineError.ReconcileCapExhausted };
                        }
                        else offlineQuarantine[slot] = this;
                    }
                    return; // Retain the token; neither Dispose nor another invocation can free it.
                }
                Completed = true;
                Retained = false;
                for (var i = 0; i < MaxQuarantined; i++)
                    if (ReferenceEquals(offlineQuarantine[i], this)) offlineQuarantine[i] = null;
                if (OwnsAdmission && !offlineTerminalPoison && offlineQuarantine.All(a => a is null))
                    Interlocked.CompareExchange(ref offlineAdmission, null, this);
                OwnsAdmission = false;
                Buffer = [];
            }
        }
        public OfflineAbandonReceipt Abandon(OfflineError fault)
        {
            lock (offlineState)
            {
                if (Completed || !OwnsAdmission || Sequence <= 0 || !ReferenceEquals(offlineAdmission, this))
                    return new(Receipt.AttemptId, false, OfflineError.InvalidRequest, OfflineStamp());
                if (Abandonment is not null) return Abandonment;
                Abandonment = new(Receipt.AttemptId, true, fault, OfflineStamp());
                Finish(OfflineOutcome.Unknown, Receipt.Error);
                return Abandonment;
            }
        }
        public OfflineReceipt Reconcile()
        {
            lock (offlineState)
            {
                if (WriterActive || Completed || !OwnsAdmission || !Retained) return receipt;
            }
            // Only this owned attempt reads; no detached writer or cap reset. Serialize same-handle readbacks.
            lock (this)
            {
                if (Completed) return Receipt;
                Resolve(Receipt.Error);
                DrainNative();
                return Receipt;
            }
        }
        internal void DrainNative()
        {
            lock (offlineState)
            {
                if (WriterActive || NativeAdmission is null) return;
                try
                {
                    if (receipt.Outcome == OfflineOutcome.Unknown) NativeAdmission.RetainUnknownPublication();
                    else
                    {
                        NativeAdmission.CompleteKnownPublicationAfterDrain(receipt.Outcome switch
                        {
                            OfflineOutcome.KnownNew => KnownImagePublication.KnownNew,
                            OfflineOutcome.KnownOld => KnownImagePublication.KnownOld,
                            _ => KnownImagePublication.Unpublished
                        });
                        NativeAdmission = null;
                    }
                }
                catch (IOException) { receipt = receipt with { Error = OfflineError.IoFailure }; }
            }
        }
        internal void Resolve(OfflineError cause)
        {
            try
            {
                var r = Receipt;
                OfflineRequire(OfflineHash(r.ExpectedFileSha256), OfflineError.ReconciliationFailed);
                using var stream = OpenSource(r.TargetPath);
                var facts = NativeAdmission?.ValidateOpenedSource(stream, r.TargetPath);
                OfflineRequire((ulong)stream.Length == FileBytes, OfflineError.ReconciliationFailed);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (stream.Position < stream.Length)
                {
                    var count = (int)Math.Min(Buffer.Length, stream.Length - stream.Position);
                    OfflineRequire(count > 0, OfflineError.ReconciliationFailed);
                    Read(stream, Buffer.AsSpan(0, count), OfflineIoPoint.TargetReadback);
                    hash.AppendData(Buffer, 0, count);
                }
                var actual = ImmutableArray.CreateRange(hash.GetHashAndReset());
                if (facts is not null) NativeAdmission!.ValidateSourceStillCurrent(stream, facts);
                if (OfflineEqual(actual, r.IntendedFileSha256)) Finish(OfflineOutcome.KnownNew, cause);
                else if (OfflineEqual(actual, r.ExpectedFileSha256)) Finish(OfflineOutcome.KnownOld, cause);
                else Finish(r.RenameMayHaveStarted ? OfflineOutcome.Unknown : OfflineOutcome.Refused, OfflineError.ReconciliationFailed);
            }
            catch (Exception ex)
            {
                var error = ex is OfflineRefusal refusal ? refusal.Error : OfflineError.ReconciliationFailed;
                Finish(Receipt.RenameMayHaveStarted ? OfflineOutcome.Unknown : OfflineOutcome.Refused, error);
            }
        }
        public void Dispose()
        {
            // No ownership release for Unknown, no throwing Dispose strategy, no background cleanup.
            lock (offlineState) { if (Completed || !OwnsAdmission) Buffer = []; }
        }
    }

    private static FileStream OfflineOpen(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    public static OfflineAttempt ReplaceOffline(OfflineReplaceRequest request, ReadOnlySpan<byte> replacement,
        CancellationToken cancellationToken = default, Action<OfflineIoEvent>? fault = null)
        => Replace(request, replacement, [], null, cancellationToken, fault);

    // The existing owner ledger supplies the continuous freeze, exact native capture,
    // and ALL mounted baselines. This is not an arbitrary live replacement API.
    internal static OfflineAttempt ReplaceCaptured(OfflineReplaceRequest request, ReadOnlySpan<byte> capture,
        ImmutableArray<VolumeIdentity> bindings, Action<VolumeIdentity> prepareSideband,
        CancellationToken cancellationToken = default, Action<OfflineIoEvent>? fault = null)
        => Replace(request, capture, bindings, prepareSideband, cancellationToken, fault);

    private static OfflineAttempt Replace(OfflineReplaceRequest request, ReadOnlySpan<byte> replacement,
        ImmutableArray<VolumeIdentity> bindings, Action<VolumeIdentity>? prepareSideband,
        CancellationToken cancellationToken, Action<OfflineIoEvent>? fault)
    {
        var attempt = new OfflineAttempt(request, fault);
        string? temporary = null;
        try
        {
            OfflineShape(request, replacement.Length, prepareSideband is not null);
            OfflineRequire(prepareSideband is null || (!bindings.IsDefaultOrEmpty && bindings.Length <= 4
                && bindings.All(b => b is not null && b.Store is not null && OfflineHash(b.PayloadSha256))), OfflineError.InvalidRequest);
            var supplied = request.ExpectedVolume;
            var expected = new VolumeIdentity(new(ImmutableArray.Create<byte>(supplied.Store.ContainerId.AsSpan()),
                supplied.Store.Revision, ImmutableArray.Create<byte>(supplied.Store.MetadataSha256.AsSpan())),
                supplied.Name, ImmutableArray.Create<byte>(supplied.PayloadSha256.AsSpan()));
            attempt.Set(attempt.Receipt with { ExpectedVolume = expected,
                Charge = attempt.Receipt.Charge with { MetadataBytes = 128 } });
            var prior = Interlocked.CompareExchange(ref offlineAdmission, attempt, null);
            if (prior is not null)
            {
                lock (offlineState)
                    attempt.Set(attempt.Receipt with { Error = prior.Retained || offlineTerminalPoison
                        ? OfflineError.QuarantinePoisoned : OfflineError.AlreadyInFlight,
                        BlockingAttemptId = prior.Receipt.AttemptId });
                attempt.Completed = true;
                return attempt;
            }
            attempt.OwnsAdmission = true;
            OfflineRequire(!offlineTerminalPoison, OfflineError.QuarantinePoisoned);
            var full = Path.GetFullPath(request.TargetPath);
            attempt.Set(attempt.Receipt with { TargetPath = full });
            if (NativeImageAdmission.PlatformMode == ImageAdmissionPlatformMode.GuardedLinux)
            {
                var native = NativeImageAdmission.TryBeginPublication(full, request.AttemptId, cancellationToken);
                if (native.Admission is null)
                    throw new OfflineRefusal(native.Refusal switch
                    {
                        ImageAdmissionRefusal.Busy => OfflineError.Busy,
                        ImageAdmissionRefusal.Quarantined => OfflineError.QuarantinePoisoned,
                        ImageAdmissionRefusal.Cancelled => OfflineError.Cancelled,
                        ImageAdmissionRefusal.Unsupported or ImageAdmissionRefusal.InvalidDomain => OfflineError.UnsupportedStore,
                        _ => OfflineError.IoFailure
                    });
                attempt.NativeAdmission = native.Admission;
            }
            // All ancestors must be local ordinary directories, not reparse/symlink entry paths.
            for (FileSystemInfo? item = new FileInfo(full); item is not null;
                 item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                OfflineRequire((item.Attributes & FileAttributes.ReparsePoint) == 0, OfflineError.UnsupportedStore);
            OfflineRequire((File.GetAttributes(full) & FileAttributes.ReadOnly) == 0, OfflineError.ReadOnly);
            if (!OperatingSystem.IsWindows())
                OfflineRequire((File.GetUnixFileMode(full) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0,
                    OfflineError.ReadOnly);
            attempt.Observe(OfflineIoPoint.SourceOpen);
            using (var source = attempt.OpenSource(full))
            {
                var sourceFacts = attempt.NativeAdmission?.ValidateOpenedSource(source, full);
                var length = checked((ulong)source.Length);
                attempt.FileBytes = length;
                OfflineRequire(length >= 64 && length <= request.Limits.MaxFileBytes && length <= uint.MaxValue
                    && checked(2 * length) <= request.Limits.MaxTransactionBytes
                    && checked(7 * length + 64) <= request.Limits.MaxIoBytes, OfflineError.ResourceLimit);
                var codecLimits = OfflineCodecLimits(request.Limits);
                // The bounded enclosing footer is admitted before any variable-size allocation.
                attempt.Set(attempt.Receipt with { Charge = attempt.Receipt.Charge with { ReservedIoBytes = request.Limits.MaxIoBytes } });
                byte[] footer = new byte[64];
                source.Position = (long)length - 64;
                attempt.Read(source, footer, OfflineIoPoint.FooterRead);
                var inspected = HddCatalogCodec.InspectHddFooter(footer, length, codecLimits);
                OfflineRequire(inspected.Success, OfflineError.InvalidContainer);
                var view = inspected.Value!;
                OfflineBudget(attempt, view);
                var index = new byte[checked((int)view.IndexBytes)];
                source.Position = checked((long)view.IndexStart);
                attempt.Read(source, index, OfflineIoPoint.IndexRead);
                var decoded = HddCatalogCodec.DecodeHdd(footer, index, length, codecLimits);
                OfflineRequire(decoded.Success, OfflineError.InvalidContainer);
                var descriptor = decoded.Value!;
                using var tokenHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                tokenHash.AppendData(index); tokenHash.AppendData(footer);
                var token = ImmutableArray.CreateRange(tokenHash.GetHashAndReset());
                OfflineRequire(view.ContainerId.AsSpan().SequenceEqual(expected.Store.ContainerId.AsSpan())
                    && view.Revision == expected.Store.Revision && OfflineEqual(token, expected.Store.MetadataSha256), OfflineError.StaleIdentity);
                foreach (var binding in bindings)
                    OfflineRequire(binding.Store.Revision == view.Revision
                        && binding.Store.ContainerId.AsSpan().SequenceEqual(view.ContainerId)
                        && OfflineEqual(binding.Store.MetadataSha256, token)
                        && descriptor.Entries.Any(e => StringComparer.OrdinalIgnoreCase.Equals(e.Name, binding.Name)), OfflineError.StaleIdentity);
                var selected = descriptor.Entries.FirstOrDefault(e => StringComparer.OrdinalIgnoreCase.Equals(e.Name, expected.Name));
                OfflineRequire(selected is not null, OfflineError.StaleIdentity);
                OfflineRequire(!selected!.ReadOnly, OfflineError.Protected);
                OfflineRequire(view.Revision < ulong.MaxValue, OfflineError.ResourceLimit);
                attempt.Buffer = new byte[OfflineBufferBytes];
                var oldHash = OfflineValidate(attempt, source, descriptor.Entries, index, footer,
                    selected.SlotOrdinal, expected.PayloadSha256, OfflineIoPoint.SourceValidationRead, bindings);
                attempt.Set(attempt.Receipt with { ExpectedFileSha256 = oldHash });
                var replacementCrc = VolumeCatalogMetadata.Crc32(replacement);
                var entries = descriptor.Entries.Select(e => e.SlotOrdinal == selected.SlotOrdinal
                    ? e with { ImageCrc = replacementCrc } : e).ToImmutableArray();
                var encoded = HddCatalogCodec.EncodeHdd(view.ContainerId, checked(view.Revision + 1), entries, codecLimits);
                OfflineRequire(encoded.Success, OfflineError.InvalidContainer);
                var metadata = encoded.Value!;
                using var newTokenHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                newTokenHash.AppendData(metadata.Index.AsSpan()); newTokenHash.AppendData(metadata.Footer.AsSpan());
                var intended = new VolumeIdentity(new(ImmutableArray.CreateRange(view.ContainerId), view.Revision + 1,
                    ImmutableArray.CreateRange(newTokenHash.GetHashAndReset())), selected.Name, OfflineDigest(replacement));
                attempt.Set(attempt.Receipt with { IntendedVolume = intended });
                prepareSideband?.Invoke(intended); // Must finish BEFORE any publication.
                cancellationToken.ThrowIfCancellationRequested();
                temporary = full + ".hc-" + Guid.NewGuid().ToString("N");
                attempt.Observe(OfflineIoPoint.TempCreate);
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
                {
                    source.Position = 0;
                    for (ulong offset = 0; offset < view.ArenaBytes;)
                    {
                        var count = (int)Math.Min((ulong)OfflineBufferBytes, view.ArenaBytes - offset);
                        attempt.Read(source, attempt.Buffer.AsSpan(0, count), OfflineIoPoint.SourceValidationRead);
                        var selectedStart = (ulong)selected.SlotOrdinal.Value * VolumeCatalogMetadata.SlotBytes;
                        if (offset >= selectedStart && offset < selectedStart + VolumeCatalogMetadata.SlotBytes)
                            attempt.Write(target, replacement.Slice(checked((int)(offset - selectedStart)), count));
                        else attempt.Write(target, attempt.Buffer.AsSpan(0, count));
                        offset += (ulong)count;
                    }
                    attempt.Write(target, metadata.Index.AsSpan());
                    attempt.Write(target, metadata.Footer.AsSpan());
                }
                using (var verify = OfflineOpen(temporary))
                {
                    var newHash = OfflineValidate(attempt, verify, entries, metadata.Index.AsSpan(), metadata.Footer.AsSpan(),
                        selected.SlotOrdinal, intended.PayloadSha256, OfflineIoPoint.TempVerifyRead);
                    attempt.Set(attempt.Receipt with { IntendedFileSha256 = newHash });
                }
                // Reopen the path: rechecking only an old open inode would miss replaced-path races.
                using (var recheck = attempt.OpenSource(full))
                {
                    if (sourceFacts is not null) attempt.NativeAdmission!.ValidateSourceStillCurrent(recheck, sourceFacts);
                    var again = OfflineValidate(attempt, recheck, descriptor.Entries, index, footer, selected.SlotOrdinal,
                        expected.PayloadSha256, OfflineIoPoint.SourceRecheckRead, bindings);
                    OfflineRequire(OfflineEqual(again, oldHash), OfflineError.StaleIdentity);
                }
            }
            attempt.Observe(OfflineIoPoint.BeforeMove);
            cancellationToken.ThrowIfCancellationRequested();
            attempt.NativeAdmission?.ValidatePublicationTargetStillCurrent();
            attempt.Set(attempt.Receipt with { RenameMayHaveStarted = true });
            File.Move(temporary!, full, true);
            attempt.Observe(OfflineIoPoint.AfterMove);
            attempt.Resolve(OfflineError.None);
        }
        catch (Exception ex)
        {
            var error = ex switch {
                OfflineRefusal refusal => refusal.Error,
                OperationCanceledException => OfflineError.Cancelled,
                OverflowException or OutOfMemoryException => OfflineError.ResourceLimit,
                UnauthorizedAccessException => OfflineError.ReadOnly,
                _ => OfflineError.IoFailure
            };
            if (attempt.OwnsAdmission && OfflineHash(attempt.Receipt.ExpectedFileSha256)) attempt.Resolve(error);
            else attempt.Finish(OfflineOutcome.Refused, error);
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        attempt.Observe(OfflineIoPoint.TempDelete);
                        File.Delete(temporary);
                    }
                }
                catch (Exception) { /* Cleanup cannot rewrite the publication outcome or release Unknown. */ }
            }
            lock (offlineState) attempt.WriterActive = false;
            attempt.DrainNative();
        }
        return attempt;
    }

    private static void OfflineShape(OfflineReplaceRequest request, int size, bool captured)
    {
        OfflineRequire(request is not null && request.Ownership is not null && request.Limits is not null
            && request.Usage is not null && request.ExpectedVolume is not null && request.ExpectedVolume.Store is not null,
            OfflineError.InvalidRequest);
        OfflineRequire(size == VolumeCatalogMetadata.SlotBytes, OfflineError.InvalidImageSize);
        // Reserve the fixed enclosing metadata before name allocation or the first footer read.
        OfflineRequire(request.Limits.MaxMetadataBytes >= 128, OfflineError.ResourceLimit);
        OfflineRequire(!string.IsNullOrWhiteSpace(request!.TargetPath) && !request.TargetPath.StartsWith("\\\\", StringComparison.Ordinal)
            && !request.TargetPath.StartsWith("//", StringComparison.Ordinal), OfflineError.UnsupportedStore);
        OfflineRequire(request.OwnerEpoch != Guid.Empty && request.AttemptId != Guid.Empty
            && OfflineId(request.ExpectedVolume.Store.ContainerId) && request.ExpectedVolume.Store.Revision > 0
            && OfflineHash(request.ExpectedVolume.Store.MetadataSha256) && OfflineHash(request.ExpectedVolume.PayloadSha256)
            && VolumeCatalogMetadata.CanonicalName83(request.ExpectedVolume.Name) is not null, OfflineError.InvalidRequest);
        OfflineRequire(request.Ownership is { LocalDisposable: true, ExclusivelyOwned: true, SameHostSecurityDomain: true }
            && request.Ownership.Unbound != captured,
            OfflineError.UnsupportedStore);
        var l = request.Limits; var u = request.Usage;
        OfflineRequire(l.MaxFileBytes > 0 && l.MaxEntries > 0 && l.MaxIndexBytes > 0 && l.MaxMetadataBytes > 0
            && l.MaxSelectedBytes > 0 && l.MaxSnapshotBytes > 0 && l.MaxCopyWorkBytes > 0
            && l.MaxTotalLiveBytes > 0 && l.MaxTransactionBytes > 0 && l.MaxIoBytes >= 64
            && u.ConcurrentSelectedImages is >= 1 and <= 4 && u.SnapshotBytes >= VolumeCatalogMetadata.SlotBytes
            && u.ResidentBytes <= 4UL * VolumeCatalogMetadata.SlotBytes
            && u.TransferBufferBytes <= 65536 - OfflineBufferBytes, OfflineError.ResourceLimit);
        checked
        {
            var selected = (ulong)u.ConcurrentSelectedImages * VolumeCatalogMetadata.SlotBytes;
            var copies = 3 * selected + u.TransferBufferBytes + OfflineBufferBytes;
            OfflineRequire(selected <= l.MaxSelectedBytes && u.ResidentBytes <= l.MaxResidentBytes
                && u.SnapshotBytes <= l.MaxSnapshotBytes && copies <= l.MaxCopyWorkBytes
                && u.ResidentBytes + u.SnapshotBytes + copies <= l.MaxTotalLiveBytes, OfflineError.ResourceLimit);
        }
    }
    private static HddLimits OfflineCodecLimits(PublicationLimits limits) => new(
        (uint)Math.Min(limits.MaxFileBytes, uint.MaxValue), (uint)Math.Min(limits.MaxEntries, 65535),
        (uint)Math.Min(limits.MaxIndexBytes, int.MaxValue), (uint)Math.Min(limits.MaxMetadataBytes, uint.MaxValue));
    private static void OfflineBudget(OfflineAttempt a, HddFooterView view)
    {
        var l = a.Request.Limits; var u = a.Request.Usage;
        checked
        {
            var n = (ulong)view.Entries; var i = (ulong)view.IndexBytes; var f = view.FileLength;
            var metadata = 4 * i + 128 * n + 128;
            var selected = (ulong)u.ConcurrentSelectedImages * VolumeCatalogMetadata.SlotBytes;
            var q = u.TransferBufferBytes + OfflineBufferBytes;
            var copies = 3 * selected + q;
            var total = u.ResidentBytes + u.SnapshotBytes + metadata + copies;
            var transaction = 2 * f;
            // All six normal full-file passes plus at least one reconciliation, preliminary metadata included.
            var io = 7 * f + i + 64;
            OfflineRequire(n > 0 && n <= 65535 && n <= l.MaxEntries && f <= l.MaxFileBytes && i <= l.MaxIndexBytes
                && metadata <= l.MaxMetadataBytes && selected <= l.MaxSelectedBytes
                && u.ResidentBytes <= l.MaxResidentBytes && u.SnapshotBytes <= l.MaxSnapshotBytes
                && copies <= l.MaxCopyWorkBytes && total <= l.MaxTotalLiveBytes && transaction <= l.MaxTransactionBytes
                && io <= l.MaxIoBytes && q <= 65536, OfflineError.ResourceLimit);
            a.Set(a.Receipt with { Charge = new(metadata, copies, total, transaction, l.MaxIoBytes,
                a.Receipt.Charge.ActualIoBytes, q) });
        }
    }
    private static ImmutableArray<byte> OfflineValidate(OfflineAttempt a, FileStream stream,
        ImmutableArray<HddCatalogEntry> entries, ReadOnlySpan<byte> index, ReadOnlySpan<byte> footer,
        HddSlotOrdinal selected, ImmutableArray<byte> selectedHash, OfflineIoPoint point,
        ImmutableArray<VolumeIdentity> bindings = default)
    {
        OfflineRequire((ulong)stream.Length == a.FileBytes, OfflineError.VerificationFailed);
        stream.Position = 0;
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (uint slot = 0; slot < entries.Length; slot++)
        {
            var row = entries.First(e => e.SlotOrdinal.Value == slot);
            var crc = VolumeCatalogMetadata.Crc32InitialState;
            using var imageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var remaining = (int)VolumeCatalogMetadata.SlotBytes; remaining > 0;)
            {
                var count = Math.Min(remaining, a.Buffer.Length);
                a.Read(stream, a.Buffer.AsSpan(0, count), point);
                crc = VolumeCatalogMetadata.Crc32Update(crc, a.Buffer.AsSpan(0, count));
                whole.AppendData(a.Buffer, 0, count);
                imageHash.AppendData(a.Buffer, 0, count);
                remaining -= count;
            }
            OfflineRequire(VolumeCatalogMetadata.Crc32Finish(crc) == row.ImageCrc, OfflineError.VerificationFailed);
            var hash = imageHash.GetHashAndReset();
            if (slot == selected.Value)
                OfflineRequire(hash.AsSpan().SequenceEqual(selectedHash.AsSpan()), OfflineError.StaleIdentity);
            if (!bindings.IsDefaultOrEmpty)
                foreach (var binding in bindings)
                    if (StringComparer.OrdinalIgnoreCase.Equals(binding.Name, row.Name))
                        OfflineRequire(hash.AsSpan().SequenceEqual(binding.PayloadSha256.AsSpan()), OfflineError.StaleIdentity);
        }
        // Metadata is small but still streamed/charged; no second index-sized transfer buffer.
        for (var position = 0; position < index.Length;)
        {
            var count = Math.Min(index.Length - position, a.Buffer.Length);
            a.Read(stream, a.Buffer.AsSpan(0, count), point);
            OfflineRequire(a.Buffer.AsSpan(0, count).SequenceEqual(index.Slice(position, count)), OfflineError.VerificationFailed);
            whole.AppendData(a.Buffer, 0, count); position += count;
        }
        a.Read(stream, a.Buffer.AsSpan(0, footer.Length), point);
        OfflineRequire(a.Buffer.AsSpan(0, footer.Length).SequenceEqual(footer), OfflineError.VerificationFailed);
        whole.AppendData(a.Buffer, 0, footer.Length);
        return ImmutableArray.CreateRange(whole.GetHashAndReset());
    }

}
