using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.App.Audio;
using Hc2000.App.Media;
using Hc2000.App.Network;
using Hc2000.Machine.Network;
using Hc2000.App.Debugging;
using Hc2000.Machine;
using Hc2000.Machine.Capabilities;
using Hc2000.Machine.Expansion;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Input;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Timing;
using Hc2000.Machine.Snapshots;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Snapshots;
using Hc2000.App.Snapshots;
using Hc2000.Machine.Video;
using Hc2000.Z80;
using CpuZ80 = Hc2000.Z80.Z80;
using LiveMachine = Hc2000.Machine.Machine;

namespace Hc2000.App.Runtime;

/// <summary>Composition of a verified canonical BASIC ROM set with the existing real machine seams.</summary>
public static class BasicMachineComposition
{
    // How long a stopping worker waits for a closing CP/NET exchange before it lets the machine go without reusing its RAM.
    internal static readonly TimeSpan StopDrain = TimeSpan.FromSeconds(2);

    // Historical v1 fixtures only (friend test assembly); no App launch or recovery path calls this.
    internal static EmulationWorker CreateWorker(SelectedProfileRomSet selection, InputQueue inputQueue, MediaBridge? media = null)
    {
        return CreateWorker(Prepare(selection), inputQueue, media, slot: null);
    }

    /// <summary>Validates/copies before replacing a running session; no path is read after this step.</summary>
    internal static PreparedBasicMachine Prepare(SelectedProfileRomSet selection)
    {
        var roms = ProfileRomSelection.CopyVerifiedV1(selection);
        return new PreparedBasicMachine(roms);
    }

    /// <summary>Composes a faithful v2 BASIC machine directly from the whole-chip EPROM dump.</summary>
    public static PreparedBasicMachine PrepareWholeChipV2((MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        HcsWavPolicy? snapshotPolicy = null, bool x2Vga = false, bool x3Sound = false, bool x4Math = false, bool x6Net = false)
    {
        // The whole chip IS the ROM map: main ROM 0x8000..0xFFFF (v2/basic.rom), CP/M half
        // 0xC000..0xFFFF, and the IF1 shadow ROM 0x4000..0x7FFF. No per-file slices, no padding,
        // no substitution -- the shadow page is the v2 IF1 image.
        roms.Profile.Validate();
        if (x2Vga && roms.Profile.Name != "hc2000-v2-paged-expansion-x5-v1")
            throw new ArgumentException("X2 VGA requires the X5 profile.", nameof(x2Vga));
        if (x3Sound && roms.Profile.Name != "hc2000-v2-paged-expansion-x5-v1")
            throw new ArgumentException("X3 sound requires the X5 profile.", nameof(x3Sound));
        if (x4Math && roms.Profile.Name != "hc2000-v2-paged-expansion-x5-v1")
            throw new ArgumentException("X4 math requires the X5 profile.", nameof(x4Math));
        if (x6Net && roms.Profile.Name is not ("hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1"))
            throw new ArgumentException("The X6 network card requires the X5 profile.", nameof(x6Net));
        if (roms.Profile.Name is "hc2000-v2-paged-expansion-v1" or "hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-cpnet-v1"
            or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2")
        {
            // Trusted composition opt-in, before a worker or GiB arena exists; never file-derived.
            snapshotPolicy ??= HcsWavPolicy.Default with { TransientBytes = 16L * 0x44000000 };
            snapshotPolicy.ValidateExpansionStep1Admission();
        }
        return new PreparedBasicMachine(roms, snapshotPolicy: snapshotPolicy, x2Vga: x2Vga, x3Sound: x3Sound, x4Math: x4Math, x6Net: x6Net);
    }

    /// <summary>Same verified whole-chip ROM map, with one exact admitted cold profile from the
    /// catalog (row <c>memory</c> / <c>m-view</c> plus the quiet single-device profiles) and the
    /// admitted <c>cpu-turbo</c>. No combined devices or alternative firmware are inferred.</summary>
    public static PreparedBasicMachine PrepareRamWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms, string profileId, bool textOverlay = false,
        int cpuTurbo = 1, int expansionDriverCount = 0, bool x2Vga = false, bool x3Sound = false, bool x4Math = false,
        bool x6Net = false, string mainCpuTarget = "faithful")
    {
        roms.Profile.Validate();
        if (roms.Profile.Name != MachineProfile.Hc2000V2.Name)
            throw new ArgumentException("Hardware preparation requires the verified faithful v2 ROM map.", nameof(roms));
        if (cpuTurbo is not (1 or 2 or 4 or 8))
            throw new ArgumentException(CapabilityCatalog.NoMapping, nameof(cpuTurbo));
        var profile = profileId switch
        {
            "hc2000-v2" => MachineProfile.Hc2000V2,
            "hc2000-v2-ram-banks-v1" => MachineProfile.Hc2000V2RamBanksV1,
            "hc2000-v2-paged-v1" => MachineProfile.Hc2000V2PagedV1,
            "hc2000-v2-paged-m-v1" => MachineProfile.Hc2000V2PagedMTransferV1,
            "hc2000-v2-blockport-v1" => MachineProfile.Hc2000V2BlockportV1,
            "hc2000-v2-tape-control-v1" => MachineProfile.Hc2000V2TapeControlV1,
            "hc2000-v2-paged-tape-v2" => MachineProfile.Hc2000V2PagedTapeV2,
            "hc2000-v2-paged-m-v2" => MachineProfile.Hc2000V2PagedMTransferV2,
            "hc2000-v2-paged-expansion-v1" => MachineProfile.Hc2000V2PagedExpansionV1,
            "hc2000-v2-combined-workstation-v1" => MachineProfile.Hc2000V2CombinedWorkstationV1,
            "hc2000-v2-combined-workstation-v2" => MachineProfile.Hc2000V2CombinedWorkstationV2,
            "hc2000-v2-paged-expansion-x5-v1" => MachineProfile.Hc2000V2PagedExpansionX5V1,
            "hc2000-v2-paged-expansion-x5-hdd-guest-v1" => MachineProfile.Hc2000V2PagedExpansionX5HddGuestV1,
            "hc2000-v2-cpnet-v1" => MachineProfile.Hc2000V2CpnetV1,
            _ => throw new ArgumentException("Unsupported cold RAM profile.", nameof(profileId))
        };
        if (cpuTurbo != 1 && profileId is not ("hc2000-v2" or "hc2000-v2-ram-banks-v1"))
            throw new ArgumentException(CapabilityCatalog.TurboNotAdmitted, nameof(cpuTurbo));
        // hc2000-accelerated-clock-domains: the user's main-Z80 clock choice; faithful (the default) keeps
        // the legacy mapping exactly. Combined with a legacy cpu.turbo it is refused by the profile's
        // own validation, never silently reinterpreted.
        var mainCpu = MainCpuSpeedInfo.Resolve(mainCpuTarget, profile.Name); // auto: 140 MHz extended, faithful Classic
        // Cold adoption: the derived record copy carries the adopted choice; the static profile is never mutated.
        var adopted = profile with { Video = profile.Video with { TextOverlay = textOverlay }, CpuTurboMultiplier = cpuTurbo,
            ExpansionDriverCount = expansionDriverCount, MainCpuTarget = mainCpu };
        adopted.Validate();
        if (x6Net && expansionDriverCount < 2)
            throw new ArgumentException(CapabilityCatalog.X6NeedsDrivers, nameof(x6Net));
        return PrepareWholeChipV2((adopted, roms.Basic, roms.Cpm, roms.If1), x2Vga: x2Vga, x3Sound: x3Sound, x4Math: x4Math,
            x6Net: x6Net);
    }

    /// <summary>Prepare host bytes BEFORE constructing the worker. No live machine is replaced.
    /// Installation occurs only in its owner-thread cold NEW factory, before its first instruction.</summary>
    public static PreparedBasicMachine PrepareHddWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms, IEnumerable<HddInitialMedia> images, bool textOverlay = false, int cpuTurbo = 1,
        MachineProfile? adopt = null, string mainCpuTarget = "faithful")
    {
        ArgumentNullException.ThrowIfNull(images);
        if (roms.Profile.Name != MachineProfile.Hc2000V2.Name && roms.Profile.Name != MachineProfile.Hc2000V2HddCbootV1.Name
            && roms.Profile.Name != MachineProfile.Hc2000V2HddCpm3V1.Name)
            throw new ArgumentException("HDD cold configuration requires the whole-chip faithful v2 or C:-boot ROM map.", nameof(roms));
        if (cpuTurbo != 1)
            throw new ArgumentException(CapabilityCatalog.TurboNotAdmitted, nameof(cpuTurbo));
        // The raw HDD identity, the C:-boot identity and the combined workstation (v1 HddV1 or the v2 HddGuest
        // successor) are the only identities selectable here; each stored name keeps its own identity. The C:-boot
        // ROM map (hc2000-direct-c-boot) adopts the C:-boot identity, whose disk policy is the guest-ACK wire the
        // boot loader speaks; the faithful map defaults to the raw HDD identity.
        var cpm3Name = MachineProfile.Hc2000V2HddCpm3V1.Name;
        adopt ??= roms.Profile.Name == cpm3Name ? MachineProfile.Hc2000V2HddCpm3V1
            : roms.Profile.Name == MachineProfile.Hc2000V2HddCbootV1.Name
                ? MachineProfile.Hc2000V2HddCbootV1 : MachineProfile.Hc2000V2HddV1;
        if ((adopt.Name == cpm3Name) != (roms.Profile.Name == cpm3Name))
            throw new ArgumentException("CP/M 3 requires its own C:-boot ROM map and identity.", nameof(adopt));
        if (adopt.Name is not ("hc2000-v2-hdd-v1" or "hc2000-v2-hdd-cboot-v1" or "hc2000-v2-hdd-cpm3-v1" or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2"
            or "hc2000-v2-full-workstation-v1"))
            throw new ArgumentException("HDD cold configuration adopts the raw HDD or the combined workstation identity.", nameof(adopt));
        var owned = images.Select(image =>
        {
            if (image.Drive is < 2 or > 5 || image.Bytes.IsDefault || image.Bytes.Length != HddMediaAdapter.ImageLength)
                throw new ArgumentException("Invalid initial HDD slot or raw image length.", nameof(images));
            return image with { Bytes = ImmutableArray.CreateRange(image.Bytes.AsSpan().ToArray()) };
        }).ToImmutableArray();
        if (owned.Select(image => image.Drive).Distinct().Count() != owned.Length)
            throw new ArgumentException("Duplicate initial HDD drive.", nameof(images));
        // Cold adoption: the derived record copy carries the adopted choice; the static profile is never mutated.
        var adopted = adopt with
        {
            Video = adopt.Video with { TextOverlay = textOverlay },
            CpuTurboMultiplier = cpuTurbo,
            MainCpuTarget = MainCpuSpeedInfo.Resolve(mainCpuTarget, adopt.Name) // auto: 140 MHz extended, faithful Classic
        };
        adopted.Validate();
        // The composite carries the GiB paged store and the sixteen-compute arena; it takes the same
        // trusted snapshot admission as the expansion identity, never a file-derived one.
        var snapshotPolicy = adopted.Name is "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2"
            or "hc2000-v2-full-workstation-v1"
            ? HcsWavPolicy.Default with { TransientBytes = 16L * 0x44000000 }
            : null;
        if (snapshotPolicy is not null) snapshotPolicy.ValidateExpansionStep1Admission();
        return new((adopted, roms.Basic, roms.Cpm, roms.If1), owned, snapshotPolicy: snapshotPolicy);
    }

