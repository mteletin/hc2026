using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Snapshots;
using Hc2000.Machine.Snapshots;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HcsAdmissionUiTests
{
    [AvaloniaFact]
    public async Task DefaultPanelRefusesOversizedFileBeforeEnqueueOrOwnerValidation()
    {
        var path = Path.Combine(Path.GetTempPath(), "untrusted-paged-" + Guid.NewGuid() + ".hcs");
        try
        {
            using (var file = File.Create(path)) file.SetLength(268435457);
            var queue = new InputQueue(); var panel = new HcsPanel(); panel.Attach(new(queue));
            await panel.LoadSnapshotAsync(path);
            Assert.Contains("size limit", panel.Notice);
            Assert.Equal(0, queue.BoundaryCount);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task RetiredBridgeRefusesBeforeStartingAnotherHostRead()
    {
        var queue = new InputQueue(); var bridge = new HcsBridge(queue);
        int reads = 0;
        var panel = new HcsPanel((_, _) => { reads++; return Task.FromResult(ImmutableArray.Create<byte>(1)); });
        panel.Attach(bridge); bridge.Disconnect();
        await panel.LoadSnapshotAsync("retired.hcs");
        Assert.Equal(0, reads); Assert.Equal(0, queue.BoundaryCount);
        Assert.Contains("Stale snapshot", panel.Notice);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectPanelReadCapturesTheSamePreparedLimitAsItsQueue(bool paged)
    {
        var queue = new InputQueue(); var bridge = new HcsBridge(queue);
        var admission = queue.BindSnapshotRuntime(HcsAdmissionPolicyTests.Prepare(paged));
        bridge.BindAdmission(queue, admission);
        int maximum = 0;
        var panel = new HcsPanel((_, bound) => { maximum = bound; return Task.FromResult(ImmutableArray.Create<byte>(1)); });
        panel.Attach(bridge);
        var loading = panel.LoadSnapshotAsync("untrusted-profile.hcs");
        Assert.Equal(admission.TotalBytes, maximum);
        var command = Assert.IsType<HcsCommand>(queue.TakeNext().Command);
        Assert.Equal(HcsAction.Load, command.Action);
        bridge.Publish(new(command.RequestId, Error: "InvalidFormat: owner still validates"));
        await loading;
        Assert.Contains("InvalidFormat", panel.Notice);
        queue.RetireSnapshotRuntime(admission);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadStartedBeforeTabSwitchOrRetirementNeverDispatchesToANewRuntime(bool retire)
    {
        var oldQueue = new InputQueue(); var oldBridge = new HcsBridge(oldQueue);
        var nextQueue = new InputQueue(); var nextBridge = new HcsBridge(nextQueue);
        var read = new TaskCompletionSource<ImmutableArray<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new HcsPanel((_, _) => read.Task);
        panel.Attach(oldBridge);
        var pending = panel.LoadSnapshotAsync("selected-before-await.hcs");
        Assert.False(pending.IsCompleted);
        if (retire) panel.Disconnect(); else panel.Unbind();
        panel.Attach(nextBridge);
        read.SetResult([1]);
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, oldQueue.BoundaryCount);
        Assert.Equal(0, nextQueue.BoundaryCount);
        Assert.Contains("Stale snapshot", panel.Notice);
    }

    [AvaloniaFact]
    public async Task SameQueueNewRuntimeDuringReadAlsoInvalidatesTheCapturedAdmission()
    {
        var queue = new InputQueue(); var bridge = new HcsBridge(queue);
        var first = queue.BindSnapshotRuntime(HcsAdmissionPolicyTests.Prepare(true)); bridge.BindAdmission(queue, first);
        var read = new TaskCompletionSource<ImmutableArray<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new HcsPanel((_, _) => read.Task); panel.Attach(bridge);
        var pending = panel.LoadSnapshotAsync("old-runtime.hcs");
        queue.RetireSnapshotRuntime(first);
        var next = queue.BindSnapshotRuntime(HcsAdmissionPolicyTests.Prepare(false)); bridge.BindAdmission(queue, next);
        read.SetResult([1]); await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, queue.BoundaryCount);
        Assert.Contains("Stale snapshot", panel.Notice);
        queue.RetireSnapshotRuntime(next);
    }

    [AvaloniaFact]
    public async Task InlineLoadUsesItsListedRuntimeAdmissionRatherThanTheDefault()
    {
        var directory = Directory.CreateTempSubdirectory("hcs-inline-admission-");
        try
        {
            var path = Path.Combine(directory.FullName, "large.hcs");
            using (var file = File.Create(path)) file.SetLength(0x44000000);
            var queue = new InputQueue(); var bridge = new HcsBridge(queue);
            var admission = queue.BindSnapshotRuntime(HcsAdmissionPolicyTests.Prepare(true)); bridge.BindAdmission(queue, admission);
            int maximum = 0;
            var panel = new HcsPanel((_, limit) => { maximum = limit; return Task.FromResult(ImmutableArray.Create<byte>(1)); })
                { SnapshotDirectory = directory.FullName };
            panel.Attach(bridge);
            Assert.True(Assert.Single(panel.SnapshotListed).Selectable);
            var button = Assert.Single(panel.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Load"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(admission.TotalBytes, maximum);
            var command = Assert.IsType<HcsCommand>(queue.TakeNext().Command);
            bridge.Publish(new(command.RequestId, Error: "InvalidFormat"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (panel.Notice?.Contains("InvalidFormat", StringComparison.Ordinal) != true)
                await Task.Delay(1, deadline.Token);
            queue.RetireSnapshotRuntime(admission);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void InlineListingAndRevalidationUseExactDefaultOrAdoptedBoundaries()
    {
        var directory = Directory.CreateTempSubdirectory("hcs-lengths-");
        var path = Path.Combine(directory.FullName, "boundary.hcs");
        try
        {
            foreach (var limits in new[] { new HcsLimits(), HcsLimits.ForProfile(Hc2000.Machine.MachineProfile.Hc2000V2PagedExpansionV1) })
            {
                foreach (long length in new[] { limits.TotalBytes - 1L, limits.TotalBytes, limits.TotalBytes + 1L })
                {
                    using (var file = File.Create(path)) file.SetLength(length);
                    var row = Assert.Single(InlineMediaListing.Snapshots(directory.FullName, limits: limits));
                    Assert.Equal(length <= limits.TotalBytes, row.Selectable);
                    Assert.Equal(length <= limits.TotalBytes, InlineMediaListing.RevalidateSnapshot(path, length, limits) is null);
                    if (length > 268435456)
                        Assert.False(Assert.Single(InlineMediaListing.Snapshots(directory.FullName)).Selectable);
                }
            }
            using (var file = File.Create(path)) file.SetLength(0);
            using (var file = File.OpenWrite(path)) file.SetLength(1);
            Assert.Contains("changed", InlineMediaListing.RevalidateSnapshot(path, 0));
            Assert.Contains("changed", InlineMediaListing.RevalidateSnapshot(path, 2));
        }
        finally { directory.Delete(true); }
    }
}
