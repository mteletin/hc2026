using System.Collections.Immutable;
using System.Runtime.Versioning;
using Hc2000.App.Media;
using Hc2000.HostFiles;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class DiskReadReservationTests
{
    private static MediaReply Reply(Media.DiskSlot slot, string? error = null)
    {
        var a = slot == Media.DiskSlot.A;
        var present = new DriveView("disk", true, false, 0, false, 1);
        var absent = new DriveView(null, false, false, 0, false, 0);
        return new MediaReply(1, a ? present : absent, a ? absent : present, Error: error);
    }

    [Fact, SupportedOSPlatform("linux")]
    public void AnOperationClaimRefusesWhileANativeReadIsHeld()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var held = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(held.Refusal); Assert.NotNull(held.Admission);
        try
        {
            var ex = Assert.Throws<AdmissionException>(() =>
                Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A }));
            Assert.Equal(ImageAdmissionRefusal.Busy, ex.Refusal);
        }
        finally { held.Admission!.Dispose(); }
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task AFileSwappedAfterTheReadRefusesBeforeEnqueueAndTheParentRetainsTheFenceUntilItsRelease()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.B });
        var reservation = await operation.ReadMemberAsync(Media.DiskSlot.B, path);
        Assert.Equal([1, 2, 3], (byte[])reservation.Bytes.ToArray());
        File.Delete(path);
        File.WriteAllBytes(path, [9]); // new inode, new length
        var ex = Assert.Throws<AdmissionException>(() => reservation.MarkDispatched());
        Assert.Equal(ImageAdmissionRefusal.InvalidDomain, ex.Refusal);
        reservation.DrainAtStop();
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // the member terminal releases nothing
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        await operation.ReleaseOnceAsync("drain at stop");
        var again = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(again.Refusal); again.Admission!.Dispose();
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task AStaleOwnerOutcomeThrowsAndTheParentOperationRetainsTheFenceUntilItsRelease()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
        var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
        reservation.MarkDispatched();
        File.Delete(path);
        File.WriteAllBytes(path, [9]);
        var ex = Assert.Throws<AdmissionException>(() => reservation.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.A)));
        Assert.Equal(ImageAdmissionRefusal.InvalidDomain, ex.Refusal);
        Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // the stale terminal releases nothing
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        await operation.ReleaseOnceAsync("drain at stop");
        var again = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(again.Refusal); again.Admission!.Dispose();
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task ARefusedOwnerOutcomeReturnsNoIdentityAndTheParentOperationRetainsTheFenceUntilItsRelease()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
        var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
        reservation.MarkDispatched();
        Assert.Null(reservation.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.A, "drive absent")));
        Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // the no-bind outcome is the bind/no-bind outcome
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        await operation.ReleaseOnceAsync("configured start accounted");
        var again = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(again.Refusal); again.Admission!.Dispose();
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task AConfirmedFreshOutcomeReturnsTheColdReadIdentityAndTheFenceOutlivesIt()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
        var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
        reservation.MarkDispatched();
        var identity = reservation.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.A));
        Assert.NotNull(identity);
        Assert.Equal(reservation.Identity, identity);
        Assert.Equal(Path.GetFullPath(path), identity!.CanonicalPath);
        Assert.Equal(3, identity.Length);
        Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // the real bind outcome releases nothing
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        await operation.ReleaseOnceAsync("configured start accounted");
        var again = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(again.Refusal); again.Admission!.Dispose();
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task TheFenceIsHeldFromTheClaimThroughEveryTerminalAndFreeOnlyAfterTheParentRelease()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
        // Reading: a cooperating external publisher cannot take the gate between the claim and the read.
        var held = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Equal(ImageAdmissionRefusal.Busy, held.Refusal!.Value);
        var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
        held = NativeImageAdmission.TryAcquireColdRead(default); // held across the read itself
        Assert.Equal(ImageAdmissionRefusal.Busy, held.Refusal!.Value);
        // Dispatched: the pre- and post-enqueue identity revalidations run under the still-held fence.
        reservation.MarkDispatched();
        held = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Equal(ImageAdmissionRefusal.Busy, held.Refusal!.Value);
        // The real owner outcome releases no fence: the parent retains it through the bind outcome.
        var identity = reservation.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.A));
        Assert.NotNull(identity);
        held = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Equal(ImageAdmissionRefusal.Busy, held.Refusal!.Value);
        held = NativeImageAdmission.TryAcquireColdRead(default); // still held after the owner outcome
        Assert.Equal(ImageAdmissionRefusal.Busy, held.Refusal!.Value);
        await operation.ReleaseOnceAsync("configured start accounted");
        var nowFree = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(nowFree.Refusal); nowFree.Admission!.Dispose();
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task ADisposedDispatchedMemberStillLeavesTheParentFenceUntilTheNamedDrain()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
        var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
        reservation.MarkDispatched();
        reservation.Dispose(); // host-side pending work: disposing the instance never silently completes it
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // the exclusion outlives the instance
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        Assert.Equal(Media.DiskReadReservation.Phase.Dispatched, reservation.CurrentPhase);
        reservation.DrainAtStop(); // the named drain completes the member; the fence stays with the parent
        Assert.Equal(Media.DiskReadReservation.Phase.Terminal, reservation.CurrentPhase);
        busy = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        await operation.ReleaseOnceAsync("window closed");
        var free = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(free.Refusal); free.Admission!.Dispose();
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task ADispatchedMemberRefusesCancellationBeforeEnqueueAndDrainsUnderTheParentFence()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
        var reservation = await operation.ReadMemberAsync(Media.DiskSlot.A, path);
        reservation.MarkDispatched();
        Assert.Throws<InvalidOperationException>(() => reservation.CancelBeforeEnqueue());
        reservation.DrainAtStop();
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // drain is fence-neutral
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        await operation.ReleaseOnceAsync("worker completed");
        var again = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(again.Refusal); again.Admission!.Dispose();
    }

    [Fact]
    public async Task ALegacyHostReadsPlainlyAndReportsNoIdentity()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = NativeImageAdmission.EnterPrivateTestDomain(domain.Root, ImageAdmissionPlatformMode.LegacyOnly);
        var path = Path.Combine(domain.Root, "disk.img");
        File.WriteAllBytes(path, [1, 2, 3]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.B });
        Assert.False(operation.Guarded);
        var reservation = await operation.ReadMemberAsync(Media.DiskSlot.B, path);
        Assert.False(reservation.Guarded);
        Assert.Null(reservation.Identity);
        Assert.Equal([1, 2, 3], (byte[])reservation.Bytes.ToArray());
        reservation.MarkDispatched();
        Assert.Null(reservation.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.B)));
        await operation.ReleaseOnceAsync("configured start accounted"); // no fence to release on a legacy host
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task AnOversizedMemberRefusesBeforeEnqueueAndTheParentFenceSurvivesUntilItsRelease()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var path = Path.Combine(domain.Root, "disk.img");
        var big = new byte[Media.MediaFiles.MaxDiskBytes + 1];
        big[0] = 7;
        File.WriteAllBytes(path, big);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A });
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() =>
            operation.ReadMemberAsync(Media.DiskSlot.A, path));
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // the failed member is terminal, fence retained
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        await operation.ReleaseOnceAsync("member read failed before enqueue");
        var again = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(again.Refusal); again.Admission!.Dispose();
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task AConfiguredPairOperationHoldsOneFenceThroughBothMembersAndReleasesOnceAfterAccounting()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var pathA = Path.Combine(domain.Root, "a.img");
        File.WriteAllBytes(pathA, [1, 2, 3]);
        var pathB = Path.Combine(domain.Root, "b.img");
        File.WriteAllBytes(pathB, [4, 5]);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A, Media.DiskSlot.B });
        Assert.Equal(2, operation.ExpectedMemberCount);
        var a = await operation.ReadMemberAsync(Media.DiskSlot.A, pathA);
        Assert.Equal([1, 2, 3], (byte[])a.Bytes.ToArray());
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // one fence, between the two members
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        var b = await operation.ReadMemberAsync(Media.DiskSlot.B, pathB);
        Assert.Equal([4, 5], (byte[])b.Bytes.ToArray());
        busy = NativeImageAdmission.TryAcquireColdRead(default); // no second fence was ever taken
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        a.MarkDispatched();
        Assert.NotNull(a.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.A)));
        Assert.False(operation.AllMembersTerminal);
        busy = NativeImageAdmission.TryAcquireColdRead(default); // retained through the first real receipt
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        b.MarkDispatched();
        Assert.NotNull(b.CompleteAfterOwnerOutcome(Reply(Media.DiskSlot.B)));
        Assert.True(operation.AllMembersTerminal);
        busy = NativeImageAdmission.TryAcquireColdRead(default); // both receipts accounted; the parent still holds
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        await operation.ReleaseOnceAsync("all members accounted");
        Assert.True(operation.Released);
        var free = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(free.Refusal); free.Admission!.Dispose();
        await operation.ReleaseOnceAsync("again"); // a released operation is a no-op
    }

    [Fact, SupportedOSPlatform("linux")]
    public async Task AFailedSecondMemberAbortsTheWholeOperationBeforeEnqueueAndReleasesTheFenceOnce()
    {
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var pathA = Path.Combine(domain.Root, "a.img");
        File.WriteAllBytes(pathA, [1, 2, 3]);
        var pathB = Path.Combine(domain.Root, "b.img");
        var big = new byte[Media.MediaFiles.MaxDiskBytes + 1];
        big[0] = 7;
        File.WriteAllBytes(pathB, big);
        var operation = Media.DiskReadReservation.Operation.ClaimStart(new[] { Media.DiskSlot.A, Media.DiskSlot.B });
        var a = await operation.ReadMemberAsync(Media.DiskSlot.A, pathA);
        Assert.Equal([1, 2, 3], (byte[])a.Bytes.ToArray());
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() =>
            operation.ReadMemberAsync(Media.DiskSlot.B, pathB));
        var busy = NativeImageAdmission.TryAcquireColdRead(default); // the failed member is terminal; the fence survives
        Assert.Equal(ImageAdmissionRefusal.Busy, busy.Refusal!.Value);
        // No member was enqueued, so the whole configured start aborts before any enqueue and the fence
        // is released exactly once; the prepared but never-dispatched first member is drained, not lost.
        operation.AbortBeforeEnqueue("prestart oversize");
        Assert.Equal(Media.DiskReadReservation.Phase.Terminal, a.CurrentPhase);
        var free = NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(free.Refusal); free.Admission!.Dispose();
    }
}
