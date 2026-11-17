using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal sealed record X4CardMetadata(int Version, ushort CardId, int EngineDevice, X4EngineSnapshot Engine, X4PortSnapshot Port);

internal sealed class ExpansionX4Card
{
    private readonly ushort cardId;
    private readonly int engineDevice;
    internal ExpansionX4Engine Engine { get; }
    internal ExpansionX4Port Port { get; }
    internal ushort CardId => cardId;
    internal int EngineDevice => engineDevice;
    internal bool IsQuiescent => Port.IsQuiescent;

    private ExpansionX4Card(DeviceScheduler scheduler, ExpansionController controller, ExpansionX0 x0, ushort cardId, int engineDevice)
    {
        this.cardId = cardId; this.engineDevice = engineDevice;
        Engine = new(scheduler, engineDevice);
        Port = new(scheduler, Engine, x0.Services, controller, cardId);
    }

    internal X4CardMetadata CaptureMetadata() => new(1, cardId, engineDevice, Engine.Capture(), Port.Capture());

    internal bool SavedOwnerAllowed(X3LogicalPrincipal owner, ExpansionX5State x5)
    {
        if (cardId >= x5.DeviceOwners.Length) return false;
        if (owner.Role == CpuPortRole.Main)
            return owner.UnitId == 0 && owner.Epoch == 1 && x5.DeviceOwners[cardId] == 0;
        if (owner.Role != CpuPortRole.Driver || owner.UnitId < ExpansionArena.DriverFirst) return false;
        var index = owner.UnitId - ExpansionArena.DriverFirst;
        return index < x5.Drivers.Length && x5.DeviceOwners[cardId] == owner.UnitId &&
            x5.Drivers[index].ExecutionEpoch == owner.Epoch;
    }

    internal void ValidateMetadata(X4CardMetadata state, ExpansionX0.ExpansionX0State x0, long now,
        IReadOnlyList<DeviceEvent> events, Func<X3LogicalPrincipal, bool> ownerAllowed)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(x0);
        if (state.Version != 1 || state.CardId != cardId || state.EngineDevice != engineDevice)
            throw new ArgumentException("X4 card roster mismatch.");
        var receipts = x0.Services.Cards.Where(item => item.CardId == cardId).ToArray();
        if (receipts.Length != 1 || !Engine.ValidState(state.Engine, now, events) ||
            !Port.ValidState(state.Port, state.Engine, receipts[0], now, ownerAllowed))
            throw new ArgumentException("X4 engine, transport and X0 state mismatch.");
    }

    internal void AdoptMetadata(X4CardMetadata state, ExpansionX0.ExpansionX0State x0, long now,
        IReadOnlyList<DeviceEvent> events, Func<X3LogicalPrincipal, bool> ownerAllowed)
    {
        ValidateMetadata(state, x0, now, events, ownerAllowed);
        Engine.Restore(state.Engine, now, events);
        Port.Restore(state.Port, state.Engine, x0.Services.Cards.Single(item => item.CardId == cardId), now, ownerAllowed);
    }

    internal static bool TryCompose(HcMemory memory, DeviceScheduler scheduler, ExpansionController controller,
        ExpansionX0 x0, IReadOnlyList<PortMapping> otherMappings, ushort cardId, int engineDevice, out ExpansionX4Card? card)
    {
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(controller); ArgumentNullException.ThrowIfNull(x0); ArgumentNullException.ThrowIfNull(otherMappings);
        card = null;
        if (!controller.SharesArena(memory.ExpansionArena) || !x0.Services.CanRegisterCard(cardId) || engineDevice < 0 ||
            scheduler.DeviceGenerations.Any(item => item.DeviceId == engineDevice)) return false;
        var placeholder = new PortDevice();
        PortMapping[] proposed = [new(ExpansionX4Port.CommandPort, 0xffff, placeholder, PortAccess.ReadWrite),
            new(ExpansionX4Port.DataPort, 0xffff, placeholder, PortAccess.ReadWrite)];
        try
        {
            controller.ValidateOwnedCard(cardId, "x4", 1, proposed);
            _ = new PortDecoder(otherMappings.Concat(proposed));
        }
        catch (ArgumentException) { return false; }
        var candidate = new ExpansionX4Card(scheduler, controller, x0, cardId, engineDevice);
        controller.RegisterOwnedCard(cardId, "x4", 1, candidate.Port.Mappings(), () => candidate.IsQuiescent);
        card = candidate;
        return true;
    }
}
