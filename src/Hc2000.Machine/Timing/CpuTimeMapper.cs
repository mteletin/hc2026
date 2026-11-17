namespace Hc2000.Machine.Timing;

/// <summary>Maps an absolute CPU counter to master ticks without accumulating rounded boundaries.</summary>
public sealed class CpuTimeMapper
{
    public long Numerator { get; }
    public long Denominator { get; }
    public long CpuTStates { get; private set; }
    public long WholeTicks { get; private set; }
    public long Remainder { get; private set; }

    /// <summary>
    /// The denominator of <see cref="Remainder">. Always a multiple of <see cref="Denominator"/> and bounded by the
    /// lcm of every admitted rate's denominator (11,200,000), so the exact remainder stays representable across
    /// runtime speed switches: a switch anchor's reduced fraction may have a denominator the new rate's
    /// <see cref="Denominator"/> does not divide.
    /// </summary>
    public long RemainderDenominator { get; private set; }

    public CpuTimeMapper(long numerator = 5, long denominator = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        var a = numerator;
        var b = denominator;
        while (b != 0) (a, b) = (b, a % b);
        Numerator = numerator / a;
        Denominator = denominator / a;
        RemainderDenominator = Denominator; // fresh epoch: the ratio denominator is the remainder denominator
    }

    /// <summary>Create the epoch's CPU clock from validated profile choices.
    /// Video, floppy and other device clocks remain on the unchanged master timeline.</summary>
    public static CpuTimeMapper FromProfile(MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return new(profile.CpuMasterTicksNumerator, profile.CpuMasterTicksDenominator);
    }

    private (long Whole, long Remainder, long Boundary) Project(long absoluteCpuTStates)
    {
        if (absoluteCpuTStates < CpuTStates)
            throw new ArgumentOutOfRangeException(nameof(absoluteCpuTStates), "CPU time cannot regress within an epoch.");
        var phase = (Int128)(absoluteCpuTStates - CpuTStates) * Numerator * (RemainderDenominator / Denominator) + Remainder;
        var whole = checked(WholeTicks + (long)(phase / RemainderDenominator));
        var remainder = (long)(phase % RemainderDenominator);
        var boundary = checked(whole + (remainder == 0 ? 0 : 1));
        return (whole, remainder, boundary);
    }

    internal long Peek(long absoluteCpuTStates) => Project(absoluteCpuTStates).Boundary;

    public long Map(long absoluteCpuTStates)
    {
        var (whole, remainder, boundary) = Project(absoluteCpuTStates);
        CpuTStates = absoluteCpuTStates;
        WholeTicks = whole;
        Remainder = remainder;
        return boundary;
    }

    /// <summary>
    /// The exact logical time (floor plus fraction, no ceiling) of the last <see cref="Map"/>.
    /// Device queues order CPU effects here; at faithful 5/1 it is always the integer of <see cref="Map"/>.
    /// </summary>
    public LogicalTime ExactTime() => new(WholeTicks, Remainder, RemainderDenominator);

    /// <summary>
    /// The exact logical time (floor plus fraction, no ceiling) of an absolute CPU counter,
    /// without committing it: the logical due for queue ordering of a CPU's next effect.
    /// Like <see cref="Peek"/>, a counter older than the last commit is refused.
    /// </summary>
    public LogicalTime ExactTimeAt(long absoluteCpuTStates)
    {
        var (whole, remainder, _) = Project(absoluteCpuTStates);
        return new(whole, remainder, RemainderDenominator);
    }

    /// <summary>
    /// Adopts a runtime speed-switch anchor: the absolute CPU counter <paramref name="cpuTStates"/> sits at
    /// exactly <paramref name="anchor"/> on the guest timeline, and this (new-rate) mapper continues from it
    /// without reinterpreting the phase. The anchor fraction is stored exactly: the remainder denominator
    /// becomes the lcm of this rate's denominator and the anchor's, which stays bounded because every
    /// admitted rate's denominator divides the same fine unit. The caller verifies the safe boundary
    /// (the anchor is the committed counter's exact time under the previous rate) before calling.
    /// </summary>
    public void Adopt(long cpuTStates, LogicalTime anchor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cpuTStates);
        var d = anchor.FractionDenominator;
        var n = anchor.FractionNumerator;
        var g = d; var x = Denominator;
        while (x != 0) (g, x) = (x, g % x);
        var dr = checked(Denominator / g * d); // lcm: both divide 11,200,000, so it does too
        CpuTStates = cpuTStates;
        WholeTicks = anchor.WholeTicks;
        Remainder = checked(n * (dr / d));
        RemainderDenominator = dr;
    }

    /// <summary>
    /// Seeds a fresh mapper from a versioned .hcs clock section (snapshot load): the exact anchor
    /// (whole ticks, remainder at its own denominator) at the committed CPU counter. The document
    /// is validated first; this applies the same structural rules <see cref="Adopt"/> keeps: the
    /// remainder denominator is a multiple of the ratio denominator and carries the reduced fraction
    /// exactly. A zero-epoch document restores to the fresh state; a re-anchored one (runtime speed
    /// switch) restores the switch anchor instead of re-deriving it from the counter.
    /// </summary>
    internal void Restore(long cpuTStates, long wholeTicks, long remainder, long remainderDenominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cpuTStates);
        ArgumentOutOfRangeException.ThrowIfNegative(wholeTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(remainder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(remainderDenominator);
        if (remainder >= remainderDenominator || remainderDenominator % Denominator != 0)
            throw new ArgumentOutOfRangeException(nameof(remainder), "The snapshot anchor is not representable at this rate.");
        CpuTStates = cpuTStates;
        WholeTicks = wholeTicks;
        Remainder = remainder;
        RemainderDenominator = remainderDenominator;
    }
}
