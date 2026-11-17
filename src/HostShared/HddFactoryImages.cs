using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

/// <summary>Build-host factory generator; no guest boot files, host admission or machine authority.</summary>
public static class HddFactoryImages
{
    public const string ContainerName = "HC2000-HDD.hdd";
    public const string ReceiptName = "factory-hdd.sha256";
    /// <summary>Opt-in raw C: volume for the direct-C-boot profile; the curated factory container is unchanged.</summary>
    public const string CBootVolumeName = "HC2000-CBOOT-C.DSK";

    /// <summary>The curated prepared-C: manifest (hc2000-prepared-cpm-tools-image): shipped beside the
    /// container in the installed package so a fresh install supplies the image and its per-payload
    /// record without repository access.</summary>
    public const string CuratedManifestName = "factory-c-tools-manifest.json";

    /// <summary>The closed curated C: payload set: the 20 tools the utilities export places in user area 0.
    /// The set is closed - WriteCuratedManifest refuses a C: carrying anything else, and every tool stays
    /// in U0 so each advertised command remains a bare command (no user-area or fictitious C1: hiding).
    /// SUM.COM (hc2000-cpm-asm-tool-trial) is the first in-house Z80 utility, assembled by Hc2000.Asm;
    /// X2INST.COM (hc2000-cpm-x2-devices) installs the X2 VGA console and printer devices; NETX6.COM, NETECHO.COM
    /// and SSH.COM (hc2000-ssh-terminal) start the X6 network card's driver and use it.</summary>
    public static readonly string[] CuratedNames = ["CCP.COM", "CURL.COM", "D.COM", "DDT.COM", "DRVSEL.COM", "FOLDERS.COM", "HDFMT.COM", "LOAD.COM", "MBASIC.COM", "MFMT.COM", "MKDIR.COM", "NETECHO.COM", "NETX6.COM", "PIP.COM", "RAMSTAT.COM", "RMDIR.COM", "SHELL.COM", "SSH.COM", "STAT.COM", "SUBMIT.COM", "SUM.COM", "SYSINFO.COM", "TAPEINIT.COM", "TIME.COM", "X2INST.COM", "XSUB.COM"];
    public const string Provenance = "hc2000-cpm-utilities-provenance-v1";

    /// <summary>Append-only physical packing (hc2000-sysinfo-utilities-disk): names keep their
    /// ordinal order, the added SYSINFO.COM follows all the legacy tools, the folder tools
    /// (hc2000-cpm-user-folders) follow it, and the proving tools (hc2000-cpm22-proving: the tape
    /// archiver, the standalone HDD pair and the drive-stress programs) come last, so the original 19
    /// never move a directory record or allocation. Manifest and list sorting stay ordinal,
    /// separate from this packing.</summary>
    public static IEnumerable<TSource> CuratedPacking<TSource>(this IEnumerable<TSource> source, Func<TSource, string> name)
        => source.OrderBy(item => name(item) switch
            {
                "SYSINFO.COM" => 1,
                "FOLDERS.COM" or "MKDIR.COM" or "RMDIR.COM" or "CCP.COM" or "SHELL.COM" => 2,
                "ARCH.COM" or "HDINST.COM" or "HDSWAP.COM" or "BIGFILE.COM" or "DIRLOAD.COM" or "USERS.COM" or "COPYCHK.COM"
                    or "RANDOMIO.COM" => 3,
                _ => 0
            })
            .ThenBy(name, StringComparer.Ordinal);

