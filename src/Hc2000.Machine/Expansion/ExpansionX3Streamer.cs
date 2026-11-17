using System.Buffers.Binary;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal sealed record X3StreamerSnapshot(int Version, ushort CardId, int NextChannel, long PumpToken,
    X3PlaybackSnapshot Playback);

/// <summary>Round-robin bounded X3 streaming on the existing one-channel-per-card X0 DMA.
/// Production composition and detached snapshot support are not yet wired.</summary>
internal sealed class ExpansionX3Streamer
{
    private const int PumpKind = 1;
    private readonly HcMemory memory;
    private readonly DeviceScheduler scheduler;
    private readonly ExpansionDma dma;
    private readonly int pumpDevice;
    private readonly ushort cardId;
    private readonly ExpansionX3DmaEndpoint endpoint;
    private readonly ExpansionX3Playback playback;
    private long pumpToken;
    private int nextChannel;

    internal ExpansionX3Streamer(HcMemory memory, DeviceScheduler scheduler, ExpansionDma dma, ushort cardId, int clockDevice, int pumpDevice,
        Action<X3OutputEdge> output)
    {
        ArgumentNullException.ThrowIfNull(memory); ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(dma); ArgumentNullException.ThrowIfNull(output);
        if (!CanCompose(memory, scheduler, dma, cardId, clockDevice, pumpDevice))
            throw new ArgumentException("Invalid X3 streaming roster.");
        this.memory = memory; this.scheduler = scheduler; this.dma = dma; this.cardId = cardId; this.pumpDevice = pumpDevice;
        playback = new(scheduler, clockDevice, output, OnTerminal, (_, _) => RequestPump());
        endpoint = new(cardId, playback, () => scheduler.Now, OnRefilled);
        scheduler.RegisterDevice(pumpDevice, Pump);
        dma.RegisterCard(endpoint);
    }

    internal static bool CanCompose(HcMemory memory, DeviceScheduler scheduler, ExpansionDma dma, ushort cardId,
        int clockDevice, int pumpDevice) => cardId != 0 && clockDevice >= 0 && pumpDevice >= 0 && clockDevice != pumpDevice &&
        !dma.HasCard(cardId) && dma.CanRegisterCard(cardId, clockDevice, memory, scheduler) &&
        pumpDevice != ExpansionDma.FirstDeviceId + dma.DeviceIds.Count &&
        !scheduler.DeviceGenerations.Any(row => row.DeviceId == clockDevice || row.DeviceId == pumpDevice);

    internal X3PlaybackState State(int channel) => playback.State(channel);
    internal int Buffered(int channel) => playback.Buffered(channel);
    internal bool IsQuiescent => pumpToken == 0 && endpoint.SelectedChannel == -1 && dma.Status(cardId).State != X0DmaState.Queued &&
        !Enumerable.Range(0, ExpansionX3Clock.ChannelCount).Any(channel => playback.State(channel) is X3PlaybackState.Priming or X3PlaybackState.Playing);
    internal X3ChannelView Inspect(int channel) => playback.Inspect(channel);
    internal bool SetParameters(int channel, ushort period, byte volume) => playback.SetRateAndVolume(channel, period, volume);

    internal X3StreamerSnapshot Capture() => new(1, cardId, nextChannel, pumpToken, playback.Capture());

