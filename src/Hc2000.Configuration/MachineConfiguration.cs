using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Hc2000.App.Media;
using Hc2000.App.Settings;
using Hc2000.Machine.Capabilities;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Configuration;

/// <summary>Configuration only: no runtime identity, RAM, authority, handles or dirty bytes.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MachineConfiguration
{
    [JsonRequired]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [JsonRequired]
    public string Name { get; init; } = "HC2000";
    public string Profile { get; init; } = "hc2000-v2";

    /// <summary>CPU turbo multiplier (1 = faithful). Stored via the shadow JSON property, omitted when 1.</summary>
    [JsonIgnore]
    public int CpuTurboMultiplier { get; init; } = 1;

    /// <summary>JSON shadow for <see cref="CpuTurboMultiplier"/>: old files without the key default to 1.</summary>
    [JsonPropertyName("cpuTurboMultiplier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CpuTurboMultiplierJson
    {
        get => CpuTurboMultiplier == 1 ? null : CpuTurboMultiplier;
        init { if (value is { } multiplier) CpuTurboMultiplier = multiplier; }
    }

    /// <summary>
    /// The user's main-Z80 clock choice (hc2000-accelerated-clock-domains): <c>auto</c> (the default since
    /// hc2000-speed-140-turbo-280: 140 MHz on the machines that start CP/M, faithful on Classic and the BASIC expansion machines), <c>faithful</c>, or
    /// <c>7MHz</c>/<c>14MHz</c>/<c>28MHz</c>/<c>140MHz</c>/<c>280MHz</c> targets. A distinct dimension
    /// from <see cref="CpuTurboMultiplier"/>: the two are never combined. Stored via the shadow JSON property,
    /// omitted while auto.
    /// </summary>
    [JsonIgnore]
    public string MainCpuTarget { get; init; } = Hc2000.Machine.MainCpuSpeedInfo.Auto;

    /// <summary>JSON shadow for <see cref="MainCpuTarget"/>: omitted while auto, so files without the key (old ones
    /// included, which left faithful out) read as auto; an explicit faithful is written and kept
    /// (hc2000-speed-140-turbo-280).</summary>
    [JsonPropertyName("mainCpuTarget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MainCpuTargetJson
    {
        get => Hc2000.Machine.MainCpuSpeedInfo.IsAuto(MainCpuTarget) ? null : MainCpuTarget;
        init { if (value is { } target) MainCpuTarget = target; }
    }

    /// <summary>Full-X5 driver roster; zero for all other profiles and legacy settings.</summary>
    [JsonIgnore]
    public int ExpansionDriverCount { get; init; }

    [JsonPropertyName("expansionDriverCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpansionDriverCountJson
    {
        get => ExpansionDriverCount == 0 ? null : ExpansionDriverCount;
        init { if (value is { } count) ExpansionDriverCount = count; }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool X2Vga { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool X3Sound { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool X4Math { get; init; }

    /// <summary>The X6 network card (hc2000-ssh-terminal): X5 with two or more driver Z80s.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool X6Net { get; init; }

    /// <summary>The owner's switch for this machine's outbound gateway (hc2000-ssh-terminal): off unless set; only
    /// meaningful with the X6 card. The gateway itself can also be switched while the machine runs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NetworkGateway { get; init; }

    /// <summary>The X2 VGA second screen's layout for this machine (hc2000-x2-second-screen): a
    /// presentation preference, never part of the machine. Absent until first changed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public X2ScreenSettings? X2Screen { get; init; }

    public string RomSet { get; init; } = "roms/hc2000/eprom-dump-hc2k-2";
    public int SpeedPercent { get; init; } = 100;
    // 0 = Fit (largest fitting integer); positive values are explicit caps.
    public int Scale { get; init; } = 0;
    public bool TextOverlay { get; init; }
    public string AudioPreference { get; init; } = "default";
    public ImmutableDictionary<string, string> KeyMap { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string KeyboardMode { get; init; } = "Positional";
    public FloppyConfiguration? DiskA { get; init; }
    public FloppyConfiguration? DiskB { get; init; }
    public ImmutableArray<HddConfiguration> Hdd { get; init; } = [];
    public string? LastSnapshot { get; init; }

    /// <summary>The hardware a new instance starts with (hc2000-full-workstation-default): the full workstation, CP/M
    /// from C: with every card on and the gateway open. Only a new installation's first machine and New machine take
    /// it; a saved configuration keeps its own.</summary>
    public MachineConfiguration WithNewInstanceHardware() => this with
    {
        Profile = "hc2000-v2-full-workstation-v1", ExpansionDriverCount = 2,
        X2Vga = true, X3Sound = true, X6Net = true, NetworkGateway = true
    };

    public static MachineConfiguration Import(AppSettings settings) => new()
    {
        Profile = settings.Profile, RomSet = settings.RomSet, SpeedPercent = settings.SpeedPercent,
        Scale = settings.Scale, AudioPreference = settings.AudioPreference, KeyMap = settings.KeyMap.ToImmutableDictionary(),
        MainCpuTarget = settings.MainCpuTarget
    };

    public AppSettings Preferences() => new()
    {
        Profile = Profile, RomSet = RomSet, SpeedPercent = SpeedPercent, Scale = Scale,
        AudioPreference = AudioPreference, KeyMap = KeyMap.ToDictionary(), MainCpuTarget = MainCpuTarget
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 || string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new ArgumentException("Machine IDs and names must contain 1–80 characters.");
        if (KeyMap is null) throw new ArgumentException("Key map is required.");
        Preferences().Validate();
        if (ExpansionDriverCount is < 0 or > 8 ||
            (ExpansionDriverCount != 0 && Profile is not ("hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1"
                or "hc2000-v2-full-workstation-v1")))
            throw new ArgumentException("X5 driver CPU count must be 0–8 and requires the X5 profile or the full workstation.");
        if (X2Vga && Profile is not ("hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-full-workstation-v1"))
            throw new ArgumentException("X2 VGA requires the X5 profile or the full workstation.");
        if (X3Sound && Profile is not ("hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-full-workstation-v1"))
            throw new ArgumentException("X3 sound requires the X5 profile or the full workstation.");
        if (X4Math && Profile != "hc2000-v2-paged-expansion-x5-v1")
            throw new ArgumentException("X4 math requires the X5 profile.");
        if (X6Net && (Profile is not ("hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1"
                or "hc2000-v2-full-workstation-v1") || ExpansionDriverCount < 2))
            throw new ArgumentException("The X6 network card needs the X5 profile or the full workstation, with at least two driver Z80s.");
        if (KeyboardMode is not ("Positional" or "Symbolic")) throw new ArgumentException("Unsupported keyboard mode.");
        X2Screen?.Validate();
        DiskA?.Validate(); DiskB?.Validate();
        if (Hdd.IsDefault || Hdd.Length > 4 || Hdd.Any(h => h is null) || Hdd.Select(h => h.Slot).Distinct().Count() != Hdd.Length)
            throw new ArgumentException("HDD configuration requires unique slots C–F.");
        foreach (var hdd in Hdd) hdd.Validate();
        ValidatePath(LastSnapshot);
    }

    public void ValidateEffective()
    {
        Validate();
        if (Profile is not ("hc2000-v2" or "hc2000-v2-ram-banks-v1" or "hc2000-v2-paged-v1"
                or "hc2000-v2-paged-m-v1" or "hc2000-v2-hdd-v1" or "hc2000-v2-hdd-cboot-v1" or "hc2000-v2-hdd-cpm3-v1" or "hc2000-v2-blockport-v1"
                or "hc2000-v2-tape-control-v1" or "hc2000-v2-paged-tape-v2"
                or "hc2000-v2-paged-m-v2" or "hc2000-v2-paged-expansion-v1"
                or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2"
                or "hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-cpnet-v1"
                or "hc2000-v2-full-workstation-v1")
            || RomSet != "roms/hc2000/eprom-dump-hc2k-2")
            throw new ArgumentException("Select an implemented hc2000-v2 hardware profile with the bundled whole M27C512/IF1; ROM picking is unavailable.");
        if (CpuTurboMultiplier is not (1 or 2 or 4 or 8))
            throw new ArgumentException(CapabilityCatalog.NoMapping);
        if (CpuTurboMultiplier != 1 && Profile is not ("hc2000-v2" or "hc2000-v2-ram-banks-v1"))
            throw new ArgumentException(CapabilityCatalog.TurboNotAdmitted);
        if (Scale > 8) throw new ArgumentException("Implemented scale is 0 (Fit) or 1–8.");
        if (!Hdd.IsEmpty && Profile is not ("hc2000-v2-hdd-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-hdd-cboot-v1" or "hc2000-v2-hdd-cpm3-v1"
                or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2" or "hc2000-v2-full-workstation-v1"))
            throw new ArgumentException("HDD slots require a hard-disk profile (raw HDD, X5 with hard disk, C: boot), the combined CP/M workstation or the full workstation. Remove the draft HDD slots explicitly or select one before a new cold Start.");
        if (Profile is "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2" && Hdd.Length != 4)
            throw new ArgumentException("The combined CP/M workstation admits exactly four HDD volumes, one per drive C: to F:.");
    }

    internal static void ValidatePath(string? path)
    {
        if (path is not null && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || Path.GetFullPath(path) != path || path.Contains('\0')))
            throw new ArgumentException("Stored media paths must be normalized absolute local paths.");
    }
}

/// <summary>Where and how the X2 VGA second screen shows for one machine. Shown defaults to true:
/// a machine with the card shows both screens at once. FitToWindow false is integer zoom, where
/// Scale 0 is the largest integer that fits and 1–4 caps it. X/Y (screen pixels) and Width/Height
/// (window size) are its last placement, absent until it has been placed.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record X2ScreenSettings(bool Shown = true, bool FitToWindow = false, int Scale = 0,
    int? X = null, int? Y = null, double? Width = null, double? Height = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool VgaIsPrimary = false)
{
    public void Validate()
    {
        if (Scale is < 0 or > 4) throw new ArgumentException("VGA screen zoom is 0 (largest integer) or 1–4.");
        if ((X is null) != (Y is null) || (Width is null) != (Height is null))
            throw new ArgumentException("VGA screen position and size are stored as pairs.");
        if (Width is { } w && (!double.IsFinite(w) || w is < 160 or > 16384)
            || Height is { } h && (!double.IsFinite(h) || h is < 120 or > 16384))
            throw new ArgumentException("VGA screen size must be 160×120 to 16384×16384.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FloppyConfiguration(string Path, bool Writeback = false, ImageConfiguration? Image = null)
{
    [JsonIgnore] public ImageConfiguration Descriptor => Image ?? new("DskContainer");
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        MachineConfiguration.ValidatePath(Path);
        Descriptor.ToDescriptor().ValidateForFloppy(new Hc2000.Machine.Floppy.DriveProfile(80, 2));
    }
}

/// <summary>JSON projection of the existing descriptor; never an independent codec.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImageConfiguration(string Kind, int? Cylinders = null, int? Sides = null, int? Sectors = null, int? SectorSize = null)
{
    public MediaImageDescriptor ToDescriptor()
    {
        if (Kind == "RawFloppy" && Cylinders is int c && Sides is int h && Sectors is int s && SectorSize is int b)
            return MediaImageDescriptor.RawFloppy(new DiskGeometry(c, h, s, b));
        if (Cylinders is not null || Sides is not null || Sectors is not null || SectorSize is not null)
            throw new ArgumentException("Only raw floppy descriptors carry explicit geometry.");
        return Kind switch
        {
            "DskContainer" => MediaImageDescriptor.DskContainer,
            "HddRaw8MiB" => MediaImageDescriptor.HddRaw8MiB,
            _ => throw new ArgumentException("Unsupported or incomplete image descriptor.")
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HddConfiguration(string Slot, string Path, bool WriteProtected = true, string? Volume = null)
{
    public void Validate()
    {
        if (Slot is not ("C" or "D" or "E" or "F")) throw new ArgumentException("HDD slots are C–F.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        MachineConfiguration.ValidatePath(Path);
        if (Volume is not null)
        {
            if (string.IsNullOrWhiteSpace(Volume) || Volume.Length > 32 ||
                Volume.Any(ch => ch is '/' or '\\' or ':' or '\0'))
                throw new ArgumentException("A named HDD volume is a container volume name, not a path.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceSettings(int Version, string SelectedId, ImmutableArray<MachineConfiguration> Machines)
{
    public void Validate()
    {
        if (Version != 1 || Machines.IsDefault || Machines.Length is < 1 or > 4 || Machines.Any(m => m is null))
            throw new ArgumentException("Workspace version 1 requires 1–4 machine drafts.");
        foreach (var machine in Machines) machine.Validate();
        if (Machines.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != Machines.Length || !Machines.Any(m => m.Id == SelectedId))
            throw new ArgumentException("Workspace IDs must be unique and selectedId must exist.");
    }
    /// <summary>A new installation's workspace: one new instance with the stored preferences.</summary>
    public static WorkspaceSettings NewInstance(AppSettings settings)
    {
        var machine = MachineConfiguration.Import(settings).WithNewInstanceHardware();
        return new(1, machine.Id, [machine]);
    }

    public static WorkspaceSettings Import(AppSettings settings)
    {
        var machine = MachineConfiguration.Import(settings);
        return new(1, machine.Id, [machine]);
    }
}
