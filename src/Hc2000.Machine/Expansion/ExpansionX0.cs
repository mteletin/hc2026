using System.Text.Json.Serialization;
using Hc2000.Machine.Network;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

/// <summary>
/// The X0 shared platform: the ONE main-CPU guest-service dispatcher and the ONE shared
/// physical-DMA engine. It exists only under the explicit expansion profile and is composed on
/// the emulation owner thread; no allocation or group exists for other profiles.
/// </summary>
public sealed class ExpansionX0
{
    public const byte StateVersion = 1;

    public ExpansionDma Dma { get; }
    public ExpansionServiceDispatcher Services { get; }

    /// <summary>Host composition only: the X0-bound CP/NET card, when the profile composes it.
    /// Set before any CPU step; captured with the engine state as its bounded sub-snapshot.
    /// Absent on a v1 document, so existing expansion snapshots stay readable.</summary>
    public CpNetX0Card? CpNet { get; set; }

    public ExpansionX0(Hc2000.Machine.Memory.HcMemory memory, DeviceScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(scheduler);
        Dma = new(memory, scheduler);
        Services = new();
    }

    /// <summary>Adopt existing engine/dispatcher instances (tests, partial composition).</summary>
    public ExpansionX0(ExpansionDma dma, ExpansionServiceDispatcher services)
    {
        ArgumentNullException.ThrowIfNull(dma); ArgumentNullException.ThrowIfNull(services);
        Dma = dma;
        Services = services;
    }

    internal sealed record X0DmaChannelState(ushort CardId, ulong EndpointEpoch, byte State, byte[] Staging,
        int StagingCount, bool HasRecord, ushort Page, ushort Offset, uint Count, uint Tag, byte Direction,
        long DueTick, long Token, byte Refusal, byte[]? Data, byte[] EndpointState);

    internal sealed record X0DmaSnapshotState(byte Version, int SetupTicks, int RateNumerator,
        int RateDenominator, X0DmaChannelState[] Channels);

    internal sealed record X0ServiceCardState(ushort CardId, byte State, byte ServiceId, long Deadline,
        long SubmitTick, bool Completed, byte[] Result);

    internal sealed record X0ServiceSnapshotState(byte Version, X0ServiceCardState[] Cards);

    internal sealed record ExpansionX0State(byte Version, X0DmaSnapshotState Dma, X0ServiceSnapshotState Services,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CpNetX0CardSnapshot? CpNet = null);

    /// <summary>
    /// Detached bounded metadata only. A card holding live external state while non-idle makes
    /// the capture unsupported; the host-boundary decision keeps that out of a snapshot.
    /// </summary>
    internal ExpansionX0State CaptureState(Func<ushort, bool>? composedOfflineCard = null)
    {
        var channels = Dma.CaptureChannels();
        foreach (var c in channels)
            if ((c.State == X0DmaState.Queued || c.State == X0DmaState.Programming) && !c.Offline &&
                !(composedOfflineCard?.Invoke(c.CardId) ?? false))
                throw new InvalidOperationException("DMA card holds live external state.");
        var services = Services.CaptureCards();
        foreach (var s in services)
            if (s.State == (byte)X0ServiceState.Pending && !s.Completed)
                throw new InvalidOperationException("Service holds live external state.");
        return new(StateVersion,
            new(StateVersion, Dma.Policy.SetupTicks, Dma.Policy.RateNumerator, Dma.Policy.RateDenominator,
                channels.Select(c => new X0DmaChannelState(c.CardId, c.EndpointEpoch, (byte)c.State, c.Staging,
                    c.StagingCount, c.HasRecord, c.Page, c.Offset, c.Count, c.Tag, c.Direction, c.DueTick,
                    c.Token, (byte)c.Refusal, c.Data, c.EndpointState)).ToArray()),
            new(StateVersion, services.ToArray()),
            CpNet?.TakeSnapshot());
    }

    /// <summary>
    /// Adopt into a detached candidate; every length, cap, capability, generation and event
    /// roster is validated before any live state changes.
    /// </summary>
    internal void ValidateState(ExpansionX0State s, long now, IReadOnlyList<DeviceEvent> events,
        Func<X0DmaChannelState, bool>? transferAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Version != StateVersion || s.Dma.Version != StateVersion || s.Services.Version != StateVersion)
            throw new ArgumentException("Unsupported expansion X0 state version.");
        if (s.CpNet is not null)
        {
            if (CpNet is null)
                throw new ArgumentException("CP/NET card snapshot without the live card.");
        }
        else if (CpNet is not null)
            throw new ArgumentException("CP/NET card snapshot missing for the live card.");
        var policy = Dma.Policy;
        if (s.Dma.SetupTicks != policy.SetupTicks || s.Dma.RateNumerator != policy.RateNumerator ||
            s.Dma.RateDenominator != policy.RateDenominator)
            throw new ArgumentException("DMA timing policy does not match the profile.");
        Dma.ValidateChannels(s.Dma.Channels, now, events, transferAllowed);
        Services.ValidateCards(s.Services.Cards);
    }

    internal void AdoptState(ExpansionX0State s, long now, IReadOnlyList<DeviceEvent> events)
    {
        ValidateState(s, now, events);
        Dma.AdoptChannels(s.Dma.Channels, now, events);
        Services.AdoptCards(s.Services.Cards);
        if (s.CpNet is not null && !CpNet!.Restore(s.CpNet))
            throw new ArgumentException("CP/NET card snapshot does not match the live card.");
    }

    /// <summary>
    /// The v1-save rule: a save without the expansion-x0 component is an explicit empty engine.
    /// Valid only when the live engine roster is empty and no event references a DMA device id.
    /// </summary>
    private ExpansionX0State EmptyState(long now, IReadOnlyList<DeviceEvent> events)
    {
        if (events.Any(e => e.DeviceId >= ExpansionDma.FirstDeviceId))
            throw new ArgumentException("DMA events without the expansion-x0 component.");
        var policy = Dma.Policy;
        return new(StateVersion,
            new(StateVersion, policy.SetupTicks, policy.RateNumerator, policy.RateDenominator, []),
            new(StateVersion, []));
    }

    internal void ValidateEmpty(long now, IReadOnlyList<DeviceEvent> events) =>
        ValidateState(EmptyState(now, events), now, events);

    internal void AdoptEmpty(long now, IReadOnlyList<DeviceEvent> events) =>
        AdoptState(EmptyState(now, events), now, events);
}
