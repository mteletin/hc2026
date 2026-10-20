namespace Hc2000.App;

/// <summary>Parse outcome: either the record, or a single-line value fit for the RuntimeStatus text — never an
/// exception, so a bad command line is always reported, not thrown.</summary>
public sealed record LaunchOptionsResult(bool Ok, LaunchOptions? Options, string? StatusLine)
{
    public static LaunchOptionsResult Fail(string statusLine) => new(false, null, statusLine);
}

/// <summary>The parsed command line: pure data, value-equal for equal input. Fields are exactly the surviving
/// surface (re-scoped acceptance 17:40Z, superseding the 15:53Z bar) — <c>--disk A:&lt;path&gt;</c>/
/// <c>--disk B:&lt;path&gt;</c> (kept raw, drive prefix included), <c>--start</c>, <c>--help</c>.
/// The explicit <c>--hdd</c> upgrade starts offline NEW-machine preparation, never live switching.
/// There is no <c>--rom-set</c> and no <c>--asset-root</c>: one bundled whole-chip ROM, no ROM selection
/// (owner rulings 626711/626718, arch-2 contract 626727; PO re-scope 626755). If passed, both are reported as
/// unknown flags and appear nowhere as valid options. Existence and readability of a disk path are runtime
/// facts reported on the RuntimeStatus line by the consumer; this record never checks them and never
/// throws. <c>--sound-dump &lt;path&gt;</c> (the sound-dump route, hc2000-app-sound-dump-route) writes the
/// machine's audio stream to a WAV while the machine runs; its value is kept raw here, and the fence
/// (user home only) is owned by <see cref="Audio.SoundDumpGate"/>.</summary>
public sealed record LaunchOptions(
    string? DiskA,
    string? DiskB,
    bool Start,
    bool Help,
    bool HddEnabled = false,
    bool? Writeback = null,
    string? Session = null,
    string? SoundDump = null)
{
    /// <summary>Pure argv -> result (deterministic: equal input yields an equal result, no I/O, no state).
    /// Failures — unknown option, missing/duplicate value, malformed <c>--disk</c>, stray positional — come
    /// back as <see cref="LaunchOptionsResult.StatusLine"/>. <c>--help</c> anywhere in the arguments wins over
    /// everything else, exiting 0; a rejection is a reported result with a non-zero exit.</summary>
    public static LaunchOptionsResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(a => string.Equals(a, "--help", StringComparison.Ordinal)))
            return new LaunchOptionsResult(true, new(null, null, false, true), null);

        string? diskA = null, diskB = null, session = null, soundDump = null;
        var start = false;
        var hdd = false;
        bool? writeback = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i] ?? "";
            switch (arg)
            {
                case "--hdd":
                    hdd = true;
                    break;
                case "--start":
                    start = true;
                    break;
                case "--writeback":
                    if (writeback == false) return LaunchOptionsResult.Fail("--writeback and --no-writeback are mutually exclusive.");
                    writeback = true;
                    break;
                case "--no-writeback":
                    if (writeback == true) return LaunchOptionsResult.Fail("--writeback and --no-writeback are mutually exclusive.");
                    writeback = false;
                    break;
                case "--session":
                    if (i + 1 >= args.Count || args[i + 1] is null || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return LaunchOptionsResult.Fail("Missing value for --session.");
                    if (session is not null) return LaunchOptionsResult.Fail("Duplicate --session.");
                    session = args[++i];
                    break;

                case "--sound-dump":
                    if (i + 1 >= args.Count || args[i + 1] is null || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return LaunchOptionsResult.Fail("Missing value for --sound-dump.");
                    if (soundDump is not null) return LaunchOptionsResult.Fail("Duplicate --sound-dump.");
                    soundDump = args[++i];
                    break;

                case "--disk":
                {
                    if (i + 1 >= args.Count || args[i + 1] is null || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return LaunchOptionsResult.Fail("Missing value for --disk.");
                    var value = args[++i];
                    if (value.Length < 3 || value[1] != ':' || value[0] is not ('A' or 'a' or 'B' or 'b'))
                        return LaunchOptionsResult.Fail($"Malformed --disk: expected A:<path> or B:<path>, got {value}.");
                    if (char.ToUpperInvariant(value[0]) == 'A')
                    {
                        if (diskA is not null)
                            return LaunchOptionsResult.Fail("Duplicate --disk A:.");
                        diskA = value;
                    }
                    else
                    {
                        if (diskB is not null)
                            return LaunchOptionsResult.Fail("Duplicate --disk B:.");
                        diskB = value;
                    }
                    break;
                }

                default:
                    return arg.StartsWith("-", StringComparison.Ordinal)
                        ? LaunchOptionsResult.Fail($"Unknown option: {arg}.")
                        : LaunchOptionsResult.Fail($"Unexpected argument: {arg}.");
            }
        }

        if (hdd && start)
            return LaunchOptionsResult.Fail("--hdd requires cold image preparation; --start cannot bypass the NEW-machine confirmation.");
        return new LaunchOptionsResult(true, new(diskA, diskB, start, false, hdd, writeback, session, soundDump), null);
    }
}
