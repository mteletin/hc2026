using System.Buffers.Binary;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>
/// Independent native boundary tests for the guest switch v3 ABI
/// (KB hc2000-hdd-guest-ack-v1, abi v3): the real FrameRunner / Machine
/// instruction boundary, and the real ticket and slot paths. The CPU stop
/// is verified through Machine.TryRunFrame with the seam
/// "() => !port.GuestSwitchHoldsCpu": while the switch is held the frame
/// runner must not step the CPU at all; host pacing may continue, but it
/// never releases the hold. All commit / cancel / detach receipts use real
/// 8 MB images through the real HddSlot. Deliberately independent of
/// HddGuestAckProducerTests.
/// </summary>
public sealed class HddGuestAckBoundaryTests
{
    private const int Descriptor = 0x8000;
    private const ushort DmaAddress = 0x8200;
    private const uint ImageBytes = 8_388_608;

    /// <summary>A real Machine around the same bus memory the guest port is wired into.</summary>
    private sealed class MachineRig
    {
        private readonly Counter _cpu;

        public HcMemory Memory { get; }
        public Machine Machine { get; }
        public BlockDiskPort Port { get; }
        public int Steps => _cpu.StepCount;
        public long Cpu => _cpu.Value;

        public MachineRig()
        {
            var scheduler = new DeviceScheduler();
            Memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
            var clock = new FrameClock(scheduler, 1);
            var adapter = new CpuDeviceTimeAdapter(scheduler, new CpuTimeMapper());
            var ula = new Ula(Memory, new KeyboardMatrix(), scheduler, clock, 2);
            _cpu = new Counter();
            Machine = new Machine(scheduler, clock, adapter, ula,
                () => { _cpu.StepCount++; _cpu.Value += 100; },
                () => _cpu.Value);
            Port = new BlockDiskPort(Memory,
                new FloppyController(new DeviceScheduler(), 100, new(new()), new(new())),
                hddEnabled: true, guestAckEnabled: true);
        }

