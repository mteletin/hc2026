namespace Hc2000.Machine.Timing;

/// <summary>
/// An exact logical point on the master timeline: whole ticks plus a reduced fraction.
/// Devices stay on integer ticks; the main CPU's bus effects may be fractional (for example
/// the legacy 5/2 divider), and they must order exactly, not at a rounded ceiling. Compared
/// by Int128 cross-multiplication, so all admitted rates keep exact ordering without a global
/// fine-tick counter; overflow is rejected, never wrapped.
/// </summary>
public readonly struct LogicalTime : IComparable<LogicalTime>, IEquatable<LogicalTime>
{
    /// <summary>Whole master ticks (the floor of the exact time).</summary>
    public long WholeTicks { get; }
    public long FractionNumerator { get; }
    /// <summary>Never 0: <c>default(LogicalTime)</c> skips the constructor, so its stored denominator is 0 and reads as the
    /// canonical zero 0/1. A 0 here made every fraction compare equal to a fresh scheduler's time.</summary>
    public long FractionDenominator => fractionDenominator == 0 ? 1 : fractionDenominator;
    private readonly long fractionDenominator;

    public LogicalTime(long wholeTicks, long fractionNumerator = 0, long fractionDenominator = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wholeTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(fractionNumerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fractionDenominator);
        if (fractionNumerator > 0)
        {
            wholeTicks = checked(wholeTicks + fractionNumerator / fractionDenominator);
            fractionNumerator %= fractionDenominator;
            var a = fractionNumerator; var b = fractionDenominator;
            var aa = a; var bb = b;
            while (bb != 0) (aa, bb) = (bb, aa % bb);
            fractionNumerator = a / aa;
            fractionDenominator = b / aa;
        }
        else
        {
            fractionDenominator = 1; // zero fraction has one canonical form
        }
        WholeTicks = wholeTicks;
        FractionNumerator = fractionNumerator;
        this.fractionDenominator = fractionDenominator;
    }

    /// <summary>An integer master-tick time (all devices, and every CPU time at faithful 5/1).</summary>
    public static LogicalTime Tick(long wholeTicks) => new(wholeTicks);

    /// <summary>True when the time has no fraction.</summary>
    public bool IsInteger => FractionNumerator == 0;

    public int CompareTo(LogicalTime other)
    {
        var whole = WholeTicks.CompareTo(other.WholeTicks);
        if (whole != 0) return whole;
        var lhs = (Int128)FractionNumerator * other.FractionDenominator;
        var rhs = (Int128)other.FractionNumerator * FractionDenominator;
        return lhs.CompareTo(rhs);
    }

    public bool Equals(LogicalTime other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is LogicalTime other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(WholeTicks, FractionNumerator, FractionDenominator);

    public static bool operator ==(LogicalTime a, LogicalTime b) => a.Equals(b);
    public static bool operator !=(LogicalTime a, LogicalTime b) => !a.Equals(b);
    public static bool operator <(LogicalTime a, LogicalTime b) => a.CompareTo(b) < 0;
    public static bool operator <=(LogicalTime a, LogicalTime b) => a.CompareTo(b) <= 0;
    public static bool operator >(LogicalTime a, LogicalTime b) => a.CompareTo(b) > 0;
    public static bool operator >=(LogicalTime a, LogicalTime b) => a.CompareTo(b) >= 0;

    public override string ToString() => IsInteger
        ? WholeTicks.ToString()
        : $"{WholeTicks}+{FractionNumerator}/{FractionDenominator}";
}
