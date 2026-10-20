using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;
using Z80Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Tests;

// Admission fixtures only: no production roster, controller or expansion activation.
public sealed class CpuPortOriginTests
{
    [Theory]
    [InlineData(0, 0, CpuPortRole.Main, true)]
    [InlineData(1, 0, CpuPortRole.Main, false)]
    [InlineData(0, 1, CpuPortRole.Main, false)]
    [InlineData(0, 0, CpuPortRole.Compute, false)]
    [InlineData(0, 0, CpuPortRole.Driver, false)]
    public void DefaultIdentityIsExact(int unit, int epoch, CpuPortRole role, bool allowed)
    {
        var endpoint = new OriginEndpoint([]);
        var decoder = new PortDecoder([new(0, 0, endpoint, PortAccess.ReadWrite)]);
        var view = decoder.CreateView(new((uint)unit, (ulong)epoch, role));
        var (bus, _, _) = Machine(decoder, view);
        Assert.Equal(allowed ? 0x42 : 0xff, bus.In(0xab87));
        bus.Out(0xab87, 0xd3);
        Assert.Equal(allowed ? 2 : 0, endpoint.Events.Count);
        endpoint.Events.Clear();
        var (legacy, _, _) = Machine(decoder);
        Assert.Equal(0x42, legacy.In(0xab87));
        legacy.Out(0xab87, 0xd3);
        Assert.Equal(2, endpoint.Events.Count);
    }

    [Theory]
    [InlineData(CpuPortRole.Compute, PortAccess.Read)]
    [InlineData(CpuPortRole.Driver, PortAccess.Write)]
    public void ConfiguredAdmissionChecksIdentityRoleEpochDirectionAndOwnership(CpuPortRole role, PortAccess access)
    {
        var trace = new List<string>();
        var policy = new OriginPolicy(trace) { Current = new(7, 11, role), Access = access };
        var endpoint = new OriginEndpoint(trace);
        var decoder = new PortDecoder([new(0, 0, endpoint, PortAccess.ReadWrite)], policy, new(0, 0, CpuPortRole.Main));
        var origins = new[] { policy.Current, policy.Current with { UnitId = 8 },
            policy.Current with { Epoch = 10 }, policy.Current with { Role = CpuPortRole.Main } };
        foreach (var origin in origins)
        {
            trace.Clear();
            var (bus, scheduler, _) = Machine(decoder, decoder.CreateView(origin));
            scheduler.AdvanceTo(123);
            var readAllowed = origin == policy.Current && access == PortAccess.Read;
            var writeAllowed = origin == policy.Current && access == PortAccess.Write;
            Assert.Equal(readAllowed ? 0x42 : 0xff, bus.In(0xab87));
            bus.Out(0xab87, 0xd3);
            Assert.Equal(readAllowed ? new[] { "policy:Read", "read:AB87:123", "policy:Write" } :
                writeAllowed ? new[] { "policy:Read", "policy:Write", "write:AB87:D3:123" } :
                new[] { "policy:Read", "policy:Write" }, trace);
        }
        trace.Clear();
        var (wrongPort, _, _) = Machine(decoder, decoder.CreateView(policy.Current));
        Assert.Equal(0xff, wrongPort.In(0xac87));
        wrongPort.Out(0xac87, 0x42);
        Assert.Equal(new[] { "policy:Read", "policy:Write" }, trace);
    }

