using System.Collections.Immutable;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Injected owner-contract tests, NOT native FIFO/guest-write integration evidence.
/// The sandbox writer calls the existing MediaFiles helper; fixtures use the existing real Dsk factory.</summary>
public sealed class DiskWritebackCoordinatorTests : IDisposable
{
    private readonly HddColdAdmissionTests.PrivateScopedDomain nativeDomain = new();
    public void Dispose() => nativeDomain.Dispose();
    [Theory]
    [InlineData(WritebackIntent.Shortcut)]
    [InlineData(WritebackIntent.Eject)]
    [InlineData(WritebackIntent.Close)]
    public async Task BPublicationReopensRealDskAndKeepsDirtyUntilGuardedCompletion(WritebackIntent intent)
    {
        using var rig = new Rig();
        Assert.NotNull((await rig.Bind("B")).Binding);
        var result = await rig.Coordinator.PersistAsync("B", intent);
        Assert.Equal(WritebackPublication.Published, result.Outcome);
        Assert.Equal(rig.Owner.Live["B"].ToArray(), await File.ReadAllBytesAsync(rig.Paths["B"]));
        Assert.Equal(1, rig.Owner.Dirty["B"]);
        Assert.Equal(1, rig.Files.Writes);
        Assert.Equal(intent != WritebackIntent.Shortcut, rig.Owner.Frozen);
        var completed = await rig.Coordinator.CompleteAsync(intent, drive: intent == WritebackIntent.Eject ? "B" : null);
        Assert.True(completed.Accepted, completed.Error);
        Assert.False(rig.Owner.Frozen);
        if (intent == WritebackIntent.Eject) Assert.Equal("B", rig.Owner.CompletedDrive);
    }