    // X0 independently validates its own complete FIFO, endpoint and event roster. This checks
    // the relationships X0 cannot know: X3 channel progress, its pump, and the saved grants.
    internal bool ValidState(X3StreamerSnapshot? state, ExpansionX0.X0DmaChannelState? transfer, long now,
        IReadOnlyList<DeviceEvent> events, Func<ushort, bool> pageAllowed)
    {
        if (state is not { Version: 1 } || state.CardId != cardId || state.NextChannel is < 0 or >= ExpansionX3Clock.ChannelCount ||
            state.PumpToken < 0 || transfer is null || transfer.CardId != cardId || transfer.EndpointEpoch != endpoint.Identity.Epoch ||
            events is null || pageAllowed is null || !playback.ValidState(state.Playback, now, events) ||
            !ExpansionX3DmaEndpoint.Decode(transfer.EndpointState, out var savedEndpoint)) return false;
        var pumpEvents = events.Where(item => item.DeviceId == pumpDevice).ToArray();
        if (state.PumpToken == 0 ? pumpEvents.Length != 0 :
            pumpEvents.Length != 1 || pumpEvents[0].Kind != PumpKind || pumpEvents[0].Phase != EventPhase.Controller ||
            pumpEvents[0].InsertionSequence != state.PumpToken || pumpEvents[0].DueTick <= now || pumpEvents[0].Payload != 0)
            return false;
        var queued = transfer.State == (byte)X0DmaState.Queued;
        if (savedEndpoint.Admitted != queued || queued && (state.PumpToken != 0 ||
            transfer.Direction != X0DmaDirection.RamToCard || transfer.Tag != savedEndpoint.Channel ||
            transfer.Page != savedEndpoint.Segment.Page || transfer.Offset != savedEndpoint.Segment.Offset ||
            transfer.Count != savedEndpoint.Segment.Count)) return false;
        for (var channel = 0; channel < ExpansionX3Clock.ChannelCount; channel++)
        {
            var item = state.Playback.Channels[channel];
            var selected = queued && savedEndpoint.Channel == channel;
            if (selected ? item.Segment != savedEndpoint.Segment || item.SegmentGeneration != savedEndpoint.Generation :
                item.Segment is not null) return false;
            if (item.State is X3PlaybackState.Priming or X3PlaybackState.Playing)
            {
                if (!SourcePermitted(item.Sample, pageAllowed)) return false;
                if (!queued && state.PumpToken == 0 && (item.State == X3PlaybackState.Priming ||
                    !item.FillEnded && item.Fifo.Contents.Length <= ExpansionX3Sample.MaximumSegment)) return false;
            }
        }
        return true;
    }

    internal void Restore(X3StreamerSnapshot state, ExpansionX0.X0DmaChannelState transfer, long now,
        IReadOnlyList<DeviceEvent> events, Func<ushort, bool> pageAllowed)
    {
        if (!ValidState(state, transfer, now, events, pageAllowed)) throw new ArgumentException("Invalid X3 streamer state.", nameof(state));
        playback.Restore(state.Playback, now, events);
        nextChannel = state.NextChannel; pumpToken = state.PumpToken;
    }

    internal X3PortResult CheckStart(int channel, ExpansionX3Sample sample)
    {
        if ((uint)channel >= ExpansionX3Clock.ChannelCount || !sample.IsValid) return X3PortResult.Invalid;
        if (playback.Generation(channel) == ulong.MaxValue) return X3PortResult.ResourceLimit;
        if (scheduler.Now > long.MaxValue - 2L - 5L * sample.Period) return X3PortResult.TimeLimit;
        return SourcePermitted(sample) ? X3PortResult.Accepted : X3PortResult.SourceDenied;
    }

    internal bool Start(int channel, ExpansionX3Sample sample)
    {
        if (CheckStart(channel, sample) != X3PortResult.Accepted) return false;
        if (!CancelChannelRefill(channel)) return false;
        if (!playback.Configure(channel, sample)) throw new InvalidOperationException("X3 replacement failed after preflight.");
        RequestPump();
        return true;
    }

    private bool SourcePermitted(ExpansionX3Sample sample) => SourcePermitted(sample, page => memory.TryDmaPageEpoch(page, out _));

    private static bool SourcePermitted(ExpansionX3Sample sample, Func<ushort, bool> pageAllowed)
    {
        if (!sample.IsValid) return false;
        var last = (long)sample.Page + ((long)sample.Offset + sample.Length - 1) / ExpansionDma.PageBytes;
        for (var page = (int)sample.Page; page <= last; page++)
            if (!pageAllowed((ushort)page)) return false;
        return true;
    }

