using System.Globalization;

namespace Hc2000.Machine.Capabilities;

/// <summary>
/// The only hardware-capability table. Surfaces submit a <see cref="CapabilitySet"/> and print
/// <see cref="Validate"/> unchanged. Decision hc2000-capability-catalog-v1.
/// </summary>
public static class CapabilityCatalog
{
    public const string NoMapping = "No admitted machine profile for this hardware set.";
    public const string TurboNotAdmitted = "cpu.turbo is not admitted with this hardware.";
    public const string MDriveBytes = "M: size must be 8388608 bytes.";
    public const string ExpansionCpu = "Expansion CPU count must be 0 or 16.";
    public const string ExpansionDriverCpu = "X5 driver CPU count must be 0–8.";
    public const string X6NeedsDrivers = "The X6 network card needs at least two X5 driver Z80s.";

    private static readonly CapabilityRow[] All =
    [
        new("memory", true, true, "off", ["off", "banks", "paged"]),
        new("m-view", false, true, "false", ["false", "true"]),
        new("m-drive-bytes", true, true, null, ["8388608"]),
        new("hdd", false, true, "false", ["false", "true"]),
        new("cpm-cold-boot", false, true, "false", ["false", "true"]),
        new("block-disk", false, true, "false", ["false", "true"]),
        new("tape-control", false, true, "false", ["false", "true"]),
        new("cpm-tape-volume", false, true, "false", ["false", "true"]),
        new("text-overlay", false, true, "false", ["false", "true"]),
        new("cpu-turbo", true, true, "1", ["1", "2", "4", "8"]),
        new("side-z80", true, true, "0", ["0", "16"]),
        new("c-net", false, true, "false", ["false", "true"]),
        new("driver-z80", true, true, "0", ["0", "1", "2", "3", "4", "5", "6", "7", "8"]),
        new("x2-vga", false, true, "false", ["false", "true"]),
        new("x3-sound", false, true, "false", ["false", "true"]),
        new("x4-math", false, true, "false", ["false", "true"]),
        // hc2000-ssh-terminal: the X6 network card, owned by a driver unit (X5 with two or more drivers).
        new("x6-net", false, true, "false", ["false", "true"])
    ];

    public static IReadOnlyList<CapabilityRow> Rows { get; } = All;

    public static IReadOnlyList<CapabilityRow> SurfaceRows { get; } = Array.FindAll(All, static row => row.Surface);

