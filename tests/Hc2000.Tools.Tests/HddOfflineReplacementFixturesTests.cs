using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.Machine.Storage;
using Xunit;
using static Hc2000.Tools.StorageCommand;

namespace Hc2000.Tools.Tests;

[CollectionDefinition("Offline replacement private files", DisableParallelization = true)]
public sealed class OfflineReplacementCollection { }

// Independent literal input/expected output, not another writer/codec. All I/O is in a named
// private proof root; the producer runs this serial collection once focused and once in full.
[Collection("Offline replacement private files")]
public sealed class HddOfflineReplacementFixturesTests : IDisposable
{
    private readonly string nativeDomain = Path.Combine(Path.GetTempPath(), "hc-offline-domain-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable nativeScope;
    public HddOfflineReplacementFixturesTests()
    {
        Directory.CreateDirectory(nativeDomain);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(nativeDomain, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        nativeScope = Hc2000.HostFiles.NativeImageAdmission.EnterPrivateTestDomain(nativeDomain, OperatingSystem.IsLinux() ? Hc2000.HostFiles.ImageAdmissionPlatformMode.GuardedLinux : Hc2000.HostFiles.ImageAdmissionPlatformMode.LegacyOnly);
    }
    public void Dispose()
    {
        nativeScope.Dispose();
        Directory.Delete(nativeDomain, true);
    }
    private const int S = 8_388_608;
    private const ulong F = 16_777_340;
    private const string OldIndex = "4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce";
    private const string OldFooter = "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7";
    private const string NewIndex = "4843564401000200564f4c312020202044534b000000800000000000d82b848e564f4c322020202044534b010000800001000000b9eccf254937d2b3";
    private const string NewFooter = "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10020000000000000000000001000000003c0000004937d2b300000000001b8767";
    private const string P0 = "9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F";
    private const string P1 = "F484C7D32E06CBA31D0DD508A1EBC7C26CC4ECDC18A738E040CC3D6BEF7F1EC3";
    private const string P2 = "CB4AA887F4C3A25FCC9BDE077C2F981B21772BDC9407F7FE6ACF57EC8C1BB398";
    private const string OldMetadata = "AF3406F99F322A33D6AE6A52A5ECB66BA90342A271582CDCD5020273BDB0B5E2";
    private const string NewMetadata = "3C4A269C738F296224E69CED8259AE1E5BCEAE248D7876372D3F7E7C8BA736F6";
    private const string NewFile = "21CC527C9D100F38B05B06942BFED8AA7EDF78896DB807ECB8C559F93C40DDB6";
    private static ImmutableArray<byte> Hex(string hex) => [.. Convert.FromHexString(hex)];
    internal static byte[] Payload(byte content)
    {
        var bytes = new byte[S];
        bytes.AsSpan(0, 32768).Fill(0xe5);
        Convert.FromHexString("0053454544202020205458540000000108000000000000000000000000000000").CopyTo(bytes, 0);
        bytes.AsSpan(32768, 4096).Fill(content);
        return bytes;
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(path)!, "receipts.jsonl"),
            JsonSerializer.Serialize(new { label = "fixture-read-outside-operation", path, bytes = stream.Length, hash }) + Environment.NewLine);
        return hash;
    }
    private static HddSelectionLimits Limits() => new(F, 2, 60, 624, S, 0, 2UL * S,
        3UL * S + 32768, 64UL * 1024 * 1024, 2 * F, 512UL * 1024 * 1024);

    private static readonly Lazy<string> ProofRoot = new(() => ResolveProofRoot(
        Environment.GetEnvironmentVariable("HDD_OFFLINE_PROOF_ROOT"), Environment.GetEnvironmentVariable("TMPDIR")));

