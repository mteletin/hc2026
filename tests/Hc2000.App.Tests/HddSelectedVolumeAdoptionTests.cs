using System.Security.Cryptography;
using System.Runtime.Versioning;
using Hc2000.App.Media;
using Hc2000.App.Input;
using Hc2000.App.Runtime;
using Hc2000.HostFiles;
using Hc2000.Machine;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Selected-volume helper lifetime only: disposing or cancelling the caller-visible
/// <see cref="HddColdVolume"/> after real prepare must not release the native admission fence
/// before the actual owner acknowledges or the existing no-owner drain policy applies. The
/// generic native state machine is the existing <c>HddColdAdmissionTests</c>; nothing here
/// duplicates parser, selector, positive reader or stale-candidate coverage. The private-domain
/// fixture is the delivered <see cref="HddColdVolumeTests"/> container, reused exactly as
/// <c>HddColdCallerTests</c> does.</summary>
public sealed class HddSelectedVolumeAdoptionTests
{
    [Fact, SupportedOSPlatform("linux")]
    public async Task PreparedSelectedHelperDisposeRetainsFenceUntilActualOwnerAcknowledges()
    {
        if (!OperatingSystem.IsLinux()) return; // Native qualification is Linux only; no simulated Windows pass.
        using var fixture = new HddColdVolumeTests();
        var before = SHA256.HashData(File.ReadAllBytes(fixture.PathName));
        using var selected = HddColdVolume.Open(fixture.PathName, "VOL2.DSK");
        var prepared = BasicMachineComposition.PrepareAdmittedHddWholeChipV2(Roms(), selected.Media(4), selected.Admission);
        AssertBusy(fixture.PathName);
        selected.Dispose(); // Caller drops its reference after prepare, before any work was enqueued.
        AssertBusy(fixture.PathName); // A prepared candidate is still capable of future adoption.
        Assert.Throws<ObjectDisposedException>(() => selected.Media(4));
        var queue = new InputQueue();
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        var bridge = new MediaBridge(queue);
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, bridge);
        try
        {
            var reply = await bridge.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect))).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(reply.Error);
            var drive = reply.Hdd!.Drives.Single(v => v.Drive == 4);
            Assert.True(drive.Present); Assert.True(drive.WriteProtected); Assert.False(drive.Dirty);
            Assert.Equal("VOL2.DSK", drive.Name);
            AssertReadable(fixture.PathName); // Release came through the REAL worker RuntimeContext boundary.
            // Later native publications can enter only after this owner acknowledgement.
            var publication = NativeImageAdmission.TryBeginPublication(fixture.PathName, Guid.NewGuid(), default);
            using var admitted = publication.Admission;
            Assert.NotNull(admitted); Assert.Null(publication.Refusal);
            admitted.CompleteKnownPublicationAfterDrain(KnownImagePublication.Unpublished);
        }
        finally
        {
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        AssertReadable(fixture.PathName);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.PathName)));
    }

    [Fact, SupportedOSPlatform("linux")]
    public void CancelAfterPrepareWithEnqueuedSelectedWorkRetainsFenceUntilExistingDrain()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var before = SHA256.HashData(File.ReadAllBytes(fixture.PathName));
        using var selected = HddColdVolume.Open(fixture.PathName, "VOL2.DSK");
        var prepared = BasicMachineComposition.PrepareAdmittedHddWholeChipV2(Roms(), selected.Media(4), selected.Admission);
        selected.Admission.MarkEnqueued(prepared); // The real CreateWorker path marks this the same way.
        try
        {
            selected.Dispose();
            Assert.Throws<InvalidOperationException>(() => selected.Admission.CancelBeforeEnqueue());
            AssertBusy(fixture.PathName); // Queued work cannot be disposed or cancelled away by its creator.
        }
        finally
        {
            selected.Admission.RejectBeforeOwnerStarted(); // No worker was constructed: no future owner can start.
        }
        AssertReadable(fixture.PathName);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.PathName)));
    }

    [Fact, SupportedOSPlatform("linux")]
    public void CancellationAfterPrepareWithoutEnqueueReleasesByExistingDrainPolicy()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var before = SHA256.HashData(File.ReadAllBytes(fixture.PathName));
        using var selected = HddColdVolume.Open(fixture.PathName, "VOL2.DSK");
        _ = BasicMachineComposition.PrepareAdmittedHddWholeChipV2(Roms(), selected.Media(4), selected.Admission);
        AssertBusy(fixture.PathName);
        selected.Dispose(); // The helper dispose releases nothing after prepare.
        AssertBusy(fixture.PathName);
        selected.Admission.CancelBeforeEnqueue(); // No work was enqueued: existing drain policy, no new release rule.
        AssertReadable(fixture.PathName);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.PathName)));
    }

    private static (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) Roms() =>
        (MachineProfile.Hc2000V2, new byte[16384], new byte[16384], new byte[16384]);
    private static void AssertBusy(string path)
    {
        var writer = NativeImageAdmission.TryBeginPublication(path, Guid.NewGuid(), default);
        using var refused = writer.Admission;
        Assert.Null(refused); Assert.Equal(ImageAdmissionRefusal.Busy, writer.Refusal);
    }
    private static void AssertReadable(string path)
    {
        var reader = NativeImageAdmission.TryAcquireColdRead(default);
        using var admitted = reader.Admission;
        Assert.NotNull(admitted); Assert.Null(reader.Refusal);
    }
}
