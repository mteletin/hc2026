using System.Diagnostics;
using System.Security.Cryptography;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Xunit;

namespace Hc2000.App.Tests;

// Self-contained pin tests for DiskLibraryCopySnapshot.PrepareAsync (producer pin da98ab6,
// contract doc producer-api-refusal-fixture-contract). Private fixtures only: every test builds
// its own 0700 library root under a private test admission domain, registers one hand-laid DSK
// literal (sha256 constants computed independently with python hashlib, cross-checked against the
// working-copy fixture constants for the two base literals), creates one real working copy through
// the production path, and then exercises the exact producer result surface. No shared fixtures,
// no production seam added or re-implemented.
//
// Clause coverage:
// - Prepared: standard and extended DSK; current bytes/hash/format; fresh identity (canonical
//   path, Linux file id, native length); original/initial provenance retained in the result.
// - Modified working copy: CurrentSha256 differs from the retained InitialSha256/OriginalSha256.
// - Immutability: a second prepare of the same copy returns the same bytes, hash, identity and
//   copy id.
// - Identity bound per read: replacing the copy between reads returns a fresh file identity; a
//   stale identity is never served. The identity change strictly during the read window is a
//   host race with no production seam; its observable classes are proven here by the busy-fence
//   refusal (no image read while other live accounting holds the gate) and by the freshness
//   assertion above.
// - Aliases: a hardlink to the protected original and a hardlink to another managed copy refuse
//   RepeatOrAlias by fresh file identity. The catalog schema fixes copy paths to copies/<id>.dsk,
//   so the copy-path-equals-original-path string clause is defensive and not separately exercised.
// - Links: a linked library root, a linked copies directory and a linked copy file each refuse
//   FinalLink with the exact gap.
// - Refusals: missing copy file (Unreadable), FIFO copy (NotRegularFile), truncated header and
//   truncated track and non-DSK bytes (UnsupportedContainer through the real DSK reader; the raw
//   image clause surfaces as its "raw images require geometry" gap without a geometry input),
//   oversize pre-read length and empty read (Budget).
// - Ownership gaps: missing writer admission, the injected legacy platform (unit-only host fact,
//   the sanctioned internal test surface), and a busy cold-read fence held by this test's own real
//   ClaimStart before any file I/O (fail-closed, ownership unknown).
// - Writer accounting: an active writer accounted by copy path and by copy file identity refuses.
// - Unknown copy id and a pre-cancelled token return their closed statuses.
// - The protected original is never modified by any of these operations.
#pragma warning disable CA1416
public sealed class DiskLibraryCopySnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_BindsCurrentBytesIdentityAndProvenance(bool extended)
    {
        await using var f = await CopySnapshotFixture.CreateAsync(extended);
        await f.CreateCopyAsync();

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Prepared, result.Status);
        Assert.Null(result.Refusal);
        Assert.Equal($"copy {f.Copy.Id} prepared under the live host cold-read fence; " +
            $"original {f.Entry.Id} sha {f.Entry.Sha256} and initial sha {f.Copy.InitialSha256} " +
            $"retained as provenance; current sha {f.Hash}, {(extended ? "ExtendedDsk" : "StandardDsk")}",
            result.Message);
        var snapshot = Assert.IsType<DiskLibraryCopyContentSnapshot>(result.Snapshot);
        Assert.Equal(f.Bytes, snapshot.Bytes.AsSpan().ToArray());
        Assert.Equal(f.Hash, snapshot.CurrentSha256);
        Assert.Equal(extended ? DiskLibraryFormat.ExtendedDsk : DiskLibraryFormat.StandardDsk, snapshot.Format);
        Assert.Equal(f.Entry.Id, snapshot.Original.Id);
        Assert.Equal(f.Copy.Id, snapshot.Copy.Id);
        var identity = Assert.IsType<DiskLibraryCopyFileIdentity>(result.Identity);
        Assert.Equal(Path.GetFullPath(f.CopyPath), Path.GetFullPath(identity.CanonicalPath));
        Assert.Equal(LinuxFileFacts.Inspect(f.CopyPath).FileId, identity.FileId);
        Assert.Equal(f.Bytes.Length, identity.Length);
        f.PreserveOriginal();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModifiedCopy_ReportsCurrentHashAndRetainsInitialProvenance(bool extended)
    {
        await using var f = await CopySnapshotFixture.CreateAsync(extended);
        await f.CreateCopyAsync();
        f.WriteCopy(f.Variant);

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Prepared, result.Status);
        var snapshot = Assert.IsType<DiskLibraryCopyContentSnapshot>(result.Snapshot);
        Assert.Equal(f.Variant, snapshot.Bytes.AsSpan().ToArray());
        Assert.Equal(f.VariantHash, snapshot.CurrentSha256);
        Assert.NotEqual(f.Entry.Sha256, snapshot.CurrentSha256);
        // Provenance is untouched: the original and initial hashes stay the entry's and the
        // copy's original hashes, and the message carries them as retained provenance.
        Assert.Equal(f.Hash, f.Entry.Sha256);
        Assert.Equal(f.Hash, f.Copy.InitialSha256);
        Assert.Equal(f.Hash, f.Copy.OriginalSha256);
        Assert.Contains($"original {f.Entry.Id} sha {f.Hash}", result.Message);
        Assert.Contains($"initial sha {f.Hash} retained as provenance", result.Message);
        Assert.Contains($"current sha {f.VariantHash}", result.Message);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task SameCopy_PreparedTwice_ReturnsImmutableContentAndIdentity()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();

        var first = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);
        var second = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Prepared, first.Status);
        Assert.Equal(DiskLibraryCopySnapshotStatus.Prepared, second.Status);
        Assert.Equal(first.Snapshot!.Bytes, second.Snapshot!.Bytes);
        Assert.Equal(first.Snapshot.CurrentSha256, second.Snapshot.CurrentSha256);
        Assert.Equal(first.Identity, second.Identity);
        Assert.Equal(first.Snapshot.Copy.Id, second.Snapshot.Copy.Id);
        Assert.Equal(f.Hash, second.Snapshot.CurrentSha256);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task ReplacedCopy_BindsFreshIdentityPerRead()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();

        var before = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);
        // Delete and recreate: a new file identity under the same copy path.
        f.ReplaceCopy(f.Variant);
        var after = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Prepared, before.Status);
        Assert.Equal(DiskLibraryCopySnapshotStatus.Prepared, after.Status);
        Assert.NotNull(before.Identity);
        Assert.NotNull(after.Identity);
        Assert.NotEqual(before.Identity!.FileId, after.Identity!.FileId);
        Assert.Equal(f.VariantHash, after.Snapshot!.CurrentSha256);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task HardlinkToOriginal_RefusesRepeatOrAlias()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        f.HardlinkCopyTo(f.Source);

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.RepeatOrAlias, result.Refusal);
        Assert.Contains("copy path aliases the protected original (same file identity)", result.Message);
        Assert.Null(result.Snapshot);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task HardlinkToOtherManagedCopy_RefusesRepeatOrAlias()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        var first = f.Copy;
        var second = (await f.CreateSecondCopyAsync()).Copy!;
        f.HardlinkCopyPath(second, first);

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, second.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.RepeatOrAlias, result.Refusal);
        Assert.Contains($"copy path aliases managed copy {first.Id}", result.Message);
        Assert.Null(result.Snapshot);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task LinkedLibraryRoot_RefusesFinalLink()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.LinkedRootPath());

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.FinalLink, result.Refusal);
        Assert.Contains("library root is a link", result.Message);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task LinkedCopiesDirectory_RefusesFinalLink()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        f.LinkCopiesDirectory();

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.FinalLink, result.Refusal);
        Assert.Contains("a directory under the library root is missing or a link", result.Message);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task LinkedCopyFile_RefusesFinalLink()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        f.SymlinkCopyTo(Path.Combine(f.Root, "decoy.dsk"));

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.FinalLink, result.Refusal);
        Assert.Contains("copy path is a link", result.Message);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task MissingCopyFile_RefusesUnreadable()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        File.Delete(f.CopyPath);

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.Unreadable, result.Refusal);
        Assert.Contains("copy file is missing or unstatable", result.Message);
        Assert.Null(result.Snapshot);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task NonRegularCopyFile_RefusesNotRegularFile()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        File.Delete(f.CopyPath);
        CopySnapshotFixture.UnixProcess("mkfifo", f.CopyPath);
        try
        {
            var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

            Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
            Assert.Equal(DiskLibraryRefusal.NotRegularFile, result.Refusal);
            Assert.Contains("copy path is not a regular file", result.Message);
            Assert.Null(result.Snapshot);
        }
        finally
        {
            File.Delete(f.CopyPath);
        }
        f.PreserveOriginal();
    }

    [Fact]
    public async Task TruncatedContainer_RefusesUnsupportedContainer()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        f.WriteCopy(CopySnapshotFixture.TruncatedHeader);

        var header = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);
        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, header.Status);
        Assert.Equal(DiskLibraryRefusal.UnsupportedContainer, header.Refusal);
        Assert.Contains("Truncated disk header.", header.Message);
        Assert.Null(header.Snapshot);

        f.WriteCopy(CopySnapshotFixture.TruncatedTrack);
        var track = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);
        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, track.Status);
        Assert.Equal(DiskLibraryRefusal.UnsupportedContainer, track.Refusal);
        Assert.Contains("Truncated DSK track.", track.Message);
        Assert.Null(track.Snapshot);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task UnknownFormat_RefusesUnsupportedContainer()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        f.WriteCopy(CopySnapshotFixture.UnknownRaw);

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.UnsupportedContainer, result.Refusal);
        Assert.Contains("Unknown disk format; raw images require geometry.", result.Message);
        Assert.Null(result.Snapshot);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task OversizedCopy_RefusesBudgetBeforeAnyRead()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        await using (var oversized = new FileStream(f.CopyPath, FileMode.Truncate, FileAccess.Write))
        {
            oversized.SetLength(DiskLibrarySchema.MaxContainerBytes + 1);
        }

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.Budget, result.Refusal);
        Assert.Contains("copy file exceeds the container budget", result.Message);
        Assert.Null(result.Snapshot);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task EmptyCopy_RefusesBudget()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        await using (var empty = new FileStream(f.CopyPath, FileMode.Truncate, FileAccess.Write))
        {
            empty.SetLength(0);
        }

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.Budget, result.Refusal);
        Assert.Contains("current bytes are empty or exceed the container budget", result.Message);
        Assert.Null(result.Snapshot);
        f.PreserveOriginal();
    }

    [Fact]
    public async Task MissingWriterAdmission_ReturnsOwnershipUnknown()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        var unaccounted = DiskLibraryCatalog.Load(f.CatalogPath);

        var result = await DiskLibraryCopySnapshot.PrepareAsync(unaccounted, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.OwnershipUnknown, result.Status);
        Assert.Equal(DiskLibraryRefusal.OwnershipUnknown, result.Refusal);
        Assert.Contains("the catalog host admission (writer accounting) is not injected", result.Message);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task LegacyPlatform_ReturnsOwnershipUnknown()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        // Unit-only host fact through the sanctioned internal test surface: the same seam the
        // guarded tests enter, pointed at the legacy platform mode.
        using var legacy = NativeImageAdmission.EnterPrivateTestDomain(f.Root, ImageAdmissionPlatformMode.LegacyOnly);

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.OwnershipUnknown, result.Status);
        Assert.Equal(DiskLibraryRefusal.OwnershipUnknown, result.Refusal);
        Assert.Contains("no guarded native image admission (legacy platform)", result.Message);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task BusyColdReadFence_ReturnsOwnershipUnknownWithoutReading()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        var original = File.ReadAllBytes(f.CopyPath);
        var held = DiskReadReservation.Operation.ClaimStart(new[] { DiskSlot.A });
        try
        {
            var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root);

            Assert.Equal(DiskLibraryCopySnapshotStatus.OwnershipUnknown, result.Status);
            Assert.Equal(DiskLibraryRefusal.OwnershipUnknown, result.Refusal);
            Assert.Contains("the host cold-read fence is held or refused (Busy)", result.Message);
            Assert.Contains("other live accounting is present, no copy image was read", result.Message);
            Assert.Null(result.Snapshot);
            Assert.Equal(original, File.ReadAllBytes(f.CopyPath));
        }
        finally
        {
            await held.ReleaseOnceAsync("test busy cold-read fence");
        }
        f.PreserveOriginal();
    }

    [Fact]
    public async Task ActiveWriterAccountedByCopyPath_Refuses()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        var mounted = DiskLibraryCatalog.Load(f.CatalogPath,
            new DiskLibraryHostAdmission([new AccountedHostFile(f.CopyPath, null)], true));

        var result = await DiskLibraryCopySnapshot.PrepareAsync(mounted, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.ActiveWriter, result.Refusal);
        Assert.Contains("copy file is mounted or writeback-owned by the host accounting", result.Message);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task ActiveWriterAccountedByCopyFileId_Refuses()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        var fileId = LinuxFileFacts.Inspect(f.CopyPath).FileId;
        Assert.NotNull(fileId);
        var mounted = DiskLibraryCatalog.Load(f.CatalogPath,
            new DiskLibraryHostAdmission([new AccountedHostFile("/nonexistent/other.dsk", fileId)], true));

        var result = await DiskLibraryCopySnapshot.PrepareAsync(mounted, f.Copy.Id, f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Refused, result.Status);
        Assert.Equal(DiskLibraryRefusal.ActiveWriter, result.Refusal);
        Assert.Contains("copy file is mounted or writeback-owned by the host accounting", result.Message);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task UnknownCopyId_ReturnsUnknownCopy()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog,
            "11111111-2222-3333-4444-555555555555", f.Root);

        Assert.Equal(DiskLibraryCopySnapshotStatus.UnknownCopy, result.Status);
        Assert.Contains("copy id is not registered", result.Message);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task PreCancelledToken_ReturnsCancelled()
    {
        await using var f = await CopySnapshotFixture.CreateAsync();
        await f.CreateCopyAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await DiskLibraryCopySnapshot.PrepareAsync(f.Catalog, f.Copy.Id, f.Root, cancellation.Token);

        Assert.Equal(DiskLibraryCopySnapshotStatus.Cancelled, result.Status);
        Assert.Contains("cancelled before host admission", result.Message);
        Assert.Null(result.Snapshot);
    }

    // Self-contained private fixture: one 0700 root, one hand-laid DSK literal, one real working
    // copy. Disposing removes the whole root. The admission domain is per-fixture, so the cold-read
    // fence is isolated per test and the busy-fence case is deterministic.
    private sealed class CopySnapshotFixture : IAsyncDisposable
    {
        private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        public const string TruncatedHeaderHash = "ad50433b25d29633e48c5e9c4fba9de991cba58db1e46c1825e0f85d033bc0bd";

        public static byte[] TruncatedHeader { get; } = Truncated(standard, 200);
        public static byte[] TruncatedTrack { get; } = TruncatedTrackLiteral();
        public static byte[] UnknownRaw { get; } = Truncated(unknownRaw, 768);

        public string Root { get; }
        public string Source => Path.Combine(Root, "original.dsk");
        public string CatalogPath => Path.Combine(Root, "catalog.json");
        public string CopiesPath => Path.Combine(Root, "copies");
        public byte[] Bytes { get; }
        public byte[] Variant { get; }
        public string Hash { get; }
        public string VariantHash { get; }
        public DiskLibraryCatalog Catalog { get; private set; } = null!;
        public DiskLibraryEntry Entry { get; private set; } = null!;
        public DiskLibraryCopy Copy { get; private set; } = null!;
        public string CopyPath => Path.Combine(Root, Copy.RelativePath);
        private readonly IDisposable domain;

        private CopySnapshotFixture(bool extended)
        {
            Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux private-directory/native identity contract");
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "hc-copy-snap-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root, Private);
            domain = NativeImageAdmission.EnterPrivateTestDomain(Root);
            Bytes = Literal(extended, i => (byte)i);
            Variant = Literal(extended, i => (byte)(i + 1));
            Hash = Convert.ToHexStringLower(SHA256.HashData(Bytes));
            VariantHash = Convert.ToHexStringLower(SHA256.HashData(Variant));
            // Independent oracle cross-check: the base literals must hash exactly to the working-copy
            // fixture's separately computed constants.
            if (extended)
                Assert.Equal("2fe90a646b74239ec52cad1e558dd932d82e21199b3d30b1e7a6c8338444d929", Hash);
            else
                Assert.Equal("09dc077064577d381d53f5240fe96a924bda9b47a6a1bf1b48ea8ef624fab23b", Hash);
            File.WriteAllBytes(Source, Bytes);
            File.SetUnixFileMode(Source, UnixFileMode.UserRead);
        }

        public static Task<CopySnapshotFixture> CreateAsync(bool extended = false)
            => Task.FromResult(new CopySnapshotFixture(extended));

        public async Task<DiskLibraryCopyResult> CreateCopyAsync()
        {
            Catalog = DiskLibraryCatalog.Load(CatalogPath, DiskLibraryHostAdmission.StoppedHost);
            var registered = await Catalog.RegisterAsync(Source, "unit-only copy-snapshot fixture", "self-contained");
            Assert.Equal(DiskLibraryRefusal.Accepted, registered.Reason);
            Entry = Assert.IsType<DiskLibraryEntry>(registered.Entry);
            var saved = await Catalog.SaveAsync();
            Assert.Equal(DiskLibrarySaveStatus.Published, saved.Status);
            var created = await Catalog.CreateWorkingCopyAsync(Entry.Id, Root);
            Assert.Equal(DiskLibraryCopyStatus.Created, created.Status);
            Copy = Assert.IsType<DiskLibraryCopy>(created.Copy);
            Assert.Equal("copies/" + Copy.Id + ".dsk", Copy.RelativePath);
            Assert.Equal(Hash, Copy.InitialSha256);
            Assert.Equal(Bytes, File.ReadAllBytes(CopyPath));
            return created;
        }

        public Task<DiskLibraryCopyResult> CreateSecondCopyAsync()
            => Catalog.CreateWorkingCopyAsync(Entry.Id, Root);

        public void WriteCopy(byte[] bytes) => File.WriteAllBytes(CopyPath, bytes);

        // Delete then write: a fresh file identity under the same copy path.
        public void ReplaceCopy(byte[] bytes)
        {
            File.Delete(CopyPath);
            File.WriteAllBytes(CopyPath, bytes);
        }

        public void HardlinkCopyTo(string targetPath)
        {
            File.Delete(CopyPath);
            UnixProcess("ln", targetPath, CopyPath);
        }

        public void HardlinkCopyPath(DiskLibraryCopy copy, DiskLibraryCopy other)
        {
            var copyPath = Path.Combine(Root, copy.RelativePath);
            File.Delete(copyPath);
            UnixProcess("ln", Path.Combine(Root, other.RelativePath), copyPath);
        }

        public void SymlinkCopyTo(string targetPath)
        {
            File.Delete(CopyPath);
            File.WriteAllBytes(targetPath, Bytes);
            UnixProcess("ln", "-s", targetPath, CopyPath);
        }

        public string LinkedRootPath()
        {
            // The native domain root stays the real directory (the fence walk opens every component
            // with O_NOFOLLOW), so the link is a sibling path naming the same root.
            var link = Root + "-link";
            UnixProcess("ln", "-s", Root, link);
            return link;
        }

        public void LinkCopiesDirectory()
        {
            var real = CopiesPath + "-real";
            Directory.Move(CopiesPath, real);
            UnixProcess("ln", "-s", real, CopiesPath);
        }

        public void PreserveOriginal()
        {
            Assert.Equal(Bytes, File.ReadAllBytes(Source));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(Source));
            Assert.Equal(Entry.FileId, LinuxFileFacts.Inspect(Source).FileId);
            Assert.Equal(Hash, Entry.Sha256);
        }

        public async ValueTask DisposeAsync()
        {
            domain.Dispose();
            await Task.Run(() =>
            {
                Directory.Delete(Root, true);
                try { File.Delete(Root + "-link"); } catch (Exception) { /* no sibling link was created */ }
            });
        }

        private static byte[] Literal(bool extended, Func<int, byte> fill)
        {
            var bytes = new byte[768];
            if (extended) "EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8.CopyTo(bytes);
            else "MV - CPCEMU Disk-File\r\nDisk-Info\r\n"u8.CopyTo(bytes);
            bytes[0x30] = bytes[0x31] = 1;
            bytes[extended ? 0x34 : 0x33] = 2;
            "Track-Info\r\n"u8.CopyTo(bytes.AsSpan(0x100));
            bytes[0x115] = 1; bytes[0x116] = 0x4e; bytes[0x117] = 0xe5; bytes[0x11a] = 1;
            if (extended) bytes[0x11e] = 128;
            for (var i = 0; i < 128; i++) bytes[0x200 + i] = fill(i);
            return bytes;
        }

        private static byte[] standard => Literal(extended: false, i => (byte)i);
        private static byte[] unknownRaw => new byte[768];
        static CopySnapshotFixture()
        {
            Array.Fill(unknownRaw, (byte)0xAB);
        }

        private static byte[] Truncated(byte[] source, int length) => source.AsSpan(0, length).ToArray();

        private static byte[] TruncatedTrackLiteral()
        {
            var bytes = Literal(extended: false, i => (byte)i);
            bytes[0x32] = 0x03; bytes[0x33] = 0x00; // track length 768: the track points past the end
            return bytes;
        }

        public static void UnixProcess(string name, params string[] args)
        {
            var start = new ProcessStartInfo(name, args)
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException(name + " did not start");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, name + " " + string.Join(' ', args) + " failed: " + error);
        }
    }
}
