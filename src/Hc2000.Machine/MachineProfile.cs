using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Video;

namespace Hc2000.Machine;

/// <summary>Versioned selected odd-only FDC/F7/EF policy, not verified aliases or live port devices.</summary>
public enum MachineIoPolicy { FaithfulOddFdcV1 }
/// <summary>Explicit opt-in virtual block-disk wire policy; the faithful profiles have none.</summary>
public enum BlockDiskPolicy { V1, HddV1, HddGuestV1 }
public enum ProfileRomRole { Basic, Cpm, CpmUpper, CpmLower, If1 }
public enum ProfileRomStore { MainRom, ShadowRom }
public enum ProfileRomAvailability { SuppliedUnverified, MissingLowerHalf }

/// <summary>Canonical source metadata only; missing regions contain no invented path or padding.</summary>
public sealed record ProfileRomRegion(ProfileRomRole Role, string? SourcePath, int Length,
    int SourceOffset, ProfileRomStore Store, int StoreOffset, ProfileRomAvailability Availability);

/// <summary>Owned named choices only; no asset IO, Machine factory or snapshot compatibility.</summary>
public sealed record MachineProfile
{
    private static readonly ProfileRomRegion[] V1Regions = [
        new(ProfileRomRole.Basic, "roms/hc2000/v1/basic.rom", 0x4000, 0, ProfileRomStore.MainRom, 0, ProfileRomAvailability.SuppliedUnverified),
        new(ProfileRomRole.Cpm, "roms/hc2000/v1/cpm.rom", 0x4000, 0, ProfileRomStore.MainRom, 0x4000, ProfileRomAvailability.SuppliedUnverified),
        new(ProfileRomRole.If1, "roms/hc2000/v1/if1.rom", 0x4000, 0, ProfileRomStore.ShadowRom, 0, ProfileRomAvailability.SuppliedUnverified)
    ];
    private static readonly ProfileRomRegion[] V2Regions = [
        new(ProfileRomRole.Basic, "roms/hc2000/v2/basic.rom", 0x4000, 0, ProfileRomStore.MainRom, 0, ProfileRomAvailability.SuppliedUnverified),
        new(ProfileRomRole.CpmUpper, "roms/hc2000/v2/cpm-hc2kzip.bin", 0x2000, 0, ProfileRomStore.MainRom, 0x6000, ProfileRomAvailability.SuppliedUnverified),
        new(ProfileRomRole.CpmLower, null, 0x2000, 0, ProfileRomStore.MainRom, 0x4000, ProfileRomAvailability.MissingLowerHalf),
        new(ProfileRomRole.If1, "roms/hc2000/v2/if1.rom", 0x4000, 0, ProfileRomStore.ShadowRom, 0, ProfileRomAvailability.SuppliedUnverified)
    ];
    private MemoryProfile memory = new();
    private VideoProfile video = OwnVideo(new());
    private IReadOnlyList<ProfileRomRegion> romRegions = OwnRegions(V2Regions);

    public MachineProfile() { }
    private MachineProfile(MachineProfile other)
    {
        Name = other.Name;
        Version = other.Version;
        Io = other.Io;
        BlockDisk = other.BlockDisk;
        TapeControl = other.TapeControl;
        CpuTurboMultiplier = other.CpuTurboMultiplier;
        memory = other.Memory is null ? null! : other.Memory with { };
        video = OwnVideo(other.Video);
        romRegions = OwnRegions(other.RomRegions);
    }

    public string Name { get; init; } = "hc2000-v2";
    public int Version { get; init; } = 1;
    public MemoryProfile Memory { get => memory; init => memory = value is null ? null! : value with { }; }
    public VideoProfile Video { get => video; init => video = OwnVideo(value); }
    public MachineIoPolicy Io { get; init; } = MachineIoPolicy.FaithfulOddFdcV1;
    public BlockDiskPolicy? BlockDisk { get; init; }
    /// <summary>Spec 14 deck control at exact 0091, on its own opt-in identity only.</summary>
    public bool TapeControl { get; init; }
    public IReadOnlyList<ProfileRomRegion> RomRegions { get => romRegions; init => romRegions = OwnRegions(value); }
    public long MasterHz => FrameTiming.MasterHz;
    /// <summary>cpu.turbo: 1 disables the upgrade; opt-in 2/4/8 accelerates only CPU time.
    /// Fixed for a machine epoch; not host playback speed or a mid-instruction clock switch.</summary>
    public int CpuTurboMultiplier { get; init; } = 1;
    public long CpuMasterTicksNumerator => 5;
    public long CpuMasterTicksDenominator => CpuTurboMultiplier;
    /// <summary>Metadata completeness only, not file presence or boot compatibility.</summary>
    public bool HasCompleteRomDescription => RomRegions.All(r => r.Availability == ProfileRomAvailability.SuppliedUnverified);

