using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hc2000.Asm;
using Hc2000.Machine.Storage;

namespace Hc2000.HostFiles;

/// <summary>Build-host producer. Provenance: KB hc2000-cpm-utilities-provenance-v1.
/// Content exports are independent of the media DPB, for the existing C: factory integration.</summary>
public static class CpmUtilitiesImages
{
    public const string FloppyName = "HC2000-UTIL.DSK";
    public const string VolumeName = "HC2000-UTIL-8M.DSK";
    public const string BootName = "CPM22-HC-ram-paged-upgrade.DSK";
    public const string ManifestName = "utilities-manifest.json";
    public const string ContentDirectory = "utilities-content";
    public const string BootHash = "fc36f11ab405d41f3d700ca7e269cd2057fc16c2306e4b7438396743d714e085";
    private const string BaseImage = "disks/cpm/CPM22-HC.DSK";
    private const string Z80Image = "disks/cpm/CPMZ80.DSK";
    private const string Firmware = "bios/src/hc2000/upgrade/ram.paged.asm";
    private const string Builder = "tools/UpgradeBiosBuilder/Program.cs";
    private const string Rom = "roms/hc2000/eprom-dump-hc2k-2/M27C512.bin";

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
        var z80Bytes = Read(Z80Image);
        Verify(Z80Image, z80Bytes, null, "66b8e4dedc08ddd872607efaecd2c2a4bf4ec8cb3869e7f3e468f966ccf12b87");
        Verify(Rom, Read(Rom), 65536, "83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b");
        Verify(Firmware, Read(Firmware), null, "a4bb0b58fc37b457963b578e75a704af935f572a83a47c9b418fb19d3fe530dd");
        Verify(Builder, Read(Builder), null, "74e05f0a97c8110446ca012493afc90e1afc03310933d0abed3a8254f37c2013");
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
        foreach (var (name, source) in new[] { ("TAPEINIT", "tape.init"), ("MFMT", "mfmt"), ("HDFMT", "hdfmt"), ("DRVSEL", "drvsel"), ("RAMSTAT", "ramstat") })
        {
            var path = "bios/src/hc2000/upgrade/" + source + ".asm";
            var assembly = Assembler.AssembleText(Encoding.UTF8.GetString(Read(path)), file: Path.Combine(root, path));
            if (!assembly.Success)
                throw new InvalidDataException($"{path}: {string.Join(';', assembly.Diagnostics)}");
            var bytes = assembly.ToBinary();
            if (assembly.Segments.Min(s => s.Address) != 0x100 || bytes.Length > 0x5f00)
                throw new InvalidDataException(path + " is not a bounded CP/M COM.");
            files.Add(name + ".COM", (path, bytes));
        }
        // Included by TAPEINIT; record all its resident source dependencies too.
        Read("bios/src/hc2000/upgrade/tape.layout.inc");
        Read("bios/src/hc2000/upgrade/tape.resident.asm");
        var floppy = CpmFs.Open(Dsk.Load(MediaBlanks.Floppy720()));
        var volume = CpmFs.FormatMVolumeV2();
        foreach (var (name, file) in files)
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
        var manifest = JsonSerializer.Serialize(new
        {
            provenance = "hc2000-cpm-utilities-provenance-v1",
            sourceCommit,
            bootProfile = "hc2000-v2-paged-m-v1", // never the M v2 identity
            floppyDpb = floppy.Parameters,
            volumeDpb = volume.Parameters,
            inputs = inputs.Select(pair => new { path = pair.Key, size = pair.Value.Length, sha256 = Hash(pair.Value) }),
            files = files.Select(pair => new
            {
                name = pair.Key, source = pair.Value.Source, size = pair.Value.Bytes.Length, sha256 = Hash(pair.Value.Bytes),
                recordSize = floppy.Get(pair.Key).Length, recordSha256 = Hash(floppy.Get(pair.Key))
            }),
            outputs = outputs.Select(pair => new { name = pair.Key, size = pair.Value.Length, sha256 = Hash(pair.Value) })
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        // Validate everything before touching the output. Never leave stale/unlisted
        // COMs in the export consumed by the existing C: integration.
        var content = Path.Combine(output, ContentDirectory);
        if (Directory.Exists(content) && Directory.EnumerateFileSystemEntries(content).Any(path => !File.Exists(path) || !files.ContainsKey(Path.GetFileName(path))))
            throw new InvalidDataException("Utilities content export contains an unlisted artifact.");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(content);
        foreach (var (name, bytes) in outputs)
            File.WriteAllBytes(Path.Combine(output, name), bytes);
        foreach (var (name, file) in files)
            File.WriteAllBytes(Path.Combine(output, ContentDirectory, name), file.Bytes);
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
