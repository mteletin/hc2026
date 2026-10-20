using System.Collections.Immutable;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddMediaAdapterTests
{
    private static BlockDiskPort Controller(bool dirty = true)
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var fdc = new FloppyController(new DeviceScheduler(), 100, new(new()), new(new()));
        var port = new BlockDiskPort(memory, fdc, hddEnabled: true);
        port.Restore(new(0, 0, [new(new byte[HddMediaAdapter.ImageLength], 1, false, dirty),
            new([], 0, false, false), new([], 0, false, false), new([], 0, false, false)]));
        return port;
    }

    [Fact]
    public void CaptureDoesNotClearDirtyAndAcknowledgementIsOneUse()
    {
        var port = Controller(); var adapter = new HddMediaAdapter(port);
        var captured = adapter.Apply(new(HddAction.CaptureSave));
        Assert.Null(captured.Error); Assert.NotNull(captured.Save);
        Assert.True(port.CaptureHdd(2).Dirty);
        Assert.Equal(HddMediaAdapter.ImageLength, captured.Save!.Bytes.Length);
        Assert.Null(adapter.Apply(new(HddAction.AcknowledgeSave, SaveId: captured.Save.Id)).Error);
        Assert.False(port.CaptureHdd(2).Dirty);
        Assert.NotNull(adapter.Apply(new(HddAction.AcknowledgeSave, SaveId: captured.Save.Id)).Error);
    }

    [Fact]
    public void FailedOrCancelledPersistenceNeverCleansDirty()
    {
        var port = Controller(); var adapter = new HddMediaAdapter(port);
        var captured = adapter.Apply(new(HddAction.CaptureSave)).Save!;
        Assert.Null(adapter.Apply(new(HddAction.CancelSave, SaveId: captured.Id)).Error);
        Assert.True(port.CaptureHdd(2).Dirty);
        Assert.NotNull(adapter.Apply(new(HddAction.AcknowledgeSave, SaveId: captured.Id)).Error);
        Assert.True(port.CaptureHdd(2).Dirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedBytesOrGenerationRefusesToClean(bool changedGeneration)
    {
        var port = Controller(); var adapter = new HddMediaAdapter(port);
        var saved = adapter.Apply(new(HddAction.CaptureSave)).Save!;
        var state = port.Capture();
        var slot = state.Slots[0];
        if (!changedGeneration) slot.Bytes[0] = 0x5a;
        state.Slots[0] = slot with { Generation = changedGeneration ? 2u : 1u, Dirty = true };
        port.Restore(state);
        Assert.NotNull(adapter.Apply(new(HddAction.AcknowledgeSave, SaveId: saved.Id)).Error);
        Assert.True(port.CaptureHdd(2).Dirty);
        Assert.Equal((byte)0, saved.Bytes[0]);
    }

    [Theory]
    [InlineData(HddAction.Attach)]
    [InlineData(HddAction.Detach)]
    public void LiveCommandsAlwaysRefusedEvenEmptyOrClean(HddAction action)
    {
        var port = Controller(dirty: false); var adapter = new HddMediaAdapter(port);
        foreach (byte drive in new byte[] { 2, 3, 4, 5 })
        {
            var before = port.CaptureHdd(drive);
            Assert.Equal(HddMediaAdapter.SwitchUnavailable, adapter.Apply(new(action, drive)).Error);
            var after = port.CaptureHdd(drive);
            Assert.Equal(before.Generation, after.Generation); Assert.Equal(before.Bytes, after.Bytes);
            Assert.Equal(before.WriteProtected, after.WriteProtected); Assert.Equal(before.Dirty, after.Dirty);
        }
    }

    [Fact]
    public void ResetRebindPreservesPendingSaveAndProtectionWithoutReattaching()
    {
        var port = Controller(); var state = port.Capture();
        state.Slots[0] = state.Slots[0] with { WriteProtected = true };
        port.Restore(state);
        var adapter = new HddMediaAdapter(port, [new(2, ImmutableArray<byte>.Empty, "C.raw")]);
        var saved = adapter.Apply(new(HddAction.CaptureSave)).Save!;
        var retained = port.Capture(); var replacement = Controller(false);
        replacement.Restore(retained); replacement.Reset(); adapter.Rebind(replacement);
        var view = adapter.Apply(new(HddAction.Inspect)).Drives[0];
        Assert.Equal("C.raw", view.Name); Assert.Equal(1u, view.Generation);
        Assert.True(view.Dirty); Assert.True(view.WriteProtected);
        Assert.Null(adapter.Apply(new(HddAction.AcknowledgeSave, SaveId: saved.Id)).Error);
        Assert.False(replacement.CaptureHdd(2).Dirty);
        Assert.True(replacement.CaptureHdd(2).WriteProtected);
    }

    [Fact]
    public void MissingProfileAndSharedSessionRefuseWithoutNewOwner()
    {
        var session = new MediaSession(new(new()), new(new()));
        var reply = session.Apply(new(MediaAction.Hdd, DiscardChanges: true, Hdd: new(HddAction.Attach)));
        Assert.NotNull(reply.Error); Assert.Empty(reply.Hdd!.Drives);
    }
}