    internal bool Stop(int channel)
    {
        if ((uint)channel >= ExpansionX3Clock.ChannelCount || playback.Generation(channel) == ulong.MaxValue) return false;
        if (!CancelChannelRefill(channel)) return false;
        if (!playback.Stop(channel)) throw new InvalidOperationException("X3 stop failed after preflight.");
        RequestPump();
        return true;
    }

    private bool CancelChannelRefill(int channel)
    {
        if (endpoint.SelectedChannel == channel)
        {
            if (dma.Status(cardId).State == X0DmaState.Queued)
            {
                var result = dma.Trigger(cardId, X0DmaTrigger.Cancel, scheduler.Now);
                if (result.State != X0DmaState.Cancelled) return false;
            }
            else if (!endpoint.Abandon()) return false;
        }
        return true;
    }

    private void RequestPump()
    {
        if (pumpToken != 0 || dma.Status(cardId).State == X0DmaState.Queued) return;
        if (scheduler.Now == long.MaxValue) return;
        pumpToken = scheduler.Schedule(new(scheduler.Now + 1, EventPhase.Controller, pumpDevice, PumpKind,
            scheduler.Generation(pumpDevice)));
    }

    private void Pump(DeviceEvent item)
    {
        if (item.Kind != PumpKind || item.InsertionSequence != pumpToken) throw new InvalidOperationException("Unexpected X3 refill event.");
        pumpToken = 0;
        var state = dma.Status(cardId).State;
        if (state == X0DmaState.Queued) return;
        if (state is X0DmaState.Done or X0DmaState.Error or X0DmaState.Cancelled or X0DmaState.TooLate)
            dma.Trigger(cardId, X0DmaTrigger.Acknowledge, scheduler.Now);
        if (dma.Status(cardId).State != X0DmaState.Idle) throw new InvalidOperationException("X3 DMA channel is not idle.");
        for (var offset = 0; offset < ExpansionX3Clock.ChannelCount; offset++)
        {
            var selected = (nextChannel + offset) % ExpansionX3Clock.ChannelCount;
            if (playback.Buffered(selected) > ExpansionX3Sample.MaximumSegment || !endpoint.Prepare(selected, out var segment)) continue;
            nextChannel = (selected + 1) % ExpansionX3Clock.ChannelCount;
            var descriptor = new byte[ExpansionDma.DescriptorBytes];
            descriptor[0] = 1; descriptor[1] = X0DmaDirection.RamToCard;
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(4), segment.Page);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(6), segment.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), (uint)segment.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), (uint)selected);
            dma.Trigger(cardId, X0DmaTrigger.Begin, scheduler.Now);
            foreach (var value in descriptor) dma.WriteDescriptor(cardId, value);
            var result = dma.Trigger(cardId, X0DmaTrigger.Start, scheduler.Now);
            if (result.State != X0DmaState.Queued)
            {
                if (!endpoint.Abandon()) throw new InvalidOperationException("X3 refused DMA retained its staging.");
                playback.FailDma(selected);
                RequestPump();
            }
            return;
        }
    }

    private void OnRefilled(int channel, long tick)
    {
        if (playback.State(channel) == X3PlaybackState.Priming && !playback.Start(channel))
            playback.FailDma(channel);
        // The shared DMA transitions to Done after its endpoint callback returns.
        if (pumpToken == 0 && tick < long.MaxValue)
            pumpToken = scheduler.Schedule(new(tick + 1, EventPhase.Controller, pumpDevice, PumpKind, scheduler.Generation(pumpDevice)));
    }

    private void OnTerminal(int channel, long tick)
    {
        if (endpoint.SelectedChannel == channel && dma.Status(cardId).State == X0DmaState.Queued)
        {
            var result = dma.Trigger(cardId, X0DmaTrigger.Cancel, tick);
            if (result.State != X0DmaState.Cancelled) throw new InvalidOperationException("X3 terminal could not cancel its refill.");
        }
        RequestPump();
    }
}
