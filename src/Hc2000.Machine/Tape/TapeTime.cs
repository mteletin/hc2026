using System.Numerics;

namespace Hc2000.Machine.Tape;

/// <summary>Reduced nonnegative rational seconds (spec-10-tape v2 §3). Default is 0/1.
/// No projected scheduler deadline is stored here. Arithmetic/projection belongs to the core.</summary>
public readonly record struct TapeTime : IComparable<TapeTime>
{
    private readonly Int128 denominator;
    public Int128 Numerator { get; }
    public Int128 Denominator => denominator == 0 ? 1 : denominator;

    public TapeTime(Int128 numerator, Int128 denominator)
    {
        if (numerator < 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        var divisor = BigInteger.GreatestCommonDivisor((BigInteger)numerator, (BigInteger)denominator);
        Numerator = (Int128)((BigInteger)numerator / divisor);
        // Store canonical zero the same way as default(TapeTime), including record equality.
        this.denominator = Numerator == 0 ? 0 : (Int128)((BigInteger)denominator / divisor);
    }

    internal static TapeTime Reduced(BigInteger num, BigInteger den)
    {
        if (num < 0 || den <= 0) throw new ArgumentOutOfRangeException(nameof(num));
        var gcd = BigInteger.GreatestCommonDivisor(num, den);
        return new(checked((Int128)(num / gcd)), checked((Int128)(den / gcd)));
    }
    public int CompareTo(TapeTime other) => ((BigInteger)Numerator * (BigInteger)other.Denominator)
        .CompareTo((BigInteger)other.Numerator * (BigInteger)Denominator);
    public static bool operator <(TapeTime a, TapeTime b) => a.CompareTo(b) < 0;
    public static bool operator >(TapeTime a, TapeTime b) => a.CompareTo(b) > 0;
    public static bool operator <=(TapeTime a, TapeTime b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TapeTime a, TapeTime b) => a.CompareTo(b) >= 0;
    public static TapeTime operator +(TapeTime a, TapeTime b) => Reduced(
        (BigInteger)a.Numerator * (BigInteger)b.Denominator + (BigInteger)b.Numerator * (BigInteger)a.Denominator,
        (BigInteger)a.Denominator * (BigInteger)b.Denominator);
    public static TapeTime operator -(TapeTime a, TapeTime b) => Reduced(
        (BigInteger)a.Numerator * (BigInteger)b.Denominator - (BigInteger)b.Numerator * (BigInteger)a.Denominator,
        (BigInteger)a.Denominator * (BigInteger)b.Denominator);
    public static TapeTime operator *(TapeTime a, ulong factor) => Reduced(
        (BigInteger)a.Numerator * factor, (BigInteger)a.Denominator);
    public static TapeTime operator /(TapeTime a, ulong divisor) => Reduced(
        (BigInteger)a.Numerator, (BigInteger)a.Denominator * divisor);
    public long Ceiling() => checked((long)(((BigInteger)Numerator + (BigInteger)Denominator - 1) / (BigInteger)Denominator));
    public ulong Floor() => checked((ulong)(Numerator / Denominator));
    internal ulong Quotient(TapeTime divisor) => checked((ulong)(
        (BigInteger)Numerator * (BigInteger)divisor.Denominator / ((BigInteger)Denominator * (BigInteger)divisor.Numerator)));
}

/// <summary>Explicit post-inverter EAR/D6 level with a positive duration.</summary>
public sealed record TapeRun
{
    public bool Level { get; }
    public TapeTime Duration { get; }
    public TapeRun(bool level, TapeTime duration)
    {
        if (duration.Numerator == 0) throw new ArgumentOutOfRangeException(nameof(duration));
        Level = level;
        Duration = duration;
    }
}