    public static MachineProfile Default => Hc2000V2;
    public static MachineProfile Hc2000V2 => new();
    public static MachineProfile Hc2000V1 => new()
    {
        Name = "hc2000-v1",
        Memory = new(HcRomLayout.V1),
        RomRegions = V1Regions
    };
    public static MachineProfile Hc2000V2BlockportV1 => new()
    {
        Name = "hc2000-v2-blockport-v1",
        BlockDisk = BlockDiskPolicy.V1
    };
    public static MachineProfile Hc2000V2HddV1 => new()
    {
        Name = "hc2000-v2-hdd-v1",
        BlockDisk = BlockDiskPolicy.HddV1
    };
    public static MachineProfile Hc2000V2HddGuestV1 => new()
    {
        Name = "hc2000-v2-hdd-guest-v1",
        BlockDisk = BlockDiskPolicy.HddGuestV1
    };
    public static MachineProfile Hc2000V2TapeControlV1 => new()
    {
        Name = "hc2000-v2-tape-control-v1",
        TapeControl = true
    };
    public static MachineProfile Hc2000V2RamBanksV1 => new()
    {
        Name = "hc2000-v2-ram-banks-v1",
        Memory = new(HcRomLayout.V2, RamBytes: BankPolicy.RamBytes,
            RamBanks: new(BankPolicy.PolicyId, BankPolicy.BankCount, BankPolicy.BankBytes))
    };

    /// <summary>Structural validation only; a valid v2 description still has no registry source file for the CP/M lower half (0x4000..0x5fff); the v2 8 KiB CP/M image is the complete BIOS, not a broken set.</summary>
    public static MachineProfile Hc2000V2PagedV1 => new()
    {
        Name = "hc2000-v2-paged-v1",
        Memory = new(HcRomLayout.V2, RamBytes: PagedRamPolicy.RamBytes, RamPaged: new())
    };

    /// <summary>Tape storage v2: spec 12 paging restricted to bound CP/M banks, and existing 0091 control.</summary>
    public static MachineProfile Hc2000V2PagedTapeV2 => new()
    {
        Name = "hc2000-v2-paged-tape-v2",
        TapeControl = true,
        Memory = new(HcRomLayout.V2, RamBytes: PagedRamPolicy.RamBytes, RamPaged: new() { BoundCpmWindow = true })
    };

    /// <summary>Faithful base view with explicit C7-only expanded transfer access; not installed M firmware.</summary>
    public static MachineProfile Hc2000V2PagedMTransferV1 => new()
    {
        Name = "hc2000-v2-paged-m-v1",
        Memory = new(HcRomLayout.V2, RamBytes: PagedRamPolicy.RamBytes,
            RamPaged: new(PagedRamPolicy.MTransferPolicyId))
    };

    /// <summary>M-transfer view v2: the 8-MiB M: (pages 0-511) under its own identity; host wiring identical to v1.</summary>
    public static MachineProfile Hc2000V2PagedMTransferV2 => new()
    {
        Name = "hc2000-v2-paged-m-v2",
        Memory = new(HcRomLayout.V2, RamBytes: PagedRamPolicy.RamBytes,
            RamPaged: new(PagedRamPolicy.MTransferV2PolicyId))
    };

    /// <summary>Explicit expansion identity; retains M compatibility paging and the native main map.</summary>
    public static MachineProfile Hc2000V2PagedExpansionV1 => Hc2000V2PagedMTransferV1 with
    {
        Name = "hc2000-v2-paged-expansion-v1"
    };

