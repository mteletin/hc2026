using System.Buffers.Binary;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal sealed record X2CardMetadata(int Version, ushort CardId, int DeviceId, ushort BackFirst, ushort FrontFirst,
    X2EngineMetadata Engine, X2PortState Port);

/// <summary>Owner-thread X2 components and their shared admission fence; not yet a machine profile.</summary>
internal sealed class ExpansionX2Card
{
    internal ExpansionX2Engine Engine { get; }
    internal ExpansionX2Upload Upload { get; }
    internal ExpansionX2Port Port { get; }
    internal bool IsQuiescent => Port.IsQuiescent && Upload.IsQuiescent;
    private readonly int deviceId;
    private readonly ushort cardId;
    private readonly ushort? backFirst, frontFirst;
    internal int DeviceId => deviceId;

    internal ExpansionX2Card(DeviceScheduler scheduler, int deviceId, ushort cardId, Memory<byte> back, Memory<byte> front,
        ExpansionServiceDispatcher services,
        ushort? backFirst = null, ushort? frontFirst = null,
        ExpansionDma? dma = null)
    {
        this.deviceId = deviceId; this.cardId = cardId; this.backFirst = backFirst; this.frontFirst = frontFirst;
        ExpansionX2Engine? engine = null;
        Upload = new(cardId, back, () => engine is not null && engine.IsQuiescent, data => engine?.Console.Write(data));
        Engine = engine = new(scheduler, deviceId, back, front,
            kind => kind == X2CommandKind.Present ? Upload.CanPresent : Upload.IsQuiescent,
            kind => { if (kind == X2CommandKind.Clear) Upload.ResetFenceAfterClear(); });
        Port = new(Engine, services, cardId, Upload, dma);
    }

    internal IReadOnlyList<PortMapping> Mappings() => Port.Mappings();
    internal static IReadOnlyList<ExpansionPortAuthority> Authorities =>
        [ExpansionPortAuthority.MainOnly, ExpansionPortAuthority.Exclusive, ExpansionPortAuthority.MainOnly];

    internal X2CardMetadata CaptureMetadata()
    {
        if (backFirst is null || frontFirst is null) throw new InvalidOperationException("X2 planes are not arena-backed.");
        return new(1, cardId, deviceId, backFirst.Value, frontFirst.Value, Engine.CaptureMetadata(), Port.CaptureState());
    }

    internal void ValidateMetadata(X2CardMetadata state, ExpansionX0.ExpansionX0State x0, long now,
        IReadOnlyList<DeviceEvent> events)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(x0);
        if (state.Version != 1 || state.CardId != cardId || state.DeviceId != deviceId ||
            state.BackFirst != backFirst || state.FrontFirst != frontFirst)
            throw new ArgumentException("X2 card roster mismatch.");
        Engine.ValidateMetadata(state.Engine, now, events);
        ExpansionX2Port.ValidateState(state.Port, state.Engine.State);
        if (state.Port.Collecting && state.Engine.PresentPending)
            throw new ArgumentException("X2 port staging conflicts with pending presentation.");
        var channels = x0.Dma.Channels.Where(channel => channel.CardId == cardId).ToArray();
        if (channels.Length != 1 || !Upload.CanRestoreWithEngine(channels[0].EndpointState, state.Engine))
            throw new ArgumentException("X2 upload and rendering state mismatch.");
        if (channels[0].State != (byte)X0DmaState.Idle && state.Port.ChainTag != 0 && channels[0].Tag != 0 && channels[0].Tag != state.Port.ChainTag)
            throw new ArgumentException("X2 chain tag mismatch.");
        if (state.Port.QueryReceipt is not null)
        {
            if (state.Port.QueryReceipt.Length != 20 || state.Port.QueryReceipt[1] != 1)
                throw new ArgumentException("X2 query receipt content invalid.");
            if (state.Port.QueryReceipt[0] == 0xB1)
            {
                if (!Enum.IsDefined((X2UploadPhase)state.Port.QueryReceipt[2]) ||
                    !Enum.IsDefined((X2PortResult)state.Port.QueryReceipt[3]) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(state.Port.QueryReceipt.AsSpan(12)) != state.Port.ChainTag)
                    throw new ArgumentException("X2 query receipt content invalid.");
            }
            else if (state.Port.QueryReceipt[0] is not (0xB2 or 0xB3))
                throw new ArgumentException("X2 query receipt content invalid.");
        }
        var receipts = x0.Services.Cards.Where(receipt => receipt.CardId == cardId).ToArray();
        if (receipts.Length != 1 || receipts[0].State != (byte)X0ServiceState.Idle &&
            (receipts[0].State != (byte)X0ServiceState.Done || receipts[0].ServiceId != ExpansionX2Port.CommandService ||
             receipts[0].Result is not { Length: 1 } || !Enum.IsDefined((X2PortResult)receipts[0].Result[0])))
            throw new ArgumentException("X2 service receipt mismatch.");
    }

    internal void AdoptMetadata(X2CardMetadata state, ExpansionX0.ExpansionX0State x0, long now,
        IReadOnlyList<DeviceEvent> events)
    {
        ValidateMetadata(state, x0, now, events);
        Engine.AdoptMetadata(state.Engine, now, events);
        Port.AdoptState(state.Port);
    }

    /// <summary>Preflight every cold owner-thread admission before reserving pages or publishing a card.
    /// The caller supplies the complete non-controller decoder mappings for the candidate machine.</summary>
    internal static bool TryCompose(HcMemory memory, DeviceScheduler scheduler, ExpansionController controller,
        ExpansionX0 x0, IReadOnlyList<PortMapping> otherMappings, int deviceId, ushort cardId,
        ushort backFirst, ushort frontFirst, out ExpansionX2Card? card)
    {
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(controller); ArgumentNullException.ThrowIfNull(x0);
        ArgumentNullException.ThrowIfNull(otherMappings);
        card = null;
        if (!controller.SharesArena(memory.ExpansionArena) || !x0.Dma.CanRegisterCard(cardId, deviceId, memory, scheduler) ||
            !x0.Services.CanRegisterCard(cardId) ||
            !memory.CanReserveX2Planes(backFirst, frontFirst, cardId)) return false;
        var placeholder = new PortDevice();
        PortMapping[] proposed =
        [new(ExpansionX2Port.CommandPort, 0xffff, placeholder, PortAccess.ReadWrite),
         new(ExpansionX2Port.DataPort, 0xffff, placeholder, PortAccess.ReadWrite),
         new(ExpansionX2Port.SelectorPort, 0xffff, placeholder, PortAccess.ReadWrite)];
        try
        {
            controller.ValidateOwnedCard(cardId, "x2", 1, proposed, Authorities);
            _ = new PortDecoder(otherMappings.Concat(proposed));
        }
        catch (ArgumentException) { return false; }
        if (!memory.TryReserveX2Planes(backFirst, frontFirst, out var back, out var front, cardId))
            throw new InvalidOperationException("Validated X2 plane reservation changed during composition.");
        var candidate = new ExpansionX2Card(scheduler, deviceId, cardId, back, front, x0.Services, backFirst, frontFirst, x0.Dma);
        x0.Dma.RegisterCard(candidate.Upload);
        controller.RegisterOwnedCard(cardId, "x2", 1, candidate.Mappings(), () => candidate.IsQuiescent, Authorities);
        card = candidate;
        return true;
    }
}
