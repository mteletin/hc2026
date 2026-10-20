using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Xunit;
using Z80Cpu = Hc2000.Z80.Z80;

namespace Hc2000.Machine.Tests;

// Independent one-file actual-bus tests for the additive IOriginPortDevice cut
// (hc2000-cpu-port-origin-endpoint-forward @ 6572787). The admitted immutable origin is
// forwarded exactly once through PortDecoder's two dispatch sites; the legacy IPortDevice
// overloads are distinct and observable in byte and state, so an accidental fallback fails
// these tests. Bounded ordinary fixtures only: no App, no ROM boot, no 1GiB allocation.
public sealed class OriginAwarePortDeviceTests
{
    private const ushort Port = 0xab87;
    private static readonly CpuPortOrigin Origin = new(9, 20, CpuPortRole.Driver);
    private static readonly CpuPortOrigin DefaultMain = new(0, 0, CpuPortRole.Main);

    [Fact]
    public void AwareEndpointReceivesAdmittedOriginFullPortValueAndTickExactlyOncePerAccess()
    {
        var endpoint = new AwareEndpoint();
        var policy = new SetPolicy(Origin);
        var decoder = new PortDecoder([new(Port, 0xffff, endpoint, PortAccess.ReadWrite)], policy, DefaultMain);
        var (bus, scheduler, _) = Machine(decoder, decoder.CreateView(Origin));

        scheduler.AdvanceTo(123);
        Assert.Equal(AwareEndpoint.AwareReadByte, bus.In(Port));
        bus.Out(Port, 0x5c);
        Assert.Single(endpoint.AwareReads);
        Assert.Single(endpoint.AwareWrites);
        Assert.Equal((Origin, Port, 123L), endpoint.AwareReads[0]);
        Assert.Equal((Origin, Port, 0x5c, 123L), endpoint.AwareWrites[0]);
        Assert.Equal(0, endpoint.LegacyReads);
        Assert.Equal(0, endpoint.LegacyWrites);

        scheduler.AdvanceTo(130);
        Assert.Equal(AwareEndpoint.AwareReadByte, bus.In(Port));
        bus.Out(Port, 0x5d);
        Assert.Equal(2, endpoint.AwareReads.Count);
        Assert.Equal(2, endpoint.AwareWrites.Count);
        Assert.Equal((Origin, Port, 130L), endpoint.AwareReads[1]);
        Assert.Equal((Origin, Port, 0x5d, 130L), endpoint.AwareWrites[1]);
        Assert.Equal(0, endpoint.LegacyReads + endpoint.LegacyWrites);
        Assert.Equal(endpoint.AwareReads.Count, endpoint.AwareReads.Distinct().Count());
    }

    [Fact]
    public void LegacyOverloadIsNotInvokedAfterAwareDeliveryAndRemainsObservable()
    {
        var endpoint = new AwareEndpoint();
        var policy = new SetPolicy(Origin);
        var decoder = new PortDecoder([new(Port, 0xffff, endpoint, PortAccess.ReadWrite)], policy, DefaultMain);
        var (bus, scheduler, _) = Machine(decoder, decoder.CreateView(Origin));

        // The observed byte and the endpoint state must both prove the aware path: a
        // legacy fallback would return LegacyReadByte and increment the legacy counters.
        scheduler.AdvanceTo(61);
        Assert.Equal(AwareEndpoint.AwareReadByte, bus.In(Port));
        Assert.NotEqual(AwareEndpoint.LegacyReadByte, bus.In(Port));
        bus.Out(Port, 0x10);
        Assert.Equal(2, endpoint.AwareReads.Count);
        Assert.Single(endpoint.AwareWrites);
        Assert.Equal(0, endpoint.LegacyReads);
        Assert.Equal(0, endpoint.LegacyWrites);
        Assert.Equal(3, endpoint.TotalInvocations);
        Assert.All(endpoint.AwareReads, e => Assert.Equal(Origin, e.Origin));
        Assert.All(endpoint.AwareWrites, e => Assert.Equal(Origin, e.Origin));
    }

