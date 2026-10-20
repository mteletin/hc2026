using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Hc2000.App.Media;
using Hc2000.App.Settings;
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
    public string RomSet { get; init; } = "roms/hc2000/eprom-dump-hc2k-2";
    public int SpeedPercent { get; init; } = 100;
    public int Scale { get; init; } = 2;
    public bool TextOverlay { get; init; }
    public string AudioPreference { get; init; } = "default";
    public ImmutableDictionary<string, string> KeyMap { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string KeyboardMode { get; init; } = "Positional";
    public FloppyConfiguration? DiskA { get; init; }
    public FloppyConfiguration? DiskB { get; init; }
    public ImmutableArray<HddConfiguration> Hdd { get; init; } = [];
    public string? LastSnapshot { get; init; }

    public static MachineConfiguration Import(AppSettings settings) => new()
    {
        Profile = settings.Profile, RomSet = settings.RomSet, SpeedPercent = settings.SpeedPercent,
        Scale = settings.Scale, AudioPreference = settings.AudioPreference, KeyMap = settings.KeyMap.ToImmutableDictionary()
    };

    public AppSettings Preferences() => new()
    {
        Profile = Profile, RomSet = RomSet, SpeedPercent = SpeedPercent, Scale = Scale,
        AudioPreference = AudioPreference, KeyMap = KeyMap.ToDictionary()
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 || string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new ArgumentException("Machine IDs and names must contain 1–80 characters.");
        if (KeyMap is null) throw new ArgumentException("Key map is required.");
        Preferences().Validate();
        if (KeyboardMode is not ("Positional" or "Symbolic")) throw new ArgumentException("Unsupported keyboard mode.");
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
                or "hc2000-v2-paged-m-v1" or "hc2000-v2-paged-m-v2" or "hc2000-v2-hdd-v1" or "hc2000-v2-paged-expansion-v1")
            || RomSet != "roms/hc2000/eprom-dump-hc2k-2")
            throw new ArgumentException("Select an implemented hc2000-v2 hardware profile with the bundled whole M27C512/IF1; ROM picking is unavailable.");
        if (Scale > 8) throw new ArgumentException("Implemented scale is 1–8.");
        if (!Hdd.IsEmpty && Profile != "hc2000-v2-hdd-v1")
            throw new ArgumentException("HDD slots require the raw HDD profile; extended RAM and HDD cannot be combined. Remove the draft HDD slots explicitly or select HDD before a new cold Start.");
    }

    internal static void ValidatePath(string? path)
    {
        if (path is not null && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || Path.GetFullPath(path) != path || path.Contains('\0')))
            throw new ArgumentException("Stored media paths must be normalized absolute local paths.");
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
    public static WorkspaceSettings Import(AppSettings settings)
    {
        var machine = MachineConfiguration.Import(settings);
        return new(1, machine.Id, [machine]);
    }
}