    /// <summary>The full workstation (hc2000-full-workstation-default): the C:-boot ROM map with C:-F: on the guest-ACK
    /// disk policy, the tape, 1 GiB of paged RAM with M:, the sixteen side Z80s, driver units and the X2, X3 and X6
    /// cards, in one explicitly admitted identity. X6 needs two driver units, as on X5.</summary>
    public static PreparedBasicMachine PrepareFullWorkstationWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms, IEnumerable<HddInitialMedia> images,
        int expansionDriverCount = 2, bool x2Vga = true, bool x3Sound = true, bool x6Net = true, bool textOverlay = false,
        string mainCpuTarget = "faithful")
    {
        if (roms.Profile.Name != MachineProfile.Hc2000V2HddCbootV1.Name)
            throw new ArgumentException("The full workstation requires the whole-chip C:-boot ROM map.", nameof(roms));
        if (x6Net && expansionDriverCount < 2)
            throw new ArgumentException(CapabilityCatalog.X6NeedsDrivers, nameof(x6Net));
        var hdd = PrepareHddWholeChipV2(roms, images, textOverlay,
            adopt: MachineProfile.Hc2000V2FullWorkstationV1 with { ExpansionDriverCount = expansionDriverCount },
            mainCpuTarget: mainCpuTarget);
        return new PreparedBasicMachine(hdd.Roms, hdd.InitialHdd, snapshotPolicy: hdd.SnapshotPolicy,
            x2Vga: x2Vga, x3Sound: x3Sound, x6Net: x6Net);
    }

    // The full workstation's C:-F: as named volumes of one container (its default, HC2000-WORKSTATION.hdd), admitted
    // cold through the same HddColdVolume path as the combined workstation.
    internal static PreparedBasicMachine PrepareAdmittedFullWorkstationMapWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms, HddColdVolume volume,
        int expansionDriverCount, bool x2Vga, bool x3Sound, bool x6Net, bool textOverlay, string mainCpuTarget = "faithful")
    {
        var cold = PrepareFullWorkstationWholeChipV2(roms, volume.MappedMedia, expansionDriverCount, x2Vga, x3Sound, x6Net,
            textOverlay, mainCpuTarget);
        var prepared = new PreparedBasicMachine(cold.Roms, cold.InitialHdd, volume.Admission, cold.SnapshotPolicy,
            x2Vga: x2Vga, x3Sound: x3Sound, x6Net: x6Net);
        volume.Admission.BindPrepared(prepared);
        return prepared;
    }

    public static PreparedBasicMachine PrepareX5HddWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        IEnumerable<HddInitialMedia> images, bool textOverlay = false, int expansionDriverCount = 0, bool x6Net = false,
        string mainCpuTarget = "faithful")
    {
        var hdd = PrepareHddWholeChipV2(roms, images, textOverlay, mainCpuTarget: mainCpuTarget);
        var x5 = PrepareRamWholeChipV2(roms, "hc2000-v2-paged-expansion-x5-hdd-guest-v1",
            textOverlay, expansionDriverCount: expansionDriverCount, x6Net: x6Net, mainCpuTarget: mainCpuTarget);
        return new PreparedBasicMachine(x5.Roms, hdd.InitialHdd, snapshotPolicy: x5.SnapshotPolicy, x6Net: x6Net);
    }

    internal static PreparedBasicMachine PrepareAdmittedX5HddMapWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms, HddColdVolume volume,
        bool textOverlay, int expansionDriverCount, bool x6Net, string mainCpuTarget = "faithful")
    {
        var cold = PrepareX5HddWholeChipV2(roms, volume.MappedMedia, textOverlay, expansionDriverCount, x6Net, mainCpuTarget);
        var prepared = new PreparedBasicMachine(cold.Roms, cold.InitialHdd, volume.Admission,
            cold.SnapshotPolicy, x6Net: x6Net);
        volume.Admission.BindPrepared(prepared);
        return prepared;
    }

    // Selected native volumes only; direct immutable raw/profile-off inputs keep their original path.
    internal static PreparedBasicMachine PrepareAdmittedHddWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        HddInitialMedia image, HddColdAdmission admission)
        => PrepareAdmittedHddImagesWholeChipV2(roms, [image], admission);

    internal static PreparedBasicMachine PrepareAdmittedHddMapWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        HddColdVolume volume, string mainCpuTarget = "faithful", MachineProfile? adopt = null)
        => PrepareAdmittedHddImagesWholeChipV2(roms, volume.MappedMedia, volume.Admission, guestAckEnabled: true, mainCpuTarget, adopt);

    private static PreparedBasicMachine PrepareAdmittedHddImagesWholeChipV2(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        IEnumerable<HddInitialMedia> images, HddColdAdmission admission, bool guestAckEnabled = false, string mainCpuTarget = "faithful",
        MachineProfile? adopt = null)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var copied = PrepareHddWholeChipV2(roms, images, adopt: adopt, mainCpuTarget: mainCpuTarget);
        var selected = copied.Roms;
        var target = selected.Profile.MainCpuTarget;
        // The resident guest installer queries the existing acknowledgement ABI even at cold boot.
        // This does not grant guest-safe live remapping or snapshot restore. Writable selection is a separate explicit opt-in.
        // Only the raw HDD identity is swapped for its guest-ACK twin: the C:-boot identity (hc2000-direct-c-boot) and the
        // combined workstation v2 already carry the guest-ACK disk policy and are kept as adopted.
        if (guestAckEnabled && selected.Profile.Name == MachineProfile.Hc2000V2HddV1.Name)
            selected.Profile = MachineProfile.Hc2000V2HddGuestV1 with { MainCpuTarget = target };
        var prepared = new PreparedBasicMachine(selected, copied.InitialHdd, admission, copied.SnapshotPolicy);
        admission.BindPrepared(prepared);
        return prepared;
    }

    public static EmulationWorker CreateWorker(PreparedBasicMachine prepared, InputQueue inputQueue, MediaBridge? media = null,
        HcsBridge? snapshots = null, bool audioEnabled = false, string? soundDump = null)
        => CreateWorker(prepared, inputQueue, media, slot: null, snapshots, audioEnabled, soundDump: soundDump);

    internal static EmulationWorker CreateWorker(PreparedBasicMachine prepared, InputQueue inputQueue, MediaBridge? media,
        HcsBridge? snapshots, bool audioEnabled, DiskWritebackOwnerLedger? writeback, string? soundDump = null,
        CpNetHostGate? hostGate = null, CpNetBridge? network = null, CpNetHostGate? inboundHostGate = null,
        CpNetServerBridge? networkServer = null, OutboundGateway? outbound = null,
        Func<X6SessionParameters, IX6Channel?, CancellationToken, Task<IX6Channel>>? x6Ssh = null,
        X6DefaultConnection? x6Default = null)
        => CreateWorker(prepared, inputQueue, media, slot: null, snapshots, audioEnabled, writeback, soundDump,
            hostGate: hostGate, network: network, inboundHostGate: inboundHostGate, networkServer: networkServer,
            outbound: outbound, x6Ssh: x6Ssh, x6Default: x6Default);

    /// <summary>Same production worker. <paramref name="slot"/> publishes the composed FDC so a
    /// test can observe READ DATA without touching the owner-thread machine.</summary>
    internal static EmulationWorker CreateWorker(PreparedBasicMachine prepared, InputQueue inputQueue,
        MediaBridge? media, ComposedFloppySlot? slot, HcsBridge? snapshots = null, bool audioEnabled = false,
        DiskWritebackOwnerLedger? writeback = null, string? soundDump = null,
        Action<CpNetRuntime>? observeCpNet = null, Action<LiveMachine>? observeBoundary = null,
        CpNetHostGate? hostGate = null, CpNetBridge? network = null,
        Func<string, int, CancellationToken, Task<ICpNetRemoteConnection>>? networkConnect = null,
        CpNetHostGate? inboundHostGate = null, Action<CpNetInboundRuntime>? observeCpNetInbound = null,
        CpNetServerBridge? networkServer = null, OutboundGateway? outbound = null,
        Func<X6SessionParameters, IX6Channel?, CancellationToken, Task<IX6Channel>>? x6Ssh = null,
        X6DefaultConnection? x6Default = null, IHostClock? hostClock = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(inputQueue);
        hostGate ??= new CpNetHostGate();
        inboundHostGate ??= new CpNetHostGate();
        slot ??= new ComposedFloppySlot();
        // Honest rate reporting (hc2000-accelerated-clock-domains): one meter per worker, fed once per
        // rendered frame by the owner and read as an immutable sample by the UI. The requested rate
        // is the profile's exact rate (faithful, the legacy turbo, or the explicit target choice);
        // a live switch that takes effect rebases it on the owner.
        var clock = hostClock ?? new MonotonicHostClock();
        var rateMeter = new CpuRateMeter(prepared.Roms.Profile.CpuTStatesPerSecond, clock.Frequency);
        // Owner-written, UI-read outcome of the last live main-CPU clock switch (array element so the
        // closure may use it volatile; a volatile local cannot be captured).
        string?[] mainCpuSwitchBox = [null];
        // A live main-CPU clock switch that met an unsafe boundary waits for the next safe one (hc2000-speed-140-turbo-280:
        // on the extended machine a boundary where a side Z80, not the main, was the last to act is not safe, and the
        // Turbo button must not be refused for that). Owner thread only.
        MainCpuSpeed? pendingMainCpu = null;
        // Applies the pending switch when this boundary is safe; otherwise keeps it for the next boundary. The previous
        // selection and the machine state stay as they were until it applies.
        void TrySwitchMainCpu(LiveMachine machine)
        {
            if (pendingMainCpu is not { } target) return;
            try
            {
                machine.SetMainCpuTarget(target);
                pendingMainCpu = null;
                rateMeter.SetRequested(target.TStatesPerSecond());
                // Done: the rate line itself now names the new choice and what it achieves (no standing message over it).
                Volatile.Write(ref mainCpuSwitchBox[0], null);
            }
            catch (InvalidOperationException)
            {
                Volatile.Write(ref mainCpuSwitchBox[0],
                    $"Main CPU switch to {target.Setting()} waits for a safe point");
            }
        }
        RuntimeContext? runtimeContext = null;
        MediaSession? session = null;
        FloppyDrive? driveA = null;
        FloppyDrive? driveB = null;
        TapeTransport? liveTape = null;
        TapeDeckState? retainedTape = null;
        BlockDiskPort? liveHdd = null;
        CpNetRuntime? liveCpNet = null;
        CpNetInboundRuntime? liveCpNetInbound = null;
        CpNetOwnerSession? cpNetOwner = null;
        CpNetServerOwnerSession? cpNetServerOwner = null;
        CpNetRetirements? cpNetRetirements = null;
        IDisposable? inheritedHostLease = null;
        void BindInheritedHostLease(LiveMachine machine)
        {
            inheritedHostLease?.Dispose();
            inheritedHostLease = hostGate.State == CpNetHostGate.GateState.Available &&
                inboundHostGate.State == CpNetHostGate.GateState.Available
                ? null : machine.AcquireExternalState();
        }
        void BindLiveNetwork(LiveMachine machine, CpNetRuntime? runtime)
        {
            cpNetRetirements ??= new();
            liveCpNet = runtime;
            liveCpNetInbound = runtime is null ? null : new CpNetInboundRuntime(machine, inboundHostGate);
            cpNetRetirements.Bind(machine, liveCpNet, liveCpNetInbound);
            BindInheritedHostLease(machine);
            cpNetOwner = runtime is null ? null : new CpNetOwnerSession(runtime, networkConnect);
            cpNetServerOwner = liveCpNetInbound is null ? null : new CpNetServerOwnerSession(liveCpNetInbound);
            network?.Bind();
            networkServer?.Bind();
            if (runtime is not null) observeCpNet?.Invoke(runtime);
            if (liveCpNetInbound is not null) observeCpNetInbound?.Invoke(liveCpNetInbound);
        }
        // The X6 network card's host side (hc2000-ssh-terminal): one runtime per composed machine, reaching out
        // only through the session's outbound gateway. While a session is open the machine holds live external
        // state, so no snapshot can capture a connection it could not restore.
        ExpansionX6NetCard? pendingX6 = null, replacementX6 = null;
        X6NetRuntime? liveX6 = null;
        IDisposable? x6Lease = null;
        void BindX6(LiveMachine machine, ExpansionX6NetCard? card)
        {
            x6Lease?.Dispose(); x6Lease = null;
            liveX6?.Dispose(); liveX6 = null;
            if (card is null || outbound is null) return;
            var runtime = new X6NetRuntime(outbound, x6Ssh, defaults: x6Default);
            liveX6 = runtime;
            card.EventPending = () => runtime.EventPending;
            card.Host = request =>
            {
                var reply = runtime.Handle(request);
                if (runtime.OpenSessions > 0) x6Lease ??= machine.AcquireExternalState();
                else { x6Lease?.Dispose(); x6Lease = null; }
                return reply;
            };
        }
        // The current machine's CPU/RAM for request-gated debugger boundary copies.
        CpuZ80? liveCpu = null; HcMemory? liveMemory = null;
        BlockDiskPort? replacementHdd = null;
        BlockDiskPortState? retainedHdd = null;
        HddMediaAdapter? hddAdapter = null;
        bool coldNew = true;
        BeeperAudioOutput? audio = null;
        string? audioReason = audioEnabled ? null : "Host audio disabled.";
        var earEdges = new List<BeeperEdge>();
        var x3Edges = new List<X3PcmEdge>();
        bool monitoring = true;
        (LiveMachine Machine, CpuZ80 Cpu, HcMemory Memory, FloppyController Controller, Ula Ula, Action ActivateMonitor, CpNetRuntime? CpNet)? replacement = null;
        // The main CPU rate of a loaded replacement: a snapshot keeps the speed it was saved at.
        long replacementTStates = 0;
        // The ROM scans keys on interrupts; two frames span a scan and its debounce.
        // Use emulated ticks, not host sleeps or a single instruction-boundary deferral.
        var input = new InputController(inputQueue, (control, _) =>
        {
            if (writeback?.Frozen == true && control.Kind != InputControlKind.Shutdown) return;
            if (control.Kind == InputControlKind.Shutdown)
            {
                if (writeback?.Frozen == true && writeback.ShutdownPermitted != true) return;
                runtimeContext?.Shutdown();
                writeback?.OnShutdown();
            }
            if (control.Kind == InputControlKind.Pause) { runtimeContext?.Pause(); audio?.Pause(); }
            if (control.Kind == InputControlKind.Resume)
            {
                runtimeContext?.Resume();
                // Exact pause exclusion (hc2000-accelerated-clock-domains): a resumed machine starts a
                // fresh rate window, so paused wall time never shows as a diluted (over)load. The meter
                // alone cannot tell a pause from a slow host; the owner knows when it resumes.
                rateMeter.Rebase();
            }
            if (control.Kind == InputControlKind.Step) runtimeContext?.Step();
            if (control.Kind == InputControlKind.SetSpeed) runtimeContext?.SetSpeed((SpeedMode)control.SpeedPercent);
            if (control.Kind == InputControlKind.SetMainCpuSpeed && runtimeContext is not null)
            {
                // Live main-CPU clock switch (hc2000-accelerated-clock-domains): serialized on the
                // owner at this instruction boundary. An unsafe boundary is refused explicitly and
                // the previous selection and machine state are kept; only a switch that took effect
                // rebases the meter's window, so two rates never mix in one window.
                pendingMainCpu = (MainCpuSpeed)control.MainCpuSpeedIndex;
                TrySwitchMainCpu(runtimeContext.Machine);
            }
            if (control.Kind == InputControlKind.Reset && runtimeContext is not null)
            {
                var capture = liveTape!.Capture();
                if (!capture.Success) throw new InvalidOperationException($"Tape reset transfer refused: {capture.Failure!.Detail}");
                retainedTape = capture.Value;
                // Spec 14: reset discards the session, never publishes its dirty image.
                runtimeContext.Machine.TapeVolume?.Cancel();
                hddAdapter?.RetireGuestSwitch();
                retainedHdd = liveHdd?.Capture(); // Same media, not a new-image initialization exception.
                runtimeContext.Reset();
            }
        }, media: media is null ? null : (command, _) =>
        {
            var floppy = slot.Controller ?? throw new InvalidOperationException(
                "Media command before the faithful FDC was composed.");
            session ??= new MediaSession(floppy.DriveA, floppy.DriveB, liveTape, manualDeck: true, hdd: hddAdapter,
                tapeVolume: () => runtimeContext?.Machine.TapeVolume); // The delegate resolves the CURRENT machine after every reset.
            var rawHddObservation = command.Action == MediaAction.Hdd
                && command.Hdd?.Action is HddAction.Inspect or HddAction.CaptureSave or HddAction.CancelSave;
            if (command.Action == MediaAction.Writeback || writeback?.Frozen == true && command.Action != MediaAction.Inspect && !rawHddObservation)
            {
                var viewed = session.Apply(new(MediaAction.Inspect));
                var ownerReply = command.Action == MediaAction.Writeback
                    ? writeback?.Apply(command.Writeback, session, runtimeContext, inputQueue)
                        ?? new WritebackOwnerReply(false, false, Error: "Writeback owner unavailable.")
                    : new WritebackOwnerReply(false, true, Error: "Writeback freeze: media change refused.");
                media.Publish(viewed with { RequestId = command.RequestId, Error = ownerReply.Error, Writeback = ownerReply });
                return;
            }
            if (command.Action == MediaAction.MonitorTape) { monitoring = command.Value; if (audio is not null) audio.TapeMonitoringEnabled = monitoring; }
            var reply = session.Apply(command);
            var view = liveTape!.Deck.Observe();
            earEdges.Add(new(runtimeContext!.Machine.MasterTick, view.EarLevel,
                view.Mode == TapeDeckMode.Playing && !view.Paused && view.HoldReason == TapeHoldReason.None));
            media.Publish(reply with { Tape = reply.Tape is null ? null : reply.Tape with
                { AudioAvailable = audio?.Available == true, AudioReason = audioReason ?? (audio?.Available == true ? null : "Host audio unavailable.") } });
        }, snapshot: snapshots is null ? null : (command, _) =>
        {
            if (writeback?.Frozen == true)
            {
                snapshots.Publish(new(command.RequestId, Error: "Writeback freeze: snapshot refused."));
                return;
            }
            if (command.Action != HcsAction.Load)
            {
                if (command.Action == HcsAction.Save)
                {
                    try
                    {
                        liveHdd?.ValidateLogicalSnapshot();
                        runtimeContext!.Machine.ValidateSessionSnapshotSave();
                    }
                    // A state the save refuses is reported, never thrown into the worker (an ArgumentException from a
                    // schema check once stopped the emulation instead of failing the save).
                    catch (Exception ex) when (ex is InvalidOperationException or HcsException or ArgumentException)
                    {
                        snapshots.Publish(new(command.RequestId, Error: ex.Message));
                        return;
                    }
                }
                snapshots.Publish(new HcsSession(runtimeContext!.Machine).Apply(command));
                return;
            }
            var replacementError = new HcsSession(runtimeContext!.Machine).ValidateReplacement();
            if (replacementError is not null)
            {
                snapshots.Publish(new(command.RequestId, Error: replacementError));
                return;
            }
            var view = liveTape!.Deck.Observe();
            if (!command.DiscardChanges && (view.A.Dirty || view.B.Dirty || driveA!.DirtySectors.Count != 0 || driveB!.DirtySectors.Count != 0 ||
                liveHdd?.Capture().Slots.Any(slot => slot.Dirty) == true))
            {
                snapshots.Publish(new(command.RequestId, Error: "DirtyConfirmationRequired: save edits or explicitly allow discard before snapshot Load."));
                return;
            }
            // Validate on a detached real composition. Never load into the running machine,
            // never publish its FDC or audio, and never step a candidate before adoption.
            var candidateSlot = new ComposedFloppySlot();
            Ula? candidateUla = null;
            CpNetRuntime? candidateCpNet = null;
            BlockDiskPort? candidateHdd = null;
            ExpansionX6NetCard? candidateX6 = null;
            bool adopted = false;
            LiveMachine? candidate = null;
            HcsReply loaded;
            try
            {
                // A snapshot keeps its speed (hc2000-speed-140-turbo-280): the candidate runs at the clock the file was saved
                // at - Turbo's 280 MHz, or the faithful default of an older version - not at this session's start-up speed,
                // which the load would refuse as another clock.
                var loadRoms = prepared.Roms;
                if (HcsCodec.PeekClockDivider(command.Bytes.AsSpan()) is { } divider &&
                    MainCpuSpeedInfo.FromDivider(divider, out var savedSpeed, out var savedTurbo) &&
                    (savedSpeed != loadRoms.Profile.MainCpuTarget || savedTurbo != loadRoms.Profile.CpuTurboMultiplier))
                    loadRoms = loadRoms with { Profile = loadRoms.Profile with { MainCpuTarget = savedSpeed, CpuTurboMultiplier = savedTurbo } };
                candidate = CreateMachine(loadRoms, candidateSlot, new(new()), new(new()),
                    earEdges.Add, out var candidateCpu, out var candidateMemory,
                    observeUla: value => candidateUla = value, canMonitor: () => adopted, snapshotPolicy: prepared.SnapshotPolicy,
                    observeHdd: value => candidateHdd = value,
                    experimentalX2: prepared.X2Vga, experimentalX3: prepared.X3Sound, experimentalX4: prepared.X4Math,
                    x3Output: x3Edges.Add, observeCpNet: value => candidateCpNet = value, hostGate: hostGate,
                    experimentalX6: prepared.X6Net, observeX6: card => candidateX6 = card);
                loaded = new HcsSession(candidate).Apply(command);
                if (loaded.Applied)
                {
                    candidateCpNet?.MarkRestored();
                    replacementX6 = candidateX6;
                    replacementHdd = candidateHdd;
                    // A candidate never adopted is retired/disposed: its RAM goes back to the pool.
                    replacement?.Machine.Dispose();
                    replacement = (candidate, candidateCpu, candidateMemory, candidateSlot.Controller!, candidateUla!, () => adopted = true, candidateCpNet);
                    replacementTStates = loadRoms.Profile.CpuTStatesPerSecond;
                    runtimeContext!.Reset();
                }
                else candidate.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[BasicMachineComposition Load Error] {ex}");
                candidate?.Dispose();
                snapshots.Publish(new(command.RequestId, Error: ex.Message));
                return;
            }
            snapshots.Publish(loaded);
        }, network: (command, _) =>
        {
            if (network is null || !network.IsCurrent(command)) return;
            var status = cpNetOwner is null
                ? new CpNetSessionStatus(CpNetSessionState.Failed, false, "Network hardware is not present.")
                : command.Action switch
                {
                    CpNetAction.Connect => cpNetOwner.Start(command.Host, command.Port),
                    CpNetAction.Disconnect => cpNetOwner.Stop(),
                    CpNetAction.Observe => cpNetOwner.Observe(),
                    _ => new CpNetSessionStatus(CpNetSessionState.Failed, false, "Invalid network command.")
                };
            network.Publish(command, status);
        }, networkServer: (command, _) =>
        {
            if (networkServer is null || !networkServer.IsCurrent(command)) return;
            var settings = command.Action == CpNetServerAction.Start ? networkServer.TakeActivation(command) : null;
            CpNetServerStatus status;
            try
            {
                if (cpNetServerOwner is null)
                {
                    settings?.Dispose();
                    status = new(CpNetServerState.Failed, false, Error: "Network hardware is not present.");
                }
                else status = command.Action switch
                {
                    CpNetServerAction.Start when settings is not null => cpNetServerOwner.Start(settings),
                    CpNetServerAction.Stop => cpNetServerOwner.Stop(),
                    CpNetServerAction.Observe => cpNetServerOwner.Observe(),
                    _ => new(CpNetServerState.Failed, false, Error: "Invalid server command.")
                };
            }
            catch
            {
                settings?.Dispose();
                status = new(CpNetServerState.Failed, false, Error: "Server action failed.");
            }
            networkServer.Publish(command, status);
        }, minimumHoldTicks: 2 * FrameTiming.FrameTicks);
        prepared.ColdAdmission?.MarkEnqueued(prepared);
        HcsLimits? admission = null;
        try
        {
        admission = inputQueue.BindSnapshotRuntime(prepared);
        snapshots?.BindAdmission(inputQueue, admission);
        var worker = new EmulationWorker(
            () =>
            {
                try
                {
                prepared.ColdAdmission?.EnterOwner();
                if (replacement is { } loaded)
                {
                    replacement = null; retainedTape = null; retainedHdd = null;
                    liveHdd = replacementHdd; replacementHdd = null;
                    // Snapshot bytes never revive host paths, writable bindings, pending saves or switch tickets.
                    hddAdapter = liveHdd is null ? null : new HddMediaAdapter(liveHdd);
                    BindLiveNetwork(loaded.Machine, loaded.CpNet);
                    BindX6(loaded.Machine, replacementX6);
                    replacementX6 = null;
                    liveCpu = loaded.Cpu; liveMemory = loaded.Memory;
                    driveA = loaded.Controller.DriveA; driveB = loaded.Controller.DriveB;
                    slot.Publish(loaded.Controller); liveTape = loaded.Machine.Tape!;
                    session = new(driveA, driveB, liveTape, manualDeck: true, hdd: hddAdapter,
                        tapeVolume: () => loaded.Machine.TapeVolume);
                    session.RestoredMetadata(liveTape.Capture().Value.Cassette);
                    earEdges.Clear(); x3Edges.Clear(); loaded.ActivateMonitor();
                    audio!.Reset(loaded.Machine.MasterTick, loaded.Ula.Beeper.Speaker, loaded.Ula.Beeper.Microphone);
                    if (loaded.Machine.ExpansionX3 is { } loadedX3)
                    {
                        var channels = loadedX3.Streamer.Capture().Playback.Channels;
                        for (var channel = 0; channel < channels.Length; channel++)
                            x3Edges.Add(new(channel, loaded.Machine.MasterTick, channels[channel].Current, channels[channel].Volume));
                    }
                    var view = liveTape.Deck.Observe();
                    earEdges.Add(new(loaded.Machine.MasterTick, view.EarLevel, view.Mode == TapeDeckMode.Playing && !view.Paused && view.HoldReason == TapeHoldReason.None));
                    // The rate line and Turbo show the loaded machine's speed; a switch asked before the load is dropped.
                    rateMeter.SetRequested(replacementTStates);
                    pendingMainCpu = null; Volatile.Write(ref mainCpuSwitchBox[0], null);
                    return loaded.Machine;
                }
                // All calls run on the same owner. Keep media (including dirty overlays and
                // MediaSession names), not the CPU, RAM, scheduler or powered drive mechanics.
                driveA ??= new FloppyDrive(new());
                driveB ??= new FloppyDrive(new());
                driveA.PowerCycle();
                driveB.PowerCycle();
                earEdges.Clear(); x3Edges.Clear();
                audio ??= BeeperAudioOutput.Open(FrameTiming.MasterHz, audioEnabled, reason => audioReason = reason, soundDump, stereo: prepared.X3Sound);
                audio.Reset(); audio.TapeMonitoringEnabled = monitoring;
                LiveMachine machine;
                if (coldNew)
                {
                    coldNew = false;
                    machine = CreateColdNewMachine(prepared, slot, driveA, driveB, earEdges.Add, value => liveHdd = value,
                        (c, m) => { liveCpu = c; liveMemory = m; }, value => liveCpNet = value, x3Output: x3Edges.Add, hostGate: hostGate,
                        observeX6: card => pendingX6 = card);
                    if (liveHdd is not null) hddAdapter = new(liveHdd, prepared.InitialHdd);
                }
                else
                {
                    machine = CreateMachine(prepared.Roms, slot, driveA, driveB, earEdges.Add,
                        out var freshCpu, out var freshMemory, observeHdd: value => liveHdd = value,
                        snapshotPolicy: prepared.SnapshotPolicy, experimentalX2: prepared.X2Vga,
                        experimentalX3: prepared.X3Sound, x3Output: x3Edges.Add, experimentalX4: prepared.X4Math,
                        observeCpNet: value => liveCpNet = value, hostGate: hostGate, experimentalX6: prepared.X6Net,
                        observeX6: card => pendingX6 = card);
                    liveCpu = freshCpu; liveMemory = freshMemory;
                    if (retainedHdd is not null)
                    {
                        liveHdd!.Restore(retainedHdd);
                        liveHdd.Reset();
                        retainedHdd = null;
                    }
                    if (liveHdd is not null) hddAdapter?.Rebind(liveHdd);
                }
                liveTape = machine.Tape!;
                BindLiveNetwork(machine, liveCpNet);
                BindX6(machine, pendingX6);
                pendingX6 = null;
                if (retainedTape is not null)
                {
                    var restored = liveTape.Restore(retainedTape);
                    if (!restored.Success) throw new InvalidOperationException($"Tape reset adoption refused: {restored.Failure!.Detail}");
                    liveTape.OnMicChanged(machine.MasterTick, false);
                    retainedTape = null;
                }
                session?.RebindTape(liveTape);
                // A reset rebuilds the machine at its chosen speed: the rate line and Turbo say so (Turbo or an applied
                // recommendation lasted only as long as the machine it switched), and a pending switch is dropped.
                rateMeter.SetRequested(prepared.Roms.Profile.CpuTStatesPerSecond);
                pendingMainCpu = null; Volatile.Write(ref mainCpuSwitchBox[0], null);
                return machine;
                }
                catch
                {
                    // A failed owner factory cannot run again on this worker.
                    prepared.ColdAdmission?.RejectAfterOwnerDrain();
                    throw;
                }
            },
            context =>
            {
                runtimeContext = context;
                // RuntimeContext now owns the NEW machine and the media adapter is installed;
                // this boundary precedes its first instruction, even when immediately paused.
                prepared.ColdAdmission?.AcknowledgeInstalled(prepared, context.Machine, hddAdapter);
                input.ApplyPending(context.Machine.MasterTick, context.Machine.Keyboard);
                if (pendingMainCpu is not null && !context.Resetting && !context.Stopping) TrySwitchMainCpu(context.Machine);
                if (!context.Resetting && !context.Stopping)
                {
                    hddAdapter?.PumpGuestSwitch();
                    cpNetRetirements!.Pump();
                    if (cpNetOwner is not null) cpNetOwner.Pump();
                    else liveCpNet?.Pump();
                    liveCpNetInbound?.Pump();
                    if (inheritedHostLease is not null && hostGate.State == CpNetHostGate.GateState.Available &&
                        inboundHostGate.State == CpNetHostGate.GateState.Available)
                    {
                        inheritedHostLease.Dispose();
                        inheritedHostLease = null;
                    }
                    observeBoundary?.Invoke(context.Machine);
                }
            },
            audio: (frame, speed, cancellation) =>
            {
                var paced = audio!.ProcessFrameWithX3(frame.StartMasterTick, frame.EndMasterTick,
                    frame.BeeperEdges.ToArray(), speed, cancellation, earEdges.ToArray(), x3Edges.ToArray());
                earEdges.Clear();
                x3Edges.Clear();
                return paced;
            },
            cleanup: context =>
            {
                try
                {
                    network?.Retire();
                    networkServer?.Retire();
                    cpNetRetirements?.Retire(liveCpNet);
                    cpNetRetirements?.Retire(liveCpNetInbound);
                    liveCpNet = null;
                    liveCpNetInbound = null;
                    cpNetOwner = null;
                    cpNetServerOwner = null;
                    inheritedHostLease?.Dispose();
                    inheritedHostLease = null;
                    x6Lease?.Dispose(); x6Lease = null;
                    liveX6?.Dispose(); liveX6 = null;
                    hddAdapter?.RetireGuestSwitch();
                    input.ReleaseOwned(context.Machine.Keyboard);
                    if (!context.Resetting) audio?.Dispose();
                }
                finally
                {
                    if (!context.Resetting) prepared.ColdAdmission?.RejectAfterOwnerDrain();
                    // The retired machine never runs again; its RAM goes back to the pool (hc2000-ram-one-buffer). A retiring
                    // CP/NET runtime may still read that RAM until its exchange has closed, so the machine waits for it: the
                    // next machine's pump disposes it on reset or an adopted load, and at stop a short drain does, after which
                    // a machine still read is only let go (retired), never reused under a live reader.
                    if (cpNetRetirements is null) context.Machine.Dispose();
                    else cpNetRetirements.DisposeWhenClosed(context.Machine);
                    if (!context.Resetting)
                    {
                        replacement?.Machine.Dispose();
                        foreach (var stillRead in cpNetRetirements?.Drain(StopDrain) ?? []) stillRead.Retire();
                    }
                }
            },
            executionAllowed: () => liveHdd?.GuestSwitchHoldsCpu != true,
            debugSnapshot: _ =>
            {
                // Request-gated boundary copy; runs on the worker's owner thread only.
                if (liveCpu is not { } cpu || liveMemory is not { } memory) return null;
                var state = new Z80SnapshotSeam(cpu).Capture();
                return new DebuggerSession($"HC2000 {prepared.Roms.Profile.Name} (boundary copy)")
                    .Boundary(DebuggerSession.RegisterView(state), memory, slot.Controller?.Fdc, state.TStates);
            },
            hostClock: clock,
            rateMeter: rateMeter,
            mainCpuSwitchStatus: () => Volatile.Read(ref mainCpuSwitchBox[0])
        );
        inputQueue.TrackSnapshotRuntime(admission, worker.Completion);
        _ = worker.Completion.ContinueWith(_ =>
        {
            snapshots?.Disconnect();
            network?.Disconnect();
            inputQueue.RetireSnapshotRuntime(admission);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return worker;
        }
        catch
        {
            if (admission is not null) inputQueue.RetireSnapshotRuntime(admission);
            // Constructor failure occurs before any worker was started.
            prepared.ColdAdmission?.RejectBeforeOwnerStarted();
            throw;
        }
    }

    // Never called by Reset or HCS. No CPU instruction has run when this factory returns.
    internal static LiveMachine CreateColdNewMachine(PreparedBasicMachine prepared, ComposedFloppySlot slot,
        FloppyDrive driveA, FloppyDrive driveB, Action<BeeperEdge> monitor, Action<BlockDiskPort> observeHdd,
        Action<CpuZ80, HcMemory>? observeCpu = null, Action<CpNetRuntime>? observeCpNet = null,
        bool experimentalX2 = false, Action<PortDecoder>? observePorts = null,
        Action<X3PcmEdge>? x3Output = null, CpNetHostGate? hostGate = null, Action<ExpansionX6NetCard>? observeX6 = null,
        Func<DateTime>? hostCivilTime = null)
    {
        prepared.ColdAdmission?.VerifyFreshOwnerCandidate(prepared);
        BlockDiskPort? controller = null;
        var machine = CreateMachine(prepared.Roms, slot, driveA, driveB, monitor,
            out var cpu, out var memory, observeHdd: value => { controller = value; observeHdd(value); },
            snapshotPolicy: prepared.SnapshotPolicy, experimentalX2: experimentalX2 || prepared.X2Vga, observePorts: observePorts,
            experimentalX3: prepared.X3Sound, x3Output: x3Output, experimentalX4: prepared.X4Math,
            observeCpNet: observeCpNet, hostGate: hostGate, experimentalX6: prepared.X6Net, observeX6: observeX6,
            hostCivilTime: hostCivilTime);
        observeCpu?.Invoke(cpu, memory);
        prepared.ColdAdmission?.ValidateBeforeAdoption(prepared, machine);
        foreach (var image in prepared.InitialHdd)
            controller!.AttachHdd(image.Drive, image.Bytes.AsSpan(), image.WriteProtected);
        return machine;
    }

    private static LiveMachine CreateMachine((MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        ComposedFloppySlot slot, FloppyDrive driveA, FloppyDrive driveB, Action<BeeperEdge> monitor,
        out CpuZ80 cpu, out HcMemory memory,
        Action<Ula>? observeUla = null, Func<bool>? canMonitor = null, Action<BlockDiskPort>? observeHdd = null,
        HcsWavPolicy? snapshotPolicy = null, bool experimentalX2 = false, Action<PortDecoder>? observePorts = null,
        bool experimentalX3 = false, Action<X3PcmEdge>? x3Output = null, bool experimentalX4 = false,
        Action<CpNetRuntime>? observeCpNet = null, CpNetHostGate? hostGate = null,
        bool experimentalX6 = false, Action<ExpansionX6NetCard>? observeX6 = null,
        Func<DateTime>? hostCivilTime = null)
    {
        var profile = roms.Profile;
        var scheduler = new DeviceScheduler();
        var clock = new FrameClock(scheduler, 1);
        var mapper = CpuTimeMapper.FromProfile(profile);
        var adapter = new CpuDeviceTimeAdapter(scheduler, mapper);
        memory = new HcMemory(profile.Memory with { RomProvenance = $"canonical verified {profile.Name} ROM set" },
            roms.Basic, roms.Cpm, roms.If1);
        var keyboard = new KeyboardMatrix();
        var ula = new Ula(memory, keyboard, scheduler, clock, 2, profile.Video);
        observeUla?.Invoke(ula);
        // Empty at first boot; mounted media survives Reset. Instant remains false.
        var floppy = new FloppyController(scheduler, 100, driveA, driveB);
        slot.Publish(floppy);
        var driveLatch = new PortDevice(
            read: (_, _) => floppy.ReadLatch(),
            write: (_, value, _) => floppy.WriteLatch(value));
        var fdcStatus = new PortDevice(read: (_, _) => floppy.Fdc.ReadStatus());
        var fdcData = new PortDevice(
            read: (_, _) =>
            {
                slot.NoteReadData();
                return floppy.Fdc.ReadData();
            },
            write: (_, value, _) => floppy.Fdc.WriteData(value));
        var idle = new PortDevice();
        var mappings = profile.Memory.RamBanks is not null
            ? HcIoProfile.WithRamBanks(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle)
            : profile.Memory.RamPaged is not null
                ? HcIoProfile.WithPagedRam(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle)
                : HcIoProfile.Faithful(memory, ula, driveLatch, fdcStatus, fdcData, idle, idle);
        var x5Hdd = profile.Name == "hc2000-v2-paged-expansion-x5-hdd-guest-v1";
        // The full workstation (hc2000-full-workstation-default) is X5-managed like the X5 HDD successor: its HDD
        // keeps the MAIN-legacy DMA admission and the refused native switch (hc2000-x5-hdd-guest-composition-v1),
        // and it adds the tape, M: and the X2/X3 cards on its own identity.
        var fullWorkstation = profile.Name == "hc2000-v2-full-workstation-v1";
        ExpansionController? expansionRoot = null;
        BlockDiskPort? hdd = null;
        if (profile.BlockDisk is BlockDiskPolicy.HddV1 or BlockDiskPolicy.HddGuestV1)
        {
            // The successor workstation composes the side-Z80 arena with the guest driver; multi-CPU
            // native switching and restored rearm are not qualified for it (decision
            // hc2000-combined-workstation-hdd-guest-v2), so its port refuses composite native
            // initiation at the controller boundary. The idle QUERY4 doorbell still answers for HDINST.
            bool hasCompositeArena = profile.Name is ("hc2000-v2-paged-expansion-v1" or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2");
            bool compositeSwitchRefused = hasCompositeArena && profile.BlockDisk == BlockDiskPolicy.HddGuestV1;
            hdd = new BlockDiskPort(memory, floppy, hddEnabled: true,
                guestAckEnabled: profile.BlockDisk == BlockDiskPolicy.HddGuestV1,
                compositeSwitchRefused: compositeSwitchRefused || x5Hdd || fullWorkstation,
                descriptorMemoryAdmitted: x5Hdd || fullWorkstation
                    ? () => expansionRoot?.Mmu is { } mmu && mmu.SlotsOf(ExpansionMmu.MainUnit).All(slot => slot.Kind == ExpansionSlotKind.Legacy)
                    : null);
            // The composite keeps its paged base rows and appends only the device rows;
            // the single-device identities rebuild from their own base as before.
            mappings = profile.Name is "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2" || fullWorkstation
                ? [.. mappings, .. hdd.Mappings()]
                : HcIoProfile.WithBlockDisk(profile, memory, ula, driveLatch, fdcStatus, fdcData, idle, idle, hdd);
            observeHdd?.Invoke(hdd);
        }
        // Spec 14 deck control on its own profile; a reset builds a new port, so its selector starts at 0.
        var tapeControl = profile.TapeControl ? new TapeControlPort() : null;
        if (tapeControl is not null)
            mappings = profile.Name is "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2" || fullWorkstation
                ? [.. mappings, .. tapeControl.Mappings()]
                : HcIoProfile.WithTapeControl(profile, memory, ula, driveLatch, fdcStatus, fdcData, idle, idle, tapeControl);
        ExpansionArena? expansionArena = null;
        ExpansionX0? x0 = null;
        ExpansionRowInterrupt? rowInterrupt = null;
        ExpansionX2Card? x2 = null;
        ExpansionX3Card? x3 = null;
        ExpansionX4Card? x4 = null;
        ExpansionX6NetCard? x6 = null;
        CpNetRuntime? cpnetRuntime = null;
        var x5 = profile.Name == "hc2000-v2-paged-expansion-x5-v1" || x5Hdd || fullWorkstation;
        if (experimentalX2 && (!x5 || x5Hdd)) throw new ArgumentException("X2 requires the full X5 profile.");
        if (experimentalX3 && (!x5 || x5Hdd)) throw new ArgumentException("X3 requires the full X5 profile.");
        if (experimentalX4 && (!x5 || x5Hdd || fullWorkstation)) throw new ArgumentException("X4 requires the full X5 profile.");
        if (experimentalX6 && !x5) throw new ArgumentException("X6 requires the full X5 profile.");
        // A card only a HANDOFF between two driver units can own is never composed dark (hc2000-x6-net-device-v1).
        if (experimentalX6 && profile.ExpansionDriverCount < 2) throw new ArgumentException(CapabilityCatalog.X6NeedsDrivers);
        if (profile.Extended)
        {
            expansionArena = memory.EnableExpansionArena(profile);
            expansionRoot = new(expansionArena, x5 ? profile.ExpansionDriverCount : 0);
            // The side Z80s start at the machine's clock (hc2000-speed-140-turbo-280); a program may still set a
            // unit's own (SET UNIT CLOCK).
            expansionRoot.SetUnitClocks(profile.MainCpuTarget);
            if (fullWorkstation && !memory.TryReserveWorkstationPages())
                throw new ArgumentException("The full workstation's switch pages cannot be reserved.");
            // The row's single interrupt source (design contract 2): always composed on an expansion
            // machine. The DMA engine settles the arena's page loans with the transfer, and the X2
            // card raises the frame-presented source when a Present command completes.
            rowInterrupt = new ExpansionRowInterrupt();
            expansionRoot.RowInterrupt = rowInterrupt;
            x0 = new ExpansionX0(memory, scheduler);
            expansionRoot.Dma = x0.Dma;
            x0.Dma.OnLentPageSettled = (cardId, page) => expansionArena.EndLentPage(cardId, page);
            // A finished transfer raises the dma-complete source with its channel's sequence number.
            x0.Dma.OnTransferDone = sequence => rowInterrupt.Raise(ExpansionRowInterruptSource.DmaComplete, (ushort)sequence);
            if (experimentalX2)
            {
                if (!ExpansionX2Card.TryCompose(memory, scheduler, expansionRoot, x0,
                        mappings, 3000, 3, ExpansionArena.FirstUnreservedDevicePage,
                        ExpansionArena.FirstUnreservedDevicePage + 5, out x2))
                    throw new ArgumentException("X2 resources cannot be composed.");
                x2!.OnPresentCompleted = () => rowInterrupt!.Raise(ExpansionRowInterruptSource.FramePresented, 0);
            }
            if (experimentalX3 && !ExpansionX3Card.TryCompose(memory, scheduler, expansionRoot, x0,
                    mappings, 4, 3001, 3002, edge =>
                    {
                        if (canMonitor?.Invoke() != false) x3Output?.Invoke(new(edge.Channel, edge.Tick, edge.Sample, edge.Volume));
                    }, out x3))
                throw new ArgumentException("X3 resources cannot be composed.");
            if (experimentalX4 && !ExpansionX4Card.TryCompose(memory, scheduler, expansionRoot, x0,
                    mappings, 5, 3003, out x4))
                throw new ArgumentException("X4 resources cannot be composed.");
            // The X6 network card (hc2000-ssh-terminal): its host side is attached by the caller (observeX6) and
            // reaches out only through the machine's outbound gateway; without it every request is "no host".
            if (experimentalX6)
            {
                if (!ExpansionX6NetCard.TryCompose(memory, scheduler, expansionRoot, x0, mappings, out x6))
                    throw new ArgumentException("X6 network resources cannot be composed.");
                observeX6?.Invoke(x6!);
            }
            mappings = ExpansionIoProfile.Compose(profile, memory, mappings, expansionRoot);
            if (profile.CpNet)
            {
                var cpnet = new CpNetX0Card(x0!);
                x0!.CpNet = cpnet;
                cpnetRuntime = new CpNetRuntime(scheduler, x0.Services, hostGate);
                x0.Services.RegisterService(CpNetX0Card.CardId, CpNetX0Card.ExchangeServiceId, cpnetRuntime.Submit);
                mappings = [.. mappings, .. cpnet.Mappings()];
            }
        }
        // The identity bank (decision hc2000-machine-identity-bank-v1): read-only, on every extended profile only,
        // naming the cards fitted above.
        if (IdentityRecord.FromProfile(profile, x2 is not null, x3 is not null, x4 is not null, x6 is not null) is { } identity)
        {
            mappings = [.. mappings, identity.Mapping()];
            var hostClock = new HostClockPort(hostCivilTime);
            mappings = [.. mappings, hostClock.Mapping()];
        }
        var ports = expansionRoot is null ? new PortDecoder(mappings) : new PortDecoder(mappings, expansionRoot, expansionRoot.MainOrigin);
        observePorts?.Invoke(ports);
        var machineBus = new HcMachineBus(memory, ports, adapter, scheduler);
        var timedBus = new CpuTimedBus(machineBus, adapter);
        cpu = new CpuZ80(timedBus);
        // The out parameter cannot be captured in the lambdas below; use the same instance via a local.
        var newCpu = cpu;
        timedBus.Attach(cpu);
        clock.InterruptChanged += (_, asserted) => newCpu.IntLine = asserted;
        // Only the opt-in C:-boot profile and the full workstation, which boots the same way, start at the CP/M ROM cold
        // entry. It is applied before the expansion frontier exists: the frontier driver prepares the main CPU's first
        // bus effect when it is composed, so a later PC would run the effect prepared for 0000.
        if (profile.Name is "hc2000-v2-hdd-cboot-v1" or "hc2000-v2-hdd-cpm3-v1" or "hc2000-v2-full-workstation-v1")
            ApplyCpmColdEntry(cpu, memory);
        var frontier = expansionRoot is null ? null : new ExpansionFrontierDriver(scheduler, clock, mapper, expansionRoot, memory, ports, cpu);
        if (frontier is not null) { frontier.X0 = x0; frontier.X2 = x2; frontier.X3 = x3; frontier.X4 = x4; frontier.Arena = expansionArena; }
        // The machine INT line is the OR of the frame clock's interrupt line and the row's interrupt
        // source (design contract 2): both contributors update the same line; the row's enable bit
        // gates only its own contribution, so with it disabled everything works by asking, as today.
        if (rowInterrupt is { } row) {
            var updateIntLine = () => { newCpu.IntLine = clock.IntAsserted || row.LineAsserted; };
            clock.InterruptChanged += (_, _) => updateIntLine();
            row.LineChanged += updateIntLine;
            updateIntLine();
        }
        if (x5)
        {
            var mmu = new ExpansionMmu(expansionArena!);
            expansionRoot!.Mmu = mmu;
            frontier!.Mmu = mmu;
            frontier.Arena = expansionArena;
        }

        // Authentic power-on: reset PC/latches, no RAM program, screen writes or ROM routine jump.
        var machine = new LiveMachine(scheduler, clock, adapter, ula,
            () => { hdd?.NotifyGuestInstruction(); newCpu.Step(); }, () => newCpu.TStates, frontier);
        // The machine owns its memory: disposing it hands the paged RAM back to the pool (hc2000-ram-one-buffer).
        machine.Own(memory);
        var tape = machine.EnableTape(200);
        tapeControl?.Attach(tape);
        if (profile.Name is ("hc2000-v2-paged-tape-v2" or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2"
            or "hc2000-v2-full-workstation-v1"))
            machine.EnableTapeVolume(profile, memory,
                // The four raw HDD volumes occupy drives 2-5, which the guest sees as C-F
                // (drive N = letter - 'A'); slotPresent only ever reports those letters.
                hdd is null ? null : letter => hdd.CaptureHdd((byte)(letter - 'A')).Present);
        // EnableTape owns the one real ULA MIC subscription; App adds only EAR observation.
        ula.TapeInputChanged += (tick, level) =>
        {
            if (canMonitor?.Invoke() == false) return;
            var view = tape.Deck.Observe();
            monitor(new(tick, level, view.Mode == TapeDeckMode.Playing && !view.Paused && view.HoldReason == TapeHoldReason.None));
        };
        machine.AttachSnapshotState(profile, clock, memory, mapper, new Z80SnapshotSeam(cpu), 2, floppy, snapshotPolicy, hdd);
        cpnetRuntime?.Bind(machine);
        if (cpnetRuntime is not null) observeCpNet?.Invoke(cpnetRuntime);
        return machine;
    }

    /// <summary>CP/M cold entry (hc2000-direct-c-boot): the CP/M ROM stub at 0xF800 under the CP/M
    /// memory state, exactly the seam the boot tests drive. Applied at composition, before the first
    /// instruction, and re-applied by the worker's cold factory on every reset. IM2 with I=FE; IFF
    /// left false: the ROM does DI at its entry (0xE021) and EI after its own setup. The IM2 vector
    /// the EI releases into is burned into the cboot ROM (58 EE at 0x1EFF/0x1F00 -> JP 0xEE58, the
    /// console/INT handler); the builder refuses a ROM without it. Applied only for the C:-boot
    /// identity and the full workstation; no faithful machine changes.</summary>
    private static void ApplyCpmColdEntry(CpuZ80 cpu, HcMemory memory)
    {
        memory.WritePort7E(0x0F);
        memory.SetCpm(true);
        // Registers is a struct: write through the live field, not a copy (a copied write is lost).
        cpu.R.IM = 2;
        cpu.R.I = 0xFE;
        cpu.R.IFF1 = false;
        cpu.R.IFF2 = false;
        cpu.R.SP = 0x7FFE;
        cpu.R.PC = 0xF800;
        cpu.R.BC = 0;
        cpu.R.DE = 0;
    }
}

/// <summary>Detached and re-verified machine image; only BasicMachineComposition can construct it.</summary>
public sealed class PreparedBasicMachine
{
    internal HddColdAdmission? ColdAdmission { get; }
    internal (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) Roms { get; }
    internal ImmutableArray<HddInitialMedia> InitialHdd { get; }
    internal HcsWavPolicy? SnapshotPolicy { get; }
    internal bool X2Vga { get; }
    internal bool X3Sound { get; }
    internal bool X4Math { get; }
    /// <summary>The X6 network card (hc2000-ssh-terminal), X5 only; its host side reaches out only through the
    /// machine's outbound gateway.</summary>
    internal bool X6Net { get; }
    internal PreparedBasicMachine((MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) roms,
        ImmutableArray<HddInitialMedia> initialHdd = default, HddColdAdmission? coldAdmission = null, HcsWavPolicy? snapshotPolicy = null,
        bool x2Vga = false, bool x3Sound = false, bool x4Math = false, bool x6Net = false)
    {
        ColdAdmission = coldAdmission;
        Roms = roms;
        X2Vga = x2Vga;
        X3Sound = x3Sound;
        X4Math = x4Math;
        X6Net = x6Net;
        InitialHdd = initialHdd.IsDefault ? [] : initialHdd;
        // Preserve the same trusted paged no-policy selection through every App factory.
        // An explicit caller policy still wins; nonpaged composition retains Default.
        SnapshotPolicy = snapshotPolicy ?? (roms.Profile.Memory.RamPaged is null ? HcsWavPolicy.Default : null);
    }
}

/// <summary>Cross-thread publication of the FDC composed on the emulation owner.
/// ReadData counts port 0x87 reads, not status polls.</summary>
internal sealed class ComposedFloppySlot
{
    private FloppyController? controller;
    private int readData;
    public FloppyController? Controller => Volatile.Read(ref controller);
    public int ReadData => Volatile.Read(ref readData);
    internal void Publish(FloppyController value) => Volatile.Write(ref controller, value);
    internal void NoteReadData() => Interlocked.Increment(ref readData);
}