    public void Validate()
    {
        if (Name == "hc2000-v2-paged-tape-v2")
        {
            ArgumentNullException.ThrowIfNull(Memory);
            if (!TapeControl || Memory.RamPaged is not { PolicyId: PagedRamPolicy.PolicyId, BoundCpmWindow: true })
                throw new ArgumentException("Paged tape requires tape control and the bound CP/M restriction of plain ram.paged.");
            // Strip only this explicit composition; neither parent's accepted identity is widened.
            (this with { Name = "hc2000-v2-tape-control-v1", Memory = Memory with { RamBytes = 0x10000, RamPaged = null } }).Validate();
            Memory.Validate();
            if (Memory.RomBytes != 0x8000 || Memory.ShadowRomBytes != 0x4000 || Memory.ShadowRamBytes != 0x400
                || Memory.If1Traps != new If1TrapProfile() || Io != MachineIoPolicy.FaithfulOddFdcV1)
                throw new ArgumentException("Paged tape preserves the canonical v2 ROM and IF1 layout.", nameof(Memory));
            return;
        }
        if (Name is "hc2000-v2-paged-v1" or "hc2000-v2-paged-m-v1" or "hc2000-v2-paged-m-v2" or "hc2000-v2-paged-expansion-v1")
        {
            ArgumentNullException.ThrowIfNull(Memory);
            var expectedPolicy = Name == "hc2000-v2-paged-m-v2" ? PagedRamPolicy.MTransferV2PolicyId
                : Name == "hc2000-v2-paged-v1" ? PagedRamPolicy.PolicyId : PagedRamPolicy.MTransferPolicyId;
            if (Memory.RamPaged?.PolicyId != expectedPolicy || Memory.RamPaged.BoundCpmWindow)
                throw new ArgumentException("Paged profile requires its exact ram.paged policy.", nameof(Memory));
            (this with { Name = "hc2000-v2", Memory = Memory with { RamBytes = 0x10000, RamPaged = null } }).Validate();
            Memory.Validate();
            if (Memory.RomBytes != 0x8000 || Memory.ShadowRomBytes != 0x4000 || Memory.ShadowRamBytes != 0x400
                || Memory.If1Traps != new If1TrapProfile() || Io != MachineIoPolicy.FaithfulOddFdcV1)
                throw new ArgumentException("Paged profile preserves the canonical v2 ROM and IF1 layout.", nameof(Memory));
            if (CpuTurboMultiplier != 1) throw new ArgumentException("Paged profile does not compose CPU turbo.");
            return;
        }
        if (Memory is { RamPaged: not null })
            throw new ArgumentException("ram.paged belongs only to its explicit paged profile identities.", nameof(Memory));
        if (TapeControl != (Name == "hc2000-v2-tape-control-v1"))
            throw new ArgumentException("Tape control (0091) belongs to hc2000-v2-tape-control-v1 alone.", nameof(TapeControl));
        if (TapeControl)
        {
            if (BlockDisk is not null || Memory is { RamBanks: not null } || CpuTurboMultiplier != 1)
                throw new ArgumentException("The tape-control identity adds only 0091.", nameof(Name));
            if (Version != 1) throw new ArgumentOutOfRangeException(nameof(Version), "Only profile version 1 is supported.");
            ArgumentNullException.ThrowIfNull(Memory);
            ArgumentNullException.ThrowIfNull(Video);
            ArgumentNullException.ThrowIfNull(RomRegions);
            Memory.Validate();
            Video.Validate();
            if (Memory.RomLayout != HcRomLayout.V2 || Memory.RamBytes != 0x10000 || !RomRegions.SequenceEqual(V2Regions))
                throw new ArgumentException("The tape-control identity uses the canonical v2 machine.", nameof(Name));
            return;
        }
        if (Name is "hc2000-v2-blockport-v1" or "hc2000-v2-hdd-v1" or "hc2000-v2-hdd-guest-v1")
        {
            ValidateBlockportUpgrade();
            return;
        }
        if (Name is "hc2000-v1-ram-banks-v1" or "hc2000-v2-ram-banks-v1")
        {
            ValidateRamBanksUpgrade();
            return;
        }
        if (Name is not ("hc2000-v1" or "hc2000-v2"))
            throw new ArgumentException("Unknown profile ID (case-sensitive).", nameof(Name));
        // Faithful identities never upgrade: the device is profile-gated and has no implicit enable.
        if (BlockDisk is not null)
            throw new ArgumentException($"The faithful {Name} profile never carries the block-disk device; use hc2000-v2-blockport-v1.", nameof(BlockDisk));
        if (Memory is { RamBanks: not null })
            throw new ArgumentException($"The faithful {Name} profile never carries the RAM-bank device; use its ram-banks-v1 upgrade identity.", nameof(Name));
        if (Version != 1) throw new ArgumentOutOfRangeException(nameof(Version), "Only profile version 1 is supported.");
        if (CpuTurboMultiplier is not (1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(nameof(CpuTurboMultiplier), "cpu.turbo supports only x2/x4/x8, or 1 (disabled).");
        ArgumentNullException.ThrowIfNull(Memory);
        ArgumentNullException.ThrowIfNull(Video);
        ArgumentNullException.ThrowIfNull(RomRegions);
        Memory.Validate();
        Video.Validate();
        if (!Enum.IsDefined(Io)) throw new ArgumentOutOfRangeException(nameof(Io));
        var expectedLayout = Name == "hc2000-v1" || Name == "hc2000-v1-ram-banks-v1" ? HcRomLayout.V1 : HcRomLayout.V2;
        if (Memory.RomLayout != expectedLayout)
            throw new ArgumentException("Faithful profile ID and ROM layout disagree.", nameof(Memory));
        var expected = expectedLayout == HcRomLayout.V1 ? V1Regions : V2Regions;
        if (!RomRegions.SequenceEqual(expected))
            throw new ArgumentException("Use the canonical faithful ROM regions; no substitution or padding.", nameof(RomRegions));
    }

    /// <summary>Blockport upgrade identity pins canonical v2 choices and the typed v1 device policy.</summary>
    private void ValidateBlockportUpgrade()
    {
        if (Version != 1) throw new ArgumentOutOfRangeException(nameof(Version), "Only profile version 1 is supported.");
        var expectedDisk = Name switch
        {
            "hc2000-v2-hdd-v1" => BlockDiskPolicy.HddV1,
            "hc2000-v2-hdd-guest-v1" => BlockDiskPolicy.HddGuestV1,
            _ => BlockDiskPolicy.V1
        };
        if (BlockDisk != expectedDisk) throw new ArgumentException("The blockport/HDD identity requires its explicit disk policy.", nameof(BlockDisk));
        if (CpuTurboMultiplier != 1) throw new ArgumentException("The blockport profile does not enable CPU turbo.", nameof(CpuTurboMultiplier));
        ArgumentNullException.ThrowIfNull(Memory);
        ArgumentNullException.ThrowIfNull(Video);
        ArgumentNullException.ThrowIfNull(RomRegions);
        Memory.Validate();
        Video.Validate();
        if (Memory.RomLayout != HcRomLayout.V2 || Memory.RamBanks is not null || Memory.RamBytes != 0x10000 ||
            Memory.RomBytes != 0x8000 || Memory.ShadowRomBytes != 0x4000 || Memory.ShadowRamBytes != 0x400 ||
            Memory.If1Traps != new If1TrapProfile() || Io != MachineIoPolicy.FaithfulOddFdcV1)
            throw new ArgumentException("Blockport must preserve the canonical v2 memory and FDC policies.", nameof(Memory));
        var canonicalVideo = new VideoProfile();
        if (Video.FullRaster || Video.FlashPeriodNumerator != canonicalVideo.FlashPeriodNumerator ||
            Video.FlashPeriodDenominator != canonicalVideo.FlashPeriodDenominator ||
            !Video.Palette.SequenceEqual(canonicalVideo.Palette))
            throw new ArgumentException("Blockport must preserve the canonical v2 video policy.", nameof(Video));
        if (!RomRegions.SequenceEqual(V2Regions))
            throw new ArgumentException("Blockport reuses canonical v2 ROM regions; no substitution or padding.", nameof(RomRegions));
    }

    /// <summary>
    /// Upgrade identity (hc2000-ram-banks-abi-v2): original base layout/regions plus the explicit
    /// bank device at its exact pinned sizes; version 1. A faithful identity never upgrades,
    /// and a malformed policy/store size/selector fails construction, never repairs.
    /// </summary>
    private void ValidateRamBanksUpgrade()
    {
        if (Version != 1) throw new ArgumentOutOfRangeException(nameof(Version), "Only profile version 1 is supported.");
        if (BlockDisk is not null) throw new ArgumentException("RAM-bank and blockport upgrades have separate profile identities.", nameof(BlockDisk));
        if (CpuTurboMultiplier is not (1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(nameof(CpuTurboMultiplier), "cpu.turbo supports only x2/x4/x8, or 1 (disabled).");
        ArgumentNullException.ThrowIfNull(Memory);
        ArgumentNullException.ThrowIfNull(Video);
        ArgumentNullException.ThrowIfNull(RomRegions);
        if (Memory.RamBanks is not { } device) throw new ArgumentException("Profile ID declares the RAM-bank device; memory profile must carry it.", nameof(Memory));
        device.Validate();
        if (Memory.RamBytes != BankPolicy.RamBytes)
            throw new ArgumentOutOfRangeException(nameof(Memory), $"{device.PolicyId} pins physical RAM at exactly 110000h (1,114,112) bytes.");
        Memory.Validate();
        Video.Validate();
        if (!Enum.IsDefined(Io)) throw new ArgumentOutOfRangeException(nameof(Io));
        var expectedLayout = Name == "hc2000-v1-ram-banks-v1" ? HcRomLayout.V1 : HcRomLayout.V2;
        if (Memory.RomLayout != expectedLayout)
            throw new ArgumentException("Profile ID and ROM layout disagree.", nameof(Memory));
        var expected = expectedLayout == HcRomLayout.V1 ? V1Regions : V2Regions;
        if (!RomRegions.SequenceEqual(expected))
            throw new ArgumentException("Upgrade profiles reuse the canonical base ROM regions; no substitution or padding.", nameof(RomRegions));
    }

    private static VideoProfile OwnVideo(VideoProfile value) => value is null ? null! : value with
    {
        Palette = value.Palette is null ? null! : Array.AsReadOnly(value.Palette.ToArray())
    };
    private static IReadOnlyList<ProfileRomRegion> OwnRegions(IReadOnlyList<ProfileRomRegion> values) =>
        values is null ? null! : Array.AsReadOnly(values.Select(r => r is null ? null! : r with { }).ToArray());
}
