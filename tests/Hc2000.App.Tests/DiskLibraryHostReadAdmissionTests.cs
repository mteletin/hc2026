using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Input;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Host-read admission for the disk library: on a guarded host the native cold-read gate is held
/// from the bounded read until the real terminal (owner outcome, refusal or named drain) — never a read-only
/// window. The witnesses are the real reservation API, the real panel/host wiring (MainWindow's own
/// reserver/completed/held/protected delegates, forwarded through pausing wrappers) and the real kernel
/// gate: the second same-domain cold read is the only contender, and nothing is synthesized — no fake
/// reply, no fake reserver, no mounted flag, no second runtime. The early-release falsifier is red on the
/// pre-fix producer (gate released when the read completed) and green on the fix.</summary>
public sealed class DiskLibraryHostReadAdmissionTests(ITestOutputHelper output)
{
    // The private test domain is process-global; serialize this class's own domain scopes.
    private static readonly SemaphoreSlim OneDomainAtATime = new(1, 1);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string alias);

    private static MediaReply Reply(Media.DiskSlot slot, string? error = null)
    {
        var a = slot == Media.DiskSlot.A;
        var present = new DriveView("witness", true, false, 0, false, 1);
        var absent = new DriveView(null, false, false, 0, false, 0);
        return new MediaReply(1, a ? present : absent, a ? absent : present, Error: error);
    }

    // The only contender: a second, private-domain cold read against the real kernel gate. When it can
    // acquire, it disposes itself immediately, so a free gate is never left held.
    private static bool GateBusy()
    {
        var contender = NativeImageAdmission.TryAcquireColdRead(default);
        try
        {
            return contender.Refusal == ImageAdmissionRefusal.Busy;
        }
        finally
        {
            contender.Admission?.Dispose();
        }
    }

    private static void SwapFile(string path, byte[] other)
    {
        // A rewrite in place keeps the inode; staging then replacing guarantees a distinct inode even on ext4.
        var staged = path + ".swapped";
        File.WriteAllBytes(staged, other);
        File.Move(staged, path, overwrite: true);
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task ReadingPhaseHoldKeepsTheKernelGateBusyUntilCancelBeforeEnqueue()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var path = Path.Combine(domain.Root, "witness.img");
            File.WriteAllBytes(path, [1, 2, 3]);
            var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
            var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
            try
            {
                Assert.True(reservation.Guarded);
                Assert.Equal(Media.DiskReadReservation.Phase.Reading, reservation.CurrentPhase);
                Assert.True(GateBusy(), "the read has completed but no terminal has run; the gate must still be held");
                reservation.CancelBeforeEnqueue();
                Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
                Assert.True(GateBusy(), "a member terminal releases nothing; the parent operation still holds the fence");
                await operation.ReleaseOnceAsync("cancel before enqueue");
                Assert.False(GateBusy(), "the named release frees the fence exactly once");
            }
            finally
            {
                reservation.Dispose();
                await operation.ReleaseOnceAsync("test teardown");
            }
        }
        finally
        {
            OneDomainAtATime.Release();
        }
    }

    // The placement's negative witness, run through the panel-dispatch path: after MarkDispatched the gate
    // must still be held until the real owner outcome. On the pre-fix producer the gate was released when
    // the read completed, so the contender below acquires it free and this test is red there.
    [Fact, SupportedOSPlatform("linux")]
    public async Task DispatchedReservationHoldsTheKernelGateUntilTheRealOwnerOutcome()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var path = Path.Combine(domain.Root, "witness.img");
            File.WriteAllBytes(path, [1, 2, 3]);
            var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
            var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
            try
            {
                Assert.True(reservation.Guarded);
                reservation.MarkDispatched();
                Assert.Equal(Media.DiskReadReservation.Phase.Dispatched, reservation.CurrentPhase);
                Assert.True(GateBusy(), "dispatched but not yet acknowledged; the gate must still be held");
                var identity = reservation.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.A));
                Assert.NotNull(identity);
                Assert.Equal(reservation.CanonicalPath, identity!.CanonicalPath);
                Assert.Equal(3, identity.Length);
                Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
                Assert.True(GateBusy(), "the owner outcome completed the member, but the parent operation still holds the fence");
                await operation.ReleaseOnceAsync("owner outcome accounted");
                Assert.False(GateBusy(), "the named release frees the fence exactly once");
            }
            finally
            {
                reservation.Dispose();
                await operation.ReleaseOnceAsync("test teardown");
            }
        }
        finally
        {
            OneDomainAtATime.Release();
        }
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task RefusedOwnerOutcomeYieldsNoIdentityAndReleasesTheGate()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var path = Path.Combine(domain.Root, "witness.img");
            File.WriteAllBytes(path, [1, 2, 3]);
            var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.B });
            var reservation = await operation.ReadMemberAsync(Media.DiskSlot.B, path);
            try
            {
                reservation.MarkDispatched();
                Assert.True(GateBusy());
                var identity = reservation.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.B, error: "invalid DSK input"));
                Assert.Null(identity); // a refused or absent drive returns no identity
                Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
                Assert.True(GateBusy(), "the refusal completed the member, but the parent operation still holds the fence");
                await operation.ReleaseOnceAsync("refusal accounted");
                Assert.False(GateBusy(), "the named release frees the fence exactly once");
            }
            finally
            {
                reservation.Dispose();
                await operation.ReleaseOnceAsync("test teardown");
            }
        }
        finally
        {
            OneDomainAtATime.Release();
        }
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task ChangedSourceAtDispatchRefusesBeforeEnqueueAndDrainReleasesTheGate()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var path = Path.Combine(domain.Root, "witness.img");
            File.WriteAllBytes(path, [1, 2, 3]);
            var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
            var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
            try
            {
                SwapFile(path, [9]); // new inode, new length: the snapshot can no longer authorize a mount
                var ex = Assert.Throws<AdmissionException>(() => reservation.MarkDispatched());
                Assert.Equal(ImageAdmissionRefusal.InvalidDomain, ex.Refusal);
                // A refused dispatch never enqueued; the reservation is still Reading and may be drained.
                Assert.Equal(Media.DiskReadReservation.Phase.Reading, reservation.CurrentPhase);
                reservation.DrainAtStop();
                Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
                Assert.True(GateBusy(), "drain is fence-neutral; the parent operation still holds the fence");
                await operation.ReleaseOnceAsync("drain at stop");
                Assert.False(GateBusy(), "the named release frees the fence exactly once");
            }
            finally
            {
                reservation.Dispose();
                await operation.ReleaseOnceAsync("test teardown");
            }
        }
        finally
        {
            OneDomainAtATime.Release();
        }
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task StaleOwnerOutcomeThrowsAndReleasesTheGate()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var path = Path.Combine(domain.Root, "witness.img");
            File.WriteAllBytes(path, [1, 2, 3]);
            var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
            var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
            try
            {
                reservation.MarkDispatched();
                SwapFile(path, [9, 8]);
                var ex = Assert.Throws<AdmissionException>(() => reservation.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.A)));
                Assert.Equal(ImageAdmissionRefusal.InvalidDomain, ex.Refusal);
                Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
                Assert.True(GateBusy(), "the stale outcome refused the member, but the parent operation still holds the fence");
                await operation.ReleaseOnceAsync("stale outcome accounted");
                Assert.False(GateBusy(), "the named release frees the fence exactly once");
            }
            finally
            {
                reservation.Dispose();
                await operation.ReleaseOnceAsync("test teardown");
            }
        }
        finally
        {
            OneDomainAtATime.Release();
        }
    }

    [Fact, SupportedOSPlatform("linux")]
    public void DispatchedReservationRejectsCancellationAndOnlyDrainCompletesIt()
    {
        // Synchronous: the point is that cancellation throws and does not touch the hold.
        OneDomainAtATime.Wait();
        try
        {
            RunSync();
        }
        finally
        {
            OneDomainAtATime.Release();
        }

        void RunSync()
        {
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var path = Path.Combine(domain.Root, "witness.img");
            File.WriteAllBytes(path, [1, 2, 3]);
            var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.B });
            var reservation = operation.ReadMemberAsync(Media.DiskSlot.B, path).GetAwaiter().GetResult();
            try
            {
                reservation.MarkDispatched();
                var ex = Assert.Throws<InvalidOperationException>(() => reservation.CancelBeforeEnqueue());
                Assert.Contains("owner outcome", ex.Message, StringComparison.Ordinal);
                // Cancellation must not acknowledge a dispatched reservation.
                Assert.Equal(Media.DiskReadReservation.Phase.Dispatched, reservation.CurrentPhase);
                Assert.True(GateBusy(), "still dispatched and unacknowledged; the gate must still be held");
                reservation.DrainAtStop();
                Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
                Assert.True(GateBusy(), "drain is fence-neutral; the parent operation still holds the fence");
                operation.ReleaseOnceAsync("named drain").GetAwaiter().GetResult();
                Assert.False(GateBusy(), "the named release frees the fence exactly once");
            }
            finally
            {
                reservation.Dispose();
                operation.ReleaseOnceAsync("test teardown").GetAwaiter().GetResult();
            }
        }
    }

    [AvaloniaFact]
    public async Task LaunchDisksSettleThroughTheRealWiringAndLeaveTheKernelGateFree()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            Assert.Null(RomSelectionFixture.MissingAssets());
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var root = RomSelectionFixture.RepositoryRoot();
            var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(root));
            var diskA = Path.Combine(domain.Root, "A-launch.dsk");
            var diskB = Path.Combine(domain.Root, "B-launch.dsk");
            var source = Path.Combine(root, "disks", "cpm", "CPM22-HC.DSK");
            Assert.True(File.Exists(source) && new FileInfo(source).Length > 256, "Repository disk is not materialized.");
            File.Copy(source, diskA);
            File.Copy(source, diskB);
            var window = new MainWindow(new LaunchOptions("A:" + diskA, "B:" + diskB, true, false), root);
            try
            {
                window.Show();
                var media = window.FindControl<MediaPanel>("MediaControls")!;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while (!HasBothDisks(media))
                {
                    AssertNoBootError(window, media);
                    await Task.Delay(20, deadline.Token);
                }
                await AssertUsable(window, media, roms.Basic, deadline.Token);
                Assert.False(media.DiskReadHeldFor!(Media.DiskSlot.A), "A's real receipt settled; the host hold must be cleared");
                Assert.False(media.DiskReadHeldFor!(Media.DiskSlot.B), "B's real receipt settled; the host hold must be cleared");
                Assert.False(GateBusy(), "both launch members settled through the real wiring; the kernel gate must be free");
            }
            finally
            {
                window.Close();
                await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            OneDomainAtATime.Release();
        }
    }

    // Early-release falsifier and held-owner completion through the REAL wiring: MainWindow's own
    // reserver/completed delegates are captured and forwarded through pausing wrappers. Pausing after the
    // real reservation proves the gate survives the read-only window (red before the fix); pausing inside
    // the real completed hook proves the gate survives the held-owner completion until its real terminal.
    [AvaloniaFact]
    public async Task RealWiringHoldsTheKernelGateUntilTheRealCompletedHookSettles()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            Assert.Null(RomSelectionFixture.MissingAssets());
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var root = RomSelectionFixture.RepositoryRoot();
            var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(root));
            var disk = Path.Combine(domain.Root, "A-witness.dsk");
            var source = Path.Combine(root, "disks", "cpm", "CPM22-HC.DSK");
            Assert.True(File.Exists(source) && new FileInfo(source).Length > 256, "Repository disk is not materialized.");
            File.Copy(source, disk);
            var window = new MainWindow(new LaunchOptions(null, null, true, false), root);
            var afterReserve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var afterReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? insert = null;
            try
            {
                window.Show();
                var media = window.FindControl<MediaPanel>("MediaControls")!;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await AssertUsable(window, media, roms.Basic, deadline.Token);
                // Capture the real delegates MainWindow wired at boot; the wrappers forward to them and only
                // add a pause — no substitute reply, no fake reserver, no second runtime.
                var realReserver = media.DiskReadReserver!;
                var realCompleted = media.DiskReadCompleted!;
                media.DiskReadReserver = async (slot, path, ct) =>
                {
                    var reservation = await realReserver(slot, path, ct);
                    await afterReserve.Task;
                    return reservation;
                };
                media.DiskReadCompleted = async (reservation, slot, reply) =>
                {
                    await afterReply.Task;
                    return await realCompleted(reservation, slot, reply);
                };
                insert = media.InsertDiskAsync(disk, Media.DiskSlot.A);
                // The real reserver registered its real held read before my pause.
                while (!media.DiskReadHeldFor!(Media.DiskSlot.A))
                {
                    Assert.False(insert.IsCompleted, "the insert must wait inside the real held reservation");
                    await Task.Delay(20, deadline.Token);
                }
                Assert.True(GateBusy(),
                    "early-release falsifier: the real bounded read is done and the bytes are held, but the kernel gate must still be held");
                afterReserve.TrySetResult();
                // The real dispatch and the real reply ran (the label reflects the reply); the real terminal
                // is parked inside the real completed hook.
                while (!DriveLabel(media, Media.DiskSlot.A).Contains("A-witness.dsk", StringComparison.Ordinal))
                {
                    Assert.False(insert.IsCompleted, "the real completed hook must not have settled yet");
                    await Task.Delay(20, deadline.Token);
                }
                Assert.True(media.DiskReadHeldFor!(Media.DiskSlot.A), "the real hold clears only in the real terminal, which is still parked");
                Assert.True(GateBusy(), "held-owner completion: the gate must survive the parked real completed hook until its real terminal");
                Assert.True(media.PathIsProtectedOriginal!(disk), "the held real read makes its exact source a protected original");
                afterReply.TrySetResult();
                await insert.WaitAsync(deadline.Token);
                Assert.False(media.DiskReadHeldFor!(Media.DiskSlot.A), "the real terminal ran; the host hold must be cleared");
                // The confirmed mount source stays a protected original through the writeback
                // observation (writeback OFF, Off state) even after the held read is cleared.
                Assert.True(media.PathIsProtectedOriginal!(disk), "the confirmed mount source must remain a protected original");
                Assert.False(GateBusy(), "the real terminal must release the kernel gate");
            }
            finally
            {
                afterReserve.TrySetResult();
                afterReply.TrySetResult();
                try
                {
                    if (insert is not null) await insert.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // A faulting insert must not outlive the window; the real drain at close settles it.
                }
                window.Close();
                await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            OneDomainAtATime.Release();
        }
    }

    // A refused replacement (invalid DSK bytes) must be reported on the panel notice line
    // (the panel's RunAsync catches the refusal), keep the current disk's provenance,
    // and clear the host hold and release the kernel gate through the real refusal terminal.
    [AvaloniaFact]
    public async Task RefusedReplacementKeepsTheCurrentProvenanceAndReleasesTheGate()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            Assert.Null(RomSelectionFixture.MissingAssets());
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var root = RomSelectionFixture.RepositoryRoot();
            var roms = await Task.Run(() => ProfileRomSelection.LoadWholeChipV2(root));
            var current = Path.Combine(domain.Root, "A-current.dsk");
            var source = Path.Combine(root, "disks", "cpm", "CPM22-HC.DSK");
            Assert.True(File.Exists(source) && new FileInfo(source).Length > 256, "Repository disk is not materialized.");
            File.Copy(source, current);
            var invalid = Path.Combine(domain.Root, "A-invalid.dsk");
            // A standard-DSK header that is truncated below the 256-byte header: Dsk.Load refuses
            // it with "Truncated disk header." before it can eject the mounted disk.
            File.WriteAllBytes(invalid, "MV - CPCEMU Disk-File\r\nDisk-Info\r\n\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00"u8.ToArray());
            var window = new MainWindow(new LaunchOptions(null, null, true, false), root);
            try
            {
                window.Show();
                var media = window.FindControl<MediaPanel>("MediaControls")!;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await AssertUsable(window, media, roms.Basic, deadline.Token);
                await media.InsertDiskAsync(current, Media.DiskSlot.A).WaitAsync(deadline.Token);
                Assert.False(GateBusy(), "the first real mount's terminal must release the kernel gate");
                var before = DriveLabel(media, Media.DiskSlot.A);
                Assert.Contains("A-current.dsk", before, StringComparison.Ordinal);
                // The refusal is surfaced on the notice line: RunAsync catches the
                // InvalidOperationException("Truncated disk header.") from the reserved path.
                await media.InsertDiskAsync(invalid, Media.DiskSlot.A).WaitAsync(deadline.Token);
                var notice = media.Notice;
                output.WriteLine("refusal notice: " + notice);
                Assert.Contains("Media action failed", notice, StringComparison.Ordinal);
                Assert.Contains("Truncated disk header", notice, StringComparison.Ordinal);
                Assert.DoesNotContain("Stale media runtime reply ignored", notice, StringComparison.Ordinal);
                var after = DriveLabel(media, Media.DiskSlot.A);
                // Invalid input never ejects the old disk.
                Assert.Contains("A-current.dsk", after, StringComparison.Ordinal);
                Assert.DoesNotContain("A-invalid.dsk", after, StringComparison.Ordinal);
                Assert.False(media.DiskReadHeldFor!(Media.DiskSlot.A), "the real refusal terminal must clear the host hold");
                Assert.False(GateBusy(), "the refusal terminal must release the kernel gate");
                // A refused insert is never recorded and never erases the predecessor observation.
                Assert.True(media.PathIsProtectedOriginal!(current), "the confirmed mount source must stay a protected original after a refused replacement");
                // The refusal is panel-level; the machine keeps running.
                Assert.Contains("Real hc2000-v2 whole-chip machine running",
                    window.FindControl<TextBlock>("RuntimeStatus")!.Text, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
                await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            OneDomainAtATime.Release();
        }
    }

    // (b) Bind-failure preservation through the real host seam: A bound, then B mounted on a hard link
    // of A's bound source. The refused bind must keep the ACTUAL new mount visible as UnboundError with
    // the collision reason - not a return to the predecessor and not a fabricated owner refusal. A second
    // host witnesses a symlink source, which fails validation (a link is not the unchanged mounted
    // regular file) and is kept as UnboundError the same way. Named control gap (board 632762): the real
    // producer releases its fence only after awaiting NoteMounted's accounting Task; the host seam this
    // test drives is synchronous, so the interval where that Task is pending (observed state still
    // Pending, fence released) is not observable here - it belongs to the producer seam
    // (hc2000-disk-library-host-read-admission, 1ca1b01).
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task BindFailureKeepsTheActualMountAsUnboundError()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var source = Path.Combine(domain.Root, "source.dsk");
        var alias = Path.Combine(domain.Root, "alias.dsk");
        File.WriteAllBytes(source, DiskLibraryCatalogLiteralFixtures.Standard());
        Assert.Equal(0, Link(source, alias));
        var mounted = ImmutableArray.CreateRange(DiskLibraryCatalogLiteralFixtures.Standard());

        var host = new DiskWritebackHost(new DiskWritebackOwnerLedger(), new MediaBridge(new InputQueue()), enabled: true);
        await host.NoteMounted(Media.DiskSlot.A, source, mounted, Reply(Media.DiskSlot.A));
        await host.NoteMounted(Media.DiskSlot.B, alias, mounted, Reply(Media.DiskSlot.B));
        var observations = host.CaptureMountObservations();
        Assert.Equal(DiskWritebackHost.MountBindingState.Bound, observations.Single(o => o.Slot == Media.DiskSlot.A).BindingState);
        var refused = observations.Single(o => o.Slot == Media.DiskSlot.B);
        Assert.Equal(DiskWritebackHost.MountBindingState.UnboundError, refused.BindingState);
        Assert.Equal("A/B writeback target collision.", refused.Error);
        Assert.Equal(Path.GetFullPath(alias), refused.CanonicalSourcePath);
        Assert.True(host.ReportsTarget);

        var leak = Path.Combine(domain.Root, "leak.dsk");
        File.CreateSymbolicLink(leak, source);
        var fresh = new DiskWritebackHost(new DiskWritebackOwnerLedger(), new MediaBridge(new InputQueue()), enabled: true);
        await fresh.NoteMounted(Media.DiskSlot.A, leak, mounted, Reply(Media.DiskSlot.A));
        var witness = fresh.CaptureMountObservations().Single(o => o.Slot == Media.DiskSlot.A);
        Assert.Equal(DiskWritebackHost.MountBindingState.UnboundError, witness.BindingState);
        Assert.Equal("Binding refused: Invalid or oversized disk image.", witness.Error);
    }

    // (c) The machine's own replacement discipline, through the real FDC and drive: a disk with a dirty
    // sector refuses a fresh insert until it is explicitly discarded, and a motor-on (busy) drive refuses
    // replacement until it is stopped; PowerCycle keeps the medium and allows the next insert.
    [Fact]
    public void DirtyOrBusyDriveRefusesMediaReplacementUntilDiscardedOrStopped()
    {
        var scheduler = new DeviceScheduler();
        var a = new FloppyDrive(new());
        var board = new FloppyController(scheduler, 10, a, new FloppyDrive(new()));
        var media = new MediaSession(a, board.DriveB);
        Assert.Null(media.Apply(new(MediaAction.InsertDisk, Bytes: MediaSessionTests.Disk(), Name: "first")).Error);

        board.WriteLatch(0x1a);
        foreach (var value in new byte[] { 3, 0xdf, 1, 0x45, 0, 0, 0, 1, 0, 1, 0x2a, 0x80 }) board.Fdc.WriteData(value);
        Ready(); board.Fdc.WriteData(0x72);
        board.WriteLatch(0x1b); Ready();
        while (board.Fdc.Phase == FdcPhase.Result) board.Fdc.ReadData();
        board.WriteLatch(0x10); // deselect motors; head unload remains scheduler-owned
        int events = 0;
        while (a.HeadLoaded && ++events < 1000) scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
        Assert.Single(a.DirtySectors);

        var refused = media.Apply(new(MediaAction.InsertDisk, Bytes: MediaSessionTests.Disk(0x5a), Name: "second"));
        Assert.Equal("Unsaved changes: save a copy or explicitly allow discard before replacing media.", refused.Error);
        Assert.True(refused.A.Present);
        Assert.Equal("first", refused.A.Name);
        Assert.Equal(1, refused.A.Generation);
        Assert.Equal(1, refused.A.DirtySectors);

        var discarded = media.Apply(new(MediaAction.InsertDisk, Bytes: MediaSessionTests.Disk(0x5a), Name: "second", DiscardChanges: true));
        Assert.Null(discarded.Error);
        Assert.Equal("second", discarded.A.Name);
        Assert.Equal(2, discarded.A.Generation);
        Assert.Equal(0, discarded.A.DirtySectors);

        board.WriteLatch(0x1a);
        Ready();
        Assert.True(a.MotorOn);
        var busy = media.Apply(new(MediaAction.InsertDisk, Bytes: MediaSessionTests.Disk(0x55), Name: "third"));
        Assert.Equal("Stop/unload the drive before replacing media.", busy.Error);
        Assert.True(busy.A.Busy);
        Assert.Equal("second", busy.A.Name);
        Assert.Equal(2, busy.A.Generation);

        a.PowerCycle();
        var stopped = media.Apply(new(MediaAction.InsertDisk, Bytes: MediaSessionTests.Disk(0x55), Name: "third"));
        Assert.Null(stopped.Error);
        Assert.Equal("third", stopped.A.Name);
        Assert.Equal(3, stopped.A.Generation);

        void Ready()
        {
            int count = 0;
            while ((board.Fdc.ReadStatus() & 0x80) == 0 && ++count < 1000)
                scheduler.AdvanceTo(scheduler.PendingEvents.Min(e => e.DueTick));
            Assert.NotEqual(0, board.Fdc.ReadStatus() & 0x80);
        }
    }

    // (e) Alias exclusion: after the source's inode becomes the managed copy's inode, working-copy
    // creation must refuse with the alias refusal rather than copy a file from itself. The live snapshot
    // accounts an unrelated file, so the alias check - not the protected-original check - is what fires.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task WorkingCopyRefusesASourceThatAliasedItsOwnCopy()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var root = domain.Root;
            var catalog = DiskLibraryCatalog.Load(Path.Combine(root, "catalog.json"), DiskLibraryHostAdmission.StoppedHost);
            var source = Path.Combine(root, "source.dsk");
            var other = Path.Combine(root, "other.dsk");
            File.WriteAllBytes(source, DiskLibraryCatalogLiteralFixtures.Standard());
            File.WriteAllBytes(other, [1, 2, 3]);
            var registered = await catalog.RegisterAsync(source, "fixture");
            Assert.Equal(DiskLibraryRefusal.Accepted, registered.Reason);
            var entry = Assert.IsType<DiskLibraryEntry>(registered.Entry);
            var otherId = Assert.IsType<string>(LinuxFileFacts.Inspect(other).FileId);
            catalog.SetAdmission(new DiskLibraryHostAdmission([new AccountedHostFile(other, otherId)], false));

            var created = await catalog.CreateWorkingCopyAsync(entry.Id, root);
            Assert.Equal(DiskLibraryCopyStatus.Created, created.Status);
            var copyFile = Path.Combine(root, created.Copy!.RelativePath);
            Assert.True(File.Exists(copyFile));

            File.Delete(source);
            Assert.Equal(0, Link(copyFile, source));
            var refused = await catalog.CreateWorkingCopyAsync(entry.Id, root);
            Assert.Equal(DiskLibraryCopyStatus.Refused, refused.Status);
            Assert.Equal("source aliases another original or managed copy", refused.Message);
            Assert.Null(refused.Copy);
            Assert.Equal(DiskLibraryCatalogLiteralFixtures.Standard(), File.ReadAllBytes(source));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "copies")), copyFile);
        }
        finally { OneDomainAtATime.Release(); }
    }

    // (f) Protected-original denial: a source that the live host snapshot accounts (mounted or
    // writeback-owned) is refused before any copy stage; the original stays untouched and no managed
    // copy file is produced.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task WorkingCopyRefusesAnActiveWritebackOwnedOriginal()
    {
        await OneDomainAtATime.WaitAsync();
        try
        {
            using var domain = new HddColdAdmissionTests.PrivateDomain();
            using var scope = domain.Enter();
            var root = domain.Root;
            var catalog = DiskLibraryCatalog.Load(Path.Combine(root, "catalog.json"), DiskLibraryHostAdmission.StoppedHost);
            var source = Path.Combine(root, "source.dsk");
            File.WriteAllBytes(source, DiskLibraryCatalogLiteralFixtures.Standard());
            var registered = await catalog.RegisterAsync(source, "fixture");
            Assert.Equal(DiskLibraryRefusal.Accepted, registered.Reason);
            var entry = Assert.IsType<DiskLibraryEntry>(registered.Entry);
            var sourceId = Assert.IsType<string>(LinuxFileFacts.Inspect(source).FileId);
            catalog.SetAdmission(new DiskLibraryHostAdmission([new AccountedHostFile(source, sourceId)], false));

            var refused = await catalog.CreateWorkingCopyAsync(entry.Id, root);
            Assert.Equal(DiskLibraryCopyStatus.Refused, refused.Status);
            Assert.Equal("active mounted or writeback-owned file", refused.Message);
            Assert.Null(refused.Copy);
            Assert.Null(refused.OrphanPath);
            Assert.Equal(DiskLibraryCatalogLiteralFixtures.Standard(), File.ReadAllBytes(source));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "copies")));
        }
        finally { OneDomainAtATime.Release(); }
    }

    private static string DriveLabel(MediaPanel media, Media.DiskSlot slot) =>
        media.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Text?.StartsWith(slot + ":", StringComparison.Ordinal) == true).Text!;

    private static bool HasBothDisks(MediaPanel media) =>
        DriveLabel(media, Media.DiskSlot.A).Contains("A-launch.dsk", StringComparison.Ordinal)
        && DriveLabel(media, Media.DiskSlot.B).Contains("B-launch.dsk", StringComparison.Ordinal);

    private static void AssertNoBootError(MainWindow window, MediaPanel media)
    {
        var status = window.FindControl<TextBlock>("RuntimeStatus")!.Text ?? "";
        Assert.DoesNotContain("Disk A:", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Disk B:", status, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed", media.Notice, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AssertUsable(MainWindow window, MediaPanel media, byte[] basic, CancellationToken token)
    {
        var screen = "(no frame)";
        while (true)
        {
            AssertNoBootError(window, media);
            if (window.LatestPresentedFrame is { } frame)
            {
                screen = BasicMachineCompositionTests.DecodeBasicScreen(frame, basic);
                var buttons = media.GetLogicalDescendants().OfType<Button>()
                    .Where(b => Equals(b.Content, "Insert DSK…")).ToArray();
                if (screen.Contains("HC2000", StringComparison.Ordinal)
                    && screen.Contains("I.C.E.", StringComparison.Ordinal)
                    && screen.Contains("FELIX", StringComparison.Ordinal)
                    && buttons.Length == 2 && buttons.All(b => b.IsEnabled)) break;
            }
            await Task.Delay(20, token);
        }
        output.WriteLine("Faithful v2 screen: " + screen);
        Assert.Equal(16384, window.BootedIf1Length);
        Assert.NotNull(window.CurrentRuntime);
        Assert.Contains("Real hc2000-v2 whole-chip machine running",
            window.FindControl<TextBlock>("RuntimeStatus")!.Text, StringComparison.Ordinal);
    }
}
