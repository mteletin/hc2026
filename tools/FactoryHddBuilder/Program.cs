using System.Security.Cryptography;
using System.Text.Json;
using Hc2000.HostFiles;

if (args.Length == 2 && args[0] == "source-digest")
{
    Console.WriteLine(PublicGuestPayload.SourceDigest(args[1]));
    return 0;
}

if (args.Length == 4 && args[0] == "copy-public")
{
    PublicGuestPayload.Copy(args[1], args[2], args[3]);
    return 0;
}

if (args.Length is not (7 or 8 or 9) || (args.Length == 9 && args[8] != "--public"))
{
    Console.Error.WriteLine("Usage: FactoryHddBuilder <output-directory> <utilities-output-directory> <boot-payload> <shell> <workstation-boot-payload> <workstation-shell> <workstation-bdos> [<repo-root>]");
    return 2;
}

// Consume the existing provenance-checked, geometry-independent export rather than
// copying the M: image, whose allocation blocks and directory layout differ from HDD.
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "utilities-manifest.json")));
if (manifest.RootElement.GetProperty("provenance").GetString() != "hc2000-cpm-utilities-provenance-v1")
    throw new InvalidDataException("Unsupported utilities provenance.");
var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
var sources = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
{
    var name = file.GetProperty("name").GetString()!;
    if (Path.GetFileName(name) != name || name.Contains('\\') || !name.EndsWith(".COM", StringComparison.Ordinal))
        throw new InvalidDataException("Invalid exported utilities name.");
    var bytes = File.ReadAllBytes(Path.Combine(args[1], "utilities-content", name));
    if (bytes.Length != file.GetProperty("size").GetInt32()
        || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != file.GetProperty("sha256").GetString())
        throw new InvalidDataException("Utilities export does not match its manifest: " + name);
    files.Add(name, bytes);
    sources[name] = file.GetProperty("source").GetString()!;
}
// The prepared curated C: carries exactly the curated tool set: the stress tools the utilities
// export adds (ARCH, BIGFILE, COPYCHK, DIRLOAD, RANDOMIO, USERS) stay on the utilities images,
// not on the factory container's advertised C:.
var curatedFiles = files
    .Where(pair => HddFactoryImages.CuratedNames.Contains(pair.Key))
    .ToDictionary(pair => pair.Key, pair => pair.Value);
HddFactoryImages.GenerateInto(args[0], overwrite: true, cFiles: curatedFiles);
// The curated prepared-C: manifest (hc2000-prepared-cpm-tools-image): shipped beside the container
// so a fresh installed package supplies the image and its per-payload record without repository access.
var curated = HddFactoryImages.WriteCuratedManifest(args[0], Path.Combine(args[0], HddFactoryImages.ContainerName),
    manifest.RootElement.GetProperty("sourceCommit").GetString()!, sources);
// A separate opt-in image preserves the published curated container and its exact manifest.
var bootImage = File.ReadAllBytes(args[2]);
if (Convert.ToHexString(SHA256.HashData(bootImage)).ToLowerInvariant() != "2341feb887030dd40a3aee2f6bd87ae253d68379ca869a55ca8e5c1a457eebe7")
    throw new InvalidDataException("C: boot payload differs from the pinned asset.");
var container = File.ReadAllBytes(Path.Combine(args[0], HddFactoryImages.ContainerName));
// The folder shell (hc2000-cpm-user-folders) the C: boot brings in, pinned like the boot payload.
var shell = File.ReadAllBytes(args[3]);
if (Convert.ToHexString(SHA256.HashData(shell)).ToLowerInvariant() != "2bee6eb84cdda6ddfd4a66f4d96435e14e2f4ab12295f7e3cebbfea81a909710")
    throw new InvalidDataException("The folder shell differs from the pinned asset.");
var bootVolume = HddFactoryImages.CreateCBootVolume(container.AsSpan(0, 8 * 1024 * 1024), bootImage, shell);
File.WriteAllBytes(Path.Combine(args[0], HddFactoryImages.CBootVolumeName), bootVolume);
Console.WriteLine($"CBOOT_VOLUME {HddFactoryImages.CBootVolumeName} sha256={Convert.ToHexString(SHA256.HashData(bootVolume)).ToLowerInvariant()}");
// The full workstation's disks (hc2000-full-workstation-default): its C: with its own boot image (the paged-drive
// switch) and its folder shell above the TPA, both pinned, on the same curated C:, and empty D:-F:.
var workstationBoot = File.ReadAllBytes(args[4]);
if (Convert.ToHexString(SHA256.HashData(workstationBoot)).ToLowerInvariant() != "46c2b7edab995f99769daac4be9fcb7e1825ac4e70088a5ee4af5e481eaa8530")
    throw new InvalidDataException("The workstation boot payload differs from the pinned asset.");
var workstationShell = File.ReadAllBytes(args[5]);
if (Convert.ToHexString(SHA256.HashData(workstationShell)).ToLowerInvariant() != "f088cd69aa5168d885940f7796147b73faf9b8cec9fc9e70fe1f2423ba54ef3e")
    throw new InvalidDataException("The workstation folder shell differs from the pinned asset.");
var workstationBdos = File.ReadAllBytes(args[6]);
if (Convert.ToHexString(SHA256.HashData(workstationBdos)).ToLowerInvariant() != "1945a76a11852bd7a342f869b80f3c60c7654a499c645ff60f20e629a990663d")
    throw new InvalidDataException("The workstation BDOS.SYS differs from the pinned asset.");
// With the repository root (the 8th argument), the workstation's drives carry the disk inventory's layout.
var workstationContainer = HddFactoryImages.CreateWorkstationContainer(container.AsSpan(0, 8 * 1024 * 1024), workstationBoot, workstationShell, workstationBdos,
    args.Length >= 8 ? volumes =>
    {
        var absent = WorkstationLayout.Apply(args[7], volumes, includePrivateLibrary: args.Length != 9);
        Console.WriteLine($"WORKSTATION_LAYOUT {WorkstationLayout.FileName} absent_sources={absent}");
    } : null);
File.WriteAllBytes(Path.Combine(args[0], HddFactoryImages.WorkstationContainerName), workstationContainer);
Console.WriteLine($"WORKSTATION_HDD {HddFactoryImages.WorkstationContainerName} sha256={Convert.ToHexString(SHA256.HashData(workstationContainer)).ToLowerInvariant()}");
Console.Write(File.ReadAllText(Path.Combine(args[0], HddFactoryImages.ReceiptName)));
Console.WriteLine($"FACTORY_HDD HCVD C=C.DSK D=D.DSK E=E.DSK F=F.DSK total_image_bytes=33554432 manifest={Path.GetFileName(curated)} profile=hc2000-cpm22-large-drive-dpb-v1");
return 0;