    [Fact]
    public void TwoLegitimateBoundOriginsOnOneMappingStayDistinguishable()
    {
        var o1 = new CpuPortOrigin(1, 3, CpuPortRole.Compute);
        var o2 = new CpuPortOrigin(2, 3, CpuPortRole.Compute);
        var endpoint = new AwareEndpoint();
        var policy = new SetPolicy(o1, o2);
        var decoder = new PortDecoder([new(Port, 0xffff, endpoint, PortAccess.ReadWrite)], policy, DefaultMain);
        var (bus1, _, _) = Machine(decoder, decoder.CreateView(o1));
        var (bus2, _, _) = Machine(decoder, decoder.CreateView(o2));

        bus1.In(Port);
        bus1.Out(Port, 0x11);
        bus2.In(Port);
        bus2.Out(Port, 0x22);

        Assert.Equal(2, endpoint.AwareReads.Count);
        Assert.Equal(2, endpoint.AwareWrites.Count);
        Assert.Equal(o1, endpoint.AwareReads[0].Origin);
        Assert.Equal(o2, endpoint.AwareReads[1].Origin);
        Assert.Equal(o1, endpoint.AwareWrites[0].Origin);
        Assert.Equal(o2, endpoint.AwareWrites[1].Origin);
        Assert.Equal(0x11, endpoint.LastWriteByOrigin[o1]);
        Assert.Equal(0x22, endpoint.LastWriteByOrigin[o2]);
        Assert.Equal(2, endpoint.LastWriteByOrigin.Count);
        Assert.Equal(0, endpoint.LegacyReads + endpoint.LegacyWrites);
    }

    [Fact]
    public void DeniedPolicyAndStaleBoundViewInvokeNeitherOverloadAndChangeNoState()
    {
        var endpoint = new AwareEndpoint();
        var policy = new SetPolicy(Origin);
        var decoder = new PortDecoder([new(Port, 0xffff, endpoint, PortAccess.ReadWrite)], policy, DefaultMain);

        // Denied before any state exists.
        var stranger = new CpuPortOrigin(10, 20, CpuPortRole.Driver);
        var (deniedBus, deniedScheduler, _) = Machine(decoder, decoder.CreateView(stranger));
        deniedScheduler.AdvanceTo(41);
        Assert.Equal(0xff, deniedBus.In(Port));
        deniedBus.Out(Port, 0x77);
        Assert.Empty(endpoint.AwareReads);
        Assert.Empty(endpoint.AwareWrites);
        Assert.Equal(0, endpoint.LegacyReads + endpoint.LegacyWrites);
        Assert.Empty(endpoint.LastWriteByOrigin);

        // Stale bound view: the fixture is permitted at the bound epoch, then the
        // ownership/epoch moves; the old view must be denied, not refreshed.
        var (bus, scheduler, _) = Machine(decoder, decoder.CreateView(Origin));
        scheduler.AdvanceTo(51);
        Assert.Equal(AwareEndpoint.AwareReadByte, bus.In(Port));
        bus.Out(Port, 0x11);
        Assert.Single(endpoint.AwareReads);
        Assert.Single(endpoint.AwareWrites);

        policy.Admit(Origin with { Epoch = 21 });
        scheduler.AdvanceTo(52);
        Assert.Equal(0xff, bus.In(Port));
        bus.Out(Port, 0x22);
        Assert.Single(endpoint.AwareReads);
        Assert.Single(endpoint.AwareWrites);
        Assert.Equal(0, endpoint.LegacyReads + endpoint.LegacyWrites);
        Assert.Equal(0x11, endpoint.LastWriteByOrigin[Origin]);
        Assert.Single(policy.Observed[^1..]);
        Assert.Equal(Origin with { Epoch = 20 }, policy.Observed[^1]);
    }

    [Fact]
    public void ConfiguredMainIdentityCannotBypassPolicyOrSilentlyReplaceIdentity()
    {
        var main = new CpuPortOrigin(4, 7, CpuPortRole.Main);
        var endpoint = new AwareEndpoint();
        var policy = new SetPolicy(Origin, main);
        var decoder = new PortDecoder([new(Port, 0xffff, endpoint, PortAccess.ReadWrite)], policy, main);
        Assert.Equal(AwareEndpoint.AwareReadByte, decoder.Read(Port, 5));
        decoder.Write(Port, 0x33, 6);
        Assert.Single(endpoint.AwareReads);
        Assert.Single(endpoint.AwareWrites);
        Assert.Equal(main, endpoint.AwareReads[0].Origin);
        Assert.Equal(main, endpoint.AwareWrites[0].Origin);
        Assert.NotEqual(DefaultMain, endpoint.AwareReads[0].Origin);

        // The policy still runs for the main identity: denying it admits nothing.
        var other = new AwareEndpoint();
        var denying = new SetPolicy(Origin);
        var denyingDecoder = new PortDecoder([new(Port, 0xffff, other, PortAccess.ReadWrite)], denying, main);
        Assert.Equal(0xff, denyingDecoder.Read(Port, 7));
        denyingDecoder.Write(Port, 0x44, 8);
        Assert.Equal(0, other.TotalInvocations);
        Assert.Equal(2, denying.Observed.Count);
        Assert.All(denying.Observed, o => Assert.Equal(main, o));
    }

