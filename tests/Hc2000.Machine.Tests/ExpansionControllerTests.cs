using System.Buffers.Binary;
using System.Text.Json;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Z80.Frontier;
using Xunit;

namespace Hc2000.Machine.Tests;

[Collection("paged-ram")]
public sealed class ExpansionControllerTests
{
    private sealed class Rig
    {
        public readonly HcMemory Memory = new(MachineProfile.Hc2000V2PagedExpansionV1.Memory,
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        public readonly ExpansionArena Arena;
        public readonly ExpansionController Root;
        public readonly PortDecoder Ports;
        public Rig()
        {
            Arena = Memory.EnableExpansionArena(MachineProfile.Hc2000V2PagedExpansionV1);
            Root = new(Arena);
            Ports = new(Root.Mappings(), Root, Root.MainOrigin);
        }
        public void Select(ushort id) { Ports.Write(0x7bfd, (byte)id, 0); Ports.Write(0x7afd, (byte)(id >> 8), 0); }
        public long Now;
        public void Verb(byte value, long tick = 0)
        {
            Now = Math.Max(Now, tick); Ports.Write(0x79fd, value, Now);
        }
        public void Begin(ushort id, byte[] request)
        {
            Select(id); Verb(1);
            foreach (var value in request) Ports.Write(0x78fd, value, 0);
        }
        public byte[] Response()
        {
            var header = new byte[40];
            for (var i = 0; i < header.Length; i++) header[i] = Ports.Read(0x78fd, 0);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            Assert.InRange(count, 0u, 69636u);
            var all = new byte[40 + (int)count]; header.CopyTo(all, 0);
            for (var i = 40; i < all.Length; i++) all[i] = Ports.Read(0x78fd, 0);
            return all;
        }
        public byte[] Send(ushort id, byte operation, byte[]? payload = null, ulong resource = 1,
            ulong execution = 1, ulong map = 1, long tick = 0)
        {
            Begin(id, Request(operation, payload, resource, execution, map)); Verb(2, tick);
            Assert.Equal(2, Ports.Read(0x77fd, tick) & 7);
            var response = Response(); Verb(5, tick); return response;
        }
        public void Accepted(ushort id, byte op, byte[]? payload = null, ulong execution = 1, long tick = 0) =>
            Assert.Equal(0, Send(id, op, payload, execution: execution, tick: tick)[1]);
        public void Cycle(ushort id)
        {
            var unit = Root.GetUnit(id); var effect = unit.PeekNextFrontier(); var tick = unit.NextMasterTick;
            var value = effect.Kind switch
            {
                FrontierEffectKind.M1Read or FrontierEffectKind.MemoryRead => unit.ReadMemory(effect.Address),
                FrontierEffectKind.PortRead => Ports.CreateView(new(id, unit.ExecutionEpoch, CpuPortRole.Compute)).Read(effect.Address, tick),
                FrontierEffectKind.InterruptAck => (byte)0xff,
                _ => (byte)0
            };
            if (effect.Kind == FrontierEffectKind.MemoryWrite) unit.WriteMemory(effect.Address, effect.WriteValue);
            if (effect.Kind == FrontierEffectKind.PortWrite)
                Ports.CreateView(new(id, unit.ExecutionEpoch, CpuPortRole.Compute)).Write(effect.Address, effect.WriteValue, tick);
            unit.CommitFrontier(value, tick); Root.AcknowledgeFrontier(id, tick); Now = tick;
        }
    }

