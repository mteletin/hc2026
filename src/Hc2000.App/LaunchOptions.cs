using System.Collections.Immutable;
using Hc2000.Machine.Capabilities;

namespace Hc2000.App;

/// <summary>Parse outcome: either the record, or a single-line value fit for the StatusLine text — never an
/// exception, so a bad command line is always reported, not thrown.</summary>
public sealed record LaunchOptionsResult(bool Ok, LaunchOptions? Options, string? StatusLine)
{
    public static LaunchOptionsResult Fail(string statusLine) => new(false, null, statusLine);
}

/// <summary>The parsed command line: pure data, value-equal for equal input. Fields are exactly the surviving
/// surface (re-scoped acceptance 17:40Z, superseding the 15:53Z bar) — <c>--disk A:&lt;path&gt;</c>/
/// <c>--disk B:&lt;path&gt;</c> (kept raw, drive prefix included), <c>--start</c>, <c>--help</c>, and the
/// <see cref="CapabilityCatalog"/> surface rows (memory, m-view, hdd, block-disk, tape-control,
/// cpm-tape-volume, text-overlay, cpu-turbo, m-drive-bytes, side-z80, driver-z80), named and validated by the catalog alone.
/// M size and expansion count select only their exact existing cold profile identities. Plus the tape
/// volume launch actions (hc2000-tape-mount-cli): <c>--tape &lt;path&gt;</c> loads a cassette into the deck
/// before the mounts, <c>--tape-mount &lt;name&gt;</c> (repeatable, in order) mounts a volume as a CP/M
/// drive, and <c>--tape-commit</c> / <c>--tape-discard</c> (mutually exclusive) unmount committing the
/// changed data / discard it without writing. The explicit <c>--hdd</c> upgrade starts offline
/// NEW-machine preparation, never live switching.
/// There is no <c>--rom-set</c> and no <c>--asset-root</c>: one bundled whole-chip ROM, no ROM selection
/// (owner rulings 626711/626718, arch-2 contract 626727; PO re-scope 626755). If passed, both are reported as
/// unknown flags and appear nowhere as valid options. Existence and readability of a disk or tape path are
/// runtime facts reported on the StatusLine line by the consumer; this record never checks them and never
/// throws. <c>--sound-dump &lt;path&gt;</c> (the sound-dump route, hc2000-app-sound-dump-route) writes the
/// machine's audio stream to a WAV while the machine runs; its value is kept raw here, and the fence
/// (user home only) is owned by <see cref="Audio.SoundDumpGate"/>. <c>--workstation [path]</c>
/// (hc2000-combined-cpm-workstation) is the opt-in combined-workstation A: preparation: from the
/// named base disk, or the shipped unbound-template base media when no path is given, the bound
/// system disk is composed on the machine's media lane against its mounted live tape volume and
/// staged into A:; the consumer reports its refusals on the StatusLine. <c>--workstation</c> with
/// a path uses that base disk as-is; the record never checks file existence itself.</summary>
public sealed record LaunchOptions(
    string? DiskA,
    string? DiskB,
    bool Start,
    bool Help,
    bool? Writeback = null,
    string? Session = null,
    CapabilitySet? Capabilities = null,
    string? SoundDump = null,
    string? Tape = null,
    ImmutableArray<string> TapeMounts = default,
    bool TapeCommit = false,
    bool TapeDiscard = false,
    bool NetworkGateway = false,
    string? SshDefault = null,
    string? Workstation = null)
{
    /// <summary>The catalog row <c>hdd</c>: the explicit cold-image opt-in, never a live switch.</summary>
    public bool HddEnabled => CapabilityCatalog.IsOn(Capabilities ?? CapabilitySet.Empty, "hdd");

    // ImmutableArray's generated equality compares backing arrays, not ordered values, and CapabilitySet
    // is a class with reference equality. Compare mount order and catalog contents by value so direct
    // construction and repeated parses keep record semantics.
    public bool Equals(LaunchOptions? other) => other is not null
        && (DiskA, DiskB, Start, Help, HddEnabled, Writeback, Session, SoundDump, Tape, TapeCommit, TapeDiscard)
            .Equals((other.DiskA, other.DiskB, other.Start, other.Help, other.HddEnabled, other.Writeback,
                other.Session, other.SoundDump, other.Tape, other.TapeCommit, other.TapeDiscard))
        && (NetworkGateway, SshDefault) == (other.NetworkGateway, other.SshDefault)
        && Workstation == other.Workstation
        && TapeMounts.AsSpan().SequenceEqual(other.TapeMounts.AsSpan())
        && CapabilityEntries(Capabilities).SequenceEqual(CapabilityEntries(other.Capabilities));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add((DiskA, DiskB, Start, Help, HddEnabled, Writeback, Session, SoundDump, Tape, TapeCommit, TapeDiscard));
        hash.Add((NetworkGateway, SshDefault));
        hash.Add(Workstation);
        foreach (var mount in TapeMounts.AsSpan()) hash.Add(mount);
        foreach (var entry in CapabilityEntries(Capabilities)) hash.Add(entry);
        return hash.ToHashCode();
    }

    private static IEnumerable<string> CapabilityEntries(CapabilitySet? set) =>
        set is null
            ? Array.Empty<string>()
            : set.Names.OrderBy(n => n, StringComparer.Ordinal).Select(n => n + "=" + set.Get(n));

    /// <summary>Pure argv -> result (deterministic: equal input yields an equal result, no I/O, no state).
    /// Failures — unknown option, missing/duplicate value, malformed <c>--disk</c>, stray positional, and the
    /// catalog's single refusal — come back as <see cref="LaunchOptionsResult.StatusLine"/>.
    /// <c>--help</c> anywhere in the arguments wins over everything else, exiting 0; a rejection is a reported
    /// result with a non-zero exit.</summary>
    public static LaunchOptionsResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(a => string.Equals(a, "--help", StringComparison.Ordinal)))
            return new LaunchOptionsResult(true, new(null, null, false, true), null);

        string? diskA = null, diskB = null, session = null, soundDump = null, tape = null;
        var start = false;
        bool? writeback = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var tapeMounts = new List<string>();
        bool tapeCommit = false, tapeDiscard = false;
        var networkGateway = false;
        string? sshDefault = null;
        string? workstation = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i] ?? "";
            switch (arg)
            {
                case "--start":
                    start = true;
                    break;
                // hc2000-ssh-terminal: the X6 gateway on from the start, and the default SSH connection for this run.
                case "--network-gateway":
                    networkGateway = true;
                    break;
                case "--ssh-default":
                {
                    if (i + 1 >= args.Count || args[i + 1] is null || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return LaunchOptionsResult.Fail("Missing value for --ssh-default.");
                    if (sshDefault is not null) return LaunchOptionsResult.Fail("Duplicate --ssh-default.");
                    sshDefault = args[++i];
                    if (Network.X6DefaultTarget.Parse(sshDefault, out var why) is null)
                        return LaunchOptionsResult.Fail($"Malformed --ssh-default: {why}.");
                    break;
                }
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

                case "--tape":
                    if (i + 1 >= args.Count || args[i + 1] is null || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return LaunchOptionsResult.Fail("Missing value for --tape.");
                    if (tape is not null) return LaunchOptionsResult.Fail("Duplicate --tape.");
                    tape = args[++i];
                    break;
                case "--tape-mount":
                    if (i + 1 >= args.Count || args[i + 1] is null || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return LaunchOptionsResult.Fail("Missing value for --tape-mount.");
                    tapeMounts.Add(args[++i]);
                    break;
                case "--tape-commit":
                    if (tapeDiscard) return LaunchOptionsResult.Fail("--tape-commit and --tape-discard are mutually exclusive.");
                    tapeCommit = true;
                    break;
                case "--tape-discard":
                    if (tapeCommit) return LaunchOptionsResult.Fail("--tape-commit and --tape-discard are mutually exclusive.");
                    tapeDiscard = true;
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

                case "--workstation":
                    // Opt-in combined-workstation A: preparation. The path is optional: a value
                    // selects the base disk; without one the consumer resolves the shipped
                    // unbound-template base media. An empty string marks the bare flag.
                    if (workstation is not null) return LaunchOptionsResult.Fail("Duplicate --workstation.");
                    if (i + 1 < args.Count && args[i + 1] is { } workstationValue && !workstationValue.StartsWith("--", StringComparison.Ordinal))
                        workstation = args[++i];
                    else
                        workstation = "";
                    break;

                default:
                {
                    var consumed = CapabilityCatalog.TryConsume(args, i, values);
                    if (consumed.Matched)
                    {
                        if (consumed.Error is not null)
                            return LaunchOptionsResult.Fail(consumed.Error);
                        i = consumed.Index;
                    }
                    else
                    {
                        return arg.StartsWith("-", StringComparison.Ordinal)
                            ? LaunchOptionsResult.Fail($"Unknown option: {arg}.")
                            : LaunchOptionsResult.Fail($"Unexpected argument: {arg}.");
                    }
                    break;
                }
            }
        }

        if (start && values.ContainsKey("hdd"))
            return LaunchOptionsResult.Fail("--hdd requires cold image preparation; --start cannot bypass the NEW-machine confirmation.");
        var capabilities = values.Count > 0 ? CapabilitySet.Of(values) : null;
        if (capabilities is not null && CapabilityCatalog.Validate(capabilities) is { } refusal)
            return LaunchOptionsResult.Fail(refusal);
        return new LaunchOptionsResult(true, new(diskA, diskB, start, false, writeback, session, capabilities,
            soundDump, Tape: tape, TapeMounts: tapeMounts.ToImmutableArray(), TapeCommit: tapeCommit, TapeDiscard: tapeDiscard,
            NetworkGateway: networkGateway, SshDefault: sshDefault, Workstation: workstation), null);
    }
}
