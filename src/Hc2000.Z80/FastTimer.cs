using System.Runtime.CompilerServices;

namespace Hc2000.Z80;

/// <summary>
/// Batched T-state delta accumulator for the IArrayBus fast path.
/// Non-M1 bus accesses (data fetches, reads, writes, idle) accumulate their
/// T-state cost here instead of writing to <see cref="Z80.TStates"/> every time.
/// The M1 fetch still updates <see cref="Z80.TStates"/> directly (exact per-access,
/// as required by <see cref="IM1FetchBus"/> observers).
/// At instruction end (and at any externally observable boundary), call
/// <see cref="Flush"/> to apply the accumulated delta to the real counter.
/// </summary>
public sealed class FastTimer
{
    private long _delta;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count) => _delta += count;

    /// <summary>Apply the accumulated delta to the real TStates field and reset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush(ref long tstates)
    {
        tstates += _delta;
        _delta = 0;
    }
}