    [Fact]
    public void AlreadyBoundBusAndViewRecheckCurrentOwnerEpochAndLegacyMainCannotBypassDeny()
    {
        var trace = new List<string>();
        var policy = new OriginPolicy(trace);
        var endpoint = new OriginEndpoint(trace);
        var decoder = new PortDecoder([new(0, 0, endpoint, PortAccess.ReadWrite)], policy, policy.Current);
        var view = decoder.CreateView(policy.Current);
        var (bus, _, _) = Machine(decoder, view);
        Assert.Equal(0x42, view.Read(0xab87, 9));
        bus.Out(0xab87, 1);
        policy.Current = policy.Current with { Epoch = 12 };
        trace.Clear();
        Assert.Equal(0xff, view.Read(0xab87, 10));
        view.Write(0xab87, 2, 10);
        Assert.Equal(0xff, bus.In(0xab87));
        bus.Out(0xab87, 3);
        Assert.Equal(0xff, decoder.Read(0xab87, 10));
        decoder.Write(0xab87, 4, 10);
        var (legacy, _, _) = Machine(decoder);
        Assert.Equal(0xff, legacy.In(0xab87));
        legacy.Out(0xab87, 5);
        Assert.Equal(8, trace.Count);
        Assert.All(trace, e => Assert.StartsWith("policy:", e));
        policy.Current = policy.Current with { Epoch = 11, UnitId = 99 };
        Assert.Equal(0xff, bus.In(0xab87));
        policy.Current = new(7, 11, CpuPortRole.Driver);
        Assert.Equal(0x42, bus.In(0xab87));
    }

    [Fact]
    public void DecoderCannotAcceptAnotherDecodersView()
    {
        var first = new PortDecoder([]);
        var second = new PortDecoder([]);
        Assert.Throws<ArgumentException>(() => Machine(second, first.CreateView(new(0, 0, CpuPortRole.Main))));
    }

    [Fact]
    public void LegacyAndMainViewPreserveDirectionalFullPortValuesOrderAndTicks()
    {
        foreach (var useView in new[] { false, true })
        {
            var trace = new List<string>();
            var read = new OriginEndpoint(trace);
            var write = new OriginEndpoint(trace);
            var decoder = new PortDecoder([new(0x87, 0xff, read, PortAccess.Read),
                new(0xab87, 0xffff, write, PortAccess.Write)]);
            var (bus, scheduler, _) = Machine(decoder, useView ? decoder.CreateView(new(0, 0, CpuPortRole.Main)) : null);
            scheduler.AdvanceTo(71);
            Assert.Equal(0x42, bus.In(0xab87));
            bus.Out(0xab87, 0x91);
            scheduler.AdvanceTo(74);
            Assert.Equal(0x42, bus.In(0xcd87));
            bus.Out(0xcd87, 0x92);
            Assert.Equal(0xff, bus.In(0xab86));
            Assert.Equal(new[] { "read:AB87:71", "write:AB87:91:71", "read:CD87:74" }, trace);
            Assert.Equal(74, scheduler.Now); // Port dispatch does not advance device time itself.
        }
    }

    [Fact]
    public void OverlapValidationRemainsDirectional()
    {
        var device = new OriginEndpoint([]);
        Assert.Throws<ArgumentException>(() => new PortDecoder([new(0x87, 0xff, device, PortAccess.Read),
            new(0xab87, 0xffff, device, PortAccess.ReadWrite)]));
        _ = new PortDecoder([new(0x87, 0xff, device, PortAccess.Read), new(0xab87, 0xffff, device, PortAccess.Write)]);
    }