    public static string Cold(string adoptedProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adoptedProfile);
        return $"Hardware changed from {adoptedProfile}; a new cold machine is required. Use New machine; selected Reset keeps the adopted hardware.";
    }

    public static string MissingValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"Missing value for --{name}.";
    }

    public static string Duplicate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"Duplicate --{name}.";
    }

    public static CapabilityRow? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        for (var i = 0; i < All.Length; i++)
            if (All[i].Name == name) return All[i];
        return null;
    }

    public static string? Validate(CapabilitySet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Admit(set).Refusal;
    }

    public static ProfileResult ToProfile(CapabilitySet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var admitted = Admit(set);
        if (admitted.Refusal is null && (admitted.Profile is null || admitted.Profile == "hc2000-v2-hdd-guest-v1"))
            throw new InvalidOperationException("Catalog admitted a set with no selectable profile.");
        return new ProfileResult(admitted.Profile, admitted.Refusal);
    }

    public static bool IsOn(CapabilitySet set, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set.Get(name) == "true";
    }

    public static string Effective(CapabilitySet set, string name)
    {
        ArgumentNullException.ThrowIfNull(set);
        var row = Find(name) ?? throw new ArgumentException($"Unknown capability {name}.", nameof(name));
        return set.Get(name) ?? row.Default ?? throw new InvalidOperationException($"{name} is absent.");
    }

    public static CapabilitySet? FromProfile(string? profile, bool textOverlay = false, int cpuTurbo = 1, int driverCount = 0, bool x2Vga = false, bool x3Sound = false, bool x4Math = false, bool x6Net = false)
    {
        var x5 = profile is "hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-full-workstation-v1";
        if (profile is "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-full-workstation-v1" && driverCount is < 0 or > 8) return null;
        if (x6Net && (!x5 || driverCount < 2)) return null;
        if (driverCount != 0 && !x5) return null;
        if (x2Vga && profile is not ("hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-full-workstation-v1")) return null;
        if (x3Sound && profile is not ("hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-full-workstation-v1")) return null;
        if (x4Math && profile != "hc2000-v2-paged-expansion-x5-v1") return null;
        var set = profile switch
        {
            "hc2000-v2" => CapabilitySet.Empty,
            "hc2000-v2-ram-banks-v1" => CapabilitySet.Empty.With("memory", "banks"),
            "hc2000-v2-paged-v1" => CapabilitySet.Empty.With("memory", "paged"),
            "hc2000-v2-paged-m-v1" => CapabilitySet.Empty.With("memory", "paged").With("m-view", "true"),
            "hc2000-v2-paged-m-v2" => CapabilitySet.Empty.With("memory", "paged").With("m-view", "true").With("m-drive-bytes", "8388608"),
            "hc2000-v2-paged-expansion-v1" => CapabilitySet.Empty.With("memory", "paged").With("m-view", "true").With("side-z80", "16"),
            "hc2000-v2-cpnet-v1" => CapabilitySet.Empty.With("memory", "paged").With("m-view", "true")
                .With("side-z80", "16").With("c-net", "true"),
            "hc2000-v2-paged-expansion-x5-v1" => CapabilitySet.Empty.With("memory", "paged").With("m-view", "true")
                .With("side-z80", "16").With("driver-z80", driverCount.ToString(CultureInfo.InvariantCulture)),
            "hc2000-v2-paged-expansion-x5-hdd-guest-v1" => CapabilitySet.Empty.With("memory", "paged").With("m-view", "true")
                .With("side-z80", "16").With("driver-z80", driverCount.ToString(CultureInfo.InvariantCulture)).With("hdd", "true"),
            "hc2000-v2-hdd-v1" => CapabilitySet.Empty.With("hdd", "true"),
            "hc2000-v2-hdd-cboot-v1" => CapabilitySet.Empty.With("hdd", "true").With("cpm-cold-boot", "true"),
            "hc2000-v2-blockport-v1" => CapabilitySet.Empty.With("block-disk", "true"),
            "hc2000-v2-tape-control-v1" => CapabilitySet.Empty.With("tape-control", "true"),
            "hc2000-v2-paged-tape-v2" => CapabilitySet.Empty
                .With("memory", "paged").With("tape-control", "true").With("cpm-tape-volume", "true"),
            "hc2000-v2-combined-workstation-v1" => CapabilitySet.Empty
                .With("memory", "paged").With("m-view", "true").With("m-drive-bytes", "8388608")
                .With("hdd", "true").With("tape-control", "true").With("cpm-tape-volume", "true")
                .With("side-z80", "16"),
            "hc2000-v2-combined-workstation-v2" => CapabilitySet.Empty
                .With("memory", "paged").With("m-view", "true").With("m-drive-bytes", "8388608")
                .With("hdd", "true").With("tape-control", "true").With("cpm-tape-volume", "true")
                .With("side-z80", "16"),
            "hc2000-v2-full-workstation-v1" => CapabilitySet.Empty
                .With("memory", "paged").With("m-view", "true").With("m-drive-bytes", "8388608")
                .With("hdd", "true").With("cpm-cold-boot", "true").With("tape-control", "true").With("cpm-tape-volume", "true")
                .With("side-z80", "16").With("driver-z80", driverCount.ToString(CultureInfo.InvariantCulture)),
            _ => null
        };
        if (set is null) return null;
        if (textOverlay) set = set.With("text-overlay", "true");
        if (cpuTurbo != 1) set = set.With("cpu-turbo", cpuTurbo.ToString(CultureInfo.InvariantCulture));
        if (x2Vga) set = set.With("x2-vga", "true");
        if (x3Sound) set = set.With("x3-sound", "true");
        if (x4Math) set = set.With("x4-math", "true");
        if (x6Net) set = set.With("x6-net", "true");
        return set;
    }

    public static ConsumeResult TryConsume(IReadOnlyList<string> args, int index, IDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(values);
        if ((uint)index >= (uint)args.Count) return new ConsumeResult(false, index, null);
        if (!TryMatch(args[index], out var name, out var takesValue, out var inline))
            return new ConsumeResult(false, index, null);
        if (values.ContainsKey(name))
            return new ConsumeResult(true, takesValue && inline is null ? SkipValue(args, index) : index, Duplicate(name));
        if (!takesValue)
        {
            values[name] = "true";
            return new ConsumeResult(true, index, null);
        }
        if (inline is not null)
        {
            if (inline.Length == 0) return new ConsumeResult(true, index, MissingValue(name));
            values[name] = inline;
            return new ConsumeResult(true, index, null);
        }
        var valueIndex = index + 1;
        if (valueIndex >= args.Count || args[valueIndex].StartsWith('-'))
            return new ConsumeResult(true, index, MissingValue(name));
        values[name] = args[valueIndex];
        return new ConsumeResult(true, valueIndex, null);
    }

    private static (string? Profile, string? Refusal) Admit(CapabilitySet set)
    {
        if (set.Has("m-drive-bytes") && set.Get("m-drive-bytes") != "8388608") return (null, MDriveBytes);
        if (set.Has("side-z80") && set.Get("side-z80") is not ("0" or "16")) return (null, ExpansionCpu);
        if (set.Has("driver-z80") && !Contains(Find("driver-z80")!.AdmittedValues, set.Get("driver-z80")!))
            return (null, ExpansionDriverCpu);
        var turbo = set.Get("cpu-turbo") ?? "1";
        if (turbo is "2" or "4" or "8" && TurboIncompatible(set)) return (null, TurboNotAdmitted);
        if (IsOn(set, "x6-net") && set.Get("driver-z80") is not ("2" or "3" or "4" or "5" or "6" or "7" or "8"))
            return (null, X6NeedsDrivers);
        if (OutOfRange(set)) return (null, NoMapping);
        var profile = Map(set);
        return profile is null ? (null, NoMapping) : (profile, null);
    }

    private static bool TurboIncompatible(CapabilitySet set)
    {
        var memory = set.Get("memory") ?? "off";
        return memory is not ("off" or "banks")
            || set.Has("m-drive-bytes")
            || set.Has("driver-z80")
            || set.Get("side-z80") == "16"
            || IsOn(set, "m-view")
            || IsOn(set, "hdd")
            || IsOn(set, "cpm-cold-boot")
            || IsOn(set, "block-disk")
            || IsOn(set, "tape-control")
            || IsOn(set, "cpm-tape-volume");
    }

    private static bool OutOfRange(CapabilitySet set)
    {
        foreach (var name in set.Names)
        {
            var row = Find(name);
            var value = set.Get(name);
            if (row is null || value is null || !Contains(row.AdmittedValues, value)) return true;
        }
        return false;
    }

    private static string? Map(CapabilitySet set)
    {
        var memory = set.Get("memory") ?? "off";
        var turbo = set.Get("cpu-turbo") ?? "1";
        var mView = IsOn(set, "m-view");
        var hdd = IsOn(set, "hdd");
        var cpmColdBoot = IsOn(set, "cpm-cold-boot");
        var block = IsOn(set, "block-disk");
        var tape = IsOn(set, "tape-control");
        var cpm = IsOn(set, "cpm-tape-volume");
        var cpnet = IsOn(set, "c-net");
        var quiet = !mView && !hdd && !cpmColdBoot && !block && !tape && !cpm && !cpnet;
        var mSizeRequested = set.Has("m-drive-bytes");
        var sideCount = set.Get("side-z80") ?? "0";
        if (cpnet)
            return memory == "paged" && mView && !hdd && !cpmColdBoot && !block && !tape && !cpm &&
                turbo == "1" && !mSizeRequested && sideCount == "16" && !set.Has("driver-z80") &&
                !IsOn(set, "x2-vga") && !IsOn(set, "x3-sound") && !IsOn(set, "x4-math") && !IsOn(set, "x6-net")
                ? "hc2000-v2-cpnet-v1" : null;
        if ((IsOn(set, "x2-vga") || IsOn(set, "x3-sound") || IsOn(set, "x4-math") || IsOn(set, "x6-net")) &&
            (!set.Has("driver-z80") || sideCount != "16")) return null;
        if (set.Has("driver-z80") && sideCount != "16") return null;
        // The full workstation (hc2000-full-workstation-default): the composite below booted from C:, with its
        // driver units and the X2, X3 and X6 cards on its own identity.
        if (mSizeRequested && sideCount == "16" && memory == "paged" && mView && hdd && tape && cpm && cpmColdBoot && !block && !cpnet && !IsOn(set, "x4-math") && turbo == "1")
            return "hc2000-v2-full-workstation-v1";
        // The one explicitly qualified composite: M v2 + C-F + tape volume + the fixed-16 roster, turbo off.
        // Fresh requests admit the HddGuest successor (decision hc2000-combined-workstation-hdd-guest-v2);
        // a stored v1 profile string still means v1. Every other mixture of these rows keeps its refusal below.
        if (mSizeRequested && sideCount == "16" && memory == "paged" && mView && hdd && tape && cpm && !cpmColdBoot && !block && !cpnet && !set.Has("driver-z80") && !IsOn(set, "x2-vga") && !IsOn(set, "x3-sound") && !IsOn(set, "x4-math") && turbo == "1")
            return "hc2000-v2-combined-workstation-v2";
        if (hdd && memory == "paged" && mView && !block && !tape && !cpm && !cpnet && turbo == "1"
            && !mSizeRequested && sideCount == "16" && set.Has("driver-z80")
            && !IsOn(set, "x2-vga") && !IsOn(set, "x3-sound") && !IsOn(set, "x4-math"))
            return "hc2000-v2-paged-expansion-x5-hdd-guest-v1";
        if (mSizeRequested || sideCount == "16")
        {
            if (memory != "paged" || !mView || hdd || cpmColdBoot || block || tape || cpm || turbo != "1") return null;
            if (mSizeRequested && sideCount == "0") return "hc2000-v2-paged-m-v2";
            if (!mSizeRequested && sideCount == "16")
                return set.Has("driver-z80") ? "hc2000-v2-paged-expansion-x5-v1" : "hc2000-v2-paged-expansion-v1";
            return null; // No shipped identity combines M v2 with the fixed-16 expansion roster.
        }
        if (cpm && tape && memory == "paged" && !mView && !hdd && !cpmColdBoot && !block && turbo == "1")
            return "hc2000-v2-paged-tape-v2";
        if (mView && memory == "paged" && !hdd && !cpmColdBoot && !block && !tape && !cpm && !cpnet && turbo == "1")
            return "hc2000-v2-paged-m-v1";
        if (memory == "paged" && quiet && turbo == "1") return "hc2000-v2-paged-v1";
        if (hdd && cpmColdBoot && memory == "off" && !mView && !block && !tape && !cpm && turbo == "1")
            return "hc2000-v2-hdd-cboot-v1";
        if (hdd && memory == "off" && !mView && !block && !tape && !cpm && turbo == "1")
            return "hc2000-v2-hdd-v1";
        if (block && memory == "off" && !mView && !hdd && !cpmColdBoot && !tape && !cpm && turbo == "1")
            return "hc2000-v2-blockport-v1";
        if (tape && memory == "off" && !mView && !hdd && !cpmColdBoot && !block && !cpm && turbo == "1")
            return "hc2000-v2-tape-control-v1";
        if (memory == "banks" && quiet && turbo is "1" or "2" or "4" or "8") return "hc2000-v2-ram-banks-v1";
        if (memory == "off" && quiet && turbo is "1" or "2" or "4" or "8") return "hc2000-v2";
        return null;
    }

    private static bool TryMatch(string arg, out string name, out bool takesValue, out string? inline)
    {
        name = "";
        takesValue = false;
        inline = null;
        if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3) return false;
        var body = arg[2..];
        var eq = body.IndexOf('=');
        var token = eq >= 0 ? body[..eq] : body;
        CapabilityRow? row = null;
        for (var i = 0; i < All.Length; i++)
        {
            if (All[i].Surface && All[i].Name == token)
            {
                row = All[i];
                break;
            }
        }
        if (row is null || (!row.TakesValue && eq >= 0)) return false;
        name = row.Name;
        takesValue = row.TakesValue;
        if (eq >= 0) inline = body[(eq + 1)..];
        return true;
    }

    private static int SkipValue(IReadOnlyList<string> args, int index)
    {
        var next = index + 1;
        return next < args.Count && !args[next].StartsWith('-') ? next : index;
    }

    private static bool Contains(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
            if (values[i] == value) return true;
        return false;
    }
}