    [Fact]
    public async Task DisabledAndFailedMountNeverBindOrWrite()
    {
        using var rig = new Rig();
        Assert.Null((await rig.Bind("B", enabled: false)).Binding);
        Assert.Equal(0, rig.Files.Reads);
        Assert.Null((await rig.Bind("B", succeeded: false)).Binding);
        Assert.Equal(0, rig.Files.Reads);
        Assert.Equal(WritebackPublication.UnpublishedAbort,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Close)).Outcome);
        Assert.Equal(0, rig.Owner.Begins); Assert.Equal(0, rig.Files.Writes);
        Assert.Equal(rig.Original.ToArray(), File.ReadAllBytes(rig.Paths["B"]));
    }

    [Fact]
    public async Task SamePathReportedAliasAndSymlinkRefuseBinding()
    {
        using var rig = new Rig();
        Assert.NotNull((await rig.Bind("A")).Binding);
        var receipt = rig.Mount("B");
        Assert.Null((await rig.Coordinator.BindAsync(receipt, rig.Paths["A"], rig.Original, true)).Binding);
        rig.Files.Ids[rig.Paths["B"]] = rig.Files.Ids[rig.Paths["A"]];
        Assert.Null((await rig.Bind("B")).Binding);
        rig.Files.Ids[rig.Paths["B"]] = "different";
        rig.Files.Link = true;
        Assert.Null((await rig.Bind("B")).Binding);
        Assert.Equal(0, rig.Files.Writes);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("short")]
    [InlineData("unwritable")]
    public async Task TargetRefusalRetainsCurrentFileAndLiveDirtyImage(string failure)
    {
        using var rig = new Rig();
        await rig.Bind("B");
        if (failure == "changed") File.WriteAllBytes(rig.Paths["B"], MediaSessionTests.Disk(0x44).ToArray());
        if (failure == "short") File.WriteAllBytes(rig.Paths["B"], [1, 2, 3]);
        if (failure == "unwritable") rig.Files.Writable = false;
        var before = File.ReadAllBytes(rig.Paths["B"]);
        var result = await rig.Coordinator.PersistAsync("B", WritebackIntent.Close);
        Assert.Equal(WritebackPublication.UnpublishedAbort, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(rig.Paths["B"]));
        Assert.Equal(1, rig.Owner.Dirty["B"]); Assert.Equal(0, rig.Files.Writes);
        Assert.Equal(1, rig.Owner.Aborts);
        Assert.True((await rig.Coordinator.CompleteAsync(WritebackIntent.Close, cancel: true)).Accepted);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("fdc")]
    [InlineData("lifecycle")]
    public async Task ForeignPendingBusyAndLifecycleRefuseButOwnRequestDoesNot(string conflict)
    {
        using var rig = new Rig(); await rig.Bind("B");
        rig.Owner.Conflict = conflict;
        Assert.Equal(WritebackPublication.UnpublishedAbort,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Shortcut)).Outcome);
        Assert.Equal(0, rig.Files.Writes);
        rig.Owner.Conflict = null; rig.Owner.OwnRequestPending = true;
        Assert.Equal(WritebackPublication.Published,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Shortcut)).Outcome);
    }

    [Theory]
    [InlineData("drive")]
    [InlineData("session")]
    [InlineData("replay")]
    public async Task CrossDriveNewSessionAndReusedAttemptNeverPublish(string mismatch)
    {
        using var rig = new Rig(); await rig.Bind("B");
        if (mismatch == "drive") rig.Owner.TokenDrive = "A";
        if (mismatch == "session") rig.Owner.SessionId = "new-session";
        if (mismatch == "replay")
        {
            Assert.Equal(WritebackPublication.Published,
                (await rig.Coordinator.PersistAsync("B", WritebackIntent.Shortcut)).Outcome);
            rig.Owner.NextAttempt = rig.Owner.LastToken!.AttemptId;
        }
        var before = rig.Files.Writes;
        Assert.Equal(WritebackPublication.Unknown,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Shortcut)).Outcome);
        Assert.Equal(before, rig.Files.Writes);
        Assert.False((await rig.Coordinator.CompleteAsync(WritebackIntent.Close)).Accepted);
    }

    [Fact]
    public async Task SameSectorChangeWithSameGenerationAndDirtyCountInvalidatesReceipt()
    {
        using var rig = new Rig(); await rig.Bind("B");
        Assert.Equal(WritebackPublication.Published,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Shortcut)).Outcome);
        rig.Owner.Live["B"] = MediaSessionTests.Disk(0x73); // Same sector, mount=1 and dirty count=1.
        Assert.False((await rig.Coordinator.CompleteAsync(WritebackIntent.Close)).Accepted);
        Assert.Equal(1, rig.Owner.Dirty["B"]); Assert.Equal(1, rig.Files.Writes);
    }

    [Fact]
    public async Task CancelledAwaitKeepsWriterAndSessionFenceUntilActualDrain()
    {
        using var rig = new Rig(); await rig.Bind("B");
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Files.Override = async (path, bytes, _) =>
        {
            await release.Task;
            return await rig.Files.Replace(path, bytes);
        };
        using var cancellation = new CancellationTokenSource();
        var operation = rig.Coordinator.PersistAsync("B", WritebackIntent.Close, cancellation.Token);
        await rig.Files.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.Equal(WritebackPublication.Unknown, (await operation).Outcome);
        Assert.True(rig.Owner.Frozen); Assert.Equal(0, rig.Owner.Aborts);
        Assert.False((await rig.Coordinator.CompleteAsync(WritebackIntent.Close, cancel: true)).Accepted);
        Assert.Equal(WritebackPublication.Unknown, (await rig.Coordinator.ReconcileAsync("B")).Outcome);
        Assert.False(await rig.Coordinator.InvalidateAsync());
        release.SetResult(true);
        await rig.Files.Writer!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WritebackPublication.Published, (await rig.Coordinator.ReconcileAsync("B")).Outcome);
        Assert.True((await rig.Coordinator.CompleteAsync(WritebackIntent.Close)).Accepted);
        Assert.Equal(1, rig.Files.Writes); Assert.Equal(1, rig.Owner.Dirty["B"]);
    }

    [Fact]
    public async Task SynchronousPublisherFaultDoesNotAbortAnUnknownWriter()
    {
        using var rig = new Rig(); await rig.Bind("B");
        rig.Files.Override = (_, _, _) => throw new IOException("Dispatch may have started a writer.");
        Assert.Equal(WritebackPublication.Unknown,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Close)).Outcome);
        Assert.True(rig.Owner.Frozen); Assert.Equal(0, rig.Owner.Aborts);
        Assert.False((await rig.Coordinator.CompleteAsync(WritebackIntent.Close)).Accepted);
    }

    [Fact]
    public async Task RenameBeforeLostAckIsReconciledWithoutSecondCommitOrWrite()
    {
        using var rig = new Rig(); await rig.Bind("B"); rig.Owner.LoseAck = true;
        Assert.Equal(WritebackPublication.Unknown,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Close)).Outcome);
        Assert.Equal(rig.Owner.Live["B"].ToArray(), File.ReadAllBytes(rig.Paths["B"]));
        Assert.True(rig.Owner.Frozen); Assert.Equal(1, rig.Owner.Dirty["B"]);
        Assert.Equal(WritebackPublication.Published, (await rig.Coordinator.ReconcileAsync("B")).Outcome);
        Assert.Equal(1, rig.Owner.Commits); Assert.Equal(1, rig.Owner.CommitInspections);
        Assert.Equal(1, rig.Files.Writes);
    }

    [Fact]
    public async Task APublishedBFailedKeepsCloseOpenAndPriorPauseState()
    {
        using var rig = new Rig(); rig.Owner.Paused = true;
        await rig.Bind("A"); await rig.Bind("B");
        Assert.Equal(WritebackPublication.Published,
            (await rig.Coordinator.PersistAsync("A", WritebackIntent.Close)).Outcome);
        Assert.True(rig.Owner.Frozen);
        rig.Files.Writable = false;
        Assert.Equal(WritebackPublication.UnpublishedAbort,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Close)).Outcome);
        Assert.False((await rig.Coordinator.CompleteAsync(WritebackIntent.Close)).Accepted);
        Assert.True(rig.Owner.Frozen); Assert.Equal(1, rig.Owner.Dirty["B"]);
        Assert.True((await rig.Coordinator.CompleteAsync(WritebackIntent.Close, cancel: true)).Accepted);
        Assert.True(rig.Owner.Paused); Assert.False(rig.Owner.Frozen);
    }

    [Fact]
    public async Task CloseKeepsHddTapeAndOtherDrivePendingGuards()
    {
        using var rig = new Rig(); await rig.Bind("A");
        rig.Owner.OtherDrivePending = true;
        Assert.Equal(WritebackPublication.Published,
            (await rig.Coordinator.PersistAsync("A", WritebackIntent.Close)).Outcome);
        Assert.True(rig.Owner.Frozen);
        Assert.False((await rig.Coordinator.CompleteAsync(WritebackIntent.Close)).Accepted);
        rig.Owner.OtherDrivePending = false; rig.Owner.HddTapeSafe = false;
        Assert.False((await rig.Coordinator.CompleteAsync(WritebackIntent.Close)).Accepted);
        Assert.True(rig.Owner.Frozen);
    }

    [Fact]
    public async Task ExplicitInvalidationAndDisableCannotKeepOldTargetPermission()
    {
        using var rig = new Rig(); await rig.Bind("B");
        Assert.True(await rig.Coordinator.InvalidateAsync("B"));
        Assert.Equal(WritebackPublication.UnpublishedAbort,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Shortcut)).Outcome);
        await rig.Bind("B"); await rig.Bind("B", enabled: false);
        Assert.Equal(WritebackPublication.UnpublishedAbort,
            (await rig.Coordinator.PersistAsync("B", WritebackIntent.Shortcut)).Outcome);
        Assert.Equal(0, rig.Files.Writes);
    }

    private sealed class Rig : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-writeback-" + Guid.NewGuid().ToString("N"));
        public ImmutableArray<byte> Original { get; } = MediaSessionTests.Disk();
        public Dictionary<string, string> Paths { get; } = new(StringComparer.Ordinal);
        public ModelOwner Owner { get; } = new();
        public SandboxFiles Files { get; } = new();
        public DiskWritebackCoordinator Coordinator { get; }
        public Rig()
        {
            Directory.CreateDirectory(directory);
            foreach (var drive in new[] { "A", "B" })
            {
                Paths[drive] = Path.Combine(directory, drive + ".dsk");
                File.WriteAllBytes(Paths[drive], Original.ToArray());
                Files.Ids[Paths[drive]] = "file-" + drive;
            }
            Coordinator = new(Owner, Files);
        }
        public OwnerMountReceipt Mount(string drive, bool succeeded = true) =>
            new(drive, Owner.SessionId, 1, DiskWritebackCoordinator.Hash(Original), succeeded);
        public Task<WritebackBindResult> Bind(string drive, bool enabled = true, bool succeeded = true) =>
            Coordinator.BindAsync(Mount(drive, succeeded), Paths[drive], Original, enabled);
        public void Dispose() => Directory.Delete(directory, true);
    }

    private sealed class SandboxFiles : IDiskWritebackPersistence
    {
        public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);
        public bool Writable { get; set; } = true;
        public bool Link { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Func<string, ImmutableArray<byte>, PersistToken, Task<WritebackPublishResult>>? Override { get; set; }
        public Task<WritebackPublishResult>? Writer { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WritebackTargetInfo> InspectAsync(string path)
        {
            Reads++;
            return new(true, Link, Writable, await MediaFiles.ReadAsync(path, MediaFiles.MaxDiskBytes), Ids[path]);
        }
        public Task<WritebackPublishResult> PublishAsync(string path, ImmutableArray<byte> bytes, PersistToken token)
        {
            Writes++; Started.TrySetResult(true);
            Writer = Override is null ? Replace(path, bytes) : Override(path, bytes, token);
            return Writer;
        }
        public async Task<WritebackPublishResult> Replace(string path, ImmutableArray<byte> bytes)
        {
            await MediaFiles.SaveAsync(path, bytes);
            Ids[path] += "-replaced"; // Atomic replace can change the reported inode/file identity.
            return new(WritebackPublication.Published, true, DiskWritebackCoordinator.Hash(bytes));
        }
        public Task<WritebackPublishResult> ReconcileAsync(string path, PersistToken token) =>
            Writer is { IsCompletedSuccessfully: true } ? Writer
                : Task.FromResult(new WritebackPublishResult(WritebackPublication.Unknown, false));
    }

    /// <summary>Model of the pin, deliberately not a worker/FDC substitute or integration adapter.</summary>
    private sealed class ModelOwner : IDiskWritebackOwner
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
        public Dictionary<string, ImmutableArray<byte>> Live { get; } = new(StringComparer.Ordinal)
            { ["A"] = MediaSessionTests.Disk(0x72), ["B"] = MediaSessionTests.Disk(0x72) };
        public Dictionary<string, int> Dirty { get; } = new(StringComparer.Ordinal) { ["A"] = 0, ["B"] = 1 };
        public bool Frozen { get; private set; }
        public bool Paused { get; set; }
        public bool HddTapeSafe { get; set; } = true;
        public bool OtherDrivePending { get; set; }
        public bool OwnRequestPending { get; set; }
        public string? Conflict { get; set; }
        public string? TokenDrive { get; set; }
        public string? NextAttempt { get; set; }
        public bool LoseAck { get; set; }
        public string? CompletedDrive { get; private set; }
        public PersistToken? LastToken { get; private set; }
        public int Begins { get; private set; }
        public int Aborts { get; private set; }
        public int Commits { get; private set; }
        public int CommitInspections { get; private set; }
        private readonly Dictionary<string, PersistToken> issued = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PersistToken> committed = new(StringComparer.Ordinal);
        private bool priorPaused;
        private bool completionHold;
        private int sequence;
        public Task<WritebackBeginResult> BeginAsync(string drive, WritebackIntent intent)
        {
            Begins++;
            // The adapter's own HasPending is not foreign work. It cannot suppress real conflicts.
            _ = OwnRequestPending;
            if (Conflict is not null || issued.ContainsKey(drive))
                return Task.FromResult(new WritebackBeginResult(null, "Foreign/FDC/lifecycle conflict."));
            if (!Frozen) priorPaused = Paused;
            Frozen = true; Paused = true;
            completionHold |= intent != WritebackIntent.Shortcut;
            var token = new PersistToken(TokenDrive ?? drive, SessionId, 1,
                DiskWritebackCoordinator.Hash(Live[drive]), NextAttempt ?? (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture));
            NextAttempt = null; LastToken = token; issued[drive] = token;
            return Task.FromResult(new WritebackBeginResult(new(token, Live[drive], true, priorPaused)));
        }
        public Task<WritebackOwnerResult> CommitAsync(PersistToken token)
        {
            Commits++;
            if (!issued.TryGetValue(token.Drive, out var current) || token != current
                || committed.ContainsKey(token.AttemptId) || token.SessionId != SessionId
                || token.SnapshotHash != DiskWritebackCoordinator.Hash(Live[token.Drive]))
                return Task.FromResult(new WritebackOwnerResult(false, Frozen, "Stale/single-use token."));
            issued.Remove(token.Drive); committed[token.AttemptId] = token; Refresh();
            if (LoseAck) { LoseAck = false; throw new IOException("Owner committed; reply lost."); }
            return Task.FromResult(new WritebackOwnerResult(true, Frozen));
        }
        public Task<WritebackOwnerResult> InspectCommitAsync(PersistToken token)
        {
            CommitInspections++;
            return Task.FromResult(new WritebackOwnerResult(committed.TryGetValue(token.AttemptId, out var known)
                && token == known, Frozen));
        }
        public Task<WritebackOwnerResult> AbortAsync(PersistToken token)
        {
            Aborts++;
            var valid = issued.TryGetValue(token.Drive, out var current) && current == token;
            if (valid) issued.Remove(token.Drive);
            Refresh();
            return Task.FromResult(new WritebackOwnerResult(valid, Frozen));
        }
        public Task<WritebackOwnerResult> CompleteAsync(WritebackIntent intent, string? drive,
            IReadOnlyList<PersistenceReceipt> receipts)
        {
            if (!Frozen) priorPaused = Paused;
            Frozen = true; Paused = true;
            var dirtyDrives = intent == WritebackIntent.Eject ? new[] { drive! } : new[] { "A", "B" };
            var valid = intent == WritebackIntent.Shortcut || dirtyDrives.All(d => Dirty[d] == 0
                || receipts.Any(r => r.Token.Drive == d && r.Token.SessionId == SessionId
                    && r.Token.MountIdentity == 1 && r.PublishedHash == DiskWritebackCoordinator.Hash(Live[d])));
            if (issued.Count != 0 || OtherDrivePending || !HddTapeSafe || !valid)
                return Task.FromResult(new WritebackOwnerResult(false, true, "Guard retained dirty media."));
            CompletedDrive = drive; completionHold = false; Refresh();
            return Task.FromResult(new WritebackOwnerResult(true, Frozen));
        }
        public Task<WritebackOwnerResult> CancelCompletionAsync()
        {
            if (issued.Count != 0 || OtherDrivePending) return Task.FromResult(new WritebackOwnerResult(false, true));
            completionHold = false; Refresh();
            return Task.FromResult(new WritebackOwnerResult(true, Frozen));
        }
        private void Refresh()
        {
            Frozen = issued.Count != 0 || OtherDrivePending || completionHold;
            Paused = Frozen || priorPaused;
        }
    }
}