        public byte Cmd(byte operation, byte primary = 2, uint generation = 0, uint lba = 0)
        {
            var command = new byte[16];
            command[0] = 2;
            command[1] = operation;
            command[2] = primary;
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), lba);
            BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(8), DmaAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), generation);
            for (var i = 0; i < 16; i++)
            {
                Memory.Write((ushort)(Descriptor + i), command[i]);
            }

            Port.Write(0x81, 0, 0);
            Port.Write(0x83, 0x80, 0);
            Port.Write(0x89, 1, 0);
            return Port.Read(0x89, 0);
        }

        public byte[] GetDma() => Enumerable.Range(0, 512).Select(i => Memory.Read((ushort)(DmaAddress + i))).ToArray();

        public void PutDma(byte[] value)
        {
            for (var i = 0; i < 512; i++)
            {
                Memory.Write((ushort)(DmaAddress + i), value[i]);
            }
        }

        /// <summary>Queries and acknowledges the pending correlated request.</summary>
        public void Acknowledge(uint generation)
        {
            var block = Query();
            PutDma(block);
            Assert.Equal(0, Cmd(5, generation: generation));
        }

        public byte[] Query()
        {
            Assert.Equal(0, Cmd(4));
            return GetDma();
        }

        private sealed class Counter
        {
            public long Value { get; set; }
            public int StepCount { get; set; }
        }
    }

    private sealed class Rig
    {
        public HcMemory Memory { get; } = new(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        public BlockDiskPort Port { get; }

        public Rig()
        {
            var fdc = new FloppyController(new DeviceScheduler(), 100, new(new()), new(new()));
            Port = new(Memory, fdc, hddEnabled: true, guestAckEnabled: true);
        }

        public byte Command(byte operation, byte primary = 2, uint generation = 0, uint lba = 0)
        {
            var command = new byte[16];
            command[0] = 2;
            command[1] = operation;
            command[2] = primary;
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), lba);
            BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(8), DmaAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), generation);
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

        public byte[] Query(byte primary = 2)
        {
            Assert.Equal(0, Command(4, primary));
            return Dma();
        }

        public void Acknowledge(uint generation)
        {
            var block = Query();
            Dma(block);
            Assert.Equal(0, Command(5, generation: generation));
        }
    }

    [Fact]
    public void NoCpuStepBetweenAckAndAccountedFinish()
    {
        var rig = new MachineRig();
        var machine = rig.Machine;
        var port = rig.Port;

        // The CPU runs freely before the switch.
        Assert.True(machine.TryRunFrame(() => !port.GuestSwitchHoldsCpu, out _));
        var free = rig.Steps;
        Assert.True(free > 0);

        port.BindGuestAckSession(Guid.NewGuid());
        port.BeginGuestSwitch(2, 4);
        rig.Acknowledge(0);
        Assert.True(port.GuestSwitchHoldsCpu);

        // No CPU step between the ACK and the accounted finish.
        Assert.False(machine.TryRunFrame(() => !port.GuestSwitchHoldsCpu, out _));
        Assert.Equal(free, rig.Steps);

        // Host pacing keeps running, but it never releases the hold.
        machine.RunFrame();
        Assert.True(rig.Steps > free);
        Assert.True(port.GuestSwitchHoldsCpu);
        var paced = rig.Steps;
        // The host-paced frame is produced and consumed by RunFrame itself; nothing is
        // left queued, so the guest-boundary TryRunFrame stays refused and the CPU
        // takes no further steps until the accounted finish.
        Assert.False(machine.TryRunFrame(() => !port.GuestSwitchHoldsCpu, out _));
        Assert.Equal(paced, rig.Steps);

        // Commit latches CommittedHeld: the CPU stays held until accounting.
        Assert.True(port.TryGetHeldGuestSwitch(out var ticket));
        var replacement = new byte[ImageBytes];
        replacement[0] = 0x7a;
        Assert.True(port.CommitGuestSwitch(ticket, replacement, false, false).Committed);
        Assert.False(machine.TryRunFrame(() => !port.GuestSwitchHoldsCpu, out _));
        Assert.Equal(paced, rig.Steps);

        // The accounted finish releases the hold and the CPU resumes.
        port.FinishGuestSwitch(ticket);
        Assert.False(port.GuestSwitchHoldsCpu);
        Assert.True(machine.TryRunFrame(() => !port.GuestSwitchHoldsCpu, out _));
        Assert.True(rig.Steps > paced);
    }

    [Fact]
    public void CancelPathHoldsCpuUntilAccountedFinishAndKeepsMedia()
    {
        var rig = new MachineRig();
        var machine = rig.Machine;
        var port = rig.Port;
        var media = new byte[ImageBytes];
        media[0] = 0x5c;
        media[511] = 0x5d;
        port.AttachHdd(2, media);
        port.BindGuestAckSession(Guid.NewGuid());
        port.BeginGuestSwitch(2, 4);
        rig.Acknowledge(1);
        Assert.True(port.GuestSwitchHoldsCpu);
        Assert.False(machine.TryRunFrame(() => !port.GuestSwitchHoldsCpu, out _));

        // The ticketed cancel keeps the CPU held until the accounted finish.
        Assert.True(port.TryGetHeldGuestSwitch(out var ticket));
        port.CancelGuestSwitch(ticket);
        Assert.True(port.GuestSwitchHoldsCpu);
        Assert.False(machine.TryRunFrame(() => !port.GuestSwitchHoldsCpu, out _));
        port.FinishGuestSwitch(ticket);
        Assert.Equal(0x0e, port.Read(0x89, 0));
        Assert.False(port.GuestSwitchHoldsCpu);
        var held = rig.Steps;
        Assert.True(machine.TryRunFrame(() => !port.GuestSwitchHoldsCpu, out _));
        Assert.True(rig.Steps > held);

        // Media and generation are unchanged on the wire after the drain.
        Assert.Equal(0, rig.Cmd(4)); // drain the Cancelled terminal
        Assert.Equal(0, rig.Cmd(4)); // idle
        Assert.Equal(0, rig.Cmd(3));
        var identify = rig.GetDma();
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan(4)));
        Assert.Equal(0, rig.Cmd(1, generation: 1));
        Assert.Equal(0x5c, rig.GetDma()[0]);
        Assert.Equal(0x5d, rig.GetDma()[511]);
    }

    [Fact]
    public void HoldSurvivesOwnerAndLegacyAccess()
    {
        var rig = new Rig();
        var media = new byte[ImageBytes];
        media[0] = 0x33;
        rig.Port.AttachHdd(2, media);
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        var challenge = rig.Query();
        rig.Dma(challenge);
        Assert.Equal(0, rig.Command(5, generation: 1));
        Assert.True(rig.Port.GuestSwitchHoldsCpu);

        // Unticketed owner access cannot bypass the active correlation.
        Assert.Throws<InvalidOperationException>(() => rig.Port.AttachHdd(3, new byte[ImageBytes]));
        Assert.Throws<InvalidOperationException>(() => rig.Port.DetachHdd(2));
        Assert.Throws<InvalidOperationException>(() => rig.Port.Capture());
        Assert.Throws<InvalidOperationException>(() => rig.Port.Restore(null));
        Assert.Throws<InvalidOperationException>(() => rig.Port.Reset());

        // The hold precedes slot/media access on legacy HDD ops...
        Assert.Equal(0x0e, rig.Command(1, generation: 1));
        // ...and a well-formed old-floppy descriptor is refused as well.
        for (var i = 0; i < 8; i++)
        {
            rig.Memory.Write((ushort)(Descriptor + i), 0);
        }

        rig.Memory.Write(Descriptor, 1);
        rig.Port.Write(0x89, 1, 0);
        Assert.Equal(0x0e, rig.Port.Read(0x89, 0));
        Assert.True(rig.Port.GuestSwitchHoldsCpu);

        // A malformed descriptor still reports its own error before the hold.
        rig.Memory.Write(Descriptor, 2);
        rig.Memory.Write((ushort)(Descriptor + 1), 1);
        rig.Memory.Write((ushort)(Descriptor + 2), 2);
        rig.Memory.Write((ushort)(Descriptor + 3), 1);
        rig.Port.Write(0x89, 1, 0);
        Assert.Equal(0x02, rig.Port.Read(0x89, 0));
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
    }

    [Fact]
    public void CommitUsesActualSlotRulesAndOldGenerationCannotReadNewMedia()
    {
        var rig = new Rig();
        var original = new byte[ImageBytes];
        original[0] = 0x41;
        original[511] = 0x42;
        rig.Port.AttachHdd(2, original);
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 60);
        var challenge = rig.Query();
        rig.Dma(challenge);
        Assert.Equal(0, rig.Command(5, generation: 1));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));

        var replacement = new byte[ImageBytes];
        replacement[0] = 0x7a;
        var commit = rig.Port.CommitGuestSwitch(ticket, replacement, writeProtected: true, discardDirty: false);
        Assert.True(commit.Committed);
        Assert.Equal(2u, commit.Generation);

        // CommittedHeld: media already swapped in, CPU still held, ticket consumed.
        Assert.Equal(0x7a, rig.Port.CaptureHdd(2).Bytes[0]);
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelGuestSwitch(ticket));
        Assert.Throws<InvalidOperationException>(() => rig.Port.CommitGuestSwitch(ticket, replacement, false, false));

        rig.Port.FinishGuestSwitch(ticket);
        Assert.Equal(0, rig.Port.Read(0x89, 0));
        rig.Query(); // drain the Committed terminal
        rig.Query(); // idle

        // The new media on the wire: new generation, write-protected.
        Assert.Equal(0, rig.Command(3));
        var identify = rig.Dma();
        Assert.Equal(ImageBytes / 512, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan()));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan(4)));
        Assert.Equal(1, identify[8]);
        Assert.Equal(0x0d, rig.Command(1, generation: 1)); // old generation refused
        Assert.Equal(0, rig.Command(1, generation: 2));
        Assert.Equal(0x7a, rig.Dma()[0]);
        Assert.Equal(0x08, rig.Command(2, generation: 2)); // write-protected
    }

    [Fact]
    public void FailedCommitKeepsTicketHeldAndMediaUnchanged()
    {
        var rig = new Rig();
        rig.Port.AttachHdd(2, new byte[ImageBytes]);
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        var challenge = rig.Query();
        rig.Dma(challenge);
        Assert.Equal(0, rig.Command(5, generation: 1));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));

        // A badly sized image is refused; the ticket stays held and the media unchanged.
        var failed = rig.Port.CommitGuestSwitch(ticket, new byte[1], false, false);
        Assert.False(failed.Committed);
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        Assert.Equal(1u, rig.Port.CaptureHdd(2).Generation);
        Assert.False(rig.Port.CaptureHdd(2).Dirty);

        // The same ticket can then commit a valid image.
        var ok = rig.Port.CommitGuestSwitch(ticket, new byte[ImageBytes], false, false);
        Assert.True(ok.Committed);
        rig.Port.FinishGuestSwitch(ticket);
    }

    [Fact]
    public void DetachPathThroughActualSlotAndTerminalWire()
    {
        var rig = new Rig();
        rig.Port.AttachHdd(3, new byte[ImageBytes], writeProtected: true);
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(3, 60);
        var challenge = rig.Query(3);
        rig.Dma(challenge);
        Assert.Equal(0, rig.Command(5, primary: 3, generation: 1));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));

        var result = rig.Port.DetachGuestSwitch(ticket, discardDirty: false);
        Assert.True(result.Committed);
        Assert.Equal(1u, result.Generation);
        Assert.True(rig.Port.GuestSwitchHoldsCpu);
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelGuestSwitch(ticket));

        rig.Port.FinishGuestSwitch(ticket);
        rig.Query(3); // drain the Committed terminal (detach is a committed outcome)
        Assert.Equal(0, rig.Query()[1]);

        Assert.Equal(0, rig.Command(3, primary: 3));
        var identify = rig.Dma();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan()));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(identify.AsSpan(4))); // generation preserved
        Assert.Equal(0, identify[8]);
        Assert.Equal(0x05, rig.Command(1, primary: 3, generation: 1)); // no media
    }

    [Fact]
    public void TicketIsOneUseAndControllerBound()
    {
        var rig = new Rig();
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        rig.Port.BeginGuestSwitch(2, 4);
        var challenge = rig.Query();
        rig.Dma(challenge);
        Assert.Equal(0, rig.Command(5, generation: 0));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var ticket));

        // A ticket from one controller cannot drive another.
        var other = new Rig();
        other.Port.BindGuestAckSession(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => other.Port.CommitGuestSwitch(ticket, new byte[ImageBytes], false, false));
        Assert.Throws<InvalidOperationException>(() => other.Port.CancelGuestSwitch(ticket));

        rig.Port.CancelGuestSwitch(ticket);
        rig.Port.FinishGuestSwitch(ticket);

        // One use: after the accounted finish the same ticket is dead.
        Assert.Throws<InvalidOperationException>(() => rig.Port.FinishGuestSwitch(ticket));
        Assert.Throws<InvalidOperationException>(() => rig.Port.CommitGuestSwitch(ticket, new byte[ImageBytes], false, false));
        Assert.False(rig.Port.TryGetHeldGuestSwitch(out _));

        // The next correlation issues a different ticket; the old one stays dead.
        rig.Query(); // drain the Cancelled terminal
        rig.Port.BeginGuestSwitch(2, 4);
        var second = rig.Query();
        rig.Dma(second);
        Assert.Equal(0, rig.Command(5, generation: 0));
        Assert.True(rig.Port.TryGetHeldGuestSwitch(out var fresh));
        Assert.NotSame(ticket, fresh);
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelGuestSwitch(ticket));
        rig.Port.CancelGuestSwitch(fresh);
        rig.Port.FinishGuestSwitch(fresh);
    }

    [Fact]
    public void PendingCancelRequiresExactIdentityOnRealReceiver()
    {
        var rig = new Rig();
        rig.Port.BindGuestAckSession(Guid.NewGuid());
        var request = rig.Port.BeginGuestSwitch(2, 4);
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelPendingGuestSwitch(Guid.NewGuid(), request.RequestId));
        Assert.Throws<InvalidOperationException>(() => rig.Port.CancelPendingGuestSwitch(request.SessionNonce, request.RequestId + 1));

        rig.Port.CancelPendingGuestSwitch(request.SessionNonce, request.RequestId);
        Assert.Equal(0x0e, rig.Port.Read(0x89, 0));
        Assert.False(rig.Port.GuestSwitchHoldsCpu);
        Assert.False(rig.Port.TryGetHeldGuestSwitch(out _));
        Assert.Equal(4, rig.Query()[1]);
        Assert.Equal(0, rig.Query()[1]);

        var next = rig.Port.BeginGuestSwitch(2, 4);
        Assert.True(next.RequestId > request.RequestId);
        Assert.Equal(1, rig.Query()[1]);
    }
}
