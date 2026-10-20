using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Z80.Frontier;

namespace Hc2000.Machine.Expansion;

/// <summary>The machine owner dispatches one queued causal effect per runnable CPU, never Step.
/// Scheduler callbacks only commit effects/reconcile future events; they never AdvanceTo.</summary>
public sealed partial class ExpansionFrontierDriver
{
    public const int FirstCpuDeviceId = 1000;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly DeviceScheduler scheduler;
    private readonly FrameClock clock;
    private readonly CpuTimeMapper mapper;
    private readonly ExpansionController controller;
    private readonly HcMemory memory;
    private readonly PortDecoder ports;
    private readonly Z80.Z80 main;
    private readonly long[] tokens = new long[17], due = new long[17];
    private readonly ulong[] epochs = new ulong[17];
    private readonly CpuPortView mainPorts;
    private readonly CpuPortView?[] computePorts = new CpuPortView?[17];
    private readonly ulong[] viewEpochs = new ulong[17];

    public ExpansionFrontierDriver(DeviceScheduler scheduler, FrameClock clock, CpuTimeMapper mapper,
        ExpansionController controller, HcMemory memory, PortDecoder ports, Z80.Z80 main)
    {
        ArgumentNullException.ThrowIfNull(scheduler); ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(mapper); ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(ports); ArgumentNullException.ThrowIfNull(main);
        if (scheduler.Now != 0 || main.TStates != 0 || mapper.CpuTStates != 0 || mapper.Numerator != 5 || mapper.Denominator != 1)
            throw new ArgumentException("Compose the initial expansion frontier at its admitted 5:1 epoch.");
        this.scheduler = scheduler; this.clock = clock; this.mapper = mapper; this.controller = controller;
        this.memory = memory; this.ports = ports; this.main = main;
        mainPorts = ports.CreateView(controller.MainOrigin);
        for (ushort id = 0; id <= ExpansionArena.ComputeCount; id++)
        {
            var captured = id;
            scheduler.RegisterDevice(FirstCpuDeviceId + id, e => Dispatch(captured, e));
        }
        Synchronize();
    }

    /// <summary>Owner composition/host controls may reconcile at the current common frontier.
    /// Ordinary root requests reconcile in the same main CPU callback, before later unit IDs.</summary>
    public void Synchronize()
    {
        VerifyOwner();
        for (ushort id = 0; id <= ExpansionArena.ComputeCount; id++)
        {
            var unit = id == 0 ? null : controller.GetUnit(id);
            if (unit is not null && !unit.Runnable) { Cancel(id); continue; }
            var epoch = unit?.ExecutionEpoch ?? controller.MainOrigin.Epoch;
            if (id != 0 && viewEpochs[id] != epoch)
            {
                computePorts[id] = ports.CreateView(new(id, epoch, CpuPortRole.Compute)); viewEpochs[id] = epoch;
            }
            var tick = unit is null ? checked(main.PeekNextFrontier().AtCpuTState * 5) : unit.NextMasterTick;
            if (tokens[id] != 0 && epochs[id] == epoch && due[id] == tick) continue;
            Cancel(id);
            if (tick <= scheduler.Now) throw new InvalidOperationException("A new CPU frontier must be strictly future.");
            due[id] = tick; epochs[id] = epoch;
            tokens[id] = scheduler.Schedule(new(tick, EventPhase.Cpu, FirstCpuDeviceId + id, 0,
                scheduler.Generation(FirstCpuDeviceId + id)));
        }
    }
    private void Cancel(ushort id)
    {
        if (tokens[id] == 0) return;
        scheduler.Cancel(tokens[id]); scheduler.InvalidateDevice(FirstCpuDeviceId + id);
        tokens[id] = due[id] = 0; epochs[id] = 0;
    }
    private void Dispatch(ushort id, DeviceEvent item)
    {
        VerifyOwner();
        if (tokens[id] != item.InsertionSequence || due[id] != item.DueTick || item.Phase != EventPhase.Cpu || item.Kind != 0)
            throw new InvalidOperationException("CPU queue identity differs from its admitted frontier.");
        tokens[id] = due[id] = 0;
        if (id == 0)
        {
            var effect = main.PeekNextFrontier();
            if (mapper.Map(effect.AtCpuTState) != item.DueTick) throw new InvalidOperationException("Main CPU clock mismatch.");
            var value = effect.Kind switch
            {
                FrontierEffectKind.M1Read => memory.ReadM1(effect.Address),
                FrontierEffectKind.MemoryRead => memory.Read(effect.Address),
                FrontierEffectKind.PortRead => mainPorts.Read(effect.Address, item.DueTick),
                FrontierEffectKind.InterruptAck => PortDecoder.IntVector(),
                _ => (byte)0
            };
            if (effect.Kind == FrontierEffectKind.MemoryWrite) memory.Write(effect.Address, effect.WriteValue);
            if (effect.Kind == FrontierEffectKind.PortWrite) mainPorts.Write(effect.Address, effect.WriteValue, item.DueTick);
            main.CommitFrontier(value, clock.IntAsserted);
        }
        else
        {
            var unit = controller.GetUnit(id);
            if (!unit.Runnable || epochs[id] != unit.ExecutionEpoch) throw new InvalidOperationException("A stale compute event reached dispatch.");
            var effect = unit.PeekNextFrontier();
            var view = computePorts[id] ?? throw new InvalidOperationException("Missing current compute port binding.");
            var value = effect.Kind switch
            {
                FrontierEffectKind.M1Read or FrontierEffectKind.MemoryRead => unit.ReadMemory(effect.Address),
                FrontierEffectKind.PortRead => view.Read(effect.Address, item.DueTick),
                FrontierEffectKind.InterruptAck => PortDecoder.IntVector(),
                _ => (byte)0
            };
            if (effect.Kind == FrontierEffectKind.MemoryWrite) unit.WriteMemory(effect.Address, effect.WriteValue);
            if (effect.Kind == FrontierEffectKind.PortWrite) view.Write(effect.Address, effect.WriteValue, item.DueTick);
            unit.CommitFrontier(value, item.DueTick);
            controller.AcknowledgeFrontier(id, item.DueTick);
        }
        Synchronize();
    }
    public bool TryRunFrame(Func<bool> commonFrontier)
    {
        VerifyOwner(); ArgumentNullException.ThrowIfNull(commonFrontier);
        var requested = checked(clock.CompletedFrames + 1);
        Synchronize();
        while (clock.CompletedFrames < requested)
        {
            if (!commonFrontier()) return false;
            var next = long.MaxValue;
            for (var id = 0; id < tokens.Length; id++) if (tokens[id] != 0) next = Math.Min(next, due[id]);
            if (next <= scheduler.Now || next == long.MaxValue) throw new InvalidOperationException("The main CPU has no future frontier.");
            scheduler.AdvanceTo(next);
        }
        return true;
    }
    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("CPU effects belong to the emulation owner.");
    }
}
