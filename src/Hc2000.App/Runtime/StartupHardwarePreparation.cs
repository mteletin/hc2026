using System.Collections.Immutable;
using Hc2000.App.Configuration;
using Hc2000.App.Media;
using Hc2000.Machine;

namespace Hc2000.App.Runtime;

/// <summary>Detached cold preparation only. Never owns a worker, live mount or settings save.
/// A named selection (a container volume recorded per slot) is admitted cold through the existing
/// HddColdVolume path; the HddPanel live named/admitted volumes retain their separate existing
/// preparation and owner-ACK path.</summary>
public static class StartupHardwarePreparation
{
    public static async Task<PreparedBasicMachine> PrepareAsync(
        (MachineProfile Profile, byte[] Basic, byte[] Cpm, byte[] If1) verifiedRoms,
        MachineConfiguration configuration, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        configuration.ValidateEffective();
        verifiedRoms.Profile.Validate();
        const string cpm3 = "hc2000-v2-hdd-cpm3-v1";
        if ((configuration.Profile == cpm3) != (verifiedRoms.Profile.Name == cpm3))
            throw new ArgumentException("The CP/M 3 profile requires its matching ROM map.", nameof(verifiedRoms));
        if (verifiedRoms.Profile.Name != MachineProfile.Hc2000V2.Name
            && verifiedRoms.Profile.Name != MachineProfile.Hc2000V2HddCbootV1.Name
            && verifiedRoms.Profile.Name != cpm3
            || verifiedRoms.Basic.Length != 0x4000 || verifiedRoms.Cpm.Length != 0x4000 || verifiedRoms.If1.Length != 0x4000)
            throw new ArgumentException("Cold Start requires the verified whole-chip v2 ROM map.", nameof(verifiedRoms));
        if (configuration.Profile == cpm3 && (!configuration.Hdd.Any(h => h.Slot == "C")
            || configuration.Hdd.Any(h => h.Volume is not null)))
            throw new IOException("CP/M 3 requires a prepared raw 8 MiB C: image and cannot use named volumes.");
        var x5Hdd = configuration.Profile == "hc2000-v2-paged-expansion-x5-hdd-guest-v1";
        var fullWorkstation = configuration.Profile == "hc2000-v2-full-workstation-v1";
        if (configuration.Profile is not ("hc2000-v2-hdd-v1" or "hc2000-v2-hdd-cboot-v1" or cpm3 or "hc2000-v2-combined-workstation-v1"
                or "hc2000-v2-combined-workstation-v2") && !x5Hdd && !fullWorkstation)
            return BasicMachineComposition.PrepareRamWholeChipV2(verifiedRoms, configuration.Profile, configuration.TextOverlay,
                configuration.CpuTurboMultiplier, configuration.ExpansionDriverCount, configuration.X2Vga, configuration.X3Sound, configuration.X4Math,
                configuration.X6Net, configuration.MainCpuTarget);
        // Each stored name keeps its own identity; a v1 draft never composes v2 and vice versa.
        var combined = configuration.Profile is "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2";

        // Cold profile lane (hc2000-hdd-context-mount-profile-lane): a named selection records a
        // container volume per slot and is admitted cold through the existing HddColdVolume path --
        // the same cold admission HddPanel uses; no third mount mechanism and no hot swap.
        var named = configuration.Hdd.Where(h => h.Volume is not null).ToList();
        if (named.Count > 0)
        {
            // The v2 workstation admits the installed named C:-F: set through the same cold admission as the raw HDD
            // machine (its guest-ACK driver is the one HDINST speaks); v1 keeps its raw-only HddV1 identity.
            if (configuration.Profile == "hc2000-v2-combined-workstation-v1")
                throw new IOException("The combined CP/M workstation v1 cold-starts from raw 8 MiB volumes; named container volumes need the v2 workstation.");
            if (configuration.Hdd.Any(h => h.Volume is null))
                throw new IOException("Named HDD volumes and raw 8 MiB slots cannot share one cold start.");
            if (configuration.DiskA is not null || configuration.DiskB is not null)
                throw new IOException("A named HDD cold volume cannot start with configured A/B disks; the single cold-read fence belongs to the disk reservation.");
            var containers = named.Select(h => Path.GetFullPath(h.Path)).Distinct(StringComparer.Ordinal).ToList();
            if (containers.Count != 1)
                throw new IOException("The named HDD selection maps one container to its cold slots.");
            var container = named[0].Path;
            var info = new FileInfo(container);
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("HDD must be an existing regular local file, not a link.");
            if (OperatingSystem.IsLinux())
            {
                var facts = LinuxFileFacts.Inspect(container);
                if (!facts.Known || !facts.Regular || facts.Link)
                    throw new IOException("HDD regular-file identity is unavailable.");
            }
            var requests = named.Select(h => new HddColdVolumeRequest((byte)(h.Slot[0] - 'A'), h.Volume!, null, !h.WriteProtected)).ToImmutableArray();
            using var volume = HddColdVolume.OpenMapped(container, requests);
            if (fullWorkstation)
                return BasicMachineComposition.PrepareAdmittedFullWorkstationMapWholeChipV2(verifiedRoms, volume,
                    configuration.ExpansionDriverCount, configuration.X2Vga, configuration.X3Sound, configuration.X6Net,
                    configuration.TextOverlay, configuration.MainCpuTarget);
            if (x5Hdd)
                return BasicMachineComposition.PrepareAdmittedX5HddMapWholeChipV2(verifiedRoms, volume,
                    configuration.TextOverlay, configuration.ExpansionDriverCount, configuration.X6Net, configuration.MainCpuTarget);
            return BasicMachineComposition.PrepareAdmittedHddMapWholeChipV2(verifiedRoms, volume, configuration.MainCpuTarget,
                combined ? MachineProfile.Hc2000V2CombinedWorkstationV2 : null);
        }

        var images = new List<HddInitialMedia>();
        foreach (var hdd in configuration.Hdd)
        {
            cancellation.ThrowIfCancellationRequested();
            var info = new FileInfo(hdd.Path);
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("HDD must be an existing regular local file, not a link.");
            if (OperatingSystem.IsLinux())
            {
                var facts = LinuxFileFacts.Inspect(hdd.Path);
                if (!facts.Known || !facts.Regular || facts.Link)
                    throw new IOException("HDD regular-file identity is unavailable.");
            }
            // Existing raw cold route: exact-size private bytes, never a named-container flattening,
            // writable host binding or live attach. Guest protection is retained independently.
            // The CP/M 3 ROM checks the C: boot payload; the host filename does not identify its contents.
            var bytes = await HddFiles.ReadAsync(hdd.Path, cancellation);
            images.Add(new((byte)(hdd.Slot[0] - 'A'), bytes, Path.GetFileName(hdd.Path), hdd.WriteProtected));
        }
        cancellation.ThrowIfCancellationRequested();
        if (fullWorkstation)
            return BasicMachineComposition.PrepareFullWorkstationWholeChipV2(verifiedRoms, images, configuration.ExpansionDriverCount,
                configuration.X2Vga, configuration.X3Sound, configuration.X6Net, configuration.TextOverlay, configuration.MainCpuTarget);
        if (x5Hdd)
            return BasicMachineComposition.PrepareX5HddWholeChipV2(verifiedRoms, images, configuration.TextOverlay,
                configuration.ExpansionDriverCount, configuration.X6Net, configuration.MainCpuTarget);
        // The raw HDD and C:-boot identities follow the ROM map (adopt: null); the combined names keep their own.
        return BasicMachineComposition.PrepareHddWholeChipV2(verifiedRoms, images, configuration.TextOverlay, configuration.CpuTurboMultiplier,
            configuration.Profile switch
            {
                "hc2000-v2-combined-workstation-v1" => MachineProfile.Hc2000V2CombinedWorkstationV1,
                "hc2000-v2-combined-workstation-v2" => MachineProfile.Hc2000V2CombinedWorkstationV2,
                _ => null
            }, configuration.MainCpuTarget);
    }
}
