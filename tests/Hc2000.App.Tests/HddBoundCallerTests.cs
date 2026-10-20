using System.Collections.Immutable;
using Avalonia.Headless.XUnit;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.Machine;
using Xunit;

namespace Hc2000.App.Tests;

[Collection("HDD bound publication")]
public sealed class HddBoundCallerTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedWritabilityIsExplicitAndNeverOverridesCatalogReadOnly(bool writable)
    {
        using var source = new HddColdVolumeTests();
        var panel = new HddPanel();
        var calls = 0;
        panel.ConfigureCold(_ => Task.CompletedTask, bootMap: volume =>
        {
            var media = volume.MappedMedia;
            Assert.Equal(!writable, media[0].WriteProtected);
            Assert.True(media[1].WriteProtected);
            Assert.Equal(writable, media[0].Binding!.Writable);
            Assert.False(media[1].Binding!.Writable);
            calls++;
            return Task.CompletedTask;
        });
        await panel.PrepareVolumeAsync(source.PathName, "VOL1.DSK", 2, writable);
        await panel.PrepareVolumeAsync(source.PathName, "VOL2.DSK", 3, writable);
        Assert.Contains(writable ? "writable (opt-in)" : "read-only", panel.HddListed[0].Detail);
        Assert.Contains("read-only", panel.HddListed[1].Detail);
        await panel.StartColdAsync();
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public async Task LostBeginReplyKeepsCloseRefusedAndRecoveryUsesSameOwnerCapture()
    {
        var panel = new HddPanel();
        var ops = new List<WritebackOwnerOp>();
        HddSaveCapture capture = new(7, 2, 1, []);
        var hdd = new HddReply([new(2, "VOL1.DSK", true, 1, false, false)]);
        panel.Attach(command =>
        {
            if (command.Action == MediaAction.Hdd) return Task.FromResult(Reply(hdd: hdd));
            Assert.Equal(MediaAction.Writeback, command.Action);
            var request = Assert.IsType<WritebackOwnerRequest>(command.Writeback);
            ops.Add(request.Op);
            if (request.Op == WritebackOwnerOp.BeginHdd) throw new IOException("lost Begin reply");
            if (request.Op == WritebackOwnerOp.InspectHdd)
                return Task.FromResult(Reply(owner: new(true, true, HddSave: capture)));
            Assert.Equal(WritebackOwnerOp.ReconcileHdd, request.Op);
            Assert.Equal(capture.Id, request.HddSaveId);
            return Task.FromResult(Reply(owner: new(true, false, HddPublication: new(capture.Id, true, false))));
        });
        panel.Observe(hdd);
        await Assert.ThrowsAsync<IOException>(() => panel.SaveBoundAsync(2));
        Assert.False(panel.CanCloseSafely);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.SaveBoundAsync(2));
        await panel.ReconcileBoundSaveAsync();
        Assert.True(panel.CanCloseSafely);
        Assert.Equal(new[] { WritebackOwnerOp.BeginHdd, WritebackOwnerOp.InspectHdd, WritebackOwnerOp.ReconcileHdd }, ops);
        Assert.Contains("does not prove a guest deblock flush", panel.Notice);
    }

    [AvaloniaFact]
    public async Task UnknownPublicationNeverTurnsIntoCleanOrAbortOnTransportError()
    {
        var panel = new HddPanel();
        HddSaveCapture capture = new(9, 2, 1, []);
        var hdd = new HddReply([new(2, "VOL1.DSK", true, 1, false, true)]);
        panel.Attach(command =>
        {
            if (command.Action == MediaAction.Hdd) return Task.FromResult(Reply(hdd: hdd));
            var request = Assert.IsType<WritebackOwnerRequest>(command.Writeback);
            return Task.FromResult(Reply(owner: request.Op switch
            {
                WritebackOwnerOp.BeginHdd or WritebackOwnerOp.InspectHdd => new(true, true, HddSave: capture),
                WritebackOwnerOp.PublishHdd or WritebackOwnerOp.ReconcileHdd => new(false, true, Error: "unknown", HddPublication: new(capture.Id, false, true)),
                WritebackOwnerOp.AbortHdd => new(false, true, Error: "reconcile the same capture"),
                _ => throw new InvalidOperationException("Unexpected op")
            }));
        });
        panel.Observe(hdd);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.SaveBoundAsync(2));
        Assert.False(panel.CanCloseSafely);
        await Assert.ThrowsAsync<InvalidOperationException>(panel.AbortBoundSaveAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(panel.ReconcileBoundSaveAsync);
        Assert.False(panel.CanCloseSafely);
        Assert.Contains("dirty", Assert.Single(panel.HddListed).Detail);
    }

    [Fact]
    public async Task ComposedOwnerAllowsRawCaptureButRefusesAcknowledgementAndLifecycleChangesUnderFence()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = new HddColdVolumeTests();
        using var selected = HddColdVolume.OpenMapped(source.PathName, [new(2, "VOL1.DSK", Writable: true)]);
        var roms = (MachineProfile.Hc2000V2, new byte[16384], new byte[16384], new byte[16384]);
        var prepared = BasicMachineComposition.PrepareAdmittedHddMapWholeChipV2(roms, selected);
        var queue = new InputQueue();
        queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
        var bridge = new MediaBridge(queue);
        var ledger = new DiskWritebackOwnerLedger();
        using var worker = BasicMachineComposition.CreateWorker(prepared, queue, bridge, null, false, ledger);
        Task<MediaReply> Send(MediaCommand command) => bridge.RequestAsync(command).WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var begun = Assert.IsType<WritebackOwnerReply>((await Send(new(MediaAction.Writeback,
                Writeback: new(WritebackOwnerOp.BeginHdd)))).Writeback);
            Assert.True(begun.Accepted, begun.Error);
            var original = Assert.IsType<HddSaveCapture>(begun.HddSave);
            var inspected = Assert.IsType<WritebackOwnerReply>((await Send(new(MediaAction.Writeback,
                Writeback: new(WritebackOwnerOp.InspectHdd)))).Writeback);
            Assert.Equal(original.Id, inspected.HddSave?.Id); // Lost Begin recovery does not create another lease.
            var raw = await Send(new(MediaAction.Hdd, Hdd: new(HddAction.CaptureSave)));
            Assert.Null(raw.Error); Assert.Equal(original.Bytes, raw.Hdd!.Save!.Bytes);
            Assert.NotNull((await Send(new(MediaAction.Hdd, Hdd: new(HddAction.AcknowledgeSave, SaveId: raw.Hdd.Save.Id)))).Error);
            Assert.NotNull((await Send(new(MediaAction.WriteProtect))).Error);
            queue.Enqueue(new RuntimeControl(InputControlKind.Reset));
            var view = (await Send(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect)))).Hdd!;
            Assert.Equal(original.Generation, view.Drives.Single(d => d.Drive == 2).Generation);
            Assert.True(ledger.Frozen); Assert.False(ledger.ShutdownPermitted);
            var published = (await Send(new(MediaAction.Writeback,
                Writeback: new(WritebackOwnerOp.PublishHdd, HddSaveId: original.Id)))).Writeback!;
            Assert.True(published.Accepted, published.Error); Assert.False(published.SessionFrozen);
            Assert.True(published.HddPublication?.Acknowledged);
        }
        finally { worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(20)); }
    }

    private static MediaReply Reply(HddReply? hdd = null, WritebackOwnerReply? owner = null) =>
        new(0, new(null, false, false, 0, false, 0), new(null, false, false, 0, false, 0), Hdd: hdd, Writeback: owner);
}
