using System.Buffers.Binary;
using System.Linq;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>
/// Independent native wire-protocol tests for the guest switch v3 ABI
/// (KB hc2000-hdd-guest-ack-v1, abi v3) against the real BlockDiskPort
/// QUERY (operation 4) / ACK (operation 5) receiver. Every receipt here is
/// driven through guest wire bytes - 16-byte v2 descriptor at 0x8000 and
/// 512-byte DMA at 0x8200, status read at 0x89 - exactly as a guest would
/// drive them. The owner API is used only to prepare real media and session
/// state, never to fake a correlation. This class is deliberately
/// independent of HddGuestAckProducerTests.
/// </summary>
public sealed class HddGuestAckProtocolTests
{
    private const int Descriptor = 0x8000;
    private const ushort DmaAddress = 0x8200;
    private const uint ImageBytes = 8_388_608;

    private sealed class Rig
    {
        public HcMemory Memory { get; } = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        public BlockDiskPort Port { get; }

        public Rig(bool guestAckEnabled = true)
        {
            var fdc = new FloppyController(new DeviceScheduler(), 100, new(new()), new(new()));
            Port = new(Memory, fdc, hddEnabled: true, guestAckEnabled: guestAckEnabled);
        }

        /// <summary>
        /// v2 descriptor at 0x8000: [0]=2 (HDD), [1]=operation, [2]=primary unit,
        /// [3]=0, [4..8]=lba, [8..10]=dma, [12..16]=generation; then the
        /// 0x81=0 / 0x83=0x80 / 0x89=1 execute sequence. Returns the status byte.
        /// </summary>
        public byte Command(byte operation, byte primary = 2, uint generation = 0, ushort dma = DmaAddress, uint lba = 0)
        {
            var command = new byte[16];
            command[0] = 2;
            command[1] = operation;
            command[2] = primary;
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), lba);
            BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(8), dma);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), generation);
            return Raw(command);
        }

        public byte Raw(byte[] command)
        {
            Assert.Equal(16, command.Length);
            for (var i = 0; i < 16; i++)
            {
                Memory.Write((ushort)(Descriptor + i), command[i]);
            }

            Port.Write(0x81, 0, 0);
            Port.Write(0x83, 0x80, 0);
            Port.Write(0x89, 1, 0);
            return Port.Read(0x89, 0);
        }

        public byte[] Dma() => Enumerable.Range(0, 512).Select(i => Memory.Read((ushort)(DmaAddress + i))).ToArray();

        public void Dma(byte[] value)
        {
            for (var i = 0; i < 512; i++)
            {
                Memory.Write((ushort)(DmaAddress + i), value[i]);
            }
        }

        public byte[] Query()
        {
            Assert.Equal(0, Command(4));
            return Dma();
        }

        /// <summary>Queries and acknowledges the pending correlated request.</summary>
        public void Acknowledge(uint generation)
        {
            var block = Query();
            Dma(block);
            Assert.Equal(0, Command(5, generation: generation));
        }
    }

    [Fact]
    public void OldProfilesKeepLegacyBehaviourAndGuestOpsAreUnknown()
    {
        var old = new Rig(guestAckEnabled: false);
        // Operations 4/5 are explicit opt-in: on the legacy port they are unknown.
        Assert.Equal(0x01, old.Command(4));
        Assert.Equal(0x01, old.Command(5));
        // Legacy IDENTIFY is byte-identical on the old port.
        old.Port.AttachHdd(2, new byte[ImageBytes]);
        Assert.Equal(0, old.Command(3, generation: 1));
        var identify = old.Dma();
        Assert.Equal(ImageBytes / 512, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan()));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan(4)));
        Assert.Equal(3, identify[8]);
        Assert.Throws<InvalidOperationException>(() => old.Port.BindGuestAckSession(Guid.NewGuid()));

        // Only the successor profile wires the guest-capable port; every existing profile rejects it.
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2 with { BlockDisk = BlockDiskPolicy.HddGuestV1 }).Validate());
        Assert.Throws<ArgumentException>(() => (MachineProfile.Hc2000V2HddV1 with { BlockDisk = BlockDiskPolicy.HddGuestV1 }).Validate());
        Assert.Equal("hc2000-v2-hdd-guest-v1", MachineProfile.Hc2000V2HddGuestV1.Name);
        MachineProfile.Hc2000V2HddGuestV1.Validate();
    }

    [Fact]
    public void ChallengeBlockCarriesActualMaskSequenceNonceAndOldGenerations()
    {
        var rig = new Rig();
        var c = new byte[ImageBytes];
        c[0] = 0x11;
        var d = new byte[ImageBytes];
        d[0] = 0x22;
        rig.Port.AttachHdd(2, c);
        rig.Port.AttachHdd(3, d);
        var session = Guid.Parse("12345678-9abc-def0-1234-56789abcdef0");
        rig.Port.BindGuestAckSession(session);
        var request = rig.Port.BeginGuestSwitch(2, 60);

        var block = rig.Query();
        Assert.Equal(1, block[0]); // format v2
        Assert.Equal(1, block[1]); // Requested
        Assert.Equal(2, block[2]); // primary
        Assert.Equal(60, BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(4)));
        Assert.Equal(request.RequestId, BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(8)));
        Assert.Equal(Convert.FromHexString(session.ToString("N")), block[16..32]);
        // Old-slot generations are captured per mask bit: C and D attached (1), E and F empty (0).
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(32)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(36)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(40)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(44)));
        Assert.True(block[48..512].All(b => b == 0)); // reserved tail is zero
    }

    [Fact]
    public void StaleGenerationReplyCannotAuthorizeCurrentState()
    {
        var rig = new Rig();
        rig.Port.AttachHdd(2, new byte[ImageBytes]); // slot generation 1
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        var challenge = rig.Query();

        // A stale-generation descriptor is refused; nothing is issued and the
        // same request stays pending with an unchanged challenge.
        Assert.Equal(0x0e, rig.Command(5, generation: 0));
        Assert.False(rig.Port.TryGetHeldGuestSwitch(out _));
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal(challenge, rig.Query());

        // The captured old generation authorizes exactly this correlated request.
        rig.Dma(challenge);
        Assert.Equal(0, rig.Command(5, generation: 1));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        var replacement = new byte[ImageBytes];
        replacement[0] = 0x7a;
        var commit = rig.Port.CommitGuestSwitch(ticket, replacement, false, false);
        Assert.True(commit.Committed);
        Assert.Equal(2u, commit.Generation);
        rig.Port.FinishGuestSwitch(ticket);
        rig.Query(); // drain the Committed terminal

        // The stale generation can no longer authorize reads of the new media.
        Assert.Equal(0x0d, rig.Command(1, generation: 1));
        Assert.Equal(0, rig.Command(3));
        var identify = rig.Dma();
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan(4)));
        Assert.Equal(0, rig.Command(1, generation: 2));
        Assert.Equal(0x7a, rig.Dma()[0]);
    }

    [Fact]
    public void StaleBlockReplayFromPreviousRequestIsRefused()
    {
        var rig = new Rig();
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        var first = rig.Port.BeginGuestSwitch(2, 4);
        var stale = rig.Query();
        Assert.Equal(1, stale[1]);

        // Cancel the first request and drain it through the real receiver.
        rig.Port.CancelPendingGuestSwitch(first.SessionNonce, first.RequestId);
        Assert.Equal(0x0e, rig.Port.Read(0x89, 0));
        Assert.Equal(4, rig.Query()[1]); // terminal drain
        Assert.Equal(0, rig.Query()[1]); // idle

        var second = rig.Port.BeginGuestSwitch(2, 4);
        Assert.True(second.RequestId > first.RequestId);
        var current = rig.Query();

        // Replaying the previous request's block (old sequence/generation)
        // must not authorize, and must not touch the DMA buffer.
        rig.Dma(stale);
        Assert.Equal(0x0e, rig.Command(5, generation: 0));
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.False(rig.Port.TryGetHeldGuestSwitch(out _));
        Assert.Equal(stale, rig.Dma()); // the refused replay leaves the DMA buffer untouched

        rig.Dma(current);
        Assert.Equal(0, rig.Command(5, generation: 0));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        rig.Port.CancelGuestSwitch(ticket);
        rig.Port.FinishGuestSwitch(ticket);
    }

    [Fact]
    public void WrongPrimaryQueryAndAckAreRefusedWithoutDmaChange()
    {
        var rig = new Rig();
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        var request = rig.Port.BeginGuestSwitch(2, 4);
        var challenge = rig.Query();

        Assert.Equal(0x0e, rig.Command(4, primary: 3));
        Assert.Equal(challenge, rig.Dma());

        rig.Dma(challenge);
        Assert.Equal(0x0e, rig.Command(5, primary: 3, generation: 0));
        Assert.Equal(challenge, rig.Dma());
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.False(rig.Port.TryGetHeldGuestSwitch(out _));

        // The pending request is unchanged afterwards.
        Assert.Equal(challenge, rig.Query());
        rig.Port.CancelPendingGuestSwitch(request.SessionNonce, request.RequestId);
        rig.Query(); // drain the Cancelled terminal
    }

    [Fact]
    public void DescriptorErrorPrecedenceIsExact()
    {
        var rig = new Rig();
        // Malformed LBA precedes every correlation decision, even with no request.
        Assert.Equal(0x02, rig.Command(4, lba: 1));
        // Invalid DMA precedes correlation.
        Assert.Equal(0x09, rig.Command(4, dma: 0xff00));

        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        var challenge = rig.Query();
        rig.Dma(challenge);
        // LBA check precedes the DMA check on ACK.
        Assert.Equal(0x02, rig.Command(5, lba: 1, dma: 0xff00));
        // DMA check precedes block correlation; DMA is untouched on refusal.
        Assert.Equal(0x09, rig.Command(5, dma: 0xff00));
        Assert.Equal(challenge, rig.Dma());

        rig.Dma(challenge);
        Assert.Equal(0, rig.Command(5, generation: 0));
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        // While held, the hold check precedes slot/media access on legacy ops...
        Assert.Equal(0x0e, rig.Command(1));
        Assert.Equal(0x0e, rig.Command(3));
        // ...but a malformed descriptor still reports its own error first.
        Assert.Equal(0x02, rig.Raw([2, 1, 2, 1, 0, 0, 0, 0, 0x00, 0x08, 0, 0, 0, 0, 0, 0]));
        Assert.True(rig.Port.GuestSwitchHoldsCpu);

        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        rig.Port.CancelGuestSwitch(ticket);
        rig.Port.FinishGuestSwitch(ticket);
    }

    [Fact]
    public void TerminalQueryDrainsCommittedAndCancelledThenAllowsFreshRequest()
    {
        var rig = new Rig();
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        rig.Acknowledge(0);
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));
        Assert.True(rig.Port.CommitGuestSwitch(ticket, new byte[ImageBytes], false, false).Committed);
        rig.Port.FinishGuestSwitch(ticket);
        Assert.Equal(0, rig.Port.Read(0x89, 0));

        Assert.Equal(3, rig.Query()[1]); // Committed terminal
        Assert.Equal(0, rig.Query()[1]); // drained to idle

        var second = rig.Port.BeginGuestSwitch(2, 4);
        rig.Port.CancelPendingGuestSwitch(second.SessionNonce, second.RequestId);
        Assert.Equal(0x0e, rig.Port.Read(0x89, 0));
        Assert.Equal(4, rig.Query()[1]); // Cancelled terminal
        Assert.Equal(0, rig.Query()[1]); // idle

        // Fresh correlation is possible only after the drain.
        var third = rig.Port.BeginGuestSwitch(2, 4);
        Assert.True(third.RequestId > second.RequestId);
        Assert.Equal(1, rig.Query()[1]);

        // The drain and the session reset do not mutate media.
        rig.Port.InvalidateGuestAckSession();
        Assert.Equal(1u, rig.Port.CaptureHdd(2).Generation);
        Assert.False(rig.Port.CaptureHdd(2).Dirty);
    }

    [Fact]
    public void AckReplayIssuesNoSecondTicket()
    {
        var rig = new Rig();
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        var block = rig.Query();
        rig.Dma(block);
        Assert.Equal(0, rig.Command(5, generation: 0));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));

        // Replaying the same block after the switch is refused and issues nothing.
        rig.Dma(block);
        Assert.Equal(0x0e, rig.Command(5, generation: 0));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var again));
        Assert.Same(ticket, again);

        rig.Port.CancelGuestSwitch(ticket);
        rig.Port.FinishGuestSwitch(ticket);

        // Even after the terminal, a further ACK is refused.
        rig.Dma(block);
        Assert.Equal(0x0e, rig.Command(5, generation: 0));
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
    }

    [Fact]
    public void SessionAndRequestRulesRejectEmptyStaleAndRestoredGuests()
    {
        var rig = new Rig();
        Assert.Throws<ArgumentException>(() => rig.Port.BindGuestAckSession(Guid.Empty));
        Assert.Throws<InvalidOperationException>(() => rig.Port.BeginGuestSwitch(2, 4)); // unbound
        var session = Guid.NewGuid();
        rig.Port.BindGuestAckSession(session);
        Assert.Throws<ArgumentException>(() => rig.Port.BindGuestAckSession(session)); // same session twice

        var request = rig.Port.BeginGuestSwitch(2, 4);
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelPendingGuestSwitch(session, request.RequestId + 1));
        rig.Port.CancelPendingGuestSwitch(session, request.RequestId);
        Assert.Equal(0x0e, rig.Port.Read(0x89, 0));
        Assert.Equal(4, rig.Query()[1]); // drain the Cancelled terminal before owner access

        // After restore the retired guest cannot re-bind.
        rig.Port.Restore(null);
        Assert.Throws<InvalidOperationException>(() => rig.Port.BindGuestAckSession(Guid.NewGuid()));

        // An invalidated session cannot begin a request.
        var retired = new Rig();
        retired.Port.BindGuestAckSession(Guid.NewGuid());
        retired.Port.InvalidateGuestAckSession();
        Assert.Throws<InvalidOperationException>(() => retired.Port.BeginGuestSwitch(2, 4));
    }
}
