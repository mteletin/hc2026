using Hc2000.App.Media;
using Hc2000.Machine;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;

namespace Hc2000.App.Runtime;

/// <summary>
/// Deployed selection path for the accepted cpu.turbo multiplier.
/// Host playback speed (SpeedPercent / SpeedMode) is a different control and is not read here.
/// The catalog profiles stay at x1; this composes an owned copy through CpuTimeMapper.FromProfile.
/// </summary>
public static class CpuTurboSelection
{
    public const string Flag = "--cpu-turbo";
    public const string Workload = "synthetic-untimed-4t";
    public const int Frames = 3;

    public static bool IsRequest(IReadOnlyList<string> args) =>
        args.Any(a => a == Flag || a.StartsWith(Flag + "=", StringComparison.Ordinal));

    /// <summary>Headless selection. Exit 0 prints the applied multiplier and measured frame; exit 2 rejects the choice.</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (!TryParse(args, out var profileId, out var multiplier, out var error))
        {
            stderr.WriteLine(error);
            stderr.WriteLine("usage: hc2000 --cpu-turbo 1|2|4|8 [--profile hc2000-v2]");
            return 2;
        }

        var catalog = ProfileRomSelection.Describe(profileId);
        var owned = catalog with { CpuTurboMultiplier = multiplier };
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var mapper = CpuTimeMapper.FromProfile(owned);
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        var memory = new HcMemory(owned.Memory with { RomProvenance = "synthetic turbo timing fixture" },
            new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var ula = new Ula(memory, new KeyboardMatrix(), scheduler, clock, 2, owned.Video);
        var interrupts = new List<(long Tick, bool Asserted)>();
        clock.InterruptChanged += (tick, asserted) => interrupts.Add((tick, asserted));
        long states = 0, work = 0;
        var machine = new Hc2000.Machine.Machine(scheduler, clock, adapter, ula, () => { states += 4; work++; }, () => states);
        for (var frame = 0; frame < Frames; frame++) machine.RunFrame();

        stdout.Write(
            $"selection=cpu.turbo{Environment.NewLine}" +
            $"profile={catalog.Name}{Environment.NewLine}" +
            $"multiplier={owned.CpuTurboMultiplier}{Environment.NewLine}" +
            $"workload={Workload}{Environment.NewLine}" +
            $"frames={Frames}{Environment.NewLine}" +
            $"work={work}{Environment.NewLine}" +
            $"states={states}{Environment.NewLine}" +
            $"masterTick={machine.MasterTick}{Environment.NewLine}" +
            $"frameTicks={FrameTiming.FrameTicks}{Environment.NewLine}" +
            $"intAssertOffset=180{Environment.NewLine}" +
            $"intClearOffset=380{Environment.NewLine}" +
            $"intEdges={interrupts.Count}{Environment.NewLine}" +
            $"catalogMultiplier={catalog.CpuTurboMultiplier}{Environment.NewLine}" +
            $"mapperNumerator={mapper.Numerator}{Environment.NewLine}" +
            $"mapperDenominator={mapper.Denominator}{Environment.NewLine}");
        if (interrupts.Count != Frames * 2
            || interrupts.Where((edge, i) => edge.Tick != (i / 2) * FrameTiming.FrameTicks + (i % 2 == 0 ? 180 : 380)
                || edge.Asserted != (i % 2 == 0)).Any())
        {
            stderr.WriteLine("cpu.turbo selection ran, but ULA interrupt cadence was not the faithful frame cadence.");
            return 1;
        }
        return 0;
    }

    private static bool TryParse(IReadOnlyList<string> args, out string profileId, out int multiplier, out string error)
    {
        profileId = "hc2000-v2";
        multiplier = 0;
        error = "";
        string? turbo = null;
        var sawProfile = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == Flag || arg.StartsWith(Flag + "=", StringComparison.Ordinal))
            {
                if (turbo is not null) { error = "cpu.turbo may be selected only once."; return false; }
                turbo = arg == Flag ? Next(args, ref i) : arg[(Flag.Length + 1)..];
                if (turbo is null) { error = "Missing value for --cpu-turbo."; return false; }
            }
            else if (arg == "--profile" || arg.StartsWith("--profile=", StringComparison.Ordinal))
            {
                if (sawProfile) { error = "Profile may be selected only once."; return false; }
                var value = arg == "--profile" ? Next(args, ref i) : arg["--profile=".Length..];
                if (value is null) { error = "Missing value for --profile."; return false; }
                profileId = value;
                sawProfile = true;
            }
            else { error = "Unknown option " + arg + "."; return false; }
        }
        if (turbo is not ("1" or "2" or "4" or "8"))
        {
            error = "cpu.turbo supports only 1, 2, 4 or 8.";
            return false;
        }
        if (profileId != "hc2000-v2")
        {
            error = "Unknown faithful profile ID (case-sensitive).";
            return false;
        }
        multiplier = int.Parse(turbo, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static string? Next(IReadOnlyList<string> args, ref int i)
    {
        if (++i >= args.Count || args[i].StartsWith('-')) return null;
        return args[i];
    }
}
