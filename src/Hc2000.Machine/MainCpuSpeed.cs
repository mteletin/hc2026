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
    MHz280 = 4,
    /// <summary>140 MHz (hc2000-speed-140-turbo-280): appended, so the wire byte and snapshot values 0-4 keep their meaning.</summary>
    MHz140 = 5
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
        MainCpuSpeed.MHz140 => 140_000_000,
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
        MainCpuSpeed.MHz140 => "140MHz",
        MainCpuSpeed.MHz280 => "280MHz",
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null)
    };

    /// <summary>Parse a setting string (case-insensitive, trimmed; a bare number means MHz).
    /// Refuses anything that is not one of the six admitted choices.</summary>
    public static MainCpuSpeed ParseSetting(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        return text switch
        {
            "faithful" or "faithful (3.5 mhz class)" => MainCpuSpeed.Faithful,
            "7mhz" or "7" => MainCpuSpeed.MHz7,
            "14mhz" or "14" => MainCpuSpeed.MHz14,
            "28mhz" or "28" => MainCpuSpeed.MHz28,
            "140mhz" or "140" => MainCpuSpeed.MHz140,
            "280mhz" or "280" => MainCpuSpeed.MHz280,
            _ => throw new ArgumentException($"Main CPU speed must be faithful, 7MHz, 14MHz, 28MHz, 140MHz or 280MHz, not \"{value.Trim()}\".", nameof(value))
        };
    }

    /// <summary>The setting that leaves the clock to the machine (hc2000-speed-140-turbo-280, the default since
    /// 2026-11-26): 140 MHz on the machines that start CP/M, the faithful 3.5 MHz on Classic and the BASIC expansion
    /// machines. Settings files written before it
    /// carry no clock when it was faithful and read as this.</summary>
    public const string Auto = "auto";

    /// <summary>Whether <paramref name="setting"/> is <see cref="Auto"/>.</summary>
    public static bool IsAuto(string setting) => string.Equals(setting.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>The clock <see cref="Auto"/> means for a profile: 140 MHz for the machines that start CP/M (the workstations,
    /// CP/NET, the HDD guest preset), faithful for Classic and the BASIC expansion machines (the ROM's tape loader counts
    /// the real clock).</summary>
    public static MainCpuSpeed DefaultFor(string profileName) => MachineProfile.StartsCpm(profileName) ? MainCpuSpeed.MHz140 : MainCpuSpeed.Faithful;

    /// <summary>The clock a setting selects on a profile: <see cref="Auto"/> resolves per profile, anything else is
    /// parsed (and refused when it is not an admitted choice).</summary>
    public static MainCpuSpeed Resolve(string setting, string profileName) => IsAuto(setting) ? DefaultFor(profileName) : ParseSetting(setting);

    /// <summary>The clock choice whose ratio is <paramref name="divider"/> (master ticks per T-state, any equal
    /// fraction): an admitted target with no legacy turbo, or the faithful clock with its legacy cpu.turbo (1, 2, 4 or 8).
    /// False when no choice has it.</summary>
    public static bool FromDivider((long Numerator, long Denominator) divider, out MainCpuSpeed speed, out int legacyTurbo)
    {
        static bool Same((long N, long D) a, (long N, long D) b) => (System.Int128)a.N * b.D == (System.Int128)b.N * a.D;
        foreach (var turbo in new[] { 1, 2, 4, 8 })
            if (Same(divider, (5, turbo))) { speed = MainCpuSpeed.Faithful; legacyTurbo = turbo; return true; }
        foreach (var choice in All)
            if (choice != MainCpuSpeed.Faithful && Same(divider, choice.MasterTicksPerTState())) { speed = choice; legacyTurbo = 1; return true; }
        speed = MainCpuSpeed.Faithful; legacyTurbo = 1;
        return false;
    }

    /// <summary>Refuses a setting that is neither <see cref="Auto"/> nor an admitted choice.</summary>
    public static void ValidateSetting(string setting) { if (!IsAuto(setting)) ParseSetting(setting); }

    /// <summary>Every admitted choice, in UI order.</summary>
    public static IReadOnlyList<MainCpuSpeed> All { get; } =
        [MainCpuSpeed.Faithful, MainCpuSpeed.MHz7, MainCpuSpeed.MHz14, MainCpuSpeed.MHz28, MainCpuSpeed.MHz140, MainCpuSpeed.MHz280];

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
