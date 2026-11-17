using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal sealed record X3CardMetadata(int Version, ushort CardId, int ClockDevice, int PumpDevice,
    X3StreamerSnapshot Streamer, X3PortSnapshot Port);

/// <summary>Cold X3 roster admission; the production machine and offline state are wired separately.</summary>
internal sealed class ExpansionX3Card
{
    internal ExpansionX3Streamer Streamer { get; }
    internal ExpansionX3Port Port { get; }
    internal bool IsQuiescent => Port.IsQuiescent;
    private readonly ushort cardId;
    private readonly int clockDevice, pumpDevice;
    internal ushort CardId => cardId;
    internal int ClockDevice => clockDevice;
    internal int PumpDevice => pumpDevice;

    private ExpansionX3Card(HcMemory memory, DeviceScheduler scheduler, ExpansionController controller,
        ExpansionX0 x0, ushort cardId, int clockDevice, int pumpDevice, Action<X3OutputEdge> output)
    {
        this.cardId = cardId; this.clockDevice = clockDevice; this.pumpDevice = pumpDevice;
        Streamer = new(memory, scheduler, x0.Dma, cardId, clockDevice, pumpDevice, output);
        Port = new(scheduler, Streamer, x0.Services, controller, cardId);
    }

    internal X3CardMetadata CaptureMetadata() => new(1, cardId, clockDevice, pumpDevice, Streamer.Capture(), Port.Capture());

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

    internal void ValidateMetadata(X3CardMetadata state, ExpansionX0.ExpansionX0State x0, long now,
        IReadOnlyList<DeviceEvent> events, Func<ushort, bool> pageAllowed, Func<X3LogicalPrincipal, bool> ownerAllowed)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(x0);
        if (state.Version != 1 || state.CardId != cardId || state.ClockDevice != clockDevice || state.PumpDevice != pumpDevice)
            throw new ArgumentException("X3 card roster mismatch.");
        var transfers = x0.Dma.Channels.Where(item => item.CardId == cardId).ToArray();
        var receipts = x0.Services.Cards.Where(item => item.CardId == cardId).ToArray();
        if (transfers.Length != 1 || receipts.Length != 1 ||
            !Streamer.ValidState(state.Streamer, transfers[0], now, events, pageAllowed) ||
            !Port.ValidState(state.Port, receipts[0], now, ownerAllowed))
            throw new ArgumentException("X3 playback, transport and X0 state mismatch.");
    }

    internal void AdoptMetadata(X3CardMetadata state, ExpansionX0.ExpansionX0State x0, long now,
        IReadOnlyList<DeviceEvent> events, Func<ushort, bool> pageAllowed, Func<X3LogicalPrincipal, bool> ownerAllowed)
    {
        ValidateMetadata(state, x0, now, events, pageAllowed, ownerAllowed);
        Streamer.Restore(state.Streamer, x0.Dma.Channels.Single(item => item.CardId == cardId), now, events, pageAllowed);
        Port.Restore(state.Port, x0.Services.Cards.Single(item => item.CardId == cardId), now, ownerAllowed);
    }

    internal static bool TryCompose(HcMemory memory, DeviceScheduler scheduler, ExpansionController controller,
        ExpansionX0 x0, IReadOnlyList<PortMapping> otherMappings, ushort cardId, int clockDevice, int pumpDevice,
        Action<X3OutputEdge> output, out ExpansionX3Card? card)
    {
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(controller); ArgumentNullException.ThrowIfNull(x0);
        ArgumentNullException.ThrowIfNull(otherMappings); ArgumentNullException.ThrowIfNull(output);
        card = null;
        if (!controller.SharesArena(memory.ExpansionArena) || !x0.Services.CanRegisterCard(cardId) ||
            !ExpansionX3Streamer.CanCompose(memory, scheduler, x0.Dma, cardId, clockDevice, pumpDevice)) return false;
        var placeholder = new PortDevice();
        PortMapping[] proposed = [new(ExpansionX3Port.CommandPort, 0xffff, placeholder, PortAccess.ReadWrite),
            new(ExpansionX3Port.DataPort, 0xffff, placeholder, PortAccess.ReadWrite)];
        try
        {
            controller.ValidateOwnedCard(cardId, "x3", 1, proposed);
            _ = new PortDecoder(otherMappings.Concat(proposed));
        }
        catch (ArgumentException) { return false; }
        var candidate = new ExpansionX3Card(memory, scheduler, controller, x0, cardId, clockDevice, pumpDevice, output);
        controller.RegisterOwnedCard(cardId, "x3", 1, candidate.Port.Mappings(), () => candidate.IsQuiescent);
        card = candidate;
        return true;
    }
}