    [Fact]
    public void M1IntaUnmatchedAndNegativeTicksHaveNoPolicyOrDeviceEffects()
    {
        var trace = new List<string>();
        var policy = new OriginPolicy(trace);
        var decoder = new PortDecoder([new(0xab87, 0xffff, new OriginEndpoint(trace), PortAccess.ReadWrite)], policy, policy.Current);
        var view = decoder.CreateView(policy.Current);
        Assert.Equal(0xff, decoder.Read(0xab87, 4, true));
        decoder.Write(0xab87, 1, 4, true);
        Assert.Equal(0xff, view.Read(0xab87, 4, true));
        view.Write(0xab87, 1, 4, true);
        Assert.Equal(0xff, view.Read(0xab86, 4));
        view.Write(0xab86, 1, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Read(0xab87, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Write(0xab87, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Read(0xab87, -1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Write(0xab87, 1, -1, true));
        var (bus, _, _) = Machine(decoder, view);
        Assert.Equal(0xff, bus.IntVector());
        Assert.Equal(0xff, PortDecoder.IntVector());
        Assert.Equal(0, bus.FetchM1(0x5000, true, 0));
        Assert.Empty(trace);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeInstructionsCannotSelectOriginWithRegistersOrPayload(bool admitted)
    {
        var trace = new List<string>();
        var policy = new OriginPolicy(trace);
        var endpoint = new OriginEndpoint(trace);
        var decoder = new PortDecoder([new(0, 0, endpoint, PortAccess.ReadWrite)], policy, policy.Current);
        var origin = admitted ? policy.Current : policy.Current with { UnitId = 0, Role = CpuPortRole.Main };
        var (bus, scheduler, memory) = Machine(decoder, decoder.CreateView(origin));
        Assert.Equal(65536, memory.Ram.Length);
        // LD BC,AB87; LD A,07; OUT (C),A; IN A,(C); LD A,AB; OUT (87),A; IN A,(87).
        byte[] program = [0x01, 0x87, 0xab, 0x3e, 0x07, 0xed, 0x79, 0xed, 0x78, 0x3e, 0xab, 0xd3, 0x87, 0xdb, 0x87];
        program.CopyTo(memory.Ram.Span[0x5000..]);
        var cpu = new Z80Cpu(bus);
        cpu.R.PC = 0x5000;
        var time = new CpuDeviceTimeAdapter(scheduler, new CpuTimeMapper());
        foreach (var cycles in new[] { 10, 7, 12, 12, 7, 11, 11 })
        {
            time.SyncCpuTime(cpu.TStates);
            Assert.Equal(cycles, cpu.Step());
            time.SyncCpuTime(cpu.TStates);
        }
        Assert.Equal(admitted ? 0x42 : 0xff, cpu.R.A);
        Assert.Equal(70, cpu.TStates);
        Assert.Equal(4, policy.Observed.Count);
        Assert.All(policy.Observed, o => Assert.Equal(origin, o));
        Assert.Equal(admitted ? 4 : 0, endpoint.Events.Count);
        if (admitted)
        {
            Assert.Equal(new[] { "write:AB87:07:125", "read:AB87:185", "write:AB87:AB:260", "read:AB87:315" }, endpoint.Events);
        }
    }

    private static (HcMachineBus Bus, DeviceScheduler Scheduler, HcMemory Memory) Machine(PortDecoder decoder, CpuPortView? view = null)
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var scheduler = new DeviceScheduler();
        var time = new CpuDeviceTimeAdapter(scheduler, new CpuTimeMapper());
        return (new(memory, decoder, time, scheduler, view), scheduler, memory);
    }
}

file sealed class OriginEndpoint(List<string> trace) : IPortDevice
{
    public List<string> Events { get; } = [];
    public byte Read(ushort port, long masterTick)
    {
        var e = $"read:{port:X4}:{masterTick}";
        Events.Add(e);
        trace.Add(e);
        return 0x42;
    }
    public void Write(ushort port, byte value, long masterTick)
    {
        var e = $"write:{port:X4}:{value:X2}:{masterTick}";
        Events.Add(e);
        trace.Add(e);
    }
}

file sealed class OriginPolicy(List<string> trace) : IPortOriginPolicy
{
    public CpuPortOrigin Current { get; set; } = new(7, 11, CpuPortRole.Driver);
    public PortAccess Access { get; set; } = PortAccess.ReadWrite;
    public List<CpuPortOrigin> Observed { get; } = [];
    public bool Allows(in CpuPortOrigin origin, ushort port, PortAccess access)
    {
        trace.Add($"policy:{access}");
        Observed.Add(origin);
        return origin == Current && port == 0xab87 && (Access & access) == access;
    }
}