    private static byte[] Request(byte op, byte[]? payload = null, ulong resource = 1, ulong execution = 1, ulong map = 1)
    {
        payload ??= [];
        var frame = new byte[40 + payload.Length]; frame[0] = 1; frame[1] = op;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8), 123);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(16), resource);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(24), execution);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(32), map);
        payload.CopyTo(frame, 40); return frame;
    }
    private static byte[] Range(ushort start, uint count, params byte[] data)
    {
        var payload = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, start);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), count);
        data.CopyTo(payload, 8); return payload;
    }
    private static byte[] Mail(ulong id, params byte[] data)
    {
        var frame = new byte[73]; frame[0] = (byte)data.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), id); data.CopyTo(frame, 9); return frame;
    }
    private static void Receipt(byte[] response, ExpansionResult result, ExpansionLifecycle state,
        ulong resource = 1, ulong execution = 1, ulong map = 1)
    {
        Assert.Equal(1, response[0]); Assert.Equal((byte)result, response[1]);
        Assert.Equal((byte)state, response[2]); Assert.Equal(0, response[3]);
        Assert.Equal(123ul, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(8)));
        Assert.Equal(resource, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(16)));
        Assert.Equal(execution, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(24)));
        Assert.Equal(map, BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(32)));
    }

    [Fact]
    public void QueryUsesTheExactWireLayoutAndLatchedSelectorAndExplicitReceiptAck()
    {
        var r = new Rig(); r.Begin(1, Request(1, resource: 0, execution: 0, map: 0));
        r.Select(16); r.Verb(2);
        var response = r.Response(); Receipt(response, ExpansionResult.Accepted, ExpansionLifecycle.ResetStopped);
        Assert.Equal(72, response.Length); var p = response.AsSpan(40);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(p)); Assert.Equal((byte)CpuPortRole.Compute, p[2]);
        Assert.Equal(0, p[3]); Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(p[4..]));
        Assert.Equal(64, BinaryPrimitives.ReadUInt16LittleEndian(p[6..]));
        Assert.Equal(16384u, BinaryPrimitives.ReadUInt32LittleEndian(p[8..]));
        Assert.Equal(65544u, BinaryPrimitives.ReadUInt32LittleEndian(p[12..]));
        Assert.Equal(5ul, BinaryPrimitives.ReadUInt64LittleEndian(p[16..]));
        Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(p[24..]));
        Assert.Equal(255, r.Ports.Read(0x78fd, 0));
        r.Verb(1); Assert.Equal(0x12, r.Ports.Read(0x77fd, 0)); // Busy; receipt retained after complete read
        r.Verb(4); Assert.Equal(response, r.Response()); r.Verb(5);
        Assert.Equal(0, r.Ports.Read(0x77fd, 0) & 7);
        Receipt(r.Send(0, 1, resource: 0, execution: 0, map: 0), ExpansionResult.Accepted, ExpansionLifecycle.Running);
    }

    [Fact]
    public void AllSixteenBitAddressesAndOriginsKeepExactlyEightDirectionalEndpoints()
    {
        var r = new Rig(); var mappings = r.Root.Mappings();
        Assert.Equal(8, mappings.Count); Assert.All(mappings, row => Assert.Equal(0xffff, row.Mask));
        var reads = new HashSet<ushort> { 0x7bfd, 0x7afd, 0x78fd, 0x77fd, 0x76fd, 0x75fd, 0x74fd };
        var writes = new HashSet<ushort> { 0x7bfd, 0x7afd, 0x79fd, 0x78fd, 0x75fd, 0x74fd };
        for (var i = 0; i <= ushort.MaxValue; i++)
        {
            var port = (ushort)i;
            Assert.Equal(reads.Contains(port), mappings.Any(m => m.Match == port && (m.Access & PortAccess.Read) != 0));
            Assert.Equal(writes.Contains(port), mappings.Any(m => m.Match == port && (m.Access & PortAccess.Write) != 0));
        }
        var foreign = r.Ports.CreateView(new(1, 1, CpuPortRole.Compute));
        foreign.Write(0x7bfd, 16, 0); Assert.Equal(0, r.Ports.Read(0x7bfd, 0));
        Assert.Equal(255, foreign.Read(0x76fd, 0)); Assert.Equal(255, r.Ports.Read(0x74fd, 0));
        r.Root.Write(0x7bfd, 16, 0); Assert.Equal(0, r.Ports.Read(0x7bfd, 0));
        var staleMain = r.Ports.CreateView(new(0, 2, CpuPortRole.Main));
        staleMain.Write(0x7bfd, 16, 0); Assert.Equal(255, staleMain.Read(0x76fd, 0));
        Assert.Equal(0, r.Ports.Read(0x7bfd, 0)); Assert.Equal(1, r.Ports.Read(0x76fd, 0));
    }

    [Fact]
    public void MalformedStaleAndAbortedRequestsNeverPublishBytesOrEpochs()
    {
        var r = new Rig(); var lease = r.Arena.Acquire(1); lease.Write(0, 0xaa);
        Receipt(r.Send(1, 3, [0x55], execution: 2), ExpansionResult.StaleEpoch, ExpansionLifecycle.ResetStopped);
        Receipt(r.Send(1, 3, [0x55], execution: 0), ExpansionResult.InvalidFrame, ExpansionLifecycle.ResetStopped);
        Receipt(r.Send(1, 4, Range(0xffff, 2, 1, 2)), ExpansionResult.InvalidFrame, ExpansionLifecycle.ResetStopped);
        Receipt(r.Send(1, 4, Range(0, uint.MaxValue)), ExpansionResult.InvalidFrame, ExpansionLifecycle.ResetStopped);
        var bad = Request(3, [0x55]); bad[2] = 1;
        r.Begin(1, bad); r.Verb(2); Receipt(r.Response(), ExpansionResult.InvalidFrame, ExpansionLifecycle.ResetStopped); r.Verb(5);
        r.Begin(1, Request(3, [0x55])[..40]); r.Verb(2);
        Receipt(r.Response(), ExpansionResult.InvalidFrame, ExpansionLifecycle.ResetStopped); r.Verb(5);
        r.Begin(1, Request(3, [0x55])); r.Verb(3);
        Assert.Equal(0xaa, lease.Read(0)); Assert.Equal(1ul, r.Root.GetUnit(1).ExecutionEpoch);
        Receipt(r.Send(17, 1, resource: 0, execution: 0, map: 0), ExpansionResult.NotFound, ExpansionLifecycle.ResetStopped, 0, 0, 0);
        Receipt(r.Send(0, 3, [0]), ExpansionResult.DeniedOrigin, ExpansionLifecycle.Running);
    }

    [Fact]
    public void LoadClearAndRepurposeAreAtomicAndOldExecutionAndMemoryViewsAreRevoked()
    {
        var r = new Rig(); var stale = r.Ports.CreateView(new(1, 1, CpuPortRole.Compute));
        var oldLease = r.Arena.Acquire(1);
        r.Accepted(1, 4, Range(0xfffe, 2, 0x33, 0x44));
        Assert.Equal(255, stale.Read(0x74fd, 0));
        Assert.Equal(new byte[] { 0x33, 0x44 }, r.Send(1, 5, Range(0xfffe, 2), execution: 2)[40..]);
        r.Accepted(1, 3, [0x66], execution: 2);
        Assert.Equal(0x66, oldLease.Read(0)); Assert.Equal(0x66, oldLease.Read(0xffff));
        // Page 0 sits below the compute arena (map v2: pages 512-575) -> refused.
        var badPages = new byte[] { 0, 0, 0x50, 2, 0x51, 2, 0x52, 2, 0 };
        Receipt(r.Send(1, 10, badPages, execution: 3), ExpansionResult.InvalidState, ExpansionLifecycle.ResetStopped, execution: 3);
        Assert.Equal(0x66, oldLease.Read(0));
        // Pages 600-603: above the arena, unclaimed, not a unit roster.
        var pages = new byte[] { 0x50, 2, 0x51, 2, 0x52, 2, 0x53, 2, 0 };
        Receipt(r.Send(1, 10, pages, execution: 3), ExpansionResult.Accepted, ExpansionLifecycle.ResetStopped, 2, 4, 2);
        Assert.Throws<InvalidOperationException>(() => oldLease.Read(0));
        Assert.Equal(0, r.Arena.Acquire(1).Read(0)); Assert.Equal(0, r.Arena.Acquire(1).Read(0xffff));
        var peer = r.Root.GetUnit(2).CaptureFrontier(); Assert.Equal(0, peer.TStates);
    }

    [Fact]
    public void PauseAcknowledgesACycleNotRetirementAndResumeDoesNotRestartDecodedInstruction()
    {
        var r = new Rig();
        r.Accepted(1, 4, Range(0, 5, 0x3e, 0x42, 0x32, 0x00, 0x40)); // LD A,42; LD (4000),A
        r.Accepted(1, 7, execution: 2);
        r.Begin(1, Request(6, execution: 2)); r.Verb(2);
        Assert.Equal(4, r.Ports.Read(0x77fd, 0) & 7); Assert.Equal(255, r.Ports.Read(0x78fd, 0));
        r.Cycle(1);
        Receipt(r.Response(), ExpansionResult.Accepted, ExpansionLifecycle.Paused, execution: 2); r.Verb(5);
        var state = r.Root.GetUnit(1).CaptureFrontier();
        Assert.Equal(4, state.TStates); Assert.Equal((ushort)1, state.Registers.PC); Assert.NotNull(state.Pending);
        Receipt(r.Send(1, 4, Range(0, 1, 0)), ExpansionResult.StaleEpoch, ExpansionLifecycle.Paused, execution: 2);
        Receipt(r.Send(1, 4, Range(0, 1, 0), execution: 2), ExpansionResult.InvalidState, ExpansionLifecycle.Paused, execution: 2);
        r.Accepted(1, 8, execution: 2, tick: 1000);
        Assert.Equal(1015, r.Root.GetUnit(1).NextMasterTick);
        r.Cycle(1); Assert.Equal((byte)0x42, r.Root.GetUnit(1).CaptureFrontier().Registers.A);
        for (var i = 0; i < 4; i++) r.Cycle(1);
        Assert.Equal(0x42, r.Arena.Acquire(1).Read(0x4000));
    }

    [Fact]
    public void EndlessPrefixesCanPauseButRequireExplicitResetForMutation()
    {
        var r = new Rig(); r.Accepted(1, 3, [0xdd]); r.Accepted(1, 7, execution: 2);
        for (var i = 0; i < 100; i++) r.Cycle(1);
        r.Begin(1, Request(6, execution: 2)); r.Verb(2); r.Cycle(1); r.Response(); r.Verb(5);
        Assert.Equal(ExpansionLifecycle.Paused, r.Root.GetUnit(1).Lifecycle);
        Receipt(r.Send(1, 3, [0], execution: 2), ExpansionResult.InvalidState, ExpansionLifecycle.Paused, execution: 2);
        r.Accepted(1, 2, execution: 2);
        var state = r.Root.GetUnit(1).CaptureFrontier();
        Assert.Null(state.Pending); Assert.Equal(0, state.TStates); Assert.Equal((ushort)0, state.Registers.PC);
        Assert.Equal(0xdd, r.Arena.Acquire(1).Read(0));
        r.Accepted(1, 3, [0], execution: 3);
    }

    [Fact]
    public void MailboxesHaveExplicitBusyRewindAndAckAndCannotBeRetargeted()
    {
        var r = new Rig(); var one = r.Ports.CreateView(new(1, 1, CpuPortRole.Compute));
        var two = r.Ports.CreateView(new(2, 1, CpuPortRole.Compute)); var mail = Mail(7, 0x42);
        r.Accepted(1, 11, mail); Assert.Equal((byte)0xff, two.Read(0x75fd, 0));
        Receipt(r.Send(1, 11, Mail(8, 0x43)), ExpansionResult.Busy, ExpansionLifecycle.ResetStopped);
        Assert.Equal(mail, Enumerable.Range(0, 73).Select(_ => one.Read(0x75fd, 0)).ToArray());
        Assert.Equal(255, one.Read(0x75fd, 0)); one.Write(0x74fd, 4, 0);
        Assert.Equal(mail[0], one.Read(0x75fd, 0)); one.Write(0x74fd, 5, 0);
        r.Accepted(1, 11, Mail(8, 0x43));
        one.Write(0x74fd, 1, 0); foreach (var value in mail) one.Write(0x75fd, value, 0); one.Write(0x74fd, 2, 0);
        Assert.Equal(mail, r.Send(1, 12)[40..]); Assert.Equal(mail, r.Send(1, 12)[40..]);
        var id = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(id, 8);
        Receipt(r.Send(1, 13, id), ExpansionResult.InvalidState, ExpansionLifecycle.ResetStopped);
        BinaryPrimitives.WriteUInt64LittleEndian(id, 7); r.Accepted(1, 13, id);
        Receipt(r.Send(1, 12), ExpansionResult.Busy, ExpansionLifecycle.ResetStopped);
    }

    [Theory]
    [InlineData(6, ExpansionLifecycle.WaitingMailbox)]
    [InlineData(7, ExpansionLifecycle.ServiceIdle)]
    public void MailboxSuspensionIsRetiredAndPausePreventsMessageWake(byte command, ExpansionLifecycle expected)
    {
        var r = new Rig(); r.Accepted(1, 4, Range(0, 7, 0x01, 0xfd, 0x74, 0x3e, command, 0xed, 0x79));
        r.Accepted(1, 7, execution: 2);
        var unit = r.Root.GetUnit(1);
        for (var i = 0; i < 30 && unit.Runnable; i++) r.Cycle(1);
        Assert.Equal(expected, unit.Lifecycle); Assert.Null(unit.CaptureFrontier().Pending);
        r.Accepted(1, 6, execution: 2);
        r.Accepted(1, 11, Mail(3, 1), execution: 2);
        Assert.Equal(ExpansionLifecycle.Paused, unit.Lifecycle);
        r.Accepted(1, 8, execution: 2, tick: 10000);
        Assert.Equal(command == 6 ? ExpansionLifecycle.Running : ExpansionLifecycle.ServiceIdle, unit.Lifecycle);
        if (command == 7) r.Accepted(1, 8, execution: 2, tick: 10001);
        Assert.True(unit.Runnable);
    }

    [Fact]
    public void ObserveIsADetachedCompleteFrontierAndLogicalImageWithBoundedJson()
    {
        var r = new Rig(); r.Accepted(1, 4, Range(123, 1, 0x42));
        var response = r.Send(1, 9, execution: 2);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(40)); Assert.InRange(length, 1u, 4096u);
        var cpu = JsonSerializer.Deserialize<FrontierState>(response.AsSpan(44, (int)length), new JsonSerializerOptions { IncludeFields = true });
        Assert.NotNull(cpu); Assert.Equal((ushort)0xffff, cpu.Registers.SP); Assert.Equal((ushort)0xffff, cpu.Registers.AF);
        Assert.Equal(65536 + 44 + (int)length, response.Length);
        Assert.Equal(0x42, response[44 + (int)length + 123]);
        r.Arena.Acquire(1).Write(123, 0x99); Assert.Equal(0x42, response[44 + (int)length + 123]);
    }
}
