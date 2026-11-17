namespace Hc2000.Machine.Floppy;

/// <summary>Independent device rates. The 4-MHz control clock and RC/index pulse defaults are uncalibrated hardware choices.</summary>
public sealed record FdcTiming(long MasterHz = 17_734_475, long FdcHz = 4_000_000,
    int MfmBitsPerSecond = 250_000, int Rpm = 300, int IndexPulseMicroseconds = 2000)
{
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MasterHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FdcHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MfmBitsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Rpm);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(IndexPulseMicroseconds);
        if ((Int128)MasterHz * 8 < MfmBitsPerSecond || (Int128)IndexPulseMicroseconds * Rpm >= 60_000_000)
            throw new ArgumentOutOfRangeException(nameof(MfmBitsPerSecond), "Device periods must be resolvable in master ticks.");
    }
    internal static long Ceil(Int128 numerator, long denominator) => checked((long)((numerator + denominator - 1) / denominator));
    public long ControlCycles(long cycles) => Ceil((Int128)MasterHz * cycles, FdcHz);
    public long Revolutions(long count = 1) => Ceil((Int128)MasterHz * 60 * count, Rpm);
    public long MotorTicks => Ceil((Int128)MasterHz * 17, 10); // approximate 1.7-s RC monostable
}

public sealed record DriveProfile(int Cylinders = 80, int Sides = 2)
{
    internal void Validate()
    {
        // 40/80: the 3.5" mechanisms. 86: the COBRA 5.25" reference-drive profile the shared 8272 core
        // serves (the 860K format is 86 tracks, cpm860k-ro.html); a supported virtual capacity, not a
        // claim that every stock mechanism had 86 physical steps.
        if (Cylinders is not (40 or 80 or 86) || Sides is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(Cylinders));
    }
}
