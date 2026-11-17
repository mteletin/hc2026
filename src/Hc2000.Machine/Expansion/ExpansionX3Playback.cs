using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Expansion;

internal enum X3PlaybackState { Empty, Priming, Playing, Ended, Underrun, Stopped, TimeLimit, DmaError }
internal readonly record struct X3OutputEdge(int Channel, long Tick, sbyte Sample, byte Volume);
internal readonly record struct X3ChannelView(X3PlaybackState State, bool Loop, bool PendingParameters, bool Refill,
    byte Volume, ushort Period, int Buffered, uint ReadPosition, ulong Generation, long ObservedTick, long NextTick);
internal sealed record X3PlaybackChannelState(ExpansionX3Sample Sample, X3PlaybackState State,
    uint ReadPosition, uint FillPosition, bool FillEnded, X3SampleSegment? Segment, ulong SegmentGeneration,
    byte Volume, byte? PendingVolume, ushort Period, ushort? PendingPeriod, sbyte Current, X3FifoState Fifo);
internal sealed record X3PlaybackSnapshot(int Version, X3PlaybackChannelState[] Channels, X3ClockState Clock);

/// <summary>Owner-thread PCM consumption for four channels. Refills are reservations: the
/// integrating card must feed them only through the shared DMA and cancel a reservation before
/// stopping or replacing its channel.</summary>
internal sealed class ExpansionX3Playback
{
    private sealed class Channel
    {
        internal readonly ExpansionX3Fifo Fifo = new();
        internal ExpansionX3Sample Sample;
        internal X3PlaybackState State;
        internal uint ReadPosition, FillPosition;
        internal bool FillEnded;
        internal X3SampleSegment? Segment;
        internal ulong SegmentGeneration;
        internal byte Volume;
        internal byte? PendingVolume;
        internal ushort Period;
        internal ushort? PendingPeriod;
        internal sbyte Current;
    }

    private readonly DeviceScheduler scheduler;
    private readonly Action<X3OutputEdge> output;
    private readonly Action<int, long> terminal;
    private readonly Action<int, long>? lowWater;
    private readonly ExpansionX3Clock clock;
    private readonly Channel[] channels = Enumerable.Range(0, ExpansionX3Clock.ChannelCount).Select(_ => new Channel()).ToArray();

    internal ExpansionX3Playback(DeviceScheduler scheduler, int deviceId, Action<X3OutputEdge> output, Action<int, long> terminal,
        Action<int, long>? lowWater = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(terminal);
        this.scheduler = scheduler; this.output = output; this.terminal = terminal; this.lowWater = lowWater;
        clock = new(scheduler, deviceId, Consume, ClockExhausted);
    }

    internal X3PlaybackState State(int channel) => At(channel).State;
    internal int Buffered(int channel) => At(channel).Fifo.Count;
    internal ulong Generation(int channel) => At(channel).Fifo.Generation;
    internal X3ChannelView Inspect(int channel)
    {
        var item = At(channel);
        var currentClock = clock.Capture().Channels[channel];
        return new(item.State, item.Sample.LoopLength != 0, item.PendingPeriod is not null, item.Segment is not null,
            item.Volume, item.Period, item.Fifo.Count, item.ReadPosition, item.Fifo.Generation, scheduler.Now,
            currentClock.Running ? currentClock.DueTick : 0);
    }

    internal X3PlaybackSnapshot Capture() => new(1, channels.Select(item => new X3PlaybackChannelState(item.Sample, item.State,
        item.ReadPosition, item.FillPosition, item.FillEnded, item.Segment, item.SegmentGeneration, item.Volume,
        item.PendingVolume, item.Period, item.PendingPeriod, item.Current, item.Fifo.Capture())).ToArray(), clock.Capture());

