using System.Collections.Immutable;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Snapshots;
using Hc2000.App.Workspace;
using Hc2000.Machine;
using Hc2000.Machine.Snapshots;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HcsAdmissionPolicyTests
{
    internal static PreparedBasicMachine Prepare(bool paged)
    {
        var roms = ProfileRomSelection.LoadWholeChipV2(RomSelectionFixture.RepositoryRoot());
        return BasicMachineComposition.PrepareWholeChipV2(
            (paged ? MachineProfile.Hc2000V2PagedExpansionV1 : roms.Profile, roms.Basic, roms.Cpm, roms.If1));
    }

    [Fact]
    public void AnUnboundQueueOrChangedDraftCannotGrantLargeAdmission()
    {
        var tab = new MachineTabContext(new());
        var limits = tab.InputQueue.SnapshotLimits;
        tab.UpdateDraft(tab.Configuration with { Profile = "hc2000-v2-paged-expansion-v1" });
        Assert.Same(limits, tab.InputQueue.SnapshotLimits);
        Assert.Equal(new HcsLimits(), limits);
        Assert.Throws<ArgumentException>(() => tab.InputQueue.ValidateSnapshotLength(268435457));
        // Caller bytes (including any claimed profile) do not install a policy.
        tab.InputQueue.Enqueue(new HcsCommand(HcsAction.Load,
            ImmutableArray.CreateRange(System.Text.Encoding.UTF8.GetBytes("hc2000-v2-paged-expansion-v1"))));
        Assert.Same(limits, tab.InputQueue.SnapshotLimits);
        Assert.Equal(268435456, new HcsBridge(tab.InputQueue).Limits.TotalBytes);
    }

    [Theory]
    [InlineData(false, 268435456)]
    [InlineData(true, 0x44000000)]
    public void QueueAndBridgeShareExactlyThePreparedBoundUntilRetirement(bool paged, int maximum)
    {
        var queue = new InputQueue();
        var bridge = new HcsBridge(queue);
        var prepared = Prepare(paged);
        var admission = queue.BindSnapshotRuntime(prepared);
        bridge.BindAdmission(queue, admission);
        Assert.Same(admission, bridge.Limits);
        Assert.Same(admission, queue.SnapshotLimits);
        Assert.Equal(maximum, admission.TotalBytes);
        queue.ValidateSnapshotLength(maximum - 1);
        queue.ValidateSnapshotLength(maximum);
        foreach (long length in new[] { 0L, -1L, (long)maximum + 1, long.MaxValue })
            Assert.Throws<ArgumentException>(() => queue.ValidateSnapshotLength(length));
        Assert.Throws<InvalidOperationException>(() => queue.BindSnapshotRuntime(prepared));
        Assert.Equal(268435456, new InputQueue().SnapshotLimits.TotalBytes);
        queue.RetireSnapshotRuntime(admission);
        Assert.Equal(new HcsLimits(), queue.SnapshotLimits);
        Assert.Throws<InvalidOperationException>(() => { _ = bridge.RequestAsync(new(HcsAction.Save)); });
    }

    [Fact]
    public async Task ReplacementRuntimeInvalidatesOldBridgesAndPreservesFifoAndOneOutstandingRequest()
    {
        var queue = new InputQueue();
        var old = new HcsBridge(queue);
        var first = queue.BindSnapshotRuntime(Prepare(true)); old.BindAdmission(queue, first);
        queue.RetireSnapshotRuntime(first);
        var current = new HcsBridge(queue);
        var second = queue.BindSnapshotRuntime(Prepare(false)); current.BindAdmission(queue, second);
        queue.RetireSnapshotRuntime(first); // A delayed old completion cannot retire the replacement.
        Assert.Same(second, queue.SnapshotLimits);
        Assert.Throws<InvalidOperationException>(() => { _ = old.RequestAsync(new(HcsAction.Save)); });
        var key = new ReleaseAll();
        var keySequence = queue.Enqueue(key);
        var request = current.RequestAsync(new(HcsAction.Save));
        Assert.Throws<InvalidOperationException>(() => { _ = current.RequestAsync(new(HcsAction.Save)); });
        Assert.Same(key, queue.TakeNext().Command);
        var queued = queue.TakeNext();
        Assert.Equal(keySequence + 1, queued.Sequence);
        var snapshot = Assert.IsType<HcsCommand>(queued.Command);
        current.Publish(new(snapshot.RequestId, Applied: true));
        Assert.True((await request).Applied);
        queue.RetireSnapshotRuntime(second);
    }

    [Fact]
    public void CompletedOwnerCanBeReplacedBeforeItsDelayedRetirementCallback()
    {
        var queue = new InputQueue();
        var first = queue.BindSnapshotRuntime(Prepare(true));
        queue.TrackSnapshotRuntime(first, Task.CompletedTask);
        queue.Enqueue(new HcsCommand(HcsAction.Save));
        Assert.Throws<InvalidOperationException>(() => queue.BindSnapshotRuntime(Prepare(false)));
        queue.DiscardPending(); // The existing session lifecycle drains the retired FIFO.
        var second = queue.BindSnapshotRuntime(Prepare(false));
        queue.RetireSnapshotRuntime(first);
        Assert.Same(second, queue.SnapshotLimits);
        Assert.Equal(new HcsLimits(), second);
        queue.RetireSnapshotRuntime(second);
    }

    [Fact]
    public void AChangedDraftDoesNotRaiseOrLowerTheAlreadyAdoptedRuntimePolicy()
    {
        var tab = new MachineTabContext(new() { Profile = "hc2000-v2-paged-expansion-v1" });
        var adopted = tab.InputQueue.BindSnapshotRuntime(Prepare(true));
        tab.UpdateDraft(tab.Configuration with { Profile = "hc2000-v2" });
        Assert.Same(adopted, tab.InputQueue.SnapshotLimits);
        tab.InputQueue.ValidateSnapshotLength(0x44000000);
        tab.InputQueue.RetireSnapshotRuntime(adopted);
    }

    [Fact]
    public void LargeAdmissionDoesNotPermitWrongProfileDetachedValidation()
    {
        var faithful = HcsMachineFixture.Compose();
        using var input = new MemoryStream(faithful.Machine.Save());
        var paged = MachineProfile.Hc2000V2PagedExpansionV1;
        var error = Assert.Throws<HcsException>(() => HcsCodec.Read(input, new MachineSchema(paged), HcsLimits.ForProfile(paged)));
        Assert.Equal(HcsError.ProfileMismatch, error.Error);
    }

    [Fact]
    public async Task PreReadGrowthAndTruncationChecksAreBoundedBeforeEnqueue()
    {
        using var boundary = new MemoryStream(new byte[4]);
        Assert.Equal(4, (await MediaFiles.ReadAsync(boundary, 4)).Length);
        using var tooLarge = new ObservedStream(5, new byte[5]);
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaFiles.ReadAsync(tooLarge, 4));
        Assert.Equal(0, tooLarge.Reads);
        foreach (var data in new[] { new byte[5], new byte[3] })
        {
            using var changed = new ObservedStream(4, data);
            await Assert.ThrowsAsync<InvalidDataException>(() => MediaFiles.ReadAsync(changed, 8));
        }
        using var grewPastCap = new ObservedStream(4, new byte[5]);
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaFiles.ReadAsync(grewPastCap, 4));
    }

    private sealed class ObservedStream(long reportedLength, byte[] bytes) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        public override long Length => reportedLength;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
