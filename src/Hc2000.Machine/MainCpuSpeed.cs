using Hc2000.Machine.Timing;

namespace Hc2000.Machine;

/// <summary>
/// The user's main-Z80 clock choice (hc2000-accelerated-clock-domains, owner decision #317):
/// the faithful 3.5 MHz class or one of the explicit 7, 14, 28 or 280 MHz targets. The device
/// epoch, video and frame-interrupt cadence and every device clock domain stay on the unchanged
/// 17,734,475 Hz master timeline regardless of this choice; only the main CPU clock changes.
/// <para>
/// Each choice is the exact reduced rational ratio of master ticks per CPU T-state —
/// 17,734,475 / Hz with checked arithmetic, never a per-T-state rounding. This is a distinct
/// dimension from the legacy <c>cpu.turbo</c> x2/x4/x8 multipliers (multiples of 3,546,895 T/s)
/// and from the wall-clock turbo; legacy settings, profiles and snapshots are never silently
/// reinterpreted. 280 MHz is a target: achieved throughput is measured and reported, never
/// promised.
/// </para>
/// </summary>
public enum MainCpuSpeed
{
    Faithful = 0,
    MHz7 = 1,
    MHz14 = 2,
    MHz28 = 3,
    MHz280 = 4
}

public static class MainCpuSpeedInfo
{
    /// <summary>The exact requested T-states per second (the requested rate, not the achieved one).</summary>
    public static long TStatesPerSecond(this MainCpuSpeed speed) => speed switch
    {
        MainCpuSpeed.Faithful => 3_546_895,
        MainCpuSpeed.MHz7 => 7_000_000,
        MainCpuSpeed.MHz14 => 14_000_000,
        MainCpuSpeed.MHz28 => 28_000_000,
        MainCpuSpeed.MHz280 => 280_000_000,
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null)
    };

    /// <summary>The user-facing setting strings (config and UI): <c>faithful</c> or <c>7MHz</c>,
    /// <c>14MHz</c>, <c>28MHz</c>, <c>280MHz</c>. The UI labels keep the "target" wording the honest
    /// reporting requires: 280 MHz is a target, not a promise.</summary>
    public static string Setting(this MainCpuSpeed speed) => speed switch
    {
        MainCpuSpeed.Faithful => "faithful",
        MainCpuSpeed.MHz7 => "7MHz",
        MainCpuSpeed.MHz14 => "14MHz",
        MainCpuSpeed.MHz28 => "28MHz",
        MainCpuSpeed.MHz280 => "280MHz",
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null)
    };

    /// <summary>Parse a setting string (case-insensitive, trimmed; a bare number means MHz).
    /// Refuses anything that is not one of the five admitted choices.</summary>
    public static MainCpuSpeed ParseSetting(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        return text switch
        {
            "faithful" or "faithful (3.5 mhz class)" => MainCpuSpeed.Faithful,
            "7mhz" or "7" => MainCpuSpeed.MHz7,
            "14mhz" or "14" => MainCpuSpeed.MHz14,
            "28mhz" or "28" => MainCpuSpeed.MHz28,
            "280mhz" or "280" => MainCpuSpeed.MHz280,
            _ => throw new ArgumentException($"Main CPU speed must be faithful, 7MHz, 14MHz, 28MHz or 280MHz, not \"{value.Trim()}\".", nameof(value))
        };
    }

    /// <summary>Every admitted choice, in UI order.</summary>
    public static IReadOnlyList<MainCpuSpeed> All { get; } =
        [MainCpuSpeed.Faithful, MainCpuSpeed.MHz7, MainCpuSpeed.MHz14, MainCpuSpeed.MHz28, MainCpuSpeed.MHz280];

    /// <summary>The exact reduced master-ticks-per-T-state ratio: 17,734,475 / Hz, gcd-reduced, checked.
    /// The faithful choice is exactly 5/1, so faithful machines stay byte-identical to the legacy mapping.</summary>
    public static (long Numerator, long Denominator) MasterTicksPerTState(this MainCpuSpeed speed)
    {
        var master = FrameTiming.MasterHz;
        var hz = speed.TStatesPerSecond();
        long a = master, b = hz;
        while (b != 0) (a, b) = (b, a % b);
        if (a <= 0 || master % a != 0 || hz % a != 0)
            throw new OverflowException($"Main CPU ratio does not reduce integrally for {speed}.");
        return (master / a, hz / a);
    }
}