    [Fact]
    public void LegacyOnlyEndpointPreservesBytePortValueAndTickAlongsideAware()
    {
        var legacy = new LegacyEndpoint();
        var aware = new AwareEndpoint();
        var policy = new SetPolicy(Origin);
        var decoder = new PortDecoder([
            new(0x1f, 0xff, legacy, PortAccess.ReadWrite),
            new(Port, 0xffff, aware, PortAccess.ReadWrite)], policy, DefaultMain);
        var (bus, scheduler, _) = Machine(decoder, decoder.CreateView(Origin));

        scheduler.AdvanceTo(71);
        Assert.Equal(0x2a, bus.In(0x1f));
        Assert.Equal(0x2a, bus.In(0x9e1f));
        bus.Out(0x1f, 0x91);
        scheduler.AdvanceTo(74);
        Assert.Equal(0x2a, bus.In(0x9e1f));
        bus.Out(Port, 0x55);
        Assert.Equal(new[] { "read:001F:71", "read:9E1F:71", "write:001F:91:71", "read:9E1F:74" }, legacy.Events);
        Assert.Single(aware.AwareWrites);
        Assert.Empty(aware.AwareReads);
        Assert.Equal(0x55, aware.AwareWrites[0].Value);
        Assert.Equal(74L, aware.AwareWrites[0].Tick);
        Assert.Equal(74, scheduler.Now);
        Assert.Equal(0xff, bus.In(0x20));
        Assert.Equal(4, legacy.Events.Count);
    }

    [Fact]
    public void M1IntaUnmatchedPortAndDirectionAndCrossDecoderHaveNoAwareEffects()
    {
        var endpoint = new AwareEndpoint();
        var policy = new SetPolicy(Origin);
        var decoder = new PortDecoder([new(Port, 0xffff, endpoint, PortAccess.Write)], policy, DefaultMain);
        var view = decoder.CreateView(Origin);
        var (bus, scheduler, _) = Machine(decoder, view);

        Assert.Equal(0xff, decoder.Read(Port, 4, true));
        decoder.Write(Port, 1, 4, true);
        Assert.Equal(0xff, view.Read(Port, 4, true));
        view.Write(Port, 1, 4, true);
        Assert.Equal(0, endpoint.TotalInvocations);
        Assert.Empty(policy.Observed);

        // Read direction is unmatched by the write-only row: no policy, no device.
        scheduler.AdvanceTo(9);
        Assert.Equal(0xff, bus.In(Port));
        Assert.Empty(policy.Observed);

        // Unmatched full port: no policy, no device.
        Assert.Equal(0xff, bus.In(Port ^ 1));
        bus.Out(Port ^ 1, 0x66);
        Assert.Empty(policy.Observed);
        Assert.Equal(0, endpoint.TotalInvocations);

        // Admitted write reaches the aware endpoint exactly once.
        bus.Out(Port, 0x77);
        Assert.Single(endpoint.AwareWrites);
        Assert.Equal((Origin, Port, 0x77, 9L), endpoint.AwareWrites[0]);

        Assert.Equal(0xff, bus.IntVector());
        Assert.Equal(0xff, PortDecoder.IntVector());

        var other = new PortDecoder([]);
        Assert.Throws<ArgumentException>(() => Machine(other, decoder.CreateView(Origin)));
        Assert.Equal(1, endpoint.TotalInvocations);
    }

