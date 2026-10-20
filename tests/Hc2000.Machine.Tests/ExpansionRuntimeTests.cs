using System.Buffers.Binary;
using System.Text.Json;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Timing;
using Hc2000.Z80;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Machine.Tests;

// Each rig below owns a full production arena; do not also construct the unrelated GiB PagedRamFixture.
[CollectionDefinition("expansion-runtime", DisableParallelization = true)]
public sealed class ExpansionRuntimeCollection;

[Collection("expansion-runtime")]
public sealed class ExpansionRuntimeTests(ITestOutputHelper output)
{
    private sealed class Rig
    {
        public readonly DeviceScheduler Scheduler = new();
        public readonly FrameClock Clock;
        public readonly CpuTimeMapper Mapper = new();
        public readonly HcMemory Memory;
        public readonly ExpansionController Root;
        public readonly PortDecoder Ports;
        public readonly Z80.Z80 Main;
        public readonly ExpansionFrontierDriver Driver;
        public Rig(byte[]? program = null)
        {
            var profile = MachineProfile.Hc2000V2PagedExpansionV1;
            Memory = new(profile.Memory, new byte[0x4000], new byte[0x4000], new byte[0x4000]);
            // Guest test programs run in native RAM: arbitrary ROM bytes crossing 0008 would
            // correctly trigger the real IF1 M1 overlay, not continue this synthetic program.
            if (program is not null)
                for (var i = 0; i < program.Length; i++) Memory.Write(checked((ushort)(0x8000 + i)), program[i]);
            Root = new(Memory.EnableExpansionArena(profile)); Clock = new(Scheduler, 1);
            var idle = new PortDevice();
            var core = HcIoProfile.WithPagedRam(Memory, idle, idle, idle, idle, idle, idle);
            Ports = new(ExpansionIoProfile.Compose(profile, Memory, core, Root), Root, Root.MainOrigin);
            Main = new(new ForbiddenBus());
            if (program is not null) Main.R.PC = 0x8000;
            Driver = new(Scheduler, Clock, Mapper, Root, Memory, Ports, Main);
        }
        public void Write(ushort port, byte value) => Ports.Write(port, value, Scheduler.Now);
        public byte Read(ushort port) => Ports.Read(port, Scheduler.Now);
        public byte[] Send(ushort id, byte operation, byte[]? payload = null, ulong execution = 1)
        {
            Write(0x7bfd, (byte)id); Write(0x7afd, (byte)(id >> 8)); Write(0x79fd, 1);
            foreach (var value in Request(operation, payload, execution)) Write(0x78fd, value);
            Write(0x79fd, 2);
            var header = Enumerable.Range(0, 40).Select(_ => Read(0x78fd)).ToArray();
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)); Assert.InRange(count, 0u, 69636u);
            var data = Enumerable.Range(0, (int)count).Select(_ => Read(0x78fd)).ToArray();
            Write(0x79fd, 5); Driver.Synchronize(); return [.. header, .. data];
        }
    }
    private sealed class ForbiddenBus : IBus
    {
        public byte Read(ushort address) => throw new InvalidOperationException("Frontier must not call a CPU bus.");
        public void Write(ushort address, byte value) => throw new InvalidOperationException();
        public byte In(ushort port) => throw new InvalidOperationException();
        public void Out(ushort port, byte value) => throw new InvalidOperationException();
        public void Contend(ushort address, int tstates) => throw new InvalidOperationException();
        public byte IntVector() => throw new InvalidOperationException();
    }
    private static byte[] Request(byte operation, byte[]? payload = null, ulong execution = 1)
    {
        payload ??= []; var frame = new byte[40 + payload.Length]; frame[0] = 1; frame[1] = operation;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8), 321);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(24), execution);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(32), 1); payload.CopyTo(frame, 40); return frame;
    }
    private static byte[] Image(byte[] code)
    {
        var data = new byte[8 + code.Length]; BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)code.Length);
        code.CopyTo(data, 8); return data;
    }
    private static byte[] Mail(ulong id, byte value)
    {
        var data = new byte[73]; data[0] = 1; BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(1), id); data[9] = value; return data;
    }
    private static void Output(List<byte> code, ushort port, byte value) => code.AddRange(new byte[] { 1, (byte)port, (byte)(port >> 8), 0x3e, value, 0xed, 0x79 });
    private static void GuestRequest(List<byte> code, byte operation, byte[]? payload = null, ulong execution = 1, bool observe = false)
    {
        Output(code, 0x7bfd, 1); Output(code, 0x7afd, 0); Output(code, 0x79fd, 1);
        foreach (var value in Request(operation, payload, execution)) Output(code, 0x78fd, value);
        Output(code, 0x79fd, 2);
        // Poll readiness, not an assumption about target CPU retirement or host scheduling.
        code.AddRange(new byte[] { 1, 0xfd, 0x77, 0xed, 0x78, 0xe6, 2, 0x28, 0xfa });
        if (observe)
        {
            code.AddRange(new byte[] { 0x21, 0, 0x90, 1, 0xfd, 0x78 });
            for (var i = 0; i < 113; i++) code.AddRange(new byte[] { 0xed, 0x78, 0x77, 0x23 });
        }
        Output(code, 0x79fd, 5);
    }

    [Fact]
    public void PhaseFiveDeviceThenMainThenComputeOrderIsStableAndOneEventPerCpu()
    {
        var r = new Rig(); Assert.Equal(0, r.Send(1, 7)[1]);
        var events = new List<(long Tick, long Main, long Compute)>();
        r.Scheduler.RegisterDevice(5, e => events.Add((e.DueTick, r.Main.TStates, r.Root.GetUnit(1).CaptureFrontier().TStates)));
        r.Scheduler.Schedule(new(20, EventPhase.Frame, 5, 0));
        r.Scheduler.AdvanceTo(20);
        Assert.Equal(new[] { (20L, 0L, 0L) }, events);
        Assert.Equal(4, r.Main.TStates); Assert.Equal(4, r.Root.GetUnit(1).CaptureFrontier().TStates);
        var queued = r.Scheduler.PendingEvents.Where(e => e.DeviceId >= ExpansionFrontierDriver.FirstCpuDeviceId).ToArray();
        Assert.Equal(2, queued.Length); Assert.All(queued, e => Assert.Equal(5, (int)e.Phase));
        Assert.Equal(new[] { 1000, 1001 }, queued.Select(e => e.DeviceId));
        r.Scheduler.AdvanceTo(20000);
        Assert.Equal(r.Main.TStates, r.Root.GetUnit(1).CaptureFrontier().TStates);
        Assert.Equal(r.Scheduler.Now, r.Mapper.Map(r.Main.TStates));
        Assert.InRange(r.Scheduler.PendingEvents.Count(e => e.DeviceId >= 1000), 1, 17);
    }

    [Fact]
    public void MainGuestLoadsStartsExchangesBothDirectionsAndAcknowledgesTheComputeReply()
    {
        var reply = Mail(99, 0x43); var auxiliary = new List<byte> { 0x31, 0xff, 0xbf }; // LD SP,BFFF: job-private stack.
        Output(auxiliary, 0x74fd, 6); // WAIT retires before suspension, or falls through if RX is already full.
        auxiliary.AddRange(new byte[] { 1, 0xfd, 0x75, 0xed, 0x78, 0x32, 0, 0x80 });
        Output(auxiliary, 0x74fd, 5); Output(auxiliary, 0x74fd, 1);
        foreach (var value in reply) Output(auxiliary, 0x75fd, value);
        Output(auxiliary, 0x74fd, 2); Output(auxiliary, 0x74fd, 7);
        var main = new List<byte>();
        GuestRequest(main, 4, Image(auxiliary.ToArray())); GuestRequest(main, 7, execution: 2);
        GuestRequest(main, 11, Mail(98, 0x42), execution: 2);
        main.AddRange(Enumerable.Repeat((byte)0, 2000));
        GuestRequest(main, 12, execution: 2, observe: true);
        var id = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(id, 99);
        GuestRequest(main, 13, id, execution: 2); main.Add(0x76);
        var r = new Rig(main.ToArray()); Assert.True(r.Driver.TryRunFrame(() => true));
        Assert.True(r.Main.Halted, $"Main PC={r.Main.R.PC:X4} at {r.Scheduler.Now}."); Assert.Equal(0, r.Memory.Read(0x9001));
        Assert.Equal(reply, Enumerable.Range(0, 73).Select(i => r.Memory.Read((ushort)(0x9028 + i))).ToArray());
        var unit = r.Root.GetUnit(1); Assert.Equal(ExpansionLifecycle.ServiceIdle, unit.Lifecycle);
        Assert.Equal(1, unit.ReadMemory(0x8000)); Assert.Equal(ExpansionResult.Busy, (ExpansionResult)r.Send(1, 12, execution: 2)[1]);
        Assert.DoesNotContain(r.Scheduler.PendingEvents, e => e.DeviceId == 1001);
    }

    [Fact]
    public void MainRequestAtTheSameTickPrecedesTheLaterComputeFrontier()
    {
        // Matched instruction timings put both OUT effects at tick130, before either retires.
        var main = new byte[] { 1, 0xfd, 0x79, 0x3e, 2, 0xed, 0x79, 0x76 };
        var r = new Rig(main);
        Assert.Equal(0, r.Send(1, 4, Image(new byte[] { 1, 0xfd, 0x74, 0x3e, 1, 0xed, 0x79, 0x76 }))[1]);
        Assert.Equal(0, r.Send(1, 7, execution: 2)[1]);
        r.Write(0x79fd, 1); foreach (var value in Request(6, execution: 2)) r.Write(0x78fd, value);
        r.Scheduler.AdvanceTo(130);
        Assert.Equal(ExpansionLifecycle.Paused, r.Root.GetUnit(1).Lifecycle);
        Assert.Equal(2, r.Read(0x77fd) & 7);
        Assert.DoesNotContain(r.Scheduler.PendingEvents, e => e.DeviceId == 1001);
        var local = r.Ports.CreateView(new(1, 2, CpuPortRole.Compute));
        Assert.Equal(4, local.Read(0x74fd, r.Scheduler.Now) & 4);
        var held = r.Root.GetUnit(1).CaptureFrontier(); Assert.Equal(26, held.TStates); Assert.NotNull(held.Pending);
        r.Scheduler.AdvanceTo(10000);
        var later = r.Root.GetUnit(1).CaptureFrontier();
        Assert.Equal(held.TStates, later.TStates); Assert.Equal(held.Pending, later.Pending);
        Assert.Equal(held.Temporaries, later.Temporaries);
    }

    [Fact]
    public void EndlessMainPrefixesYieldToDevicesAndAllSixteenComputeCpus()
    {
        var r = new Rig(Enumerable.Repeat((byte)0xdd, 16384).ToArray());
        for (ushort id = 1; id <= 16; id++) Assert.Equal(0, r.Send(id, 7)[1]);
        r.Scheduler.AdvanceTo(1000);
        Assert.Equal(200, r.Main.TStates); Assert.False(r.Main.AtInstructionBoundary);
        for (ushort id = 1; id <= 16; id++) Assert.Equal(200, r.Root.GetUnit(id).CaptureFrontier().TStates);
        Assert.Equal(17, r.Scheduler.PendingEvents.Count(e => e.DeviceId >= 1000));
        Assert.False(r.Driver.TryRunFrame(() => false)); Assert.Equal(1000, r.Scheduler.Now);
        Assert.True(r.Driver.TryRunFrame(() => true)); Assert.Equal(1, r.Clock.CompletedFrames);
    }

    [Fact]
    public void MachineRunnerStopsAtACommonPartialFrontierAndResumesTheSameFrameWithoutStep()
    {
        var r = new Rig(new byte[] { 0x3e, 0x42, 0x76 });
        var ula = new Hc2000.Machine.Video.Ula(r.Memory, new Hc2000.Machine.Input.KeyboardMatrix(), r.Scheduler, r.Clock, 2);
        var machine = new Machine(r.Scheduler, r.Clock, new(r.Scheduler, r.Mapper), ula,
            () => throw new InvalidOperationException("Expansion must not enter legacy Step."), () => r.Main.TStates, r.Driver);
        Assert.False(machine.TryRunFrame(() => r.Scheduler.Now < 20, out var partial)); Assert.Null(partial);
        Assert.Equal(20, r.Scheduler.Now); Assert.Equal(4, r.Main.TStates);
        Assert.NotNull(r.Main.CaptureFrontier().Pending);
        Assert.True(machine.TryRunFrame(() => true, out var complete)); Assert.NotNull(complete);
        Assert.Equal(1, r.Clock.CompletedFrames); Assert.Equal(0x42, r.Main.R.A); Assert.True(r.Main.Halted);
    }

    private static Machine SnapshotMachine(Rig r, HcsWavPolicy? policy = null)
    {
        var ula = new Hc2000.Machine.Video.Ula(r.Memory, new Hc2000.Machine.Input.KeyboardMatrix(), r.Scheduler, r.Clock, 2);
        var machine = new Machine(r.Scheduler, r.Clock, new(r.Scheduler, r.Mapper), ula,
            () => throw new InvalidOperationException("Expansion must not enter legacy Step."), () => r.Main.TStates, r.Driver);
        machine.AttachSnapshotState(MachineProfile.Hc2000V2PagedExpansionV1, r.Clock, r.Memory, r.Mapper,
            new Z80SnapshotSeam(r.Main), 2, wavPolicy: policy ?? new(TransientBytes: 16L * 0x44000000));
        return machine;
    }

    [Theory]
    [InlineData(4L * 1024 * 1024 * 1024)]
    [InlineData(16L * 0x44000000 - 1)]
    public void LowerBoundPolicyRefusesBothDirectionsWithoutEscalatingOrMutating(long cap)
    {
        var r = new Rig(); var machine = SnapshotMachine(r, new(TransientBytes: cap));
        var before = r.Main.CaptureFrontier(); var events = r.Scheduler.PendingEvents.ToArray();
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => machine.Save()).Error);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() => machine.Load(Array.Empty<byte>())).Error);
        Assert.Equal(before.TStates, r.Main.TStates); Assert.Equal(before.Pending, r.Main.CaptureFrontier().Pending);
        Assert.Equal(events, r.Scheduler.PendingEvents); Assert.Equal(0, r.Scheduler.Now);
        Assert.Equal(256 * 1024 * 1024, HcsWavPolicy.Default.MaxInputBytes);
        Assert.Equal(4L * 1024 * 1024 * 1024, HcsWavPolicy.Default.TransientBytes);
    }

    [Fact]
    public void SessionSavePreflightIsReadOnlyAtPartialCommonFrontier()
    {
        var r = new Rig(Enumerable.Repeat((byte)0xdd, 64).ToArray()); var machine = SnapshotMachine(r);
        r.Send(1, 1, Image(Enumerable.Repeat((byte)0xdd, 64).ToArray())); r.Send(1, 3, execution: 2);
        r.Scheduler.AdvanceTo(25);
        var cpu = JsonSerializer.Serialize(r.Main.CaptureFrontier(), new JsonSerializerOptions { IncludeFields = true });
        var events = r.Scheduler.PendingEvents.ToArray();
        machine.ValidateSessionSnapshotSave(); machine.ValidateSessionSnapshotSave();
        Assert.Equal(cpu, JsonSerializer.Serialize(r.Main.CaptureFrontier(), new JsonSerializerOptions { IncludeFields = true }));
        Assert.Equal(events, r.Scheduler.PendingEvents); Assert.Equal(25, r.Scheduler.Now);
        Assert.Equal(20, r.Mapper.WholeTicks); Assert.Equal(4, r.Main.TStates);
    }

    private sealed class CaptureTrap : ICpuSnapshotSeam
    {
        public CpuLogicalState Capture() => throw new InvalidOperationException("CPU capture must follow total admission.");
        public void Restore(CpuLogicalState state) => throw new InvalidOperationException();
    }

    [Fact]
    public void OverTotalLimitRefusesBeforeCpuCaptureOrRamCopy()
    {
        var r = new Rig(); var keyboard = new Hc2000.Machine.Input.KeyboardMatrix();
        var ula = new Hc2000.Machine.Video.Ula(r.Memory, keyboard, r.Scheduler, r.Clock, 2);
        var snapshot = new MachineSnapshot(MachineProfile.Hc2000V2PagedExpansionV1, r.Scheduler,
            r.Clock, r.Mapper, r.Memory, ula, keyboard, new CaptureTrap(), 2, null, null, expansion: r.Driver);
        Assert.Equal(HcsError.LimitExceeded, Assert.Throws<HcsException>(() =>
            snapshot.Save(new(TransientBytes: 16L * 0x44000000), new(TotalBytes: 1024))).Error);
        Assert.Equal(0, r.Scheduler.Now);
    }

    [Fact]
    public void ExistingHcsPathRestoresPartialPrefixesRootCollectionAndLocalMailboxExactlyOnce()
    {
        void ReportMemory(string phase)
        {
            var info = GC.GetGCMemoryInfo();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            output.WriteLine($"{phase}: managed={GC.GetTotalMemory(false)} heap={info.HeapSizeBytes} fragmented={info.FragmentedBytes} available={info.TotalAvailableMemoryBytes} peakRss={process.PeakWorkingSet64}");
        }
        var a = new Rig(Enumerable.Repeat((byte)0xdd, 256).ToArray()); var first = SnapshotMachine(a);
        Assert.Equal(0, a.Send(1, 4, Image(Enumerable.Repeat((byte)0xdd, 256).ToArray()))[1]);
        Assert.Equal(0, a.Send(1, 7, execution: 2)[1]);
        var message = Mail(0x12345678, 0x42);
        Assert.Equal(0, a.Send(2, 0x0b, message)[1]);
        var localA = a.Ports.CreateView(new(2, 1, CpuPortRole.Compute)); localA.Write(0x74fd, 1, a.Scheduler.Now);
        foreach (var b in message.Take(3)) localA.Write(0x75fd, b, a.Scheduler.Now);
        var request = Request(6, execution: 2);
        a.Write(0x7bfd, 1); a.Write(0x79fd, 1);
        foreach (var b in request.Take(17)) a.Write(0x78fd, b);
        a.Scheduler.AdvanceTo(25); Assert.Equal(4, a.Main.TStates);
        ReportMemory("before save");
        var saved = first.Save();
        ReportMemory("after save");
        using (var stream = new MemoryStream(saved))
        {
            var doc = HcsCodec.Read(stream, new MachineSchema(MachineProfile.Hc2000V2PagedExpansionV1), new(TotalBytes: 0x44000000));
            Assert.Equal("nmos-z80-frontier", doc.Manifest.GetProperty("cpu").GetProperty("modelId").GetString());
            Assert.Equal(a.Scheduler.DeviceGenerations.Select(g => "device-" + g.DeviceId).Order(StringComparer.Ordinal),
                doc.Manifest.GetProperty("clock").GetProperty("deviceGenerations").EnumerateArray()
                    .Select(g => g.GetProperty("id").GetString()!));
            Assert.Single(doc.Manifest.GetProperty("assets").EnumerateArray(), x => x.GetProperty("id").GetString() == "ram");
            var expansion = Assert.Single(doc.Manifest.GetProperty("components").EnumerateArray(), x => x.GetProperty("id").GetString() == "expansion-cpu-step1");
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(expansion.GetRawText()), 1, 1024 * 1024);
        }
        ReportMemory("after inspection");
        var restored = new Rig(); var second = SnapshotMachine(restored);
        ReportMemory("before load");
        second.Load(saved);
        ReportMemory("after load");
        var json = new JsonSerializerOptions { IncludeFields = true };
        Assert.Equal(JsonSerializer.Serialize(a.Main.CaptureFrontier(), json), JsonSerializer.Serialize(restored.Main.CaptureFrontier(), json));
        Assert.Equal(a.Scheduler.PendingEvents, restored.Scheduler.PendingEvents);
        for (ushort id = 1; id <= 16; id++)
            Assert.Equal(JsonSerializer.Serialize(a.Root.GetUnit(id).CaptureFrontier(), json), JsonSerializer.Serialize(restored.Root.GetUnit(id).CaptureFrontier(), json));
        foreach (var r in new[] { a, restored })
        {
            foreach (var b in request.Skip(17)) r.Write(0x78fd, b);
            r.Write(0x79fd, 2); Assert.Equal(4, r.Read(0x77fd) & 4);
            r.Scheduler.AdvanceTo(40); Assert.Equal(ExpansionLifecycle.Paused, r.Root.GetUnit(1).Lifecycle);
            Assert.Equal(8, r.Root.GetUnit(1).CaptureFrontier().TStates);
            var local = r.Ports.CreateView(new(2, 1, CpuPortRole.Compute));
            foreach (var b in message.Skip(3)) local.Write(0x75fd, b, r.Scheduler.Now);
            local.Write(0x74fd, 2, r.Scheduler.Now); Assert.Equal(1, local.Read(0x74fd, r.Scheduler.Now) & 3);
            Assert.Equal(message, Enumerable.Range(0, 73).Select(_ => local.Read(0x75fd, r.Scheduler.Now)).ToArray());
        }
        Assert.Equal(a.Scheduler.PendingEvents, restored.Scheduler.PendingEvents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1234)]
    public void HcsRestoresLiveExchangeAfterAckAndResumeRestoresRememberedLatch(int previousBank)
    {
        var original = new Rig(); var first = SnapshotMachine(original);
        original.Memory.WritePagedPort(0x7efd, (byte)previousBank);
        original.Memory.WritePagedPort(0x7dfd, (byte)(previousBank >> 8));
        Assert.Equal(0, original.Send(1, 4, Image([0x31, 0xff, 0xbf, 1, 0xfd, 0x74, 0x3e, 7, 0xed, 0x79, 0xc3, 6, 0]))[1]);
        Assert.Equal(0, original.Send(1, 7, execution: 2)[1]); original.Scheduler.AdvanceTo(1000);
        Assert.Equal(ExpansionLifecycle.ServiceIdle, original.Root.GetUnit(1).Lifecycle);
        var local = original.Ports.CreateView(new(1, 2, CpuPortRole.Compute));
        local.Write(0x74fd, 1, original.Scheduler.Now);
        foreach (var b in Mail(7, 0x42)) local.Write(0x75fd, b, original.Scheduler.Now);
        local.Write(0x74fd, 2, original.Scheduler.Now);
        original.Root.GetUnit(1).WriteMemory(0xf000, 0x6e);
        Assert.Equal(0, original.Send(1, 12, execution: 2)[1]);
        var ack = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(ack, 7);
        Assert.Equal(0, original.Send(1, 13, ack, execution: 2)[1]);
        Assert.Equal((ushort)515, original.Memory.Latches.PagedBank);
        var saved = first.Save();
        var restored = new Rig(); var second = SnapshotMachine(restored); second.Load(saved);
        foreach (var r in new[] { original, restored })
        {
            Assert.Equal((ushort)515, r.Memory.Latches.PagedBank);
            Assert.Equal(0x6e, r.Root.GetUnit(1).ReadMemory(0xf000));
            r.Root.GetUnit(1).WriteMemory(0xf000, 0x99);
            Assert.Equal(0x6e, r.Root.GetUnit(1).ReadMemory(0xf000));
            r.Memory.WritePagedPort(0x7efd, 0x55); Assert.Equal((ushort)515, r.Memory.Latches.PagedBank);
            Assert.Equal(0, r.Send(1, 8, execution: 2)[1]);
            Assert.Equal((ushort)previousBank, r.Memory.Latches.PagedBank);
            r.Root.GetUnit(1).WriteMemory(0xf000, 0x99); Assert.Equal(0x99, r.Root.GetUnit(1).ReadMemory(0xf000));
            r.Scheduler.AdvanceTo(2000); Assert.Equal(ExpansionLifecycle.ServiceIdle, r.Root.GetUnit(1).Lifecycle);
        }
        Assert.Equal(original.Scheduler.PendingEvents, restored.Scheduler.PendingEvents);
    }

    [Fact]
    public void CanonicalCompositionProvenanceDoesNotChangeExpansionAdmissionIdentity()
    {
        var profile = MachineProfile.Hc2000V2PagedExpansionV1;
        var memory = new HcMemory(profile.Memory with { RomProvenance = $"canonical verified {profile.Name} ROM set" },
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var arena = memory.EnableExpansionArena(profile); Assert.Equal(1ul, arena.GetGrant(1).ResourceEpoch);
    }

    [Fact]
    public void RealPortTableRetainsEveryLegacyDirectionAndOnlyAddsTheEightExactEndpoints()
    {
        var r = new Rig(); var idle = new PortDevice();
        var before = HcIoProfile.WithPagedRam(r.Memory, idle, idle, idle, idle, idle, idle);
        var after = ExpansionIoProfile.Compose(MachineProfile.Hc2000V2PagedExpansionV1, r.Memory, before, r.Root);
        for (var p = 0; p <= ushort.MaxValue; p++)
        foreach (var access in new[] { PortAccess.Read, PortAccess.Write })
        {
            var existing = before.SingleOrDefault(row => (row.Access & access) != 0 && (p & row.Mask) == row.Match);
            var actual = after.SingleOrDefault(row => (row.Access & access) != 0 && (p & row.Mask) == row.Match);
            if (existing is not null) Assert.Same(existing, actual);
            else if (actual is not null) { Assert.Same(r.Root, actual.Device); Assert.Equal(0xffff, actual.Mask); }
        }
        Assert.Throws<ArgumentException>(() => ExpansionIoProfile.Compose(MachineProfile.Hc2000V2PagedMTransferV1, r.Memory, before, r.Root));
    }
}
