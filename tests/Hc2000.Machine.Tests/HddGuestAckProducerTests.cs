using System.Buffers.Binary;
using System.Security.Cryptography;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class HddGuestAckProducerTests
{
    private sealed class Rig
    {
        public HcMemory Memory { get; } = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        public BlockDiskPort Port { get; }
        public Rig(bool enabled = true)
        {
            var fdc = new FloppyController(new DeviceScheduler(), 100, new(new()), new(new()));
            Port = new(Memory, fdc, hddEnabled: true, guestAckEnabled: enabled);
        }
        public byte Command(byte operation, byte primary = 2, uint generation = 0, ushort dma = 0x8200, uint lba = 0)
        {
            var command = new byte[16];
            command[0] = 2; command[1] = operation; command[2] = primary;
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), lba);
            BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(8), dma);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), generation);
            for (var i = 0; i < 16; i++) Memory.Write((ushort)(0x8000 + i), command[i]);
            Port.Write(0x81, 0, 0); Port.Write(0x83, 0x80, 0); Port.Write(0x89, 1, 0);
            return Port.Read(0x89, 0);
        }
        public byte[] Dma() => Enumerable.Range(0, 512).Select(i => Memory.Read((ushort)(0x8200 + i))).ToArray();
    }

    [Fact]
    public void SuccessorProfileIsIsolatedAndRequiresMatchingPortCapabilities()
    {
        var profile = MachineProfile.Hc2000V2HddGuestV1;
        profile.Validate();
        Assert.Equal("hc2000-v2-hdd-guest-v1", profile.Name);
        Assert.Throws<ArgumentException>(() => (profile with { BlockDisk = BlockDiskPolicy.HddV1 }).Validate());
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2HddV1 with { BlockDisk = BlockDiskPolicy.HddGuestV1 }).Validate());
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2 with { BlockDisk = BlockDiskPolicy.HddGuestV1 }).Validate());
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2PagedMTransferV1 with { BlockDisk = BlockDiskPolicy.HddGuestV1 }).Validate());
        var rig = new Rig();
        var idle = new Hc2000.Machine.Io.PortDevice();
        var mappings = Hc2000.Machine.Io.HcIoProfile.WithBlockDisk(profile, rig.Memory, idle, idle, idle, idle, idle, idle, rig.Port);
        Assert.Equal(3, mappings.Count(m => ReferenceEquals(m.Device, rig.Port)));
        Assert.Throws<ArgumentException>(() => Hc2000.Machine.Io.HcIoProfile.WithBlockDisk(
            MachineProfile.Hc2000V2HddV1, rig.Memory, idle, idle, idle, idle, idle, idle, rig.Port));
    }

    [Fact]
    public void QueryAndAckAreExplicitlyOptIn()
    {
        var old = new Rig(false);
        Assert.Equal(1, old.Command(4)); Assert.Equal(1, old.Command(5));
        Assert.Throws<InvalidOperationException>(() => old.Port.BindGuestAckSession(Guid.NewGuid()));
        var rig = new Rig();
        Assert.Equal(0, rig.Command(4));
        var idle = new byte[512]; idle[0] = 1;
        Assert.Equal(idle, rig.Dma());
    }

    [Fact]
    public void ActualCommitRemainsHeldUntilSameTicketFinishAndTerminalDrain()
    {
        var rig = new Rig();
        var session = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        rig.Port.BindGuestAckSession(session);
        rig.Port.AttachHdd(2, new byte[8_388_608]);
        var request = rig.Port.BeginGuestSwitch(2, 60);
        Assert.Equal(0, rig.Command(4));
        var challenge = rig.Dma();
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), challenge[16..32]);
        Assert.Equal(request.RequestId, BinaryPrimitives.ReadUInt64LittleEndian(challenge.AsSpan(8)));
        Assert.Equal(0, rig.Command(5, generation: 1));
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        Assert.Throws<InvalidOperationException>(() => rig.Port.FinishGuestSwitch(ticket));
        Assert.Throws<InvalidOperationException>(() => rig.Port.DetachHdd(2));
        Assert.Equal(0x0e, rig.Command(3));
        var replacement = new byte[8_388_608]; replacement[0] = 0x73;
        Assert.True(rig.Port.CommitGuestSwitch(ticket, replacement, false, false).Committed);
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal(0x73, rig.Port.CaptureHdd(2).Bytes[0]);
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelGuestSwitch(ticket));
        Assert.Throws<InvalidOperationException>(() => rig.Port.CommitGuestSwitch(ticket, replacement, false, false));
        rig.Port.FinishGuestSwitch(ticket);
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Throws<InvalidOperationException>(() => rig.Port.FinishGuestSwitch(ticket));
        Assert.Throws<InvalidOperationException>(() => rig.Port.BeginGuestSwitch(2, 4));
        Assert.Equal(0, rig.Command(4)); Assert.Equal(3, rig.Dma()[1]);
        Assert.Equal(0, rig.Command(4)); Assert.Equal(0, rig.Dma()[1]);
        Assert.Equal(0u, rig.Port.CaptureHdd(3).Generation);
    }

    [Fact]
    public void PendingCancelDoesNotReleaseAnUnrelatedHeldRequest()
    {
        var rig = new Rig(); var session = Guid.NewGuid();
        rig.Port.BindGuestAckSession(session);
        var pending = rig.Port.BeginGuestSwitch(2, 4);
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelPendingGuestSwitch(Guid.NewGuid(), pending.RequestId));
        rig.Port.CancelPendingGuestSwitch(session, pending.RequestId);
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal(0, rig.Command(4)); Assert.Equal(4, rig.Dma()[1]);
        var next = rig.Port.BeginGuestSwitch(2, 4);
        Assert.True(next.RequestId > pending.RequestId);
        Assert.Equal(0, rig.Command(4)); Assert.Equal(0, rig.Command(5));
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelPendingGuestSwitch(session, next.RequestId));
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        rig.Port.CancelGuestSwitch(ticket);
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        rig.Port.FinishGuestSwitch(ticket);
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal(0x0e, rig.Port.Read(0x89, 0));
    }

    [Fact]
    public void FailedCommitIsAtomicAndTicketIsControllerBound()
    {
        var rig = new Rig(); rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        Assert.Equal(0, rig.Command(4)); Assert.Equal(0, rig.Command(5));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        Assert.False(rig.Port.CommitGuestSwitch(ticket, new byte[1], false, false).Committed);
        Assert.False(rig.Port.CaptureHdd(2).Present);
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        var other = new Rig(); other.Port.BindGuestAckSession(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => other.Port.CancelGuestSwitch(ticket));
        rig.Port.CancelGuestSwitch(ticket); rig.Port.FinishGuestSwitch(ticket);
    }

    // Same pinned private P4 literal volume, passed through the existing codec/selector, not a new format producer.
    private static HddPreparedSelection SelectedVolume()
    {
        var index = Convert.FromHexString("4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce");
        var footer = Convert.FromHexString("4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7");
        var descriptor = HddCatalogCodec.DecodeHdd(footer, index, 16_777_340, new(16_777_340, 2, 60, 624)).Value!;
        var store = new HddStoreToken([.. descriptor.Footer.ContainerId], descriptor.Footer.Revision, [.. SHA256.HashData([.. index, .. footer])]);
        var image = new byte[8_388_608]; image.AsSpan(0, 32768).Fill(0xe5);
        Convert.FromHexString("0053454544202020205458540000000108000000000000000000000000000000").CopyTo(image, 0);
        image.AsSpan(32768, 4096).Fill(0x41);
        var hash = Convert.FromHexString("9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F");
        Assert.Equal(hash, SHA256.HashData(image));
        var result = HddVolumeSelection.Prepare(descriptor, store, new(store, "VOL1.DSK", [.. hash]), image, false,
            new(16_777_340, 2, 60, 624, 8_388_608, 33_554_432, 0, 25_231_360, 58_786_416, 1, 1),
            new(1, 33_554_432, 0, 65536), []);
        Assert.True(result.Success, result.Detail);
        return result.Selection!;
    }

    [Theory]
    [InlineData(0)] // Requested
    [InlineData(1)] // Held
    [InlineData(2)] // CommittedHeld
    [InlineData(3)] // CancelledHeld
    [InlineData(4)] // Undrained Committed
    [InlineData(5)] // Undrained Cancelled
    [InlineData(6)] // Pending cancellation, no hold
    public void WholeControllerCaptureAndRestoreRefuseEveryUndrainedCorrelation(int phase)
    {
        var rig = new Rig(); rig.Port.AttachHdd(2, new byte[8_388_608]);
        var before = rig.Port.Capture();
        var session = Guid.NewGuid(); rig.Port.BindGuestAckSession(session);
        var request = rig.Port.BeginGuestSwitch(2, 4);
        if (phase == 6) rig.Port.CancelPendingGuestSwitch(session, request.RequestId);
        else if (phase > 0)
        {
            Assert.Equal(0, rig.Command(4)); Assert.Equal(0, rig.Command(5, generation: 1));
            Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
            if (phase is 2 or 4) Assert.True(rig.Port.DetachGuestSwitch(ticket, false).Committed);
            if (phase is 3 or 5) rig.Port.CancelGuestSwitch(ticket);
            if (phase is 4 or 5) rig.Port.FinishGuestSwitch(ticket);
        }
        var actual = rig.Port.CaptureHdd(2); // Durable slot capture remains distinct and allowed.
        Assert.Throws<InvalidOperationException>(() => rig.Port.Capture());
        Assert.Throws<InvalidOperationException>(() => rig.Port.Restore(before));
        Assert.Throws<InvalidOperationException>(() => rig.Port.Restore(null));
        Assert.Equal(actual.Bytes, rig.Port.CaptureHdd(2).Bytes);
        Assert.Equal(actual.Generation, rig.Port.CaptureHdd(2).Generation);
        var selected = SelectedVolume();
        var pointer = rig.Port.Read(0x81, 0); var status = rig.Port.Read(0x89, 0);
        foreach (var unit in new byte[] { 2, 3, 4, 5 })
        {
            var slot = rig.Port.CaptureHdd(unit);
            var refused = rig.Port.TryCaptureSelectedHdd(unit, selected, slot, true);
            Assert.Equal(12, (int)refused.Error); // Architecture-selected-capture-refusal-632004, additive value.
            Assert.False(refused.Success);
            Assert.Equal(slot.Bytes, rig.Port.CaptureHdd(unit).Bytes);
            Assert.Equal(slot.Generation, rig.Port.CaptureHdd(unit).Generation);
            Assert.Equal(slot.Dirty, rig.Port.CaptureHdd(unit).Dirty);
            Assert.Equal(slot.WriteProtected, rig.Port.CaptureHdd(unit).WriteProtected);
        }
        Assert.Equal(12, (int)rig.Port.TryCaptureSelectedHdd(2, null!, null!, false).Error);
        Assert.Equal(HddSelectedCaptureError.InvalidUnit, rig.Port.TryCaptureSelectedHdd(1, null!, null!, false).Error);
        Assert.Equal(pointer, rig.Port.Read(0x81, 0)); Assert.Equal(status, rig.Port.Read(0x89, 0));
        if (phase is 4 or 5 or 6)
        {
            Assert.Equal(0, rig.Command(4));
            Assert.NotNull(rig.Port.Capture());
            Assert.True(rig.Port.TryCaptureSelectedHdd(2, selected, rig.Port.CaptureHdd(2), true).Success);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulRestoreRetiresBindingAndCannotEnableRestoredGuest(bool empty)
    {
        var rig = new Rig(); rig.Port.AttachHdd(2, new byte[8_388_608]);
        var snapshot = rig.Port.Capture();
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.Restore(empty ? null : snapshot);
        Assert.Throws<InvalidOperationException>(() => rig.Port.BeginGuestSwitch(2, 4));
        Assert.Throws<InvalidOperationException>(() => rig.Port.BindGuestAckSession(Guid.NewGuid()));
        rig.Port.Reset();
        Assert.Throws<InvalidOperationException>(() => rig.Port.BindGuestAckSession(Guid.NewGuid()));
        Assert.Equal(0, rig.Command(4)); Assert.Equal(0, rig.Dma()[1]);
    }

    [Fact]
    public void FailedRestorePreservesLiveBindingAndResetRetiresIt()
    {
        var rig = new Rig(); rig.Port.BindGuestAckSession(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => rig.Port.Restore(new(0, 0, [])));
        var pending = rig.Port.BeginGuestSwitch(2, 4);
        rig.Port.CancelPendingGuestSwitch(pending.SessionNonce, pending.RequestId);
        Assert.Equal(0, rig.Command(4));
        rig.Port.Reset();
        Assert.Throws<InvalidOperationException>(() => rig.Port.BeginGuestSwitch(2, 4));
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        Assert.NotNull(rig.Port.BeginGuestSwitch(2, 4));
    }

    [Fact]
    public void GuestWritesAfterBeginRequireFreshDirtyConsentAndCancelDoesNotUndoThem()
    {
        var rig = new Rig(); rig.Port.AttachHdd(2, new byte[8_388_608]);
        rig.Port.BindGuestAckSession(Guid.NewGuid()); rig.Port.BeginGuestSwitch(2, 4);
        rig.Memory.Write(0x8200, 0x76);
        Assert.Equal(0, rig.Command(2, generation: 1)); // Real old-generation guest flush.
        Assert.Equal(0, rig.Command(4)); Assert.Equal(0, rig.Command(5, generation: 1));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        Assert.False(rig.Port.CommitGuestSwitch(ticket, new byte[8_388_608], false, false).Committed);
        var retained = rig.Port.CaptureHdd(2);
        Assert.True(retained.Dirty); Assert.Equal(1u, retained.Generation); Assert.Equal(0x76, retained.Bytes[0]);
        rig.Port.CancelGuestSwitch(ticket); rig.Port.FinishGuestSwitch(ticket);
        retained = rig.Port.CaptureHdd(2);
        Assert.True(retained.Dirty); Assert.Equal(1u, retained.Generation); Assert.Equal(0x76, retained.Bytes[0]);
    }

    [Fact]
    public void DetachUsesActualSlotRulesAndKeepsHoldUntilFinish()
    {
        var rig = new Rig(); rig.Port.AttachHdd(3, new byte[8_388_608], writeProtected: true);
        rig.Port.BindGuestAckSession(Guid.NewGuid()); rig.Port.BeginGuestSwitch(3, 60);
        Assert.Equal(0, rig.Command(4, primary: 3));
        Assert.Equal(0, rig.Command(5, primary: 3, generation: 1));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        var result = rig.Port.DetachGuestSwitch(ticket, false);
        Assert.True(result.Committed); Assert.Equal(1u, result.Generation);
        Assert.False(rig.Port.CaptureHdd(3).Present); Assert.True(rig.Port.GuestSwitchHoldsCpu);
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelGuestSwitch(ticket));
        rig.Port.FinishGuestSwitch(ticket);
        Assert.Equal(0, rig.Command(4, primary: 3)); Assert.Equal(3, rig.Dma()[1]);
    }

    [Fact]
    public void OwnerThreadAndBusyCorrelationAreEnforced()
    {
        var rig = new Rig(); var session = Guid.NewGuid();
        rig.Port.BindGuestAckSession(session);
        var request = rig.Port.BeginGuestSwitch(2, 4);
        Assert.Throws<InvalidOperationException>(() => rig.Port.BeginGuestSwitch(3, 8));
        Assert.Throws<InvalidOperationException>(() => rig.Port.BindGuestAckSession(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => rig.Port.Reset());
        Exception? error = null;
        var thread = new Thread(() => { try { rig.Port.CancelPendingGuestSwitch(session, request.RequestId); } catch (Exception e) { error = e; } });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, rig.Command(4)); Assert.Equal(0, rig.Command(5));
        // A valid older floppy descriptor cannot bypass a held CPU either.
        for (var i = 0; i < 8; i++) rig.Memory.Write((ushort)(0x8000 + i), 0);
        rig.Memory.Write(0x8000, 1);
        rig.Port.Write(0x89, 1, 0);
        Assert.Equal(0x0e, rig.Port.Read(0x89, 0));
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
    }

    [Fact]
    public void MalformedAndForeignAckLeaveDmaAndPendingRequestUnchanged()
    {
        var rig = new Rig(); rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        Assert.Equal(0, rig.Command(4));
        var challenge = rig.Dma();
        Assert.Equal(0x09, rig.Command(5, dma: 0xff00));
        Assert.Equal(challenge, rig.Dma());
        rig.Memory.Write(0x8230, 1);
        var malformed = rig.Dma();
        Assert.Equal(0x02, rig.Command(5)); Assert.Equal(malformed, rig.Dma());
        rig.Memory.Write(0x8230, 0); rig.Memory.Write(0x8210, 0);
        // Change the request sequence rather than relying on any random nonce byte.
        rig.Memory.Write(0x8208, 99);
        var foreign = rig.Dma();
        Assert.Equal(0x0e, rig.Command(5)); Assert.Equal(foreign, rig.Dma());
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal(0, rig.Command(4)); Assert.Equal(challenge, rig.Dma());
    }
}
