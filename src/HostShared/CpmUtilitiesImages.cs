using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hc2000.Asm;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

/// <summary>Build-host producer of the CP/M utilities disk images: the images are produced once on the
/// build host and shipped as fixed byte content.
/// Content exports are independent of the media DPB, for the existing C: factory integration.</summary>
public static class CpmUtilitiesImages
{
    public const string FloppyName = "HC2000-UTIL.DSK";
    public const string VolumeName = "HC2000-UTIL-8M.DSK";
    public const string BootName = "CPM22-HC-ram-paged-upgrade.DSK";
    public const string ManifestName = "utilities-manifest.json";
    public const string ContentDirectory = "utilities-content";
    public const string BootHash = "fc36f11ab405d41f3d700ca7e269cd2057fc16c2306e4b7438396743d714e085";
    /// <summary>The hard-drive CP/M floppy shipped beside the canonical image, refreshed from the
    /// demo #109 image with the current HDINST resident. Pinned by size and hash like every other input: a changed or missing image fails the
    /// build, it never ships silently (hc2000-hdd-resident-floppy-asset).</summary>
    public const string HddResidentImage = "disks/cpm/CPM22-HC-HDD.DSK";
    public const int HddResidentSize = 778496;
    public const string HddResidentHash = "decf650c04b03e45fb04dbcdca4033883355e5aee5a1bd8a77b2b2eb958b2427";
    private const string BaseImage = "disks/cpm/CPM22-HC.DSK";
    private const string Z80Image = "disks/cpm/CPMZ80.DSK";
    private const string Firmware = "src-library/extended/bios-upgrade/paging/ram.paged.asm";
    private const string Builder = "tools/UpgradeBiosBuilder/Program.cs";
    private const string Rom = "roms/hc2000/eprom-dump-hc2k-2/M27C512.bin";
    private static readonly LibraryTarget Extended = LibraryTargets.Find("hc2000-v2")!;

    public sealed record VendoredTool(string Name, string Source, int Size, string Sha256);
    public static IReadOnlyList<VendoredTool> VendoredTools => new VendoredTool[]
    {
        new("PIP.COM", BaseImage, 7424, "7f9e12a92e2bcfd814b5b680a2f7d5c2a2c50c9a5ef94a6891dcaa3527f08ec2"),
        new("STAT.COM", BaseImage, 5248, "614d0b1d66466177e5b2bf585251d53d1e24eda34ada136b4ae178ab5944dc73"),
        new("D.COM", BaseImage, 4608, "fb8e4d11948e9b8c068f72a6527e9fb0b5264597fc94873fe85408a999d2767a"),
        new("LOAD.COM", BaseImage, 1792, "c885d061a5dcb3830ab1c29a13e34d3c0560cc3faf569bc55b195c0330a9cbd6"),
        new("SUBMIT.COM", BaseImage, 1280, "4c3fec22ebca595b03c279eb21b19aa4b2dab139741ed29172ca6fa4c43e7066"),
        new("XSUB.COM", BaseImage, 768, "817a16b595ea6de8df0dd5e808f89a49e85dea546673283d759d01e960598bed"),
        new("DDT.COM", Z80Image, 4864, "bc4b0f085f8d1e37caf2a13850c70afa07acb626b22b1a7cb63368b3334dd448"),
        new("MBASIC.COM", BaseImage, 24320, "e6412bb1d8fcb2638b169dcde79741a4346975dae2de46272ce394d233ee2ada")
    };

