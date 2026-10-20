using System.Buffers.Binary;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.App.Tests;

// App-owner seams only: no HCS format or guest close/flush execution is claimed here.
public sealed class HddRestoredMapTests
{
    private sealed class Rig
    {
        internal readonly HcMemory Memory = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        internal readonly BlockDiskPort Port;
        internal Rig()
        {
            Port = new(Memory, new FloppyController(new DeviceScheduler(), 100, new(new()), new(new())),
                hddEnabled: true, guestAckEnabled: true);
            Port.Restore(null);
        }
        internal byte Command(byte operation)
        {
            Span<byte> descriptor = stackalloc byte[16]; descriptor.Clear();
            descriptor[0] = 2; descriptor[1] = operation; descriptor[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[8..], 0x8200);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], Port.CaptureHdd(2).Generation);
            for (int i = 0; i < descriptor.Length; i++) Memory.Write((ushort)(0x8000 + i), descriptor[i]);
            Port.Write(0x81, 0, 0); Port.Write(0x83, 0x80, 0); Port.Write(0x89, 1, 0);
            return Port.Read(0x89, 0);
        }
        internal void Acknowledge()
        {
            Assert.Equal(0, Command(4));
            Assert.Equal(0, Command(5));
            Assert.True(Port.GuestSwitchHoldsCpu);
        }
    }

    [Fact]
    public void ColdRestoreResolvesCurrentOperatorMapWithoutSavedIdentityOrPreparedBytes()
    {
        using var source = new HddColdVolumeTests();
        var rig = new Rig();
        using (var old = HddColdVolume.Open(source.PathName, "VOL1.DSK"))
            Assert.Equal(0x41, old.Media(2).Bytes[32768]);
        var before = File.ReadAllBytes(source.PathName);
        var adapter = HddMediaAdapter.RearmRestoredMap(rig.Port, source.PathName, [(2, "VOL2.DSK"), (3, "VOL1.DSK")]);
        var view = adapter.Inspect();
        Assert.Equal("VOL2.DSK", view[0].Name); Assert.Equal("VOL1.DSK", view[1].Name);
        Assert.Equal(1u, view[0].Generation); Assert.Equal(1u, view[1].Generation);
        Assert.Equal(0x42, rig.Port.CaptureHdd(2).Bytes[32768]);
        Assert.Equal(0x41, rig.Port.CaptureHdd(3).Bytes[32768]);
        Assert.True(rig.Port.Uncooperated);
        Assert.Throws<InvalidOperationException>(adapter.ValidateSessionSnapshotSave);
        Assert.Equal(before, File.ReadAllBytes(source.PathName));
    }

    [Fact]
    public void SameRestoredAppRequestWaitsForAckThenCommitsAndAccountsBeforeRelease()
    {
        using var source = new HddColdVolumeTests(); var rig = new Rig();
        var adapter = HddMediaAdapter.RearmRestoredMap(rig.Port, source.PathName, [(2, "VOL1.DSK")]);
        HddReply begun;
        using (var target = HddColdVolume.Open(source.PathName, "VOL2.DSK"))
            begun = adapter.Apply(new(HddAction.BeginSwitch, Target: new(source.PathName, target.Identity), ExpectedGeneration: 1));
        Assert.Null(begun.Error); Assert.Equal(HddSwitchState.AwaitingGuest, begun.Switch?.State);
        adapter.PumpGuestSwitch();
        Assert.True(rig.Port.Uncooperated); Assert.Equal(1u, rig.Port.CaptureHdd(2).Generation);
        Assert.Equal(0x41, rig.Port.CaptureHdd(2).Bytes[32768]);
        Assert.Throws<InvalidOperationException>(adapter.ValidateSessionSnapshotSave);
        rig.Acknowledge();
        Assert.False(rig.Port.Uncooperated);
        Assert.Throws<InvalidOperationException>(adapter.ValidateSessionSnapshotSave); // Held is not snapshot-safe.
        adapter.PumpGuestSwitch();
        var committed = adapter.Apply(new(HddAction.Inspect));
        Assert.Equal(begun.Switch?.Id, committed.Switch?.Id);
        Assert.Null(committed.Switch?.Error);
        Assert.Equal(HddSwitchState.Committed, committed.Switch?.State);
        Assert.Equal("VOL2.DSK", committed.Drives[0].Name); Assert.Equal(2u, committed.Drives[0].Generation);
        Assert.Equal(0x42, rig.Port.CaptureHdd(2).Bytes[32768]);
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        adapter.ValidateSessionSnapshotSave();
        adapter.PumpGuestSwitch(); adapter.Inspect();
        adapter.ValidateSessionSnapshotSave(); // Paused pumping and inspection consume no guest instruction.
        rig.Port.NotifyGuestInstruction();
        Assert.Throws<InvalidOperationException>(adapter.ValidateSessionSnapshotSave);
    }

    [Fact]
    public void DirtySnapshotPreflightNeverBlocksMediaCaptureOrDurableSaveAcknowledgement()
    {
        var rig = new Rig(); rig.Port.AttachHdd(2, new byte[HddMediaAdapter.ImageLength]);
        rig.Port.RearmRestoredGuestAckSession(Guid.NewGuid());
        var adapter = new HddMediaAdapter(rig.Port);
        rig.Memory.Write(0x8200, 0x5a); Assert.Equal(0, rig.Command(2));
        Assert.Contains("C:", Assert.Throws<InvalidOperationException>(adapter.ValidateSessionSnapshotSave).Message);
        var capture = adapter.Apply(new(HddAction.CaptureSave));
        Assert.Null(capture.Error); Assert.Equal(0x5a, capture.Save!.Bytes[0]);
        Assert.True(rig.Port.CaptureHdd(2).Dirty); Assert.True(rig.Port.Uncooperated);
        Assert.Null(adapter.Apply(new(HddAction.AcknowledgeSave, SaveId: capture.Save.Id)).Error);
        Assert.False(rig.Port.CaptureHdd(2).Dirty);
        Assert.True(rig.Port.Uncooperated);
        Assert.Throws<InvalidOperationException>(adapter.ValidateSessionSnapshotSave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetOrFreshRequestAfterColdRearmNeverAdmitsCommitWithoutCooperation(bool reset)
    {
        using var source = new HddColdVolumeTests(); var rig = new Rig();
        var adapter = HddMediaAdapter.RearmRestoredMap(rig.Port, source.PathName, [(2, "VOL1.DSK")]);
        if (reset) rig.Port.Reset();
        adapter.Rebind(rig.Port); // Retain the slots, but stage an ordinary fresh ACK session.
        HddReply begun;
        using (var target = HddColdVolume.Open(source.PathName, "VOL2.DSK"))
            begun = adapter.Apply(new(HddAction.BeginSwitch, Target: new(source.PathName, target.Identity), ExpectedGeneration: 1));
        Assert.Null(begun.Error); Assert.Equal(HddSwitchState.AwaitingGuest, begun.Switch?.State);
        Assert.True(rig.Port.Uncooperated);
        Assert.Contains("Uncooperated", Assert.Throws<InvalidOperationException>(() =>
            rig.Port.CommitGuestSwitch(null!, new byte[HddMediaAdapter.ImageLength], false, false)).Message);
        Assert.Contains("Uncooperated", Assert.Throws<InvalidOperationException>(() =>
            rig.Port.DetachGuestSwitch(null!, false)).Message);
        Assert.Throws<InvalidOperationException>(adapter.ValidateSessionSnapshotSave);
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal(1u, rig.Port.CaptureHdd(2).Generation);
        Assert.Equal(0x41, rig.Port.CaptureHdd(2).Bytes[32768]);
    }

    [Fact]
    public void FailedColdResolutionIsNonmutatingAndNoOrdinaryBindCanRequalifyIt()
    {
        using var source = new HddColdVolumeTests(); var rig = new Rig();
        Assert.Throws<InvalidDataException>(() => HddMediaAdapter.RearmRestoredMap(rig.Port, source.PathName, [(2, "MISSING.DSK")]));
        Assert.All(rig.Port.Capture().Slots, slot => Assert.False(slot.Present));
        Assert.Throws<InvalidOperationException>(() => rig.Port.BindGuestAckSession(Guid.NewGuid()));
        HddMediaAdapter.RearmRestoredMap(rig.Port, source.PathName, [(2, "VOL1.DSK")]);
        Assert.Throws<InvalidOperationException>(() => HddMediaAdapter.RearmRestoredMap(rig.Port, source.PathName, [(2, "VOL1.DSK")]));
    }
}
