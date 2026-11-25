using Hc2000.Machine.Expansion;
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
    /// <summary>C:-boot identity (hc2000-direct-c-boot): the canonical v2 regions with the
    /// upgraded CP/M upper ROM whose 0xF800 stub is the direct-C:-boot loader; same span, offset and store.</summary>
    private static readonly ProfileRomRegion[] V2CbootRegions = [
        V2Regions[0],
        new(ProfileRomRole.CpmUpper, "roms/hc2000/v2/cpm-hc2kzip-cboot-v1.bin", 0x2000, 0, ProfileRomStore.MainRom, 0x6000, ProfileRomAvailability.SuppliedUnverified),
        V2Regions[2],
        V2Regions[3]
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
        // The main CPU clock choice is a machine setting, not a hardware identity; a `with` copy
        // that loses it would silently run the adopted machine at the faithful clock.
        MainCpuTarget = other.MainCpuTarget;
        ExpansionDriverCount = other.ExpansionDriverCount;
        // Every `with` copy goes through this constructor. A later choice must be copied here or
        // the copy silently loses it (the cpnet cold-adoption copy lost CpNet, and Validate then
        // refused the identity the App adopts at Start).
        CpNet = other.CpNet;
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
    /// Fixed for a machine epoch; not host playback speed or a mid-instruction clock switch.
    /// Distinct from <see cref="MainCpuTarget"/>: the two are never combined.</summary>
    public int CpuTurboMultiplier { get; init; } = 1;
    /// <summary>The user-facing main CPU clock choice (hc2000-accelerated-clock-domains): faithful
    /// 3,546,895 T/s by default, or an explicit 7/14/28/280 MHz target. A machine clock choice,
    /// not a hardware identity, so every admitted profile carries it; the device epoch, video and
    /// interrupt cadence stay on the unchanged master clock. Never composes with the legacy
    /// cpu.turbo multiplier: a non-faithful target requires it to be 1.</summary>
    public MainCpuSpeed MainCpuTarget { get; init; } = MainCpuSpeed.Faithful;
    /// <summary>Full-X5 driver units (0..8); machine identity. Zero enables no driver roster.</summary>
    public int ExpansionDriverCount { get; init; }
    /// <summary>The exact reduced master-ticks-per-T-state ratio of this profile's main CPU clock.
    /// Faithful is 5 master ticks/T (the legacy cpu.turbo divides it: 5/2, 5/4, 5/8 — unchanged);
    /// an explicit target is 17,734,475 / target Hz, gcd-reduced, never reinterpreted from the
    /// turbo multiplier. Device clocks remain on the unchanged master timeline.</summary>
    public (long Numerator, long Denominator) CpuMasterTicksPerTState =>
        MainCpuTarget == MainCpuSpeed.Faithful ? (5, CpuTurboMultiplier) : MainCpuTarget.MasterTicksPerTState();
    public long CpuMasterTicksNumerator => CpuMasterTicksPerTState.Numerator;
    public long CpuMasterTicksDenominator => CpuMasterTicksPerTState.Denominator;
    /// <summary>The exact requested T-states per second (faithful 3,546,895, a turbo multiple of
    /// it, or the explicit target): the requested rate for reporting, not the achieved rate.</summary>
    public long CpuTStatesPerSecond =>
        MainCpuTarget == MainCpuSpeed.Faithful ? 3_546_895L * CpuTurboMultiplier : MainCpuTarget.TStatesPerSecond();
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
    /// <summary>C:-boot identity: the raw HDD controller (guest ACK wire) with the direct-C:-boot
    /// CP/M upper ROM. The machine enters the CP/M cold entry (0xF800) at power-on and every reset.
    /// hc2000-direct-c-boot.</summary>
    public static MachineProfile Hc2000V2HddCbootV1 => new()
    {
        Name = "hc2000-v2-hdd-cboot-v1",
        BlockDisk = BlockDiskPolicy.HddGuestV1,
        RomRegions = V2CbootRegions
    };
    public static MachineProfile Hc2000V2HddCpm3V1 => new()
    {
        Name = "hc2000-v2-hdd-cpm3-v1",
        BlockDisk = BlockDiskPolicy.HddGuestV1,
        RomRegions = [V2Regions[0], V2Regions[1] with { SourcePath = "roms/hc2000/v2/cpm-hc2kzip-cpm3cboot-v1.bin" },
            V2Regions[2], V2Regions[3]]
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

    /// <summary>Explicit full-X5 identity; managed maps are composed by the machine owner.</summary>
    public static MachineProfile Hc2000V2PagedExpansionX5V1 => Hc2000V2PagedExpansionV1 with
    {
        Name = "hc2000-v2-paged-expansion-x5-v1"
    };

    /// <summary>Full X5 with the guest-acknowledge HDD policy.</summary>
    public static MachineProfile Hc2000V2PagedExpansionX5HddGuestV1 => Hc2000V2PagedExpansionX5V1 with
    {
        Name = "hc2000-v2-paged-expansion-x5-hdd-guest-v1",
        BlockDisk = BlockDiskPolicy.HddGuestV1
    };

    /// <summary>CP/NET card identity: the paged-expansion roster with the X0-bound CP/NET card.
    /// The catalog row is c-net (cold, default off); the host gateway stays off by default.</summary>
    public static MachineProfile Hc2000V2CpnetV1 => Hc2000V2PagedExpansionV1 with
    {
        Name = "hc2000-v2-cpnet-v1",
        CpNet = true
    };

    /// <summary>The X0-bound CP/NET card; only the cpnet identity composes it.</summary>
    public bool CpNet { get; init; }

    /// <summary>Opt-in combined CP/M workstation (hc2000-combined-cpm-workstation): A:/B:, persistent C-F (HddV1),
    /// volatile M: v2 (pages 0-511), a CP/M tape volume on its actual legal free letter (reservation 576-831) and
    /// the sixteen-compute side-Z80 roster, in one explicitly admitted identity. Turbo is never composed.</summary>
    public static MachineProfile Hc2000V2CombinedWorkstationV1 => new()
    {
        Name = "hc2000-v2-combined-workstation-v1",
        TapeControl = true,
        BlockDisk = BlockDiskPolicy.HddV1,
        Memory = new(HcRomLayout.V2, RamBytes: PagedRamPolicy.RamBytes,
            RamPaged: new(PagedRamPolicy.WorkstationPolicyId))
    };

    /// <summary>Explicit HddGuest successor of the combined workstation (decision
    /// hc2000-combined-workstation-hdd-guest-v2): the same opt-in composition with C–F on the guest
    /// driver identity, so the HDINST resident's idle QUERY4 opt-in guard answers. The old v1 identity
    /// (HddV1) is retained unchanged; saved v1 drafts and snapshots keep composing v1. Composite
    /// native switching and restored rearm are refused at the port; turbo is never composed.</summary>
    public static MachineProfile Hc2000V2CombinedWorkstationV2 => new()
    {
        Name = "hc2000-v2-combined-workstation-v2",
        TapeControl = true,
        BlockDisk = BlockDiskPolicy.HddGuestV1,
        Memory = new(HcRomLayout.V2, RamBytes: PagedRamPolicy.RamBytes,
            RamPaged: new(PagedRamPolicy.WorkstationPolicyId))
    };

    /// <summary>The full workstation (hc2000-full-workstation-default): the C:-boot machine with 1 GiB of
    /// paged RAM on the workstation policy, C:-F: on the guest-ACK HDD policy and the tape control port.
    /// Its drives run from the paged-drive switch at 0xDB15 and from paged RAM, not from residents below
    /// the BDOS, so CP/M programs keep a TPA close to the stock one. CPU turbo is never composed.</summary>
    public static MachineProfile Hc2000V2FullWorkstationV1 => new()
    {
        Name = "hc2000-v2-full-workstation-v1",
        TapeControl = true,
        BlockDisk = BlockDiskPolicy.HddGuestV1,
        RomRegions = V2CbootRegions,
        Memory = new(HcRomLayout.V2, RamBytes: PagedRamPolicy.RamBytes,
            RamPaged: new(PagedRamPolicy.WorkstationPolicyId))
    };

    public void Validate()
    {
        if (!Enum.IsDefined(typeof(MainCpuSpeed), MainCpuTarget))
            throw new ArgumentOutOfRangeException(nameof(MainCpuTarget), MainCpuTarget, "Unknown main CPU clock choice.");
        // The explicit target is a distinct dimension from the legacy cpu.turbo (multiples of
        // 3,546,895 T/s); composing them would be ambiguous, so the choice is refused, never
        // silently reinterpreted.
        if (MainCpuTarget != MainCpuSpeed.Faithful && CpuTurboMultiplier != 1)
            throw new ArgumentException("An explicit main CPU target composes with no legacy cpu.turbo; set cpu.turbo to 1 (disabled).", nameof(CpuTurboMultiplier));
        if (Name is "hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-full-workstation-v1")
        {
            if (ExpansionDriverCount is < 0 or > ExpansionArena.DriverCount)
                throw new ArgumentOutOfRangeException(nameof(ExpansionDriverCount), "Full X5 and the full workstation admit 0..8 driver units.");
        }
        else if (ExpansionDriverCount != 0)
            throw new ArgumentException("Driver units belong only to the full X5 identities and the full workstation.", nameof(ExpansionDriverCount));
        if (Name == "hc2000-v2-full-workstation-v1")
        {
            ArgumentNullException.ThrowIfNull(Memory);
            if (!TapeControl || BlockDisk != BlockDiskPolicy.HddGuestV1
                || Memory.RamPaged is not { PolicyId: PagedRamPolicy.WorkstationPolicyId, BoundCpmWindow: false })
                throw new ArgumentException("The full workstation requires tape control, the guest-ACK C-F disks and the workstation paged policy.", nameof(Name));
            // Strip to the C:-boot identity it extends; no admitted parent identity is widened.
            (this with { Name = "hc2000-v2-hdd-cboot-v1", Memory = Memory with { RamBytes = 0x10000, RamPaged = null }, TapeControl = false, ExpansionDriverCount = 0 }).Validate();
            Memory.Validate();
            return;
        }
        if (Name is "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2")
        {
            ArgumentNullException.ThrowIfNull(Memory);
            // Each identity keeps its admitted C-F HDD policy; the successor is not a re-reading of v1.
            var blockDisk = Name == "hc2000-v2-combined-workstation-v1"
                ? BlockDiskPolicy.HddV1 : BlockDiskPolicy.HddGuestV1;
            if (!TapeControl || BlockDisk != blockDisk
                || Memory.RamPaged is not { PolicyId: PagedRamPolicy.WorkstationPolicyId, BoundCpmWindow: false })
                throw new ArgumentException("The combined workstation requires tape control, its admitted C-F HDD identity and its exact paged policy.", nameof(Name));
            // Strip only this explicit composition; no admitted parent identity is widened.
            (this with { Name = "hc2000-v2", Memory = Memory with { RamBytes = 0x10000, RamPaged = null }, BlockDisk = null, TapeControl = false }).Validate();
            Memory.Validate();
            if (Memory.RomBytes != 0x8000 || Memory.ShadowRomBytes != 0x4000 || Memory.ShadowRamBytes != 0x400
                || Memory.If1Traps != new If1TrapProfile() || Io != MachineIoPolicy.FaithfulOddFdcV1)
                throw new ArgumentException("The combined workstation preserves the canonical v2 ROM and IF1 layout.", nameof(Memory));
            if (CpuTurboMultiplier != 1) throw new ArgumentException("The combined workstation does not compose CPU turbo.");
            return;
        }
        if (Name == "hc2000-v2-paged-expansion-x5-hdd-guest-v1")
        {
            if (BlockDisk != BlockDiskPolicy.HddGuestV1)
                throw new ArgumentException("X5 HDD requires the guest-acknowledge HDD policy.", nameof(BlockDisk));
            (this with { Name = "hc2000-v2-paged-expansion-x5-v1", BlockDisk = null }).Validate();
            return;
        }
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
        if (Name is "hc2000-v2-paged-v1" or "hc2000-v2-paged-m-v1" or "hc2000-v2-paged-m-v2"
            or "hc2000-v2-paged-expansion-v1" or "hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-cpnet-v1")
        {
            ArgumentNullException.ThrowIfNull(Memory);
            if ((Name == "hc2000-v2-cpnet-v1") != CpNet)
                throw new ArgumentException("Only the cpnet identity composes the CP/NET card.", nameof(CpNet));
            var expectedPolicy = Name == "hc2000-v2-paged-m-v2" ? PagedRamPolicy.MTransferV2PolicyId
                : Name == "hc2000-v2-paged-v1" ? PagedRamPolicy.PolicyId : PagedRamPolicy.MTransferPolicyId;
            if (Memory.RamPaged?.PolicyId != expectedPolicy || Memory.RamPaged.BoundCpmWindow)
                throw new ArgumentException("Paged profile requires its exact ram.paged policy.", nameof(Memory));
            (this with { Name = "hc2000-v2", ExpansionDriverCount = 0, Memory = Memory with { RamBytes = 0x10000, RamPaged = null } }).Validate();
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
        if (Name is "hc2000-v2-hdd-cboot-v1" or "hc2000-v2-hdd-cpm3-v1")
        {
            ValidateCbootUpgrade();
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

    /// <summary>C:-boot identity (hc2000-direct-c-boot): the blockport/HDD machine with the guest ACK
    /// wire and exactly one ROM substitution, the direct-C:-boot CP/M upper ROM. No other change.</summary>
    private void ValidateCbootUpgrade()
    {
        if (Version != 1) throw new ArgumentOutOfRangeException(nameof(Version), "Only profile version 1 is supported.");
        if (BlockDisk != BlockDiskPolicy.HddGuestV1) throw new ArgumentException("The C:-boot identity requires the guest-ACK HDD disk policy.", nameof(BlockDisk));
        if (CpuTurboMultiplier != 1) throw new ArgumentException("The C:-boot profile does not enable CPU turbo.", nameof(CpuTurboMultiplier));
        ArgumentNullException.ThrowIfNull(Memory);
        ArgumentNullException.ThrowIfNull(Video);
        ArgumentNullException.ThrowIfNull(RomRegions);
        Memory.Validate();
        Video.Validate();
        if (Memory.RomLayout != HcRomLayout.V2 || Memory.RamBanks is not null || Memory.RamBytes != 0x10000 ||
            Memory.RomBytes != 0x8000 || Memory.ShadowRomBytes != 0x4000 || Memory.ShadowRamBytes != 0x400 ||
            Memory.If1Traps != new If1TrapProfile() || Io != MachineIoPolicy.FaithfulOddFdcV1)
            throw new ArgumentException("C:-boot must preserve the canonical v2 memory and FDC policies.", nameof(Memory));
        var canonicalVideo = new VideoProfile();
        if (Video.FullRaster || Video.FlashPeriodNumerator != canonicalVideo.FlashPeriodNumerator ||
            Video.FlashPeriodDenominator != canonicalVideo.FlashPeriodDenominator ||
            !Video.Palette.SequenceEqual(canonicalVideo.Palette))
            throw new ArgumentException("C:-boot must preserve the canonical v2 video policy.", nameof(Video));
        if (!RomRegions.SequenceEqual(Name == "hc2000-v2-hdd-cpm3-v1" ? Hc2000V2HddCpm3V1.RomRegions : V2CbootRegions))
            throw new ArgumentException("C:-boot reuses the canonical v2 regions with only the CpmUpper substitution; no other change or padding.", nameof(RomRegions));
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
