using System.Buffers.Binary;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Z80;
using Xunit;

namespace Hc2000.Machine.Tests;

[Collection("expansion-runtime")]
public sealed class ExpansionExchangeTests
{
    private sealed class Rig
    {
        public readonly DeviceScheduler Scheduler = new();
        public readonly HcMemory Memory;
        public readonly ExpansionArena Arena;
        public readonly ExpansionController Root;
        public readonly PortDecoder Ports;
        public readonly ExpansionFrontierDriver Driver;
        public Rig(ushort bank = 0)
        {
            var profile = MachineProfile.Hc2000V2PagedExpansionV1;
            Memory = new(profile.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
            Memory.WritePagedPort(0x7efd, (byte)bank); Memory.WritePagedPort(0x7dfd, (byte)(bank >> 8));
            Arena = Memory.EnableExpansionArena(profile); Root = new(Arena);
            var idle = new PortDevice();
            Ports = new(ExpansionIoProfile.Compose(profile, Memory,
                HcIoProfile.WithPagedRam(Memory, idle, idle, idle, idle, idle, idle), Root), Root, Root.MainOrigin);
            var main = new Z80.Z80(new NoBus());
            // Native RAM holds a harmless loop; the exchange latch cannot change main's code.
            Memory.Write(0x4000, 0xc3); Memory.Write(0x4001, 0); Memory.Write(0x4002, 0x40); main.R.PC = 0x4000;
            Driver = new(Scheduler, new FrameClock(Scheduler, 1), new CpuTimeMapper(), Root, Memory, Ports, main);
        }
        public byte Read(ushort port) => Ports.Read(port, Scheduler.Now);
        public void Write(ushort port, byte value) => Ports.Write(port, value, Scheduler.Now);
        public void Run() => Scheduler.AdvanceTo(Scheduler.Now + 20000);
        public byte[] Send(ushort id, byte op, byte[]? payload = null)
        {
            var grant = Arena.GetGrant(id); var unit = Root.GetUnit(id);
            payload ??= []; var request = new byte[40 + payload.Length]; request[0] = 1; request[1] = op;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)payload.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8), 321);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), grant.ResourceEpoch);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(24), unit.ExecutionEpoch);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(32), grant.MapEpoch); payload.CopyTo(request, 40);
            Write(0x7bfd, (byte)id); Write(0x7afd, (byte)(id >> 8)); Write(0x79fd, 1);
            foreach (var b in request) Write(0x78fd, b);
            Write(0x79fd, 2); Driver.Synchronize();
            // RESET/PAUSE complete on the owner's cycle-safe frontier, not on submit.
            for (var i = 0; i < 100 && (Read(0x77fd) & 2) == 0; i++) Scheduler.AdvanceTo(Scheduler.Now + 100);
            Assert.Equal(2, Read(0x77fd) & 2);
            var header = Enumerable.Range(0, 40).Select(_ => Read(0x78fd)).ToArray();
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)); Assert.InRange(count, 0u, 69636u);
            var data = Enumerable.Range(0, (int)count).Select(_ => Read(0x78fd)).ToArray();
            // Draining is not release; every request explicitly clears its response.
            Assert.Equal(2, Read(0x77fd) & 2); Write(0x79fd, 5); Driver.Synchronize();
            return [.. header, .. data];
        }
        public byte[] Accepted(ushort id, byte op, byte[]? payload = null)
        {
            var result = Send(id, op, payload); Assert.Equal((byte)ExpansionResult.Accepted, result[1]); return result[40..];
        }
        public void Load(ushort id, byte[] code) => Accepted(id, 4, Range(0, code));
        public void StartIdle(ushort id = 1)
        {
            Load(id, [0x31, 0xff, 0xbf, 0x01, 0xfd, 0x74, 0x3e, 7, 0xed, 0x79, 0xc3, 6, 0]);
            Accepted(id, 7); Run(); Assert.Equal(ExpansionLifecycle.ServiceIdle, Root.GetUnit(id).Lifecycle);
        }
        public byte[] Commit(ushort id = 1)
        {
            var mail = Mail(7); var local = Ports.CreateView(new(id, Root.GetUnit(id).ExecutionEpoch, CpuPortRole.Compute));
            local.Write(0x74fd, 1, Scheduler.Now); foreach (var b in mail) local.Write(0x75fd, b, Scheduler.Now);
            local.Write(0x74fd, 2, Scheduler.Now); return mail;
        }
    }
    private static byte[] Range(ushort offset, byte[] bytes)
    {
        var data = new byte[8 + bytes.Length]; BinaryPrimitives.WriteUInt16LittleEndian(data, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)bytes.Length); bytes.CopyTo(data, 8); return data;
    }
    private static byte[] Mail(ulong id)
    {
        var frame = new byte[73]; frame[0] = 5; BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), id);
        // Descriptor: offset zero, 4096 bytes, kind 1. Bulk bytes never enter the mailbox.
        frame[11] = 0; frame[12] = 0x10; frame[13] = 1; return frame;
    }
    private static byte[] Ack(ulong id = 7)
    {
        var data = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(data, id); return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1234)]
    public void HandoverReadMapsWindowAndResumeRestoresExactPreviousBank(int bank)
    {
        var r = new Rig((ushort)bank); r.StartIdle(); var frame = r.Commit();
        Assert.Equal(frame, r.Accepted(1, 12));
        Assert.Equal(r.Arena.GetGrant(1).Pages.Page3, r.Memory.Latches.PagedBank);
        Assert.Equal((byte)3, r.Read(0x7efd)); Assert.Equal((byte)2, r.Read(0x7dfd));
        var before = r.Root.GetUnit(1).CaptureFrontier();
        r.Accepted(1, 13, Ack());
        Assert.Equal(r.Arena.GetGrant(1).Pages.Page3, r.Memory.Latches.PagedBank);
        r.Accepted(1, 8);
        Assert.Equal((ushort)bank, r.Memory.Latches.PagedBank);
        var after = r.Root.GetUnit(1).CaptureFrontier();
        Assert.Equal(before.Registers, after.Registers); Assert.Equal(before.TStates, after.TStates);
        Assert.False(after.Halted); // Synchronize prepares the next fetch; no reset or PC skip.
        r.Run(); Assert.Equal(ExpansionLifecycle.ServiceIdle, r.Root.GetUnit(1).Lifecycle);
    }

    [Fact]
    public void WindowRoundTripsAll4096BytesAndBlocksOwnerWritesThroughAck()
    {
        var r = new Rig(); r.StartIdle(); var lease = r.Arena.Acquire(1);
        var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i * 37 + (i >> 8))).ToArray();
        for (var i = 0; i < bytes.Length; i++) lease.Write((ushort)(0xf000 + i), bytes[i]);
        r.Memory.SetCpm(true); r.Memory.WritePagedPort(0x7cfd, 1);
        r.Commit(); r.Accepted(1, 12);
        // Existing M-transfer maps the unit's page3 at main8000-BFFF; its payload is mainB000-BFFF.
        Assert.Equal(bytes, Enumerable.Range(0, 4096).Select(i => r.Memory.Read((ushort)(0xb000 + i))).ToArray());
        r.Accepted(1, 13, Ack());
        lease.Write(0xf000, 0xaa); r.Root.GetUnit(1).WriteMemory(0xc000, 0xbb);
        Assert.Equal(bytes[0], lease.Read(0xf000)); Assert.Equal(0, lease.Read(0xc000));
        for (var i = 0; i < bytes.Length; i++) r.Memory.Write((ushort)(0xb000 + i), (byte)(bytes[i] ^ 0xff));
        r.Accepted(1, 8); r.Run();
        Assert.Equal(bytes.Select(b => (byte)(b ^ 0xff)), Enumerable.Range(0, 4096).Select(i => lease.Read((ushort)(0xf000 + i))));
        lease.Write(0xf000, 0xaa); Assert.Equal(0xaa, lease.Read(0xf000));
    }

    [Fact]
    public void LiveFlipRefusesBothLatchWritesRepeatReadCopiesAndOtherHandoverIsBusy()
    {
        var r = new Rig(0x1234); r.StartIdle(); r.StartIdle(2); var frame = r.Commit(); r.Commit(2);
        Assert.Equal(frame, r.Accepted(1, 12));
        r.Write(0x7efd, 0x99); r.Write(0x7dfd, 0xab); r.Write(0x7ffd, 1);
        Assert.Equal(r.Arena.GetGrant(1).Pages.Page3, r.Memory.Latches.PagedBank);
        Assert.Equal(frame, r.Accepted(1, 12));
        Assert.Equal((byte)ExpansionResult.Busy, r.Send(2, 12)[1]);
        r.Accepted(1, 13, Ack()); r.Accepted(1, 8);
        Assert.Equal((ushort)0x1234, r.Memory.Latches.PagedBank); // Repeat read did not resample.
        Assert.Equal(frame, r.Accepted(2, 12)); r.Accepted(2, 8);
        Assert.Equal((ushort)0x1234, r.Memory.Latches.PagedBank);
    }

    [Fact]
    public void EmptyAndRunningOutboxReadsDoNotInstallFlipAndRefusedOpsDoNotRevoke()
    {
        var r = new Rig(17);
        Assert.Equal((byte)ExpansionResult.Busy, r.Send(1, 12)[1]);
        r.Commit(); r.Accepted(1, 12); Assert.Equal((ushort)17, r.Memory.Latches.PagedBank);
        r.Accepted(1, 13, Ack()); r.StartIdle(); r.Commit(); r.Accepted(1, 12);
        Assert.Equal((byte)ExpansionResult.InvalidState, r.Send(1, 7)[1]);
        Assert.Equal((byte)ExpansionResult.InvalidState, r.Send(1, 13, Ack(8))[1]);
        Assert.Equal(r.Arena.GetGrant(1).Pages.Page3, r.Memory.Latches.PagedBank);
        r.Accepted(1, 2); Assert.Equal((ushort)17, r.Memory.Latches.PagedBank);
    }

    [Fact]
    public void LoadRevokesOwnFlipWithoutChangingServiceIdleAndResetAllowsRestart()
    {
        var r = new Rig(23); r.StartIdle(); r.Commit(); r.Accepted(1, 12);
        r.Load(1, [0x31, 0xff, 0xbf, 0x3e, 0x2a, 0x32, 1, 0x81, 0x76]);
        Assert.Equal((ushort)23, r.Memory.Latches.PagedBank);
        Assert.Equal(ExpansionLifecycle.ServiceIdle, r.Root.GetUnit(1).Lifecycle);
        Assert.Equal((byte)ExpansionResult.InvalidState, r.Send(1, 7)[1]);
        r.Accepted(1, 2); r.Accepted(1, 7); r.Run();
        Assert.Equal(0x2a, r.Arena.Acquire(1).Read(0x8101));
    }

    [Fact]
    public void IdleAloneStopsBeforeFollowingHaltAndResumeExecutesItWithoutSkipping()
    {
        var r = new Rig(); r.Load(1, [0x31, 0xff, 0xbf, 1, 0xfd, 0x74, 0x3e, 7, 0xed, 0x79, 0x76, 0x3e, 0x5e, 0x32, 0, 0x81, 0x76]);
        r.Accepted(1, 7); r.Run(); var unit = r.Root.GetUnit(1);
        Assert.Equal(ExpansionLifecycle.ServiceIdle, unit.Lifecycle); Assert.False(unit.CaptureFrontier().Halted);
        Assert.Equal((ushort)10, unit.CaptureFrontier().Registers.PC);
        r.Accepted(1, 8); r.Run(); Assert.Equal(ExpansionLifecycle.Halted, unit.Lifecycle);
        Assert.Equal(0, unit.ReadMemory(0x8100)); Assert.Equal((byte)ExpansionResult.InvalidState, r.Send(1, 8)[1]);
    }

    [Fact]
    public void BareHaltRefusesLoadAndResumeUntilLatchedResetThenReloadRuns()
    {
        var r = new Rig(); r.Load(1, [0x31, 0xff, 0xbf, 0x76]); r.Accepted(1, 7); r.Run();
        Assert.Equal(ExpansionLifecycle.Halted, r.Root.GetUnit(1).Lifecycle);
        Assert.Equal((byte)ExpansionResult.InvalidState, r.Send(1, 4, Range(0, [0x76]))[1]);
        Assert.Equal((byte)ExpansionResult.InvalidState, r.Send(1, 8)[1]);
        r.Accepted(1, 2); r.Load(1, [0x31, 0xff, 0xbf, 0x3e, 0x5e, 0x32, 0, 0x81, 0x76]);
        r.Accepted(1, 7); r.Run(); Assert.Equal(0x5e, r.Arena.Acquire(1).Read(0x8100));
    }

    [Fact]
    public void RecurringIdleRetainsPrivatePagesRegistersAndStackAndWaitWakesOnMail()
    {
        var r = new Rig();
        // INC D, push/pop private stack across Idle, then return to Idle a second time.
        r.Load(1, [0x31, 0xff, 0xbf, 0x16, 0x41, 0xd5, 1, 0xfd, 0x74, 0x3e, 7, 0xed, 0x79,
            0xd1, 0x14, 0x7a, 0x32, 0, 0x81, 0xc3, 5, 0]);
        r.Accepted(1, 7); r.Run(); var unit = r.Root.GetUnit(1); var before = unit.CaptureFrontier();
        r.Commit(); r.Accepted(1, 12); r.Accepted(1, 13, Ack()); r.Accepted(1, 8); r.Run();
        Assert.Equal(0x42, unit.ReadMemory(0x8100)); Assert.Equal(before.Registers.SP, unit.CaptureFrontier().Registers.SP);
        Assert.Equal(ExpansionLifecycle.ServiceIdle, unit.Lifecycle); Assert.False(unit.CaptureFrontier().Halted);
        r.Load(1, [0x31, 0xff, 0xbf, 1, 0xfd, 0x74, 0x3e, 6, 0xed, 0x79, 0x3e, 0x55, 0x32, 1, 0x81, 0x76]);
        r.Accepted(1, 2); r.Accepted(1, 7); r.Run();
        Assert.Equal(ExpansionLifecycle.WaitingMailbox, unit.Lifecycle);
        r.Accepted(1, 11, Mail(9)); r.Run(); Assert.Equal(0x55, unit.ReadMemory(0x8101));
    }

    [Fact]
    public void GuestCommitsDescriptorAndExchangesFullPayloadAcrossTwoIdleRounds()
    {
        var r = new Rig(42); var code = new List<byte>
        {
            0x31, 0xff, 0xbf, // LD SP,BFFF
            0x21, 0, 0x10, 0x11, 0, 0xf0, 0x01, 0, 0x10, 0xed, 0xb0 // LDIR 1000 -> F000, 4096 bytes
        };
        static void Output(List<byte> image, ushort port, byte value) =>
            image.AddRange(new byte[] { 1, (byte)port, (byte)(port >> 8), 0x3e, value, 0xed, 0x79 });
        static void Handover(List<byte> image)
        {
            Output(image, 0x74fd, 1);
            foreach (var b in Mail(7)) Output(image, 0x75fd, b);
            Output(image, 0x74fd, 2); Output(image, 0x74fd, 7);
        }
        Handover(code);
        var nextRound = code.Count;
        // Complement every byte received from main, then yield again without HALT.
        code.AddRange(new byte[] { 0x21, 0, 0xf0, 0x01, 0, 0x10,
            0x7e, 0x2f, 0x77, 0x23, 0x0b, 0x78, 0xb1, 0x20, 0xf7 });
        Handover(code); code.AddRange(new byte[] { 0xc3, (byte)nextRound, (byte)(nextRound >> 8) });
        r.Load(1, code.ToArray());
        var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i * 37 + (i >> 8))).ToArray();
        r.Accepted(1, 4, Range(0x1000, bytes)); r.Accepted(1, 7);
        for (var i = 0; i < 100 && r.Root.GetUnit(1).Lifecycle != ExpansionLifecycle.ServiceIdle; i++) r.Run();
        Assert.Equal(ExpansionLifecycle.ServiceIdle, r.Root.GetUnit(1).Lifecycle);
        r.Memory.SetCpm(true); r.Memory.WritePagedPort(0x7cfd, 1);
        Assert.Equal(Mail(7), r.Accepted(1, 12));
        Assert.Equal(bytes, Enumerable.Range(0, 4096).Select(i => r.Memory.Read((ushort)(0xb000 + i))).ToArray());
        var submitted = bytes.Select(b => (byte)(b ^ 0x5a)).ToArray();
        for (var i = 0; i < submitted.Length; i++) r.Memory.Write((ushort)(0xb000 + i), submitted[i]);
        r.Accepted(1, 13, Ack()); r.Accepted(1, 8);
        Assert.Equal((ushort)42, r.Memory.Latches.PagedBank);
        for (var i = 0; i < 100 && r.Root.GetUnit(1).Lifecycle != ExpansionLifecycle.ServiceIdle; i++) r.Run();
        Assert.Equal(ExpansionLifecycle.ServiceIdle, r.Root.GetUnit(1).Lifecycle);
        Assert.Equal(Mail(7), r.Accepted(1, 12));
        Assert.Equal(submitted.Select(b => (byte)~b), Enumerable.Range(0, 4096).Select(i => r.Memory.Read((ushort)(0xb000 + i))));
    }

    [Fact]
    public void RepurposeCannotClaimAnotherFlipsPreviousBankAndOwnRepurposeRestoresBeforeClearing()
    {
        var r = new Rig(600); r.StartIdle(); r.Commit(); r.Accepted(1, 12); r.Accepted(1, 13, Ack());
        static byte[] Map(ushort first)
        {
            var data = new byte[9];
            for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), (ushort)(first + i));
            return data;
        }
        Assert.Equal((byte)ExpansionResult.InvalidState, r.Send(2, 10, Map(600))[1]);
        // Refusal and other-unit RESET/LOAD cannot steal or return unit one's live mapping.
        r.Accepted(2, 2); r.Load(2, [0x76]);
        Assert.Equal(r.Arena.GetGrant(1).Pages.Page3, r.Memory.Latches.PagedBank);
        r.Accepted(1, 10, Map(604));
        Assert.Equal((ushort)600, r.Memory.Latches.PagedBank);
        Assert.Equal((ushort)604, r.Arena.GetGrant(1).Pages.Page0);
        Assert.Equal(ExpansionLifecycle.ResetStopped, r.Root.GetUnit(1).Lifecycle);
        Assert.Equal(0, r.Arena.Acquire(1).Read(0));
    }

    [Fact]
    public void PauseRefusesOnlyLiveOwnerBeforeAndAfterAckWithoutPendingOrRevoke()
    {
        var r = new Rig(42); r.StartIdle(); r.StartIdle(2); r.Commit(); r.Commit(2);
        r.Accepted(2, 6); Assert.Equal(ExpansionLifecycle.Paused, r.Root.GetUnit(2).Lifecycle);
        r.Accepted(2, 8); Assert.Equal(ExpansionLifecycle.ServiceIdle, r.Root.GetUnit(2).Lifecycle);
        r.Accepted(1, 12); var unit = r.Root.GetUnit(1); var before = unit.CaptureFrontier();
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal((byte)ExpansionResult.InvalidState, r.Send(1, 6)[1]);
            Assert.Equal(0, r.Read(0x77fd) & 4); Assert.Equal(ExpansionLifecycle.ServiceIdle, unit.Lifecycle);
            Assert.Equal(before.Registers, unit.CaptureFrontier().Registers); Assert.Equal(before.TStates, unit.CaptureFrontier().TStates);
            Assert.Equal((ushort)515, r.Memory.Latches.PagedBank);
            if (i == 0) r.Accepted(1, 13, Ack());
        }
        r.Accepted(2, 6); Assert.Equal(ExpansionLifecycle.Paused, r.Root.GetUnit(2).Lifecycle);
        r.Accepted(1, 8); Assert.Equal((ushort)42, r.Memory.Latches.PagedBank);
        r.Accepted(1, 6); Assert.Equal(ExpansionLifecycle.Paused, unit.Lifecycle);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FlipDoesNotEnableMTransferOrAlterSurroundingMainMapping(bool cpm, bool transfer)
    {
        var r = new Rig(); r.StartIdle(); r.Arena.Acquire(1).Write(0xf000, 0x42);
        r.Memory.Write(0xb000, 0x55); r.Memory.SetCpm(true);
        if (transfer) r.Memory.WritePagedPort(0x7cfd, 1);
        r.Memory.SetCpm(cpm);
        var before = r.Memory.Pages.ToArray();
        r.Commit(); r.Accepted(1, 12);
        Assert.Equal(cpm, r.Memory.Latches.Cpm); Assert.Equal(transfer, r.Memory.Latches.PagedTransfer);
        Assert.Equal(before.Select(p => p.Mapping), r.Memory.Pages.Select(p => p.Mapping));
        for (var i = 0; i < 8; i++) Assert.Equal(before[i].Subwindows, r.Memory.Pages[i].Subwindows);
        Assert.Equal(0x55, r.Memory.Read(0xb000));
        r.Memory.SetCpm(true); r.Memory.WritePagedPort(0x7cfd, 1);
        Assert.Equal(0x42, r.Memory.Read(0xb000));
        for (var i = 0; i < 8; i++)
            if (i is not (4 or 5 or 6))
            {
                Assert.Equal(before[i].Mapping, r.Memory.Pages[i].Mapping);
                Assert.Equal(before[i].Subwindows, r.Memory.Pages[i].Subwindows);
            }
        Assert.Equal(0, r.Memory.Read(0xf000)); // Main F000 is never the exchange payload.
    }

    [Theory]
    [InlineData(0, 0xc000, true)]
    [InlineData(0, 0xc001, false)]
    [InlineData(0xbfff, 1, true)]
    [InlineData(0xbfff, 2, false)]
    [InlineData(0xc000, 1, false)]
    [InlineData(0xefff, 1, false)]
    [InlineData(0xefff, 2, false)]
    [InlineData(0xf000, 4096, true)]
    [InlineData(0xffff, 1, true)]
    [InlineData(0xffff, 2, false)]
    [InlineData(0, 65536, false)]
    public void LoadAdmitsOnlyWholePrivateOrPayloadSpans(int offset, int count, bool accepted)
    {
        var r = new Rig(); var unit = r.Root.GetUnit(1); var epoch = unit.ExecutionEpoch;
        var data = Enumerable.Repeat((byte)0xed, count).ToArray();
        var result = r.Send(1, 4, Range((ushort)offset, data));
        Assert.Equal((byte)(accepted ? ExpansionResult.Accepted : ExpansionResult.InvalidFrame), result[1]);
        Assert.Equal(epoch + (accepted ? 1UL : 0UL), unit.ExecutionEpoch);
        Assert.Equal(ExpansionLifecycle.ResetStopped, unit.Lifecycle);
        Assert.Equal((ushort)0xffff, unit.CaptureFrontier().Registers.SP); // No initializer decoding.
        for (var i = 0; i < 65536; i++)
            Assert.Equal(accepted && i >= offset && i < offset + count ? 0xed : 0, unit.ReadMemory((ushort)i));
    }

    [Fact]
    public void BadLoadPreservesLiveFlipOutboxBytesAndExecutionEpoch()
    {
        var r = new Rig(42); r.StartIdle(); var mail = r.Commit(); r.Accepted(1, 12);
        var unit = r.Root.GetUnit(1); var epoch = unit.ExecutionEpoch; var bytes = r.Arena.Acquire(1).Copy();
        Assert.Equal((byte)ExpansionResult.InvalidFrame, r.Send(1, 4, Range(0xbfff, [0xaa, 0xbb]))[1]);
        Assert.Equal(epoch, unit.ExecutionEpoch); Assert.Equal(bytes, r.Arena.Acquire(1).Copy());
        Assert.Equal((ushort)515, r.Memory.Latches.PagedBank); Assert.Equal(mail, r.Accepted(1, 12));
        r.Accepted(1, 13, Ack()); r.Accepted(1, 8); Assert.Equal((ushort)42, r.Memory.Latches.PagedBank);
    }

    [Theory]
    [InlineData(0xbfff, 0xbfff, true)]
    [InlineData(0xbfff, 0xc000, false)]
    [InlineData(0xc000, 0xbfff, false)]
    [InlineData(0xffff, 0xbfff, false)]
    public void HandoverChecksRetainedStackAndContinuationBeforePublishing(int sp, int pc, bool accepted)
    {
        var r = new Rig(42);
        r.Load(1, [0x31, (byte)sp, (byte)(sp >> 8), 1, 0xfd, 0x74, 0x3e, 7,
            0xc3, (byte)(pc - 2), (byte)((pc - 2) >> 8)]);
        r.Accepted(1, 4, Range((ushort)(pc - 2), [0xed, 0x79])); r.Accepted(1, 7); r.Run();
        var unit = r.Root.GetUnit(1); Assert.Equal(ExpansionLifecycle.ServiceIdle, unit.Lifecycle);
        Assert.Equal((ushort)pc, unit.CaptureFrontier().Registers.PC);
        var frame = r.Commit(); var before = unit.CaptureFrontier(); var response = r.Send(1, 12);
        Assert.Equal((byte)(accepted ? ExpansionResult.Accepted : ExpansionResult.InvalidState), response[1]);
        Assert.Equal((ushort)(accepted ? 515 : 42), r.Memory.Latches.PagedBank);
        Assert.Equal(before.Registers, unit.CaptureFrontier().Registers); Assert.Equal(before.TStates, unit.CaptureFrontier().TStates);
        if (accepted) Assert.Equal(frame, response[40..]); else Assert.Equal(40, response.Length);
        r.Accepted(1, 13, Ack()); // Refusal did not consume the outbox.
    }

    private sealed class NoBus : IBus
    {
        public byte Read(ushort address) => throw new InvalidOperationException();
        public void Write(ushort address, byte value) => throw new InvalidOperationException();
        public byte In(ushort port) => throw new InvalidOperationException();
        public void Out(ushort port, byte value) => throw new InvalidOperationException();
        public void Contend(ushort address, int tstates) => throw new InvalidOperationException();
        public byte IntVector() => throw new InvalidOperationException();
    }
}
