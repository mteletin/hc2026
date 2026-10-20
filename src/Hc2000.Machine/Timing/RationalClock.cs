namespace Hc2000.Machine.Timing;

/// <summary>An independent device period in master ticks. Rounded deadlines never become the phase.</summary>
public sealed class RationalClock
{
    public long Numerator { get; }
    public long Denominator { get; }
    public long WholeTicks { get; private set; }
    public long Remainder { get; private set; }

    public RationalClock(long origin, long numerator, long denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(origin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        WholeTicks = origin;
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>Restores a captured (wholeTicks, remainder) phase (snapshot restore).</summary>
    internal RationalClock(long wholeTicks, long numerator, long denominator, long remainder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wholeTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        ArgumentOutOfRangeException.ThrowIfNegative(remainder);
        WholeTicks = wholeTicks;
        Remainder = checked(remainder < denominator ? remainder : throw new ArgumentException("Remainder must be below the denominator."));
        Numerator = numerator;
        Denominator = denominator;
    }

    // Sources finer than one master tick may yield equal rounded deadlines; their device
    // handler must apply those consequences atomically, not enqueue at scheduler.Now.
    public long Advance(long periods = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periods);
        var phase = (Int128)periods * Numerator + Remainder;
        var whole = checked(WholeTicks + (long)(phase / Denominator));
        var remainder = (long)(phase % Denominator);
        var deadline = checked(whole + (remainder == 0 ? 0 : 1));
        WholeTicks = whole;
        Remainder = remainder;
        return deadline;
    }
}