    internal bool ValidState(X3PlaybackSnapshot? state, long now, IReadOnlyList<DeviceEvent> events)
    {
        if (state is not { Version: 1, Channels.Length: ExpansionX3Clock.ChannelCount } ||
            !clock.ValidState(state.Clock, now, events)) return false;
        for (var index = 0; index < channels.Length; index++)
        {
            var item = state.Channels[index];
            if (item is null || !Enum.IsDefined(item.State) || !ExpansionX3Fifo.ValidState(item.Fifo) ||
                item.Volume > 64 || item.PendingVolume > 64 || (item.PendingVolume is null) != (item.PendingPeriod is null) ||
                item.PendingPeriod is { } pending && pending < ExpansionX3Sample.MinimumPeriod) return false;
            var active = item.State is X3PlaybackState.Priming or X3PlaybackState.Playing;
            var clockState = state.Clock.Channels[index];
            if ((item.State == X3PlaybackState.Playing) != clockState.Running ||
                !active && (item.PendingPeriod is not null || item.Segment is not null || item.Current != 0) ||
                clockState.Running && (clockState.Period != item.Period || clockState.PendingPeriod != (item.PendingPeriod ?? 0))) return false;
            if (item.State == X3PlaybackState.Empty || item.State == X3PlaybackState.Stopped && item.Sample == default)
            {
                if (item.Sample != default || item.ReadPosition != 0 || item.FillPosition != 0 || item.FillEnded ||
                    item.Volume != 0 || item.Period != 0 || item.Fifo.Contents.Length != 0 || item.Fifo.Reserved != 0) return false;
            }
            else if (!item.Sample.IsValid || item.Period < ExpansionX3Sample.MinimumPeriod ||
                item.ReadPosition > item.Sample.Length || item.FillPosition > item.Sample.Length ||
                item.Sample.LoopLength != 0 && (item.ReadPosition >= item.Sample.LoopStart + item.Sample.LoopLength ||
                    item.FillPosition >= item.Sample.LoopStart + item.Sample.LoopLength || item.FillEnded) ||
                item.FillEnded && item.FillPosition != item.Sample.Length ||
                item.State == X3PlaybackState.Ended && item.ReadPosition != item.Sample.Length) return false;
            if (active && (Advance(item.Sample, item.ReadPosition, item.Fifo.Contents.Length) != item.FillPosition ||
                item.Sample.LoopLength == 0 && item.FillEnded != (item.FillPosition == item.Sample.Length) ||
                item.State == X3PlaybackState.Priming && (item.ReadPosition != 0 || item.Current != 0))) return false;
            if (item.Segment is { } segment)
            {
                if (item.Fifo.Reserved != segment.Count || item.SegmentGeneration != item.Fifo.Generation ||
                    !item.Sample.TrySegment(item.FillPosition, item.Fifo.Reserved, out var expected) || segment != expected) return false;
            }
            else if (item.Fifo.Reserved != 0 || item.SegmentGeneration != 0) return false;
        }
        return true;
    }

    private static ulong Advance(ExpansionX3Sample sample, uint position, int count)
    {
        if (sample.LoopLength == 0) return (ulong)position + (uint)count;
        var end = sample.LoopStart + sample.LoopLength;
        var untilWrap = end - position;
        return count < untilWrap ? (ulong)position + (uint)count :
            sample.LoopStart + ((ulong)(uint)count - untilWrap) % sample.LoopLength;
    }

    internal void Restore(X3PlaybackSnapshot state, long now, IReadOnlyList<DeviceEvent> events)
    {
        if (!ValidState(state, now, events)) throw new ArgumentException("Invalid X3 playback state.", nameof(state));
        for (var index = 0; index < channels.Length; index++)
        {
            var item = channels[index]; var saved = state.Channels[index];
            item.Fifo.Restore(saved.Fifo); item.Sample = saved.Sample; item.State = saved.State;
            item.ReadPosition = saved.ReadPosition; item.FillPosition = saved.FillPosition; item.FillEnded = saved.FillEnded;
            item.Segment = saved.Segment; item.SegmentGeneration = saved.SegmentGeneration;
            item.Volume = saved.Volume; item.PendingVolume = saved.PendingVolume; item.Period = saved.Period;
            item.PendingPeriod = saved.PendingPeriod; item.Current = saved.Current;
        }
        clock.Restore(state.Clock, now, events);
    }

    internal bool CanConfigure(int channel, ExpansionX3Sample sample) =>
        (uint)channel < channels.Length && sample.IsValid && channels[channel].Fifo.Generation < ulong.MaxValue;

    internal bool Configure(int channel, ExpansionX3Sample sample)
    {
        if (!CanConfigure(channel, sample)) return false;
        var item = channels[channel];
        if (item.Segment is not null) return false;
        var wasPlaying = item.State == X3PlaybackState.Playing;
        if (wasPlaying && !clock.Stop(channel)) throw new InvalidOperationException("X3 clock missing.");
        if (!item.Fifo.Reset()) throw new InvalidOperationException("X3 FIFO could not reset.");
        if (wasPlaying) output(new(channel, scheduler.Now, 0, item.Volume));
        item.Sample = sample; item.State = X3PlaybackState.Priming; item.ReadPosition = item.FillPosition = 0;
        item.FillEnded = false; item.Volume = sample.Volume; item.Period = sample.Period;
        item.PendingVolume = null; item.PendingPeriod = null; item.Current = 0;
        return true;
    }

    internal bool TryPrepareRefill(int channel, out X3SampleSegment segment, out ulong generation)
    {
        if (!PreviewRefill(channel, out segment, out generation)) return false;
        return ReserveRefill(channel, segment, generation);
    }

    internal bool PreviewRefill(int channel, out X3SampleSegment segment, out ulong generation)
    {
        segment = default; generation = 0;
        if ((uint)channel >= channels.Length) return false;
        var item = channels[channel];
        if (item.State is not (X3PlaybackState.Priming or X3PlaybackState.Playing) || item.Segment is not null || item.FillEnded ||
            !item.Sample.TrySegment(item.FillPosition, item.Fifo.Free, out segment)) return false;
        generation = item.Fifo.Generation;
        return true;
    }