    internal static string ResolveProofRoot(string? requestedRoot, string? temporaryDirectory)
    {
        var root = requestedRoot;
        if (root is null)
        {
            Assert.False(string.IsNullOrWhiteSpace(temporaryDirectory),
                "Set HDD_OFFLINE_PROOF_ROOT or supply a fresh per-run TMPDIR; never use repository assets.");
            var temporaryRoot = Path.GetFullPath(temporaryDirectory!);
            Assert.True(Directory.Exists(temporaryRoot), "The per-run TMPDIR must already exist.");
            Assert.False(File.GetAttributes(temporaryRoot).HasFlag(FileAttributes.ReparsePoint));
            root = Path.Combine(temporaryRoot, "hc-hdd-offline-proof-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
            else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.False(string.IsNullOrWhiteSpace(root), "HDD_OFFLINE_PROOF_ROOT must name a private run directory.");
        root = Path.GetFullPath(root);
        Assert.True(Directory.Exists(root), "The proof owner must create and record the fresh private run root.");
        Assert.False(File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint));
        Assert.True(new DriveInfo(root).AvailableFreeSpace >= 4L * (long)F, "Insufficient finite proof space.");
        return root;
    }

    internal sealed class PrivateCase
    {
        public string DirectoryPath { get; }
        public string Target { get; }
        public string Original { get; }
        public string Rollback { get; }
        public string OriginalSha { get; }
        public OfflineReplaceRequest Request { get; }
        public PrivateCase(string name)
        {
            var root = ProofRoot.Value;
            DirectoryPath = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Target = Path.Combine(DirectoryPath, "candidate.hdd");
            Original = Path.Combine(DirectoryPath, "original.hdd");
            Rollback = Path.Combine(DirectoryPath, "rollback.hdd");
            // Setup is separately charged, outside the operation. No full-file scratch array.
            using (var file = File.Create(Original))
            {
                file.Write(Payload(0x41)); file.Write(Payload(0x42));
                file.Write(Convert.FromHexString(OldIndex)); file.Write(Convert.FromHexString(OldFooter));
            }
            File.Copy(Original, Rollback); File.Copy(Original, Target);
            OriginalSha = Hash(Original);
            Request = new(Target, new(new(Hex("0102030405060708090a0b0c0d0e0f10"), 1, Hex(OldMetadata)), "VOL1.DSK", Hex(P0)),
                Guid.NewGuid(), Guid.NewGuid(), new(true, true, true, true), Limits(), new(1, 0, S + 80UL, 0));
            Record("setup", new { ProofRoot = root, PrivateCaseDirectory = DirectoryPath,
                FileBytes = F, Volumes = 2, RetainedFixtureFiles = 3 * F,
                SetupWrittenBytes = 3 * F, SetupCopyReadBytes = 2 * F,
                CallerSnapshotBytes = S + 80UL, AdditionalDigestAndReadbackChargedOutsideOperation = true,
                OriginalSha, NamedRollback = Rollback, OperationLimits = Request.Limits, Usage = Request.Usage });
        }
        public void Record(string label, object receipt) => File.AppendAllText(Path.Combine(DirectoryPath, "receipts.jsonl"),
            JsonSerializer.Serialize(new { label, receipt }) + Environment.NewLine);
        public OfflineAttempt Call(byte[] replacement, Action<OfflineIoEvent>? fault = null,
            OfflineReplaceRequest? request = null, CancellationToken cancellation = default)
        {
            var actual = request ?? Request;
            Record("declared-operation", new { actual, ReplacementBytes = replacement.Length });
            var attempt = ReplaceOffline(actual, replacement, cancellation, fault);
            Record("returned", attempt.Receipt);
            return attempt;
        }
        public OfflineReceipt Reconcile(OfflineAttempt attempt)
        {
            var receipt = attempt.Reconcile(); Record("reconcile", receipt); return receipt;
        }
        public void CopiesUnchanged()
        {
            Assert.Equal(OriginalSha, Hash(Original)); Assert.Equal(OriginalSha, Hash(Rollback));
        }
        public void Old()
        {
            CopiesUnchanged(); Assert.Equal(OriginalSha, Hash(Target));
            Assert.Equal(new[] { "candidate.hdd", "original.hdd", "receipts.jsonl", "rollback.hdd" },
                Directory.GetFiles(DirectoryPath).Select(Path.GetFileName).Order().ToArray());
        }
        public void New(OfflineReceipt receipt)
        {
            Assert.Equal(OfflineOutcome.KnownNew, receipt.Outcome);
            Assert.True(receipt.WriterDrained); Assert.True(receipt.RenameMayHaveStarted);
            Assert.Equal(NewFile, Hash(Target));
            using var file = File.OpenRead(Target);
            file.Position = 2L * S;
            var metadata = new byte[124]; file.ReadExactly(metadata);
            Record("fixture-metadata-read-outside-operation", new { Bytes = 124 });
            Assert.Equal(Convert.FromHexString(NewIndex + NewFooter), metadata);
            Assert.Equal(NewMetadata, Convert.ToHexString(SHA256.HashData(metadata)));
            Assert.NotNull(receipt.IntendedVolume);
            Assert.Equal("VOL1.DSK", receipt.IntendedVolume!.Name);
            Assert.Equal(2UL, receipt.IntendedVolume.Store.Revision);
            Assert.Equal(Hex(P2).ToArray(), receipt.IntendedVolume.PayloadSha256.ToArray());
            Assert.Equal(Hex(NewMetadata).ToArray(), receipt.IntendedVolume.Store.MetadataSha256.ToArray());
            Assert.Equal(Hex("0102030405060708090a0b0c0d0e0f10").ToArray(), receipt.IntendedVolume.Store.ContainerId.ToArray());
            Assert.Equal(NewFile, Convert.ToHexString(receipt.IntendedFileSha256.AsSpan()));
            Assert.InRange(receipt.Charge.TransferBufferBytes, 1UL, 65536UL);
            Assert.InRange(receipt.Charge.ActualIoBytes, 1UL, receipt.Charge.ReservedIoBytes);
            Assert.InRange(receipt.Charge.ReservedIoBytes, 1UL, Request.Limits.MaxIoBytes);
            CopiesUnchanged();
        }
    }

    [Fact]
    public void ExactNewHasLiteralMetadataAndOtherVolumeBytesIdentityAndProtection()
    {
        var f = new PrivateCase("exact-new"); var replacement = Payload(0x43);
        Assert.Equal(P2, Convert.ToHexString(SHA256.HashData(replacement)));
        using var attempt = f.Call(replacement);
        f.New(attempt.Receipt);
        Assert.True(P2 == Convert.ToHexString(SHA256.HashData(replacement)), "caller snapshot is never modified");
        // Inputs remain exclusively stable THROUGH ReplaceOffline; only after return expose and
        // alter caller-owned backing with the supported BCL marshal API (no reflection/gate bypass).
        // CreateRange(ImmutableArray) may return its argument; a real owned snapshot must detach.
        var callerIdentity = f.Request.ExpectedVolume;
        System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(callerIdentity.Store.ContainerId)![0] ^= 0xff;
        System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(callerIdentity.Store.MetadataSha256)![0] ^= 0xff;
        System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(callerIdentity.PayloadSha256)![0] ^= 0xff;
        Assert.NotEqual(1, callerIdentity.Store.ContainerId[0]);
        var snapshot = attempt.Receipt.ExpectedVolume;
        Assert.Equal(Hex("0102030405060708090a0b0c0d0e0f10").ToArray(), snapshot.Store.ContainerId.ToArray());
        Assert.Equal(Hex(OldMetadata).ToArray(), snapshot.Store.MetadataSha256.ToArray());
        Assert.Equal(Hex(P0).ToArray(), snapshot.PayloadSha256.ToArray());
        Assert.Equal(1UL, snapshot.Store.Revision); Assert.Equal("VOL1.DSK", snapshot.Name);
        Assert.Equal(JsonSerializer.Serialize(attempt.Receipt), JsonSerializer.Serialize(f.Reconcile(attempt)));
        f.New(attempt.Receipt);
        // Whole-file literal hash plus literal index asserts VOL2 bytes, ordinal and read-only bit unchanged.
        Assert.Contains("564f4c322020202044534b010000800001000000b9eccf25", NewIndex);
    }

    [Theory]
    [InlineData("revision")] [InlineData("metadata")] [InlineData("baseline")]
    [InlineData("id")] [InlineData("protected")] [InlineData("ownership")]
    [InlineData("short")] [InlineData("long")]
    public void InvalidIdentityProtectionOwnershipOrSizeNeverPublishes(string kind)
    {
        var f = new PrivateCase("refuse-" + kind); var request = f.Request;
        var expected = request.ExpectedVolume;
        request = kind switch
        {
            "revision" => request with { ExpectedVolume = expected with { Store = expected.Store with { Revision = 2 } } },
            "metadata" => request with { ExpectedVolume = expected with { Store = expected.Store with { MetadataSha256 = Hex(new string('0', 64)) } } },
            "baseline" => request with { ExpectedVolume = expected with { PayloadSha256 = Hex(P1) } },
            "id" => request with { ExpectedVolume = expected with { Store = expected.Store with { ContainerId = Hex("11111111111111111111111111111111") } } },
            "protected" => request with { ExpectedVolume = expected with { Name = "VOL2.DSK", PayloadSha256 = Hex(P1) } },
            "ownership" => request with { Ownership = new(true, true, false, true) },
            _ => request
        };
        var replacement = kind is "short" or "long" ? new byte[S + (kind == "short" ? -1 : 1)] : Payload(0x43);
        request = request with { Usage = request.Usage with { SnapshotBytes = (ulong)replacement.Length + 80 } };
        using var attempt = f.Call(replacement, request: request);
        Assert.Equal(OfflineOutcome.Refused, attempt.Receipt.Outcome);
        Assert.NotEqual(OfflineError.None, attempt.Receipt.Error);
        Assert.False(attempt.Receipt.RenameMayHaveStarted); f.Old();
    }

    [Theory]
    [InlineData("io")] [InlineData("snapshot")] [InlineData("transaction")]
    [InlineData("buffer")] [InlineData("overflow")]
    public void FiniteCallerCapsRefuseWithoutMutation(string kind)
    {
        var f = new PrivateCase("budget-" + kind); var request = f.Request;
        if (kind == "io")
        {
            // Initial bounded footer admission is independent of later variable metadata admission.
            var observedRead = false;
            using var metadataRefusal = f.Call(Payload(0x43), io => { if (io.Count > 0) observedRead = true; },
                request with { AttemptId = Guid.NewGuid(), Limits = request.Limits with { MaxMetadataBytes = 1 } });
            Assert.Equal(OfflineOutcome.Refused, metadataRefusal.Receipt.Outcome);
            Assert.Equal(OfflineError.ResourceLimit, metadataRefusal.Receipt.Error);
            Assert.False(observedRead, "admit the initial 64-byte footer before any read/allocation");
            Assert.Equal(0UL, metadataRefusal.Receipt.Charge.ActualIoBytes); f.Old();
        }
        request = kind switch
        {
            "io" => request with { Limits = request.Limits with { MaxIoBytes = 1 } },
            "snapshot" => request with { Limits = request.Limits with { MaxSnapshotBytes = S - 1 } },
            "transaction" => request with { Limits = request.Limits with { MaxTransactionBytes = 2 * F - 1 } },
            "buffer" => request with { Usage = request.Usage with { TransferBufferBytes = 65537 } },
            _ => request with { Usage = request.Usage with { SnapshotBytes = ulong.MaxValue } }
        };
        using var attempt = f.Call(Payload(0x43), request: request);
        Assert.Equal(OfflineOutcome.Refused, attempt.Receipt.Outcome);
        Assert.Equal(OfflineError.ResourceLimit, attempt.Receipt.Error);
        Assert.False(attempt.Receipt.RenameMayHaveStarted); f.Old();
    }

    [Theory]
    [InlineData(OfflineIoPoint.SourceOpen)] [InlineData(OfflineIoPoint.FooterRead)]
    [InlineData(OfflineIoPoint.IndexRead)] [InlineData(OfflineIoPoint.SourceValidationRead)]
    [InlineData(OfflineIoPoint.TempCreate)]
    [InlineData(OfflineIoPoint.TempWrite)] [InlineData(OfflineIoPoint.TempVerifyRead)]
    [InlineData(OfflineIoPoint.SourceRecheckRead)] [InlineData(OfflineIoPoint.BeforeMove)]
    [InlineData(OfflineIoPoint.AfterMove)]
    public void RealIoFaultsPreserveOldOrReconcileExactNew(OfflineIoPoint point)
    {
        var f = new PrivateCase("fault-" + point); var seen = false; var cleanupSeen = false;
        using var attempt = f.Call(Payload(0x43), io =>
        {
            if (point == OfflineIoPoint.BeforeMove && io.Point == OfflineIoPoint.TempDelete)
            {
                cleanupSeen = true; throw new IOException("secondary cleanup failure must not replace publication outcome");
            }
            if (io.Point != point) return;
            seen = true; throw new IOException("independent real-I/O fault " + point);
        });
        Assert.True(seen, "the real boundary must be reached");
        if (point == OfflineIoPoint.AfterMove) f.New(attempt.Receipt);
        else
        {
            Assert.True(attempt.Receipt.Outcome is OfflineOutcome.KnownOld or OfflineOutcome.Refused);
            Assert.False(attempt.Receipt.RenameMayHaveStarted);
            if (point == OfflineIoPoint.BeforeMove)
            {
                Assert.True(cleanupSeen, "exercise actual TempDelete after prepublish fault");
                Assert.Equal(OfflineOutcome.KnownOld, attempt.Receipt.Outcome);
                f.CopiesUnchanged(); Assert.Equal(f.OriginalSha, Hash(f.Target));
                // Retain and charge any failed-cleanup residue; never hide it by calling it a rollback.
                var residue = Directory.GetFiles(f.DirectoryPath).Select(p => new { Path = p, Bytes = new FileInfo(p).Length,
                    Sha256 = Path.GetFileName(p) == "receipts.jsonl" ? null : Hash(p) }).ToArray();
                f.Record("secondary-cleanup-residue-outside-operation", residue);
                Assert.InRange(residue.Where(p => Path.GetFileName(p.Path) != "receipts.jsonl").Sum(p => p.Bytes), 3L * (long)F, 4L * (long)F);
            }
            else f.Old();
        }
    }

    [Fact]
    public void PrivateHostReadOnlyIsRefusedAndOriginalCopyNeverChanged()
    {
        var f = new PrivateCase("host-readonly");
        File.SetAttributes(f.Target, File.GetAttributes(f.Target) | FileAttributes.ReadOnly);
        try
        {
            using var attempt = f.Call(Payload(0x43));
            Assert.Equal(OfflineOutcome.Refused, attempt.Receipt.Outcome);
            Assert.Equal(OfflineError.ReadOnly, attempt.Receipt.Error); f.Old();
        }
        finally { File.SetAttributes(f.Target, File.GetAttributes(f.Target) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public void PostRenameCancellationDrainsToExactNewRatherThanAbortingReadback()
    {
        var f = new PrivateCase("cancel-after-move"); using var cancel = new CancellationTokenSource();
        using var attempt = f.Call(Payload(0x43), io =>
        { if (io.Point == OfflineIoPoint.AfterMove) cancel.Cancel(); }, cancellation: cancel.Token);
        Assert.True(cancel.IsCancellationRequested); f.New(attempt.Receipt);
    }

    [Fact]
    public void ActualSourceChangeBeforeRecheckCannotBeOverwritten()
    {
        var f = new PrivateCase("source-changed"); var changed = false;
        using var attempt = f.Call(Payload(0x43), io =>
        {
            if (io.Point != OfflineIoPoint.SourceRecheckRead || changed) return;
            using var file = new FileStream(f.Target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            file.Position = 32768; file.WriteByte(0x99); changed = true;
        });
        Assert.True(changed); Assert.NotEqual(OfflineOutcome.KnownNew, attempt.Receipt.Outcome);
        Assert.False(attempt.Receipt.RenameMayHaveStarted);
        using var actual = File.OpenRead(f.Target); actual.Position = 32768;
        Assert.Equal(0x99, actual.ReadByte()); f.CopiesUnchanged();
        Assert.NotEqual(NewFile, Hash(f.Target));
    }

    [Fact]
    public void CancellationAtRealPreRenameCheckpointPreservesOld()
    {
        var f = new PrivateCase("cancel-before-write"); using var cancel = new CancellationTokenSource();
        // Cancellation is requested at a real earlier I/O boundary, before pre-rename checkpoint.
        using var attempt = f.Call(Payload(0x43), io => { if (io.Point == OfflineIoPoint.TempWrite) cancel.Cancel(); }, cancellation: cancel.Token);
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(OfflineOutcome.KnownOld, attempt.Receipt.Outcome);
        Assert.False(attempt.Receipt.RenameMayHaveStarted); f.Old();
    }

    [Fact]
    public void UnknownAbandonReplayNonHolderAndDisposeStayPoisonedUntilActualReadbackDrain()
    {
        var f = new PrivateCase("unknown-lifecycle"); var failReadback = true;
        var image = Payload(0x43);
        using var attempt = f.Call(image, io =>
        {
            if (io.Point == OfflineIoPoint.TargetReadback && failReadback) throw new IOException("transient unreadable publication");
        });
        try
        {
            Assert.Equal(OfflineOutcome.Unknown, attempt.Receipt.Outcome);
            attempt.Dispose(); // A5 must retain the owned handle and gate.
            var first = attempt.Abandon(OfflineError.IoFailure); f.Record("abandon-A1", first);
            Assert.True(first.Abandoned); Assert.Equal(f.Request.AttemptId, first.AttemptId);
            Assert.Equal(OfflineError.IoFailure, first.Fault); Assert.NotEqual(default, first.Utc);
            var repeated = attempt.Abandon(OfflineError.Cancelled); f.Record("abandon-A2", repeated);
            Assert.Equal(first, repeated); // original fault/UTC, no second registry entry
            // Give the refused invocation distinct caller backing, stable for its synchronous call.
            var callerId = f.Request.ExpectedVolume.Store.ContainerId.ToArray();
            var callerMetadata = f.Request.ExpectedVolume.Store.MetadataSha256.ToArray();
            var callerBaseline = f.Request.ExpectedVolume.PayloadSha256.ToArray();
            var refusedIdentity = new HddVolumeIdentity(new(
                System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(callerId), 1,
                System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(callerMetadata)), "VOL1.DSK",
                System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(callerBaseline));
            var refusedRequest = f.Request with { AttemptId = Guid.NewGuid(), ExpectedVolume = refusedIdentity,
                Usage = f.Request.Usage with { SnapshotBytes = S + 160UL } };
            using var refused = f.Call(image, request: refusedRequest);
            Assert.Equal(OfflineOutcome.Refused, refused.Receipt.Outcome);
            Assert.Equal(OfflineError.QuarantinePoisoned, refused.Receipt.Error);
            Assert.Equal(f.Request.AttemptId, refused.Receipt.BlockingAttemptId);
            var holderBefore = JsonSerializer.Serialize(attempt.Receipt);
            var marker = refused.Abandon(OfflineError.Cancelled); f.Record("abandon-A4", marker);
            Assert.False(marker.Abandoned); Assert.Equal(OfflineError.InvalidRequest, marker.Fault);
            Assert.Equal(refusedRequest.AttemptId, marker.AttemptId);
            Assert.Equal(holderBefore, JsonSerializer.Serialize(attempt.Receipt));
            Assert.False(refused.Receipt.RenameMayHaveStarted);
            var refusedBefore = JsonSerializer.Serialize(refused.Receipt);
            callerId[0] ^= 0xff; callerMetadata[0] ^= 0xff; callerBaseline[0] ^= 0xff;
            Assert.Equal(refusedBefore, JsonSerializer.Serialize(refused.Receipt));
            Assert.Equal(Hex("0102030405060708090a0b0c0d0e0f10").ToArray(), refused.Receipt.ExpectedVolume.Store.ContainerId.ToArray());
            Assert.Equal(Hex(OldMetadata).ToArray(), refused.Receipt.ExpectedVolume.Store.MetadataSha256.ToArray());
            Assert.Equal(Hex(P0).ToArray(), refused.Receipt.ExpectedVolume.PayloadSha256.ToArray());
            Assert.Equal(holderBefore, JsonSerializer.Serialize(attempt.Receipt));
        }
        finally
        {
            failReadback = false;
            f.Reconcile(attempt); // never strand process poison, even on an assertion failure
        }
        f.New(attempt.Receipt);
        Assert.Equal(attempt.Receipt.Charge, f.Reconcile(attempt).Charge);
        // Old request is stale now, but absence of QuarantinePoisoned demonstrates admission restored.
        using var next = f.Call(image, request: f.Request with { AttemptId = Guid.NewGuid() });
        Assert.Equal(OfflineOutcome.Refused, next.Receipt.Outcome);
        Assert.Equal(OfflineError.StaleIdentity, next.Receipt.Error); f.CopiesUnchanged();
    }

    [Fact]
    public void CompletedAbandonRefusesWithoutRerunMutationOrCounterReset()
    {
        var f = new PrivateCase("completed-A3"); using var attempt = f.Call(Payload(0x43));
        f.New(attempt.Receipt);
        var before = JsonSerializer.Serialize(attempt.Receipt);
        var marker = attempt.Abandon(OfflineError.Cancelled); f.Record("abandon-A3", marker);
        Assert.False(marker.Abandoned); Assert.Equal(OfflineError.InvalidRequest, marker.Fault);
        Assert.Equal(f.Request.AttemptId, marker.AttemptId); Assert.NotEqual(default, marker.Utc);
        Assert.Equal(before, JsonSerializer.Serialize(attempt.Receipt));
        Assert.Equal(before, JsonSerializer.Serialize(f.Reconcile(attempt))); f.New(attempt.Receipt);
    }

    [Fact]
    public void SameAndCrossThreadReentryAreRefusedWithTheRealHeldAttemptId()
    {
        var f = new PrivateCase("in-flight"); var image = Payload(0x43); var observed = false;
        using var attempt = f.Call(image, io =>
        {
            if (io.Point != OfflineIoPoint.BeforeMove || observed) return;
            observed = true;
            void Refuse()
            {
                using var nested = f.Call(image, request: f.Request with { AttemptId = Guid.NewGuid() });
                Assert.Equal(OfflineOutcome.Refused, nested.Receipt.Outcome);
                Assert.Equal(OfflineError.AlreadyInFlight, nested.Receipt.Error);
                Assert.Equal(f.Request.AttemptId, nested.Receipt.BlockingAttemptId);
                Assert.False(nested.Receipt.RenameMayHaveStarted);
            }
            Refuse();
            Exception? failure = null;
            var thread = new Thread(() => { try { Refuse(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
            thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "cross-thread admission must not wait/steal");
            Assert.Null(failure);
        });
        Assert.True(observed); f.New(attempt.Receipt);
    }

    [Fact]
    public void CorruptOriginalPayloadCannotPassByTrustingMetadataAlone()
    {
        var f = new PrivateCase("corruption-sensitivity");
        using (var file = new FileStream(f.Target, FileMode.Open, FileAccess.Write))
        { file.Position = S + 32768; file.WriteByte(0x99); }
        var corrupted = Hash(f.Target);
        using var attempt = f.Call(Payload(0x43));
        Assert.NotEqual(OfflineOutcome.KnownNew, attempt.Receipt.Outcome);
        Assert.False(attempt.Receipt.RenameMayHaveStarted);
        Assert.Equal(corrupted, Hash(f.Target)); f.CopiesUnchanged();
    }
    // MaxQuarantined=4 / ReconcileCapExhausted: invariant + static production-guard inspection ONLY.
    // Free->Held->Poisoned and drain-only resolution imply count<=1 on both supported seams.
    // No test claims execution of the unreachable four-retained-attempt branch or seeds fake state.
}
