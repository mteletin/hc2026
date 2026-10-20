namespace Hc2000.Machine.Timing;

/// <summary>Maps an absolute CPU counter to master ticks without accumulating rounded boundaries.</summary>
public sealed class CpuTimeMapper
{
    public long Numerator { get; }
    public long Denominator { get; }
    public long CpuTStates { get; private set; }
    public long WholeTicks { get; private set; }
    public long Remainder { get; private set; }

    public CpuTimeMapper(long numerator = 5, long denominator = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        var a = numerator;
        var b = denominator;
        while (b != 0) (a, b) = (b, a % b);
        Numerator = numerator / a;
        Denominator = denominator / a;
    }

    /// <summary>Create the epoch's CPU clock from validated profile choices.
    /// Video, floppy and other device clocks remain on the unchanged master timeline.</summary>
    public static CpuTimeMapper FromProfile(MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return new(profile.CpuMasterTicksNumerator, profile.CpuMasterTicksDenominator);
    }

    public long Map(long absoluteCpuTStates)
    {
        if (absoluteCpuTStates < CpuTStates)
            throw new ArgumentOutOfRangeException(nameof(absoluteCpuTStates), "CPU time cannot regress within an epoch.");
        var phase = (Int128)(absoluteCpuTStates - CpuTStates) * Numerator + Remainder;
        var whole = checked(WholeTicks + (long)(phase / Denominator));
        var remainder = (long)(phase % Denominator);
        var boundary = checked(whole + (remainder == 0 ? 0 : 1));
        CpuTStates = absoluteCpuTStates;
        WholeTicks = whole;
        Remainder = remainder;
        return boundary;
    }
}