    internal bool ReserveRefill(int channel, X3SampleSegment segment, ulong generation)
    {
        if (!PreviewRefill(channel, out var current, out var currentGeneration) ||
            current != segment || currentGeneration != generation) return false;
        var item = channels[channel];
        if (!item.Fifo.TryReserve(generation, segment.Count)) return false;
        item.Segment = segment; item.SegmentGeneration = generation;
        return true;
    }

    internal void CommitRefill(int channel, ulong generation, ReadOnlySpan<byte> bytes)
    {
        var item = At(channel);
        if (item.Segment is not { } segment || generation != item.SegmentGeneration || bytes.Length != segment.Count)
            throw new InvalidOperationException("Invalid X3 refill completion.");
        item.Fifo.Commit(generation, bytes);
        item.FillPosition = segment.NextPosition; item.FillEnded = segment.Ended;
        item.Segment = null; item.SegmentGeneration = 0;
    }

    internal bool CancelRefill(int channel, ulong generation)
    {
        if ((uint)channel >= channels.Length) return false;
        var item = channels[channel];
        if (item.Segment is null || generation != item.SegmentGeneration || !item.Fifo.Cancel(generation)) return false;
        item.Segment = null; item.SegmentGeneration = 0;
        return true;
    }

    internal bool Start(int channel)
    {
        if ((uint)channel >= channels.Length) return false;
        var item = channels[channel];
        if (item.State != X3PlaybackState.Priming || item.Fifo.Count == 0 || !clock.Start(channel, item.PendingPeriod ?? item.Period)) return false;
        if (item.PendingPeriod is { } period) { item.Period = period; item.PendingPeriod = null; }
        if (item.PendingVolume is { } volume) { item.Volume = volume; item.PendingVolume = null; }
        item.State = X3PlaybackState.Playing;
        return true;
    }

    internal bool SetRateAndVolume(int channel, ushort period, byte volume)
    {
        if ((uint)channel >= channels.Length || volume > 64 || period < ExpansionX3Sample.MinimumPeriod) return false;
        var item = channels[channel];
        if (item.State is not (X3PlaybackState.Priming or X3PlaybackState.Playing)) return false;
        if (item.State == X3PlaybackState.Playing && !clock.SetPeriod(channel, period)) return false;
        item.PendingPeriod = period; item.PendingVolume = volume;
        return true;
    }

    internal bool Stop(int channel)
    {
        if ((uint)channel >= channels.Length) return false;
        var item = channels[channel];
        if (item.Segment is not null || item.Fifo.Generation == ulong.MaxValue) return false;
        if (item.State == X3PlaybackState.Playing && !clock.Stop(channel)) throw new InvalidOperationException("X3 clock missing.");
        if (!item.Fifo.Reset()) throw new InvalidOperationException("X3 FIFO could not reset.");
        item.State = X3PlaybackState.Stopped; item.Current = 0; item.PendingVolume = null; item.PendingPeriod = null;
        output(new(channel, scheduler.Now, 0, item.Volume));
        return true;
    }

    internal void FailDma(int channel)
    {
        var item = At(channel);
        if (item.Segment is not null) throw new InvalidOperationException("X3 DMA reservation still active.");
        if (item.State == X3PlaybackState.Playing && !clock.Stop(channel)) throw new InvalidOperationException("X3 clock missing.");
        Finish(channel, scheduler.Now, X3PlaybackState.DmaError);
    }

    private bool Consume(int channel, long tick)
    {
        var item = channels[channel];
        if (item.State != X3PlaybackState.Playing) throw new InvalidOperationException("X3 edge for an inactive channel.");
        if (item.PendingPeriod is { } period) { item.Period = period; item.PendingPeriod = null; }
        if (item.PendingVolume is { } volume) { item.Volume = volume; item.PendingVolume = null; }
        if (item.Sample.LoopLength == 0 && item.ReadPosition == item.Sample.Length)
        {
            Finish(channel, tick, X3PlaybackState.Ended);
            return false;
        }
        if (!item.Fifo.TryRead(out var value))
        {
            Finish(channel, tick, X3PlaybackState.Underrun);
            return false;
        }
        item.Current = unchecked((sbyte)value);
        item.ReadPosition++;
        if (item.Sample.LoopLength != 0 && item.ReadPosition == item.Sample.LoopStart + item.Sample.LoopLength)
            item.ReadPosition = item.Sample.LoopStart;
        output(new(channel, tick, item.Current, item.Volume));
        if (item.Fifo.Count <= ExpansionX3Sample.MaximumSegment) lowWater?.Invoke(channel, tick);
        return true;
    }

    private void ClockExhausted(int channel, long tick) => Finish(channel, tick, X3PlaybackState.TimeLimit);

    private void Finish(int channel, long tick, X3PlaybackState state)
    {
        var item = channels[channel];
        item.State = state; item.Current = 0; item.PendingVolume = null; item.PendingPeriod = null;
        output(new(channel, tick, 0, item.Volume));
        terminal(channel, tick);
    }

    private Channel At(int channel) => (uint)channel < channels.Length ? channels[channel] : throw new ArgumentOutOfRangeException(nameof(channel));
}