    /// <summary>Which currently qualified profile/bootstrap a payload needs - honest staging, not a blanket promise.</summary>
    public static string CuratedCompatibility(string name) => name switch
    {
        "HDFMT.COM" or "DRVSEL.COM" => "hc2000-v2-hdd-v1 profile; the HDINST resident must be installed first (run HDINST from the resident floppy CPM22-HC-HDD.DSK) before C-F answer",
        "RAMSTAT.COM" or "MFMT.COM" => "hc2000-v2-paged-m-v1 profile; reports or formats the M: RAM drive",
        "TAPEINIT.COM" => "hc2000-v2-paged-m-v1 profile with a configured tape deck; installs the tape resident, which does not autostart",
        "NETX6.COM" or "NETECHO.COM" => "hc2000-v2-paged-expansion-x5-v1 profile with two driver Z80s and the X6 network card; NETX6 starts the card's driver (run it before NETECHO), the gateway must be switched on in the App, and each refuses on any other machine",
        "SSH.COM" => "hc2000-v2-paged-expansion-x5-v1 profile with two driver Z80s and the X6 network card; starts the card's driver itself, connects the App's default connection when run alone, shares A: and B: with the remote host (SSH /S), needs the gateway switched on in the App, and refuses on any other machine",
        "CURL.COM" => "hc2000-v2-paged-expansion-x5-v1 profile with two driver Z80s and the X6 network card; starts the driver itself, needs the gateway switched on, and /I preserves URL case",
        "X2INST.COM" => "hc2000-v2-paged-expansion-x5-v1 profile with the X2 VGA card; installs the resident that makes CON:=UC1: and LST:=UL1: the VGA screen, and refuses on any other machine",
        "SYSINFO.COM" => "CP/M 2.2; reports available system and HC2000 identity information",
        "TIME.COM" => "CP/M 2.2; reads host date/time on extended machines, handles absence on original",
        "FOLDERS.COM" or "MKDIR.COM" or "RMDIR.COM" => "an extended HC2000 booted from C: with the folder shell (SHELL.SYS); shows and changes the drive's folders, and refuses without the shell",
        "CCP.COM" or "SHELL.COM" => "an extended HC2000 booted from C:; switches between the folder shell and the classic CCP from the next boot on",
        _ => "stock CP/M 2.2; any profile that boots CP/M 2.2"
    };


    /// <summary>Writes the factory container and receipt, returning the container path.
    /// Optional exported utilities populate C: through the HDD formatter, never the M: DPB.
    /// The CLI retains its empty D:/E:/F: producer when no content is supplied.</summary>
    public static string[] GenerateInto(string outputDirectory, bool overwrite = false,
        IReadOnlyDictionary<string, byte[]>? cFiles = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.GetFullPath(Path.Combine(outputDirectory, ContainerName));
        var receiptPath = Path.GetFullPath(Path.Combine(outputDirectory, ReceiptName));
        if (!overwrite && (File.Exists(path) || File.Exists(receiptPath)))
            throw new IOException($"Refusing to replace existing factory image or receipt in {outputDirectory}; remove it first.");
        var temporary = path + ".hc-factory-" + Guid.NewGuid().ToString("N");
        var temporaryReceipt = temporary + ".sha256";
        // Identity is fixed for the reproducible factory artifact, not minted at build time.
        var id = Convert.FromHexString("484332303030464143544F5259444546");
        var entries = ImmutableArray.CreateBuilder<HddCatalogEntry>();
        var receipt = new StringBuilder();
        try
        {
            byte[]? cVolume = null;
            if (cFiles is not null)
            {
                if (cFiles.Count == 0) throw new InvalidDataException("Factory C: requires utilities content.");
                var fs = CpmFs.OpenHddVolume(CpmFs.FormatHddVolume());
                // Only the exact curated set uses the legacy 19 followed by SYSINFO. Arbitrary
                // factory content retains ordinal packing, including content containing SYSINFO.
                var curated = cFiles.Count == CuratedNames.Length &&
                    cFiles.Keys.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(CuratedNames, StringComparer.Ordinal);
                IEnumerable<KeyValuePair<string, byte[]>> ordered = curated
                    ? cFiles.CuratedPacking(file => file.Key)
                    : cFiles.OrderBy(file => file.Key, StringComparer.Ordinal);
                foreach (var file in ordered)
                    fs.Put(file.Key, file.Value);
                cVolume = fs.ExportVolumeBytes();
            }
            using (var container = File.Create(temporary))
            {
                foreach (var slot in cVolume is null ? new[] { "D", "E", "F" } : new[] { "C", "D", "E", "F" })
                {
                    // HDD DPB: no floppy skew or system tracks; never zero-filled `hdd create`.
                    var bytes = slot == "C" ? cVolume! : CpmFs.FormatHddVolume();
                    var name = slot + ".DSK";
                    entries.Add(new(name, false, (uint)bytes.Length, new((uint)entries.Count), VolumeCatalogMetadata.Crc32(bytes)));
                    container.Write(bytes);
                    receipt.Append(Hash(bytes)).Append("  ").Append(ContainerName).Append('#').Append(name).Append('\n');
                }
                var metadata = HddCatalogCodec.EncodeHdd(id, 1, entries.ToImmutable(), new(40 * 1024 * 1024, 4, 4096, 8192));
                if (!metadata.Success) throw new InvalidOperationException(metadata.Failure!.Detail);
                container.Write(metadata.Value!.Index.AsSpan());
                container.Write(metadata.Value.Footer.AsSpan());
            }
            using (var container = File.OpenRead(temporary))
                receipt.Append(Convert.ToHexString(SHA256.HashData(container)).ToLowerInvariant()).Append("  ").Append(ContainerName).Append('\n');
            File.WriteAllText(temporaryReceipt, receipt.ToString(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite);
            File.Move(temporaryReceipt, receiptPath, overwrite);
            return [path];
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(temporaryReceipt)) File.Delete(temporaryReceipt);
        }
    }

