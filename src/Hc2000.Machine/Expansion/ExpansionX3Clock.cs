using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal sealed record X3ClockChannel(bool Running, ushort Period, ushort PendingPeriod, long DueTick, long Token);
internal sealed record X3ClockState(int Version, X3ClockChannel[] Channels);

/// <summary>Four independent, master-tick sample clocks. The owner supplies the actual FIFO
/// consumption; this scheduler is not a host audio clock or a second DMA engine.</summary>
internal sealed class ExpansionX3Clock
{
    internal const int ChannelCount = 4;
    private readonly DeviceScheduler scheduler;
    private readonly int deviceId;
    private readonly Func<int, long, bool> edge;
    private readonly Action<int, long> exhausted;
    private readonly X3ClockChannel[] channels = Enumerable.Repeat(new X3ClockChannel(false, 0, 0, 0, 0), ChannelCount).ToArray();
    private readonly bool[] dispatching = new bool[ChannelCount];

    internal ExpansionX3Clock(DeviceScheduler scheduler, int deviceId, Func<int, long, bool> edge, Action<int, long> exhausted)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(exhausted);
        this.scheduler = scheduler; this.deviceId = deviceId; this.edge = edge; this.exhausted = exhausted;
        scheduler.RegisterDevice(deviceId, OnEdge);
    }

    internal bool Start(int channel, ushort period)
    {
        if ((uint)channel >= ChannelCount || dispatching[channel] || channels[channel].Running || period < ExpansionX3Sample.MinimumPeriod ||
            !TryDue(scheduler.Now, period, out var due)) return false;
        var token = Schedule(channel, due);
        channels[channel] = new(true, period, 0, due, token);
        return true;
    }

    internal bool SetPeriod(int channel, ushort period)
    {
        if ((uint)channel >= ChannelCount || !channels[channel].Running || period < ExpansionX3Sample.MinimumPeriod) return false;
        channels[channel] = channels[channel] with { PendingPeriod = period };
        return true;
    }

    internal bool Stop(int channel)
    {
        if ((uint)channel >= ChannelCount || !channels[channel].Running) return false;
        var state = channels[channel];
        if (state.Token != 0 && !scheduler.Cancel(state.Token)) throw new InvalidOperationException("X3 clock event missing.");
        channels[channel] = new(false, 0, 0, 0, 0);
        return true;
    }

    internal X3ClockState Capture()
    {
        if (dispatching.Any(value => value)) throw new InvalidOperationException("X3 clock capture during an edge.");
        return new(1, (X3ClockChannel[])channels.Clone());
    }

    internal bool ValidState(X3ClockState? state, long now, IReadOnlyList<DeviceEvent> events)
    {
        if (now < 0 || state is not { Version: 1, Channels.Length: ChannelCount } || events is null) return false;
        var owned = events.Where(item => item.DeviceId == deviceId).ToArray();
        for (var index = 0; index < ChannelCount; index++)
        {
            var channel = state.Channels[index];
            if (channel is null) return false;
            if (!channel.Running)
            {
                if (channel != new X3ClockChannel(false, 0, 0, 0, 0)) return false;
                continue;
            }
            if (channel.Period < ExpansionX3Sample.MinimumPeriod ||
                channel.PendingPeriod != 0 && channel.PendingPeriod < ExpansionX3Sample.MinimumPeriod ||
                channel.Token <= 0 || channel.DueTick <= now ||
                owned.Count(item => item.Kind == index + 1 && item.DueTick == channel.DueTick &&
                    item.InsertionSequence == channel.Token && item.Phase == EventPhase.SignalEdge && item.Payload == 0) != 1)
                return false;
        }
        return owned.Length == state.Channels.Count(channel => channel.Running);
    }

    internal void Restore(X3ClockState state, long now, IReadOnlyList<DeviceEvent> events)
    {
        if (dispatching.Any(value => value)) throw new InvalidOperationException("X3 clock restore during an edge.");
        if (!ValidState(state, now, events)) throw new ArgumentException("Invalid X3 clock state.", nameof(state));
        state.Channels.CopyTo(channels, 0);
    }

    private void OnEdge(DeviceEvent item)
    {
        var channel = item.Kind - 1;
        if ((uint)channel >= ChannelCount || !channels[channel].Running || channels[channel].Token != item.InsertionSequence ||
            channels[channel].DueTick != item.DueTick) throw new InvalidOperationException("Unexpected X3 sample edge.");
        var state = channels[channel];
        var period = state.PendingPeriod == 0 ? state.Period : state.PendingPeriod;
        channels[channel] = state with { Period = period, PendingPeriod = 0, Token = 0 };
        bool keepRunning;
        dispatching[channel] = true;
        try { keepRunning = edge(channel, item.DueTick); }
        finally { dispatching[channel] = false; }
        if (!channels[channel].Running) return;
        if (!keepRunning) { Stop(channel); return; }
        period = channels[channel].Period;
        if (!TryDue(item.DueTick, period, out var due))
        {
            Stop(channel);
            exhausted(channel, item.DueTick);
            return;
        }
        var token = Schedule(channel, due);
        channels[channel] = channels[channel] with { DueTick = due, Token = token };
    }

    private long Schedule(int channel, long due) => scheduler.Schedule(new(due, EventPhase.SignalEdge,
        deviceId, channel + 1, scheduler.Generation(deviceId)));

    private static bool TryDue(long now, ushort period, out long due)
    {
        var interval = 5L * period;
        if (now > long.MaxValue - interval) { due = 0; return false; }
        due = now + interval;
        return true;
    }
}
