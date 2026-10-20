using System.Collections.Immutable;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class DiskLibraryWorkingCopyPublicationTests
{
    [Theory]
    [InlineData("short")]
    [InlineData("disk-full")]
    [InlineData("corrupt-reread")]
    [InlineData("reread-failure")]
    public async Task FailedStageNeverPublishesAndOwnedFileIsRemoved(string fault)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var pub = new DiskLibraryWorkingCopyPublication();
        if (fault == "short") pub.WriteStage = async (stream, bytes, token) => await stream.WriteAsync(bytes.AsMemory()[..17], token);
        if (fault == "disk-full") pub.WriteStage = async (stream, bytes, token) =>
        { await stream.WriteAsync(bytes.AsMemory()[..17], token); throw new IOException("controlled ENOSPC"); };
        if (fault == "corrupt-reread") pub.ReadBack = (_, _) => Task.FromResult(ImmutableArray.Create<byte>(7));
        if (fault == "reread-failure") pub.ReadBack = (_, _) => throw new IOException("controlled read failure");
        var published = false;
        pub.Publish = (_, _) => published = true;
        Assert.Equal(DiskLibraryCopyStatus.Refused, (await f.Copy(pub)).Status);
        Assert.False(published);
        f.NoCandidates(); f.Preserved();
    }

    [Fact]
    public async Task CancellationBeforeAdmissionDoesNotCreateCopiesDirectory()
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Equal(DiskLibraryCopyStatus.Cancelled, (await f.Copy(cancellation: stop.Token)).Status);
        Assert.False(Directory.Exists(f.CopiesPath)); f.Preserved();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledHeldWriterMustSettleBeforeCandidateCleanup(bool afterWrite)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pub = new DiskLibraryWorkingCopyPublication(); var write = pub.WriteStage;
        pub.WriteStage = async (stream, bytes, token) =>
        {
            if (afterWrite) await write(stream, bytes, CancellationToken.None);
            entered.SetResult(); await release.Task;
            if (!afterWrite) await write(stream, bytes, CancellationToken.None);
        };
        var attempt = f.Copy(pub, cancellation: stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        try
        {
            Assert.False(attempt.IsCompleted);
            Assert.Single(Directory.GetFiles(f.CopiesPath, "*.part"));
            Assert.Empty(f.Catalog.Copies); f.Preserved();
        }
        finally { release.TrySetResult(); }
        Assert.Equal(DiskLibraryCopyStatus.Cancelled, (await attempt.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        f.NoCandidates(); f.Preserved();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyRenameExceptionIsUnknownUntilReconciledAndNeverPromoted(bool move)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var unrelated = Path.Combine(f.Root, "unrelated.dsk.part"); File.WriteAllBytes(unrelated, [19]);
        var pub = new DiskLibraryWorkingCopyPublication
        {
            Publish = (stage, final) =>
            { if (move) File.Move(stage, final); throw new IOException("controlled copy rename acknowledgement"); }
        };
        var result = await f.Copy(pub);
        Assert.Equal(DiskLibraryCopyStatus.Unknown, result.Status);
        Assert.Null(result.Copy); Assert.NotNull(result.OrphanPath);
        Assert.Empty(f.Catalog.Copies); f.Preserved();
        Assert.Equal(DiskLibraryCopyStatus.Unknown, (await f.Copy()).Status);
        var competing = NativeImageAdmission.TryAcquireColdRead(CancellationToken.None);
        using (competing.Admission) Assert.Null(competing.Admission);
        Assert.Equal(DiskLibraryCopyStatus.Refused, (await f.Catalog.ReconcileWorkingCopyAsync()).Status);
        f.NoCandidates(); f.Preserved();
        Assert.Equal(DiskLibraryCopyStatus.Refused, (await f.Catalog.ReconcileWorkingCopyAsync()).Status);
        Assert.Equal(new byte[] { 19 }, File.ReadAllBytes(unrelated));
        Assert.Equal(DiskLibraryCopyStatus.Created, (await f.Copy()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogRenameExceptionRetainsPriorViewUntilStrictReconciliation(bool move)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var pub = new DiskLibraryPublication
        {
            Publish = (stage, target) =>
            { if (move) File.Move(stage, target, true); throw new IOException("controlled catalog rename acknowledgement"); }
        };
        var result = await f.Copy(catalogPublication: pub);
        // A before-move exception can be settled immediately from fresh prior bytes
        // plus the still-owned catalog stage; after-move cannot use that proof.
        Assert.Equal(move ? DiskLibraryCopyStatus.Unknown : DiskLibraryCopyStatus.Refused, result.Status);
        Assert.Empty(f.Catalog.Copies); Assert.Equal(0, pub.InFlight);
        await pub.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        f.Preserved(!move);
        var reconciled = await f.Catalog.ReconcileWorkingCopyAsync();
        Assert.Equal(move ? DiskLibraryCopyStatus.Created : DiskLibraryCopyStatus.Refused, reconciled.Status);
        if (move)
        {
            Assert.Single(f.Catalog.Copies);
            Assert.Single(DiskLibrarySchema.Load(File.ReadAllBytes(f.CatalogPath)).Copies);
            Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Root, f.Catalog.Copies[0].RelativePath)));
        }
        else { f.NoCandidates(); f.Preserved(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringHeldCatalogWriteDrainsBeforeCleanup(bool cancel)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pub = new DiskLibraryPublication(); var write = pub.WriteStage;
        pub.WriteStage = async (path, bytes) =>
        {
            await write(path, bytes); entered.SetResult(); await release.Task;
        };
        var attempt = f.Copy(catalogPublication: pub, cancellation: stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drain = pub.DrainAsync();
        if (cancel) stop.Cancel();
        try
        {
            Assert.Equal(1, pub.InFlight); Assert.False(drain.IsCompleted);
            Assert.False(attempt.IsCompleted); Assert.Empty(f.Catalog.Copies);
            Assert.Single(Directory.GetFiles(f.CopiesPath, "*.dsk")); f.Preserved();
        }
        finally { release.TrySetResult(); }
        var result = await attempt.WaitAsync(TimeSpan.FromSeconds(5));
        await drain.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, pub.InFlight);
        Assert.Equal(cancel ? DiskLibraryCopyStatus.Cancelled : DiskLibraryCopyStatus.Created, result.Status);
        if (cancel) { f.NoCandidates(); f.Preserved(); }
        else { Assert.Single(f.Catalog.Copies); f.Preserved(false); }
    }

    [Theory]
    [InlineData("collision")]
    [InlineData("accounted")]
    [InlineData("writer")]
    public async Task FreshFactsBeforeRenameRefuseChangedTargetOrOwnership(string fault)
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var pub = new DiskLibraryWorkingCopyPublication(); var read = pub.ReadBack;
        string? collision = null;
        pub.ReadBack = async (path, token) =>
        {
            var bytes = await read(path, token);
            if (fault == "collision")
            {
                collision = path[..^5]; File.WriteAllBytes(collision, [37]);
            }
            if (fault == "writer") f.Catalog.SetAdmission(new([], true));
            if (fault == "accounted") f.Catalog.SetAdmission(new([new(path, LinuxFileFacts.Inspect(path).FileId)], false));
            return bytes;
        };
        var result = await f.Copy(pub);
        Assert.Equal(DiskLibraryCopyStatus.Refused, result.Status);
        Assert.Empty(f.Catalog.Copies); f.Preserved();
        if (collision is not null) Assert.Equal(new byte[] { 37 }, File.ReadAllBytes(collision));
        else
        {
            // An accounted/possibly-written stage is deliberately not deleted.
            Assert.NotNull(result.OrphanPath); Assert.True(File.Exists(result.OrphanPath));
        }
    }

    [Fact]
    public async Task FailedCatalogWriteRemovesImageAndPreservesPriorCatalog()
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var pub = new DiskLibraryPublication { WriteStage = (_, _) => throw new IOException("controlled catalog disk full") };
        Assert.Equal(DiskLibraryCopyStatus.Refused, (await f.Copy(catalogPublication: pub)).Status);
        f.NoCandidates(); f.Preserved(); Assert.Equal(0, pub.InFlight);
    }

    [Fact]
    public async Task CleanupFailureReportsExactOrphanAndDoesNotDeleteUnrelatedFiles()
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        var pub = new DiskLibraryWorkingCopyPublication
        {
            ReadBack = (_, _) => Task.FromResult(ImmutableArray.Create<byte>(0)),
            Delete = _ => throw new IOException("controlled cleanup denial")
        };
        var result = await f.Copy(pub);
        Assert.Equal(DiskLibraryCopyStatus.Refused, result.Status);
        var orphan = Assert.IsType<string>(result.OrphanPath);
        Assert.True(File.Exists(orphan));
        Assert.Equal(f.Bytes, File.ReadAllBytes(orphan));
        Assert.Empty(f.Catalog.Copies); f.Preserved();
    }

    [Fact]
    public async Task ReplacementIdentityAtCleanupIsNotDeleted()
    {
        using var f = new DiskLibraryWorkingCopyFixtures();
        await f.RegisterAsync();
        string? replaced = null;
        var pub = new DiskLibraryWorkingCopyPublication
        {
            ReadBack = (path, _) =>
            {
                // Keep the owned inode alive under another name to prevent inode reuse.
                File.Move(path, path + ".old"); File.WriteAllBytes(path, [21]); replaced = path;
                return Task.FromResult(ImmutableArray.Create<byte>(0));
            }
        };
        var result = await f.Copy(pub);
        Assert.Equal(DiskLibraryCopyStatus.Refused, result.Status);
        Assert.Equal(replaced, result.OrphanPath);
        Assert.Equal(new byte[] { 21 }, File.ReadAllBytes(replaced!));
        Assert.Equal(f.Bytes, File.ReadAllBytes(replaced + ".old"));
        Assert.Empty(f.Catalog.Copies); f.Preserved();
    }
}