/// <summary>One submitted value per catalog row. An absent row means that row's default.</summary>
public sealed class CapabilitySet
{
    private readonly Dictionary<string, string> values;

    private CapabilitySet(Dictionary<string, string> values) => this.values = values;

    public static CapabilitySet Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    public IReadOnlyCollection<string> Names => values.Keys;

    public string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return values.TryGetValue(name, out var value) ? value : null;
    }

    public bool Has(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return values.ContainsKey(name);
    }

    public CapabilitySet With(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var copy = new Dictionary<string, string>(values, StringComparer.Ordinal) { [name] = value };
        return new CapabilitySet(copy);
    }

    public static CapabilitySet Of(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            copy[pair.Key] = pair.Value;
        }
        return new CapabilitySet(copy);
    }
}

/// <summary>A catalog row. <see cref="Surface"/> is false for a named-but-refused row.</summary>
public sealed record CapabilityRow(
    string Name,
    bool TakesValue,
    bool Surface,
    string? Default,
    IReadOnlyList<string> AdmittedValues);

/// <summary>The profile a set selects, or the catalog refusal. Never both.</summary>
public readonly record struct ProfileResult(string? Profile, string? Refusal)
{
    public bool Admitted => Refusal is null;
}

/// <summary>One flag read. <see cref="Index"/> is the last token consumed when <see cref="Matched"/>.</summary>
public readonly record struct ConsumeResult(bool Matched, int Index, string? Error);