    /// <summary>Create a separate bootable raw C: volume from the already-generated curated C: bytes.
    /// The input remains unchanged; this does not edit a user's selected medium. With the folder shell
    /// (hc2000-cpm-user-folders), SHELL.SYS goes beside the boot image as a system file in user 0: the C: boot
    /// brings it in, and CCP.COM / SHELL.COM switch it off and on.</summary>
    public static byte[] CreateCBootVolume(ReadOnlySpan<byte> curatedVolume, byte[] bootImage, byte[]? shell = null)
    {
        ArgumentNullException.ThrowIfNull(bootImage);
        CBootImages.Verify(bootImage);
        var fs = CpmFs.OpenHddVolume(curatedVolume);
        if (fs.List().Any(file => string.Equals(file.Name, CBootImages.AssetName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The curated source already contains a boot image.");
        fs.Put(CBootImages.AssetName, bootImage);
        if (shell is not null)
        {
            fs.Put(ShellName, shell, system: true);
            // The folder shell finds commands on its path, so the tools leave /: the system tools go to /bin and the
            // ones that format, install or configure hardware to /sbin.
            foreach (var (name, user) in FolderLayout) fs.MoveToUser(name, 0, user);
        }
        return fs.ExportVolumeBytes();
    }

    /// <summary>Where the curated tools live on the C:-boot C: (hc2000-cpm-user-folders): user area 1 is /bin and 5 is
    /// /sbin in the folder shell's default map. BOOTIMG.000 and SHELL.SYS stay in / for the boot, and CCP.COM and
    /// SHELL.COM stay there too, so the classic CCP, which has no path, can still switch back.</summary>
    public static readonly IReadOnlyList<(string Name, int User)> FolderLayout =
    [
        ("CURL.COM", 1), ("D.COM", 1), ("DDT.COM", 1), ("FOLDERS.COM", 1), ("LOAD.COM", 1), ("MBASIC.COM", 1),
        ("MKDIR.COM", 1), ("NETECHO.COM", 1), ("PIP.COM", 1), ("RAMSTAT.COM", 1), ("RMDIR.COM", 1), ("SSH.COM", 1),
        ("STAT.COM", 1), ("SUBMIT.COM", 1), ("SUM.COM", 1), ("SYSINFO.COM", 1), ("TIME.COM", 1), ("XSUB.COM", 1),
        ("DRVSEL.COM", 5), ("HDFMT.COM", 5), ("MFMT.COM", 5), ("NETX6.COM", 5), ("TAPEINIT.COM", 5), ("X2INST.COM", 5),
    ];

    /// <summary>The folder shell's file on the bootable C: (disks/cpm/SHELL.SYS, built from
    /// src-library/extended/bios-upgrade/folders/folders.resident.asm).</summary>
    public const string ShellName = "SHELL.SYS";
    public const string ShellAssetPath = "disks/cpm/SHELL.SYS";

    /// <summary>The full workstation's disks (hc2000-full-workstation-default): a container like the factory one,
    /// whose C: is the curated C: with the workstation's own boot image (the paged-drive switch,
    /// disks/cpm/BOOTIMG-WS.000, from src-library/extended/bios-upgrade/workstation) and its folder shell above the
    /// TPA (disks/cpm/SHELL-WS.SYS), under the names the C: boot reads, BOOTIMG.000 and SHELL.SYS; D:, E: and F:
    /// are empty. The default machine takes C:-F: from it as named volumes, as the combined workstation does.</summary>
    public const string WorkstationContainerName = "HC2000-WORKSTATION.hdd";
    public const string WorkstationBootAssetPath = "disks/cpm/BOOTIMG-WS.000";
    public const string WorkstationShellAssetPath = "disks/cpm/SHELL-WS.SYS";
    /// <summary>System v2's paged BDOS (hc2000-v2-system), BDOS.SYS on the workstation's C:.</summary>
    public const string WorkstationBdosAssetPath = "disks/cpm/BDOS-WS.SYS";
    public const string BdosName = "BDOS.SYS";

    public static byte[] CreateWorkstationContainer(ReadOnlySpan<byte> curatedVolume, byte[] bootImage, byte[] shell, byte[] bdos,
        Action<IReadOnlyDictionary<string, CpmFs>>? fill = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(bdos);
        var withBdos = CpmFs.OpenHddVolume(CreateCBootVolume(curatedVolume, bootImage, shell));
        withBdos.Put(BdosName, bdos, system: true);
        // The prepared drives' contents (disk inventory and folders: WorkstationLayout.Apply) go on after the system.
        var volumes = new Dictionary<string, CpmFs>(StringComparer.Ordinal) { ["C"] = withBdos };
        foreach (var slot in new[] { "D", "E", "F" }) volumes[slot] = CpmFs.OpenHddVolume(CpmFs.FormatHddVolume());
        fill?.Invoke(volumes);
        // A fixed identity of its own, "HC2000WORKSTATIO", as the factory container has "HC2000FACTORYDEF".
        var id = Convert.FromHexString("484332303030574F524B53544154494F");
        var entries = ImmutableArray.CreateBuilder<HddCatalogEntry>();
        using var container = new MemoryStream();
        foreach (var slot in new[] { "C", "D", "E", "F" })
        {
            var bytes = volumes[slot].ExportVolumeBytes();
            entries.Add(new(slot + ".DSK", false, (uint)bytes.Length, new((uint)entries.Count), VolumeCatalogMetadata.Crc32(bytes)));
            container.Write(bytes);
        }
        var metadata = HddCatalogCodec.EncodeHdd(id, 1, entries.ToImmutable(), new(40 * 1024 * 1024, 4, 4096, 8192));
        if (!metadata.Success) throw new InvalidOperationException(metadata.Failure!.Detail);
        container.Write(metadata.Value!.Index.AsSpan());
        container.Write(metadata.Value.Footer.AsSpan());
        return container.ToArray();
    }

    /// <summary>Writes the curated prepared-C: manifest for the factory container, refusing a container that
    /// does not carry the exact curated set in user area 0 on C: with empty formatted D:/E:/F: work volumes.
    /// <paramref name="sources"/> maps each curated guest name to the utilities export manifest's source for it
    /// (the vendored image path or the repo assembly source); the provenance is derived from that path prefix.
    /// The output is deterministic: identical inputs give identical manifest bytes.</summary>
    public static string WriteCuratedManifest(string outputDirectory, string containerPath, string sourceCommit, IReadOnlyDictionary<string, string> sources)
    {
        if (sourceCommit.Length != 40 || sourceCommit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("A full source commit is required for the curated C: manifest.");
        foreach (var name in CuratedNames)
            if (!sources.TryGetValue(name, out var source) || string.IsNullOrEmpty(source))
                throw new InvalidDataException("Curated C: manifest is missing the source of " + name + ".");
        var bytes = File.ReadAllBytes(Path.GetFullPath(containerPath));
        const int volumeBytes = 8 * 1024 * 1024;
        var limits = new HddLimits(40 * 1024 * 1024, 4, 4096, 8192);
        var catalog = HddCatalogCodec.DecodeHdd(bytes.AsSpan(bytes.Length - 64), bytes.AsSpan(4 * volumeBytes, bytes.Length - 4 * volumeBytes - 64), (ulong)bytes.Length, limits);
        if (!catalog.Success) throw new InvalidDataException("Curated C: manifest cannot decode the container: " + catalog.Failure!.Detail);
        if (!catalog.Value.Entries.Select(entry => entry.Name).SequenceEqual(new[] { "C.DSK", "D.DSK", "E.DSK", "F.DSK" }, StringComparer.Ordinal))
            throw new InvalidDataException("Curated C: manifest requires a four-volume factory container C-F.");
        var volume = CpmFs.OpenHddVolume(bytes.AsSpan(0, volumeBytes));
        var listed = volume.List().ToList();
        if (!listed.Select(file => file.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(CuratedNames, StringComparer.Ordinal))
            throw new InvalidDataException("Curated C: manifest: C: does not carry exactly the curated tool set.");
        foreach (var file in listed)
            if (file.User != 0)
                throw new InvalidDataException("Curated C: manifest: " + file.Name + " is not in user area 0; advertised commands must not be hidden by user-area placement.");
        var emptyVolume = CpmFs.FormatHddVolume();
        var payloads = listed.OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file =>
            {
                var data = volume.Get(file.Name);
                return new CuratedPayloadRecord(file.Name, file.User, data.Length, Hash(data), sources[file.Name], ProvenanceOf(sources[file.Name], sourceCommit), CuratedCompatibility(file.Name));
            }).ToList();
        var volumes = new List<CuratedVolume> { new("C", "C.DSK", "curated tools", Hash(bytes.AsSpan(0, volumeBytes).ToArray()), payloads) };
        foreach (var slot in new[] { "D", "E", "F" })
            volumes.Add(new(slot, slot + ".DSK", "empty formatted work volume", SlotHash(bytes, slot, volumeBytes, emptyVolume), null));
        var manifest = JsonSerializer.Serialize(new
        {
            artifact = "hc2000-prepared-c-tools-image-v1",
            provenance = Provenance,
            sourceCommit,
            machine = new
            {
                profile = "hc2000-v2-hdd-v1",
                bootstrap = "Boot CP/M 2.2 from floppy A: (the bundled CPM22-HC-HDD.DSK or CPM22-HC-ram-paged-upgrade.DSK), then run HDINST to install the HDD resident; C-F answer only after that resident is in",
                notClaimed = new[]
                {
                    "direct C: (floppy-free) boot",
                    "combined-machine operation",
                    "network or VGA drivers",
                    "HELP, TAPESTAT and Z80JOB commands (proposed, not implemented)"
                }
            },
            userAreas = "Every curated tool is in user area 0, so each advertised command is a bare command at USER 0; U1-U31 hold user files only and carry no shipped payload",
            container = new { name = ContainerName, size = bytes.Length, sha256 = Hash(bytes) },
            volumes
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }) + "\n";
        var path = Path.Combine(Path.GetFullPath(outputDirectory), CuratedManifestName);
        File.WriteAllText(path, manifest, new UTF8Encoding(false));
        return path;
    }

    private sealed record CuratedPayloadRecord(string Name, int User, int Size, string Sha256, string Source, string Provenance, string Compatibility);
    private sealed record CuratedVolume(string Slot, string Name, string Role, string Sha256, IReadOnlyList<CuratedPayloadRecord>? Payloads);

    private static string ProvenanceOf(string source, string sourceCommit) =>
        (source.StartsWith("disks/cpm/", StringComparison.Ordinal)
            ? $"repo-vendored from {source}, byte-pinned by size and sha256"
            : $"assembled at build time from {source} at source commit {sourceCommit}")
        + $" (decision {Provenance})";

    private static string SlotHash(byte[] container, string slot, int volumeBytes, byte[] expectedEmpty)
    {
        var index = "DEF".IndexOf(slot[0]) + 1;
        var volume = container.AsSpan(index * volumeBytes, volumeBytes);
        if (!volume.SequenceEqual(expectedEmpty))
            throw new InvalidDataException($"Curated C: manifest: {slot}: is not the empty formatted work volume.");
        return Hash(volume.ToArray());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