    public static void GenerateInto(string root, string output, string bootDirectory, string sourceCommit)
    {
        if (sourceCommit.Length != 40 || sourceCommit.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("A full source commit is required for the utilities manifest.");
        var inputs = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        byte[] Read(string path) => inputs[path] = File.ReadAllBytes(Path.Combine(root, path));
        var baseBytes = Read(BaseImage);
        Verify(BaseImage, baseBytes, 778496, "ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008");
        var hddResidentBytes = Read(HddResidentImage);
        Verify(HddResidentImage, hddResidentBytes, HddResidentSize, HddResidentHash);
        var z80Bytes = Read(Z80Image);
        Verify(Z80Image, z80Bytes, null, "66b8e4dedc08ddd872607efaecd2c2a4bf4ec8cb3869e7f3e468f966ccf12b87");
        Verify(Rom, Read(Rom), 65536, "83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b");
        Verify(Firmware, Read(Firmware), null, "4e087f5474273dc76d770b0dc4e16ee72362f16a45f73fab9a4d7bcfafc8b381");
        Verify(Builder, Read(Builder), null, "3714a75809a0bc7b6d8e8422cbcccad11b2752c0cc75f699a9229f534e1930cb");
        Verify("ram.paged.bin", File.ReadAllBytes(Path.Combine(bootDirectory, "ram.paged.bin")), 3072,
            "9f359ef5082394bd527230d8f0e21e78fc59666b79b5cc68e9804e6e9f0daa21");
        var boot = File.ReadAllBytes(Path.Combine(bootDirectory, BootName));
        Verify(BootName, boot, 778496, BootHash);
        var fit = File.ReadAllText(Path.Combine(bootDirectory, "source-fit.txt"));
        foreach (var hash in new[] { Hash(inputs[Firmware]), Hash(inputs[Builder]), Hash(baseBytes), Hash(inputs[Rom]), BootHash })
            if (!fit.Contains(hash, StringComparison.Ordinal))
                throw new InvalidDataException("M v1 producer receipt does not name verified input/output " + hash);

        var images = new Dictionary<string, CpmFs>
        {
            [BaseImage] = CpmFs.Open(Dsk.Load(baseBytes)),
            [Z80Image] = CpmFs.Open(Dsk.Load(z80Bytes))
        };
        var files = new SortedDictionary<string, (string Source, byte[] Bytes)>(StringComparer.Ordinal);
        foreach (var tool in VendoredTools)
        {
            var bytes = images[tool.Source].Get(tool.Name);
            Verify(tool.Name, bytes, tool.Size, tool.Sha256);
            files.Add(tool.Name, (tool.Source, bytes));
        }
        foreach (var (name, source) in new[] { ("TAPEINIT", "tape.init"), ("MFMT", "mfmt"), ("HDFMT", "hdfmt"), ("DRVSEL", "drvsel"), ("RAMSTAT", "ramstat"), ("SUM", "sum"), ("XJOB", "xjob"), ("X2INST", "x2.init"), ("ARCH", "arch"), ("NETX6", "x6.init"), ("NETECHO", "netecho"), ("SSH", "ssh"), ("CURL", "curl"), ("TIME", "time"), ("SYSINFO", "sysinfo"),
            ("FOLDERS", "folders"), ("MKDIR", "mkdir"), ("RMDIR", "rmdir"), ("CCP", "ccp"), ("SHELL", "shell") })
        {
            // Installers live with their drivers, X6 programs with NETX6, and ARCH in the BIOS tree.
            var path = source == "tape.init" ? "src-library/extended/bios-upgrade/tape/tape.init.asm"
                : source == "x2.init" ? "src-library/extended/bios-upgrade/x2/x2.init.asm"
                : source == "arch" ? "bios/src/hc2000/upgrade/arch.asm"
                : source is "x6.init" or "netecho" or "ssh" or "curl" ? $"src-library/extended/bios-upgrade/x6/{source}.asm"
                : $"src-library/extended/tools/{source}/{source}.asm";
            // Built as hc build builds them for the extended machine: the shared libraries and targets.inc on the
            // search path (decision hc2000-asm-library-build-v1).
            var file = Path.Combine(root, path);
            var assembly = Assembler.AssembleText(Encoding.UTF8.GetString(Read(path)), LibraryTargets.Options(file, Extended), file);
            if (!assembly.Success)
                throw new InvalidDataException($"{path}: {string.Join(';', assembly.Diagnostics)}");
            var bytes = assembly.ToBinary();
            if (assembly.Segments.Min(s => s.Address) != 0x100 || bytes.Length > 0x5f00)
                throw new InvalidDataException(path + " is not a bounded CP/M COM.");
            files.Add(name + ".COM", (path, bytes));
        }
        // XJOB embeds the bounded byte-mailbox responder: prove it is side.asm byte-exact.
        const string xjobPath = "src-library/extended/tools/xjob/xjob.asm";
        var xjobAssembly = Assembler.AssembleText(Encoding.UTF8.GetString(Read(xjobPath)), file: Path.Combine(root, xjobPath));
        var sideAssembly = Assembler.AssembleText(Encoding.UTF8.GetString(Read("examples/expansion-mailbox/side.asm")), file: Path.Combine(root, "examples/expansion-mailbox/side.asm"));
        if (!xjobAssembly.Success || !sideAssembly.Success)
            throw new InvalidDataException("XJOB responder receipt failed to assemble.");
        var sideBytes = sideAssembly.ToBinary();
        var xjobBinary = xjobAssembly.ToBinary();
        var sideOffset = xjobAssembly.Symbols["side_start"] - 0x100;
        if (sideOffset < 0 || sideOffset + sideBytes.Length > xjobBinary.Length || !xjobBinary.AsSpan(sideOffset, sideBytes.Length).SequenceEqual(sideBytes))
            throw new InvalidDataException("XJOB.COM does not embed the verified side.asm responder byte-exact.");
        // The standalone HDD utilities are a matched pair from the same resident sources.
        foreach (var (name, path) in new[] {
            ("HDINST.COM", "bios/src/hc2000/upgrade/hdd.init.asm"),
            ("HDSWAP.COM", "bios/src/hc2000/upgrade/hdd.swap.asm") })
        {
            var assembly = Assembler.AssembleText(Encoding.UTF8.GetString(Read(path)), file: Path.Combine(root, path));
            if (!assembly.Success)
                throw new InvalidDataException($"{path}: {string.Join(';', assembly.Diagnostics)}");
            var bytes = assembly.ToBinary();
            if (assembly.Segments.Min(s => s.Address) != 0x100 || bytes.Length > 0x9700)
                throw new InvalidDataException(path + " is not a bounded CP/M COM.");
            files.Add(name, (path, bytes));
        }
        Read("bios/src/hc2000/upgrade/hdd.layout.inc");
        Read("bios/src/hc2000/upgrade/hdd.resident.asm");
        // The five drive-stress proving tools build from the stress sources.
        foreach (var (name, path) in new[] {
            ("BIGFILE", "software/tools/cpm-stress/bigfile.asm"), ("DIRLOAD", "software/tools/cpm-stress/dirload.asm"),
            ("USERS", "software/tools/cpm-stress/users.asm"), ("COPYCHK", "software/tools/cpm-stress/copychk.asm"),
            ("RANDOMIO", "software/tools/cpm-stress/randomio.asm") })
        {
            var assembly = Assembler.AssembleText(Encoding.UTF8.GetString(Read(path)), file: Path.Combine(root, path));
            if (!assembly.Success)
                throw new InvalidDataException($"{path}: {string.Join(';', assembly.Diagnostics)}");
            var bytes = assembly.ToBinary();
            if (assembly.Segments.Min(s => s.Address) != 0x100 || bytes.Length > 0x5f00)
                throw new InvalidDataException(path + " is not a bounded CP/M COM.");
            files.Add(name + ".COM", (path, bytes));
        }
        // Included by TAPEINIT, X2INST and the X6 programs; record all their source dependencies too.
        Read("src-library/extended/bios-upgrade/dispatch.sites.inc");
        Read("src-library/extended/bios-upgrade/tape/tape.layout.inc");
        Read("src-library/extended/bios-upgrade/tape/tape.resident.asm");
        Read("src-library/extended/bios-upgrade/x2/x2.layout.inc");
        Read("src-library/extended/bios-upgrade/x2/x2.resident.asm");
        Read("src-library/shared/machineid/1/machineid.inc");
        // The folder tools (hc2000-cpm-user-folders) share their bodies and the shell's header layout.
        Read("src-library/extended/tools/folders/folders.tool.inc");
        Read("src-library/extended/tools/shell/shell.tool.inc");
        Read("src-library/extended/bios-upgrade/folders/folders.layout.inc");
        Read("src-library/shared/time/1/time.inc");
        foreach (var library in new[] { "bdos/1/bdos.inc", "console/1/console.inc", "disk/1/disk.inc", $"targets/{Extended.Name}/targets.inc" })
            Read("src-library/shared/" + library);
        Read("src-library/extended/bios-upgrade/x6/x6.client.inc");
        Read("src-library/extended/bios-upgrade/x6/x6.driver.asm");
        Read("src-library/extended/bios-upgrade/x6/x6.start.inc");
        Read("src-library/extended/bios-upgrade/x6/curl.args.inc");
        Read("src-library/extended/bios-upgrade/x6/curl.disk.inc");
        Read("src-library/extended/bios-upgrade/x6/curl.http.inc");
        Read("src-library/extended/bios-upgrade/x6/curl.net.inc");
        Read("src-library/extended/bios-upgrade/x6/curl.redirect.inc");
        Read("src-library/extended/bios-upgrade/x6/curl.url.inc");
        var floppy = CpmFs.Open(Dsk.Load(MediaBlanks.Floppy720()));
        var volume = CpmFs.FormatMVolumeV2();
        // Append-only physical packing (hc2000-sysinfo-utilities-disk): the legacy 19 tools keep
        // their ordinal order, the added SYSINFO.COM follows all of them and the folder tools
        // (hc2000-cpm-user-folders) follow it, the policy the curated-C producer applies too
        // (HddFactoryImages.CuratedPacking); manifest and list sorting stay ordinal, separate from
        // this packing.
        foreach (var (name, file) in files.CuratedPacking(pair => pair.Key))
        {
            floppy.Put(name, file.Bytes);
            volume.Put(name, file.Bytes);
        }
        var outputs = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [FloppyName] = floppy.ImageBytes(),
            [VolumeName] = volume.ExportVolumeBytes(),
            [BootName] = boot
        };
        // The content export is exactly the closed curated C: set (hc2000-prepared-cpm-tools-image):
        // the factory container's C: volume is built from it and its shipped manifest refuses
        // anything else. The utilities media may additionally carry tools the curated set has
        // not adopted (XJOB, and the proving tools of hc2000-cpm22-proving): they ship on the disk,
        // never in the export.
        var exported = files.Where(pair => HddFactoryImages.CuratedNames.Contains(pair.Key)).ToList();
        if (exported.Count != HddFactoryImages.CuratedNames.Length)
            throw new InvalidDataException("The utilities export must carry every curated C: payload.");
        var exportedNames = exported.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var manifest = JsonSerializer.Serialize(new
        {
            provenance = "hc2000-cpm-utilities-provenance-v1",
            sourceCommit,
            bootProfile = "hc2000-v2-paged-m-v1", // never the M v2 identity
            floppyDpb = floppy.Parameters,
            volumeDpb = volume.Parameters,
            inputs = inputs.Select(pair => new { path = pair.Key, size = pair.Value.Length, sha256 = Hash(pair.Value) }),
            files = exported.Select(pair => new
            {
                name = pair.Key, source = pair.Value.Source, size = pair.Value.Bytes.Length, sha256 = Hash(pair.Value.Bytes),
                recordSize = floppy.Get(pair.Key).Length, recordSha256 = Hash(floppy.Get(pair.Key))
            }),
            outputs = outputs.Select(pair => new { name = pair.Key, size = pair.Value.Length, sha256 = Hash(pair.Value) })
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        // Validate everything before touching the output. Never leave stale/unlisted
        // COMs in the export consumed by the existing C: integration.
        var content = Path.Combine(output, ContentDirectory);
        if (Directory.Exists(content) && Directory.EnumerateFileSystemEntries(content).Any(path => !File.Exists(path) || !exportedNames.Contains(Path.GetFileName(path))))
            throw new InvalidDataException("Utilities content export contains an unlisted artifact.");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(content);
        foreach (var (name, bytes) in outputs)
            File.WriteAllBytes(Path.Combine(output, name), bytes);
        foreach (var pair in exported)
            File.WriteAllBytes(Path.Combine(output, ContentDirectory, pair.Key), pair.Value.Bytes);
        // Normalize absolute producer paths so independent builds have identical receipts.
        File.WriteAllText(Path.Combine(output, "m-v1-source-fit.txt"), fit.Replace(Path.GetFullPath(root) + Path.DirectorySeparatorChar, "", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(output, ManifestName), manifest);
    }

    private static void Verify(string name, byte[] bytes, int? size, string sha256)
    {
        if ((size.HasValue && size.Value != bytes.Length) || Hash(bytes) != sha256)
            throw new InvalidDataException("Utilities provenance mismatch: " + name);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
