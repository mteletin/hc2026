using System.Buffers.Binary;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

// Native wire receiver evidence, not proof of BDOS close/flush execution.
public sealed class HddRestoredRemapTests
{
    private const int ImageBytes = 8_388_608;
    private sealed class Rig
    {
        public HcMemory Memory { get; } = new(new(), new byte[16384], new byte[16384], new byte[16384]);
        public BlockDiskPort Port { get; }
        public Rig() => Port = new(Memory,
            new(new DeviceScheduler(), 100, new(new()), new(new())), hddEnabled: true, guestAckEnabled: true);

        public byte Command(byte operation, uint generation = 1)
        {
            Span<byte> descriptor = stackalloc byte[16]; descriptor.Clear();
            descriptor[0] = 2; descriptor[1] = operation; descriptor[2] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor[8..], 0x8200);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], generation);
            for (int i = 0; i < descriptor.Length; i++) Memory.Write((ushort)(0x8000 + i), descriptor[i]);
            Port.Write(0x81, 0, 0); Port.Write(0x83, 0x80, 0); Port.Write(0x89, 1, 0);
            return Port.Read(0x89, 0);
        }

        public byte[] Query()
        {
            Assert.Equal(0, Command(4));
            return Enumerable.Range(0, 512).Select(i => Memory.Read((ushort)(0x8200 + i))).ToArray();
        }

        public BlockDiskPort.GuestSwitchTicket Ack(byte[]? block = null)
        {
            block ??= Query();
            for (int i = 0; i < block.Length; i++) Memory.Write((ushort)(0x8200 + i), block[i]);
            Assert.Equal(0, Command(5, Port.CaptureHdd(2).Generation));
            Assert.True(Port.TryGetHeldGuestSwitch(out var ticket));
            return ticket;
        }

        public void ColdRestore()
        {
            Port.Restore(null);
            Port.AttachHdd(2, new byte[ImageBytes]);
            Port.RearmRestoredGuestAckSession(Guid.NewGuid());
        }

        public void CompleteCooperation()
        {
            Port.BeginGuestSwitch(2, 4);
            var ticket = Ack();
            Port.CancelGuestSwitch(ticket);
            Port.FinishGuestSwitch(ticket);
        }
    }

    [Fact]
    public void T5a_UncooperatedRefusesMutationButSameStagedRequestCanAckAndCommit()
    {
        var rig = new Rig(); rig.ColdRestore();
        Assert.True(rig.Port.Uncooperated);
        var request = rig.Port.BeginGuestSwitch(2, 4);
        Assert.Contains("Uncooperated", Assert.Throws<InvalidOperationException>(() =>
            rig.Port.CommitGuestSwitch(null!, new byte[ImageBytes], false, false)).Message);
        Assert.Contains("Uncooperated", Assert.Throws<InvalidOperationException>(() =>
            rig.Port.DetachGuestSwitch(null!, false)).Message);
        Assert.Equal(1u, rig.Port.CaptureHdd(2).Generation);
        var challenge = rig.Query();
        Assert.Equal(request.SessionNonce, new Guid(challenge.AsSpan(16, 16), bigEndian: true));
        var ticket = rig.Ack(challenge);
        Assert.False(rig.Port.Uncooperated);
        Assert.True(rig.Port.CommitGuestSwitch(ticket, new byte[ImageBytes], false, false).Committed);
        rig.Port.FinishGuestSwitch(ticket);
        Assert.Equal(2u, rig.Port.CaptureHdd(2).Generation);
    }

    [Fact]
    public void T5b_PreSaveCooperationAndOldNonceGrantNothingAfterColdRestore()
    {
        var rig = new Rig(); rig.Port.AttachHdd(2, new byte[ImageBytes]);
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        var oldChallenge = rig.Query(); var ticket = rig.Ack(oldChallenge);
        rig.Port.CancelGuestSwitch(ticket); rig.Port.FinishGuestSwitch(ticket);
        Assert.False(rig.Port.Uncooperated);
        Assert.False(rig.Port.CaptureHdd(2).Dirty);
        rig.Port.ValidateSessionSnapshotSave(); // This pre-save authority must not survive restore.
        rig.Port.InvalidateGuestAckSession();
        rig.ColdRestore();
        Assert.True(rig.Port.Uncooperated);
        Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave);
        rig.Port.BeginGuestSwitch(2, 4);
        for (int i = 0; i < oldChallenge.Length; i++) rig.Memory.Write((ushort)(0x8200 + i), oldChallenge[i]);
        Assert.Equal(0x0e, rig.Command(5));
        Assert.False(rig.Port.TryGetHeldGuestSwitch(out _));
        Assert.True(rig.Port.Uncooperated);
        ticket = rig.Ack();
        Assert.True(rig.Port.DetachGuestSwitch(ticket, false).Committed);
    }

    [Fact]
    public void T1a_DirtySnapshotRefusalNamesDriveButMediaRemainsExportable()
    {
        var rig = new Rig(); rig.ColdRestore();
        rig.Memory.Write(0x8200, 0x5a);
        Assert.Equal(0, rig.Command(2));
        Assert.Contains("C:", Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave).Message);
        Assert.True(rig.Port.CaptureHdd(2).Dirty);
        Assert.Equal(0x5a, rig.Port.ExportHdd(2)[0]);
        Assert.True(rig.Port.Uncooperated);
        Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave);
    }

    [Fact]
    public void T1b_CleanSlotsNeedAnImmediatelyPrecedingCooperationCycle()
    {
        var rig = new Rig(); rig.ColdRestore();
        Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave);
        rig.CompleteCooperation();
        rig.Port.ValidateSessionSnapshotSave();
        rig.Port.CaptureHdd(2); // Paused inspection is not guest execution.
        rig.Port.ValidateSessionSnapshotSave();
        rig.Port.NotifyGuestInstruction();
        Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave);
    }

    [Fact]
    public void StagedAndHeldSwitchesRefuseSnapshotAndPendingCancellationDoesNotCooperate()
    {
        var rig = new Rig(); rig.ColdRestore();
        var request = rig.Port.BeginGuestSwitch(2, 4);
        Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave);
        rig.Port.CancelPendingGuestSwitch(request.SessionNonce, request.RequestId);
        Assert.True(rig.Port.Uncooperated);
        rig.Query(); // Drain cancelled terminal, not a cooperation receipt.
        rig.Port.BeginGuestSwitch(2, 4);
        var ticket = rig.Ack();
        Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave);
        rig.Port.CancelGuestSwitch(ticket);
        Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave);
        rig.Port.FinishGuestSwitch(ticket);
        rig.Port.ValidateSessionSnapshotSave();
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("execution")]
    [InlineData("bus")]
    [InlineData("nonnull-restore")]
    public void RearmRefusesAnythingButTheEmptyColdRestoreBoundary(string interveningAct)
    {
        var rig = new Rig(); rig.Port.Restore(null);
        switch (interveningAct)
        {
            case "reset": rig.Port.Reset(); break;
            case "execution": rig.Port.NotifyGuestInstruction(); break;
            case "bus": rig.Port.Read(0x89, 0); break;
            case "nonnull-restore": rig.Port.Restore(rig.Port.Capture()); break;
        }
        Assert.Throws<InvalidOperationException>(() => rig.Port.RearmRestoredGuestAckSession(Guid.NewGuid()));
    }

    [Fact]
    public void RearmIsOneShotAndCannotReusePreRestoreSession()
    {
        var rig = new Rig(); var nonce = Guid.NewGuid(); rig.Port.BindGuestAckSession(nonce);
        rig.Port.Restore(null);
        Assert.Throws<ArgumentException>(() => rig.Port.RearmRestoredGuestAckSession(nonce));
        rig.Port.RearmRestoredGuestAckSession(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => rig.Port.RearmRestoredGuestAckSession(Guid.NewGuid()));
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        Assert.True(rig.Port.Uncooperated); // A fresh ordinary bind is not cooperation.
        Assert.Throws<InvalidOperationException>(rig.Port.ValidateSessionSnapshotSave);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithinCycleHostGenerationDriftRefusesCommitOrDetachWithoutChangingMedia(bool detach)
    {
        var rig = new Rig(); rig.ColdRestore();
        rig.Port.BeginGuestSwitch(2, 4);
        var ticket = rig.Ack();
        Assert.False(rig.Port.Uncooperated);
        // Fault injection only: public host mutation is already barred while a switch is held.
        // This models host drift AFTER Begin/ACK, never a restored guest's previous identity.
        var slots = Assert.IsAssignableFrom<Array>(typeof(BlockDiskPort).GetField("slots",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(rig.Port));
        var slot = slots.GetValue(0)!;
        slot.GetType().GetProperty("Generation")!.SetValue(slot, 2u);
        var before = rig.Port.CaptureHdd(2);
        var result = detach ? rig.Port.DetachGuestSwitch(ticket, false) :
            rig.Port.CommitGuestSwitch(ticket, Enumerable.Repeat((byte)0x5a, ImageBytes).ToArray(), false, false);
        Assert.False(result.Committed);
        Assert.Equal("Old generations changed.", result.Error);
        Assert.Equal(before.Generation, rig.Port.CaptureHdd(2).Generation);
        Assert.Equal(before.Bytes, rig.Port.CaptureHdd(2).Bytes);
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        rig.Port.CancelGuestSwitch(ticket); rig.Port.FinishGuestSwitch(ticket);
    }
}