    [Fact]
    public void FiniteRealZ80InOutReachesAwareEndpointWithHostBoundOriginThroughBus()
    {
        var endpoint = new AwareEndpoint();
        var policy = new SetPolicy(Origin);
        var decoder = new PortDecoder([new(Port, 0xffff, endpoint, PortAccess.ReadWrite)], policy, DefaultMain);
        var (bus, scheduler, memory) = Machine(decoder, decoder.CreateView(Origin));
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

        Assert.Equal(AwareEndpoint.AwareReadByte, cpu.R.A);
        Assert.Equal(70, cpu.TStates);
        Assert.Equal(2, endpoint.AwareWrites.Count);
        Assert.Equal(2, endpoint.AwareReads.Count);
        Assert.Equal(0, endpoint.LegacyReads + endpoint.LegacyWrites);
        Assert.Equal((Origin, Port, 0x07), (endpoint.AwareWrites[0].Origin, endpoint.AwareWrites[0].Port, endpoint.AwareWrites[0].Value));
        Assert.Equal((Origin, Port, 0xab), (endpoint.AwareWrites[1].Origin, endpoint.AwareWrites[1].Port, endpoint.AwareWrites[1].Value));
        Assert.Equal(Port, endpoint.AwareReads[0].Port);
        Assert.Equal(Port, endpoint.AwareReads[1].Port);
        var ticks = new long[] { endpoint.AwareWrites[0].Tick, endpoint.AwareReads[0].Tick,
            endpoint.AwareWrites[1].Tick, endpoint.AwareReads[1].Tick };
        for (var i = 1; i < ticks.Length; i++)
            Assert.True(ticks[i - 1] < ticks[i], $"dispatch ticks must strictly increase, saw {ticks[0]},{ticks[1]},{ticks[2]},{ticks[3]}");
        Assert.Equal(4, policy.Observed.Count);
        Assert.All(policy.Observed, o => Assert.Equal(Origin, o));
    }

    private static (HcMachineBus Bus, DeviceScheduler Scheduler, HcMemory Memory) Machine(PortDecoder decoder, CpuPortView? view = null)
    {
        var memory = new HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var scheduler = new DeviceScheduler();
        var time = new CpuDeviceTimeAdapter(scheduler, new CpuTimeMapper());
        return (new(memory, decoder, time, scheduler, view), scheduler, memory);
    }
}

file sealed class AwareEndpoint : IOriginPortDevice
{
    public const byte AwareReadByte = 0xb4;
    public const byte LegacyReadByte = 0x42;
    public readonly List<(CpuPortOrigin Origin, ushort Port, long Tick)> AwareReads = [];
    public readonly List<(CpuPortOrigin Origin, ushort Port, byte Value, long Tick)> AwareWrites = [];
    public readonly Dictionary<CpuPortOrigin, byte> LastWriteByOrigin = [];
    public int LegacyReads;
    public int LegacyWrites;
    public int TotalInvocations => AwareReads.Count + AwareWrites.Count + LegacyReads + LegacyWrites;

    public byte Read(in CpuPortOrigin origin, ushort port, long masterTick)
    {
        AwareReads.Add((origin, port, masterTick));
        return AwareReadByte;
    }

    public void Write(in CpuPortOrigin origin, ushort port, byte value, long masterTick)
    {
        AwareWrites.Add((origin, port, value, masterTick));
        LastWriteByOrigin[origin] = value;
    }

    // Legacy IPortDevice overloads: distinct byte and counters so an accidental decoder
    // fallback is observable in the returned value and in the endpoint state, not just one.
    public byte Read(ushort port, long masterTick) { LegacyReads++; return LegacyReadByte; }
    public void Write(ushort port, byte value, long masterTick) { LegacyWrites++; }
}

file sealed class SetPolicy(params CpuPortOrigin[] admitted) : IPortOriginPolicy
{
    public List<CpuPortOrigin> Observed { get; } = [];
    private readonly List<CpuPortOrigin> _admitted = [.. admitted];

    public void Admit(params CpuPortOrigin[] origins)
    {
        _admitted.Clear();
        _admitted.AddRange(origins);
    }

    public bool Allows(in CpuPortOrigin origin, ushort port, PortAccess access)
    {
        Observed.Add(origin);
        return _admitted.Contains(origin);
    }
}

file sealed class LegacyEndpoint : IPortDevice
{
    public readonly List<string> Events = [];

    public byte Read(ushort port, long masterTick)
    {
        Events.Add($"read:{port:X4}:{masterTick}");
        return 0x2a;
    }

    public void Write(ushort port, byte value, long masterTick) => Events.Add($"write:{port:X4}:{value:X2}:{masterTick}");
}
