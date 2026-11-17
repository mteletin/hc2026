using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hc2000.Asm;
using Hc2000.Machine;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Memory;

if (args.Length != 2 || args[0] is not ("blockport" or "m" or "m-paged" or "m-paged-v2" or "hdd-guest" or "hdd-guest-standalone" or "tape-guest" or "cboot"))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/UpgradeBiosBuilder -- <blockport|m|m-paged|m-paged-v2|hdd-guest|hdd-guest-standalone|tape-guest|cboot> <output-directory>");
    return 2;
}

var root = FindRoot(AppContext.BaseDirectory);
var output = Path.GetFullPath(args[1]);
if (args[0] == "m-paged")
    return BuildMPaged(root, output);
if (args[0] == "m-paged-v2")
    return BuildMPagedVariant(root, output, v2: true);
if (args[0] == "cboot")
    return BuildCboot(root, output);
Directory.CreateDirectory(output);

if (args[0] == "tape-guest")
{
    var sourcePath = Path.Combine(root, "src-library/extended/bios-upgrade/tape/tape.init.asm");
    var sourceBytes = File.ReadAllBytes(sourcePath);
    var assembled = Assembler.AssembleText(Encoding.UTF8.GetString(sourceBytes), file: sourcePath);
    File.WriteAllText(Path.Combine(output, "tape.init.lst"), assembled.ToListing());
    File.WriteAllText(Path.Combine(output, "tape.init.sym"), assembled.ToSymbolFile());
    if (!assembled.Success)
    {
        foreach (var diagnostic in assembled.Diagnostics) Console.Error.WriteLine(diagnostic.Message);
        return 3;
    }
    var image = assembled.ToBinary();
    if (assembled.Segments.Min(s => s.Address) != 0x100 || image.Length > 0x5F00)
        throw new InvalidOperationException("Tape COM exceeds its reserved TPA ceiling.");
    // Unbound template refuses installation until an owner-mounted session supplies metadata.
    File.WriteAllBytes(Path.Combine(output, "TAPEINIT.COM"), image);
    Console.WriteLine($"SOURCE {Hash(sourceBytes)} {sourcePath}");
    Console.WriteLine($"COM tape.init {image.Length} {Hash(image)} STATIC_ONLY DB=none runtime=not-run");
    foreach (var include in new[] { "tape.layout.inc", "tape.resident.asm", "../dispatch.sites.inc" })
    {
        var path = Path.Combine(root, "src-library/extended/bios-upgrade/tape", include);
        Console.WriteLine($"INCLUDE {Hash(File.ReadAllBytes(path))} {path}");
    }
    return 0;
}
else if (args[0] is "hdd-guest" or "hdd-guest-standalone")
{
    // Source-fit artifacts only, not installation or positive guest ACK proof.
    // Preserve old modes, input media and profile defaults verbatim.
    var hddSourceDir = args[0] == "hdd-guest-standalone"
        ? Path.Combine(root, "bios/src/hc2000/upgrade")
        : Path.Combine(root, "src-library/extended/bios-upgrade/hdd");
    foreach (var name in new[] { "hdd.init", "hdd.swap" })
    {
        var sourcePath = Path.Combine(hddSourceDir, name + ".asm");
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var assembled = Assembler.AssembleText(Encoding.UTF8.GetString(sourceBytes), file: sourcePath);
        File.WriteAllText(Path.Combine(output, name + ".lst"), assembled.ToListing());
        File.WriteAllText(Path.Combine(output, name + ".sym"), assembled.ToSymbolFile());
        if (!assembled.Success)
        {
            foreach (var diagnostic in assembled.Diagnostics) Console.Error.WriteLine(diagnostic.Message);
            return 3;
        }
        var image = assembled.ToBinary();
        if (assembled.Segments.Min(s => s.Address) != 0x100 || image.Length > (args[0] == "hdd-guest-standalone" ? 0x9700 : 0x8F00))
            throw new InvalidOperationException("HDD guest COM exceeds the reserved TPA ceiling.");
        File.WriteAllBytes(Path.Combine(output, name == "hdd.init" ? (args[0] == "hdd-guest-standalone" ? "HDINST.COM" : "HDDINIT.COM") : "HDSWAP.COM"), image);
        Console.WriteLine($"SOURCE {Hash(sourceBytes)} {sourcePath}");
        Console.WriteLine($"COM {name} {image.Length} {Hash(image)} STATIC_ONLY DB=none runtime=not-run");
        // The standalone pair (hc2000-cpm22-proving) includes only its own layout and resident.
        foreach (var include in args[0] == "hdd-guest-standalone"
            ? new[] { "hdd.layout.inc", "hdd.resident.asm" }
            : new[] { "hdd.layout.inc", "hdd.resident.asm", "../dispatch.sites.inc", "../tape/tape.layout.inc" })
        {
            var path = Path.Combine(hddSourceDir, include);
            Console.WriteLine($"INCLUDE {Hash(File.ReadAllBytes(path))} {path}");
        }
    }
    return 0;
}
else if (args[0] == "blockport")
{
    var sourcePath = Path.Combine(root, "src-library/extended/bios-upgrade/blockport/disk.blockport.asm");
    var sourceBytes = File.ReadAllBytes(sourcePath);
    var source = Encoding.UTF8.GetString(sourceBytes);
    var assembly = Assembler.AssembleText(source, file: sourcePath);
    if (!assembly.Success)
        throw new InvalidOperationException(string.Join("; ", assembly.Diagnostics.Select(d => d.Message)));

    var biosImage = assembly.ToBinary();
    if (biosImage.Length != 0x800)
        throw new InvalidOperationException($"Expected 0x800-byte BIOS image, got {biosImage.Length:X}.");
    var biosPath = Path.Combine(output, "disk.blockport.bin");
    File.WriteAllBytes(biosPath, biosImage);

    var mediaPath = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
    var media = Dsk.Load(File.ReadAllBytes(mediaPath));
    var systemSectors = new (int Side, int Sector)[]
    {
        (0,3), (0,5), (0,7), (0,9), (0,2), (0,4), (0,6), (0,8),
        (1,1), (1,3), (1,5), (1,7), (1,9), (1,2), (1,4)
    };
    for (var i = 11; i < systemSectors.Length; i++)
    {
        var (side, sector) = systemSectors[i];
        var destination = media.Tracks.Single(t => t.Cylinder == 0 && t.Side == side)
            .Sectors.Single(s => s.R == sector).Data;
        biosImage.AsSpan((i - 11) * 512, 512).CopyTo(destination);
    }

    var upgradedMediaPath = Path.Combine(output, "CPM22-HC-blockport-upgrade.DSK");
    File.WriteAllBytes(upgradedMediaPath, media.Save());
    var profile = MachineProfile.Hc2000V2BlockportV1;
    profile.Validate();
    var profileManifest = JsonSerializer.Serialize(profile);
    Console.WriteLine($"SOURCE {Hash(sourceBytes)} {sourcePath}");
    Console.WriteLine($"IMAGE {biosImage.Length} {Hash(biosImage)} {biosPath}");
    Console.WriteLine($"MEDIA {new FileInfo(upgradedMediaPath).Length} {Hash(File.ReadAllBytes(upgradedMediaPath))} {upgradedMediaPath}");
    Console.WriteLine($"INPUT_MEDIA {new FileInfo(mediaPath).Length} {Hash(File.ReadAllBytes(mediaPath))} {mediaPath}");
    Console.WriteLine($"PROFILE_JSON {profileManifest}");
    Console.WriteLine($"PROFILE_SHA256 {Hash(Encoding.UTF8.GetBytes(profileManifest))}");
    return 0;
}
else
{
    var sourcePath = Path.Combine(root, "src-library/extended/bios-upgrade/paging/ram.banks.asm");
    var sourceBytes = File.ReadAllBytes(sourcePath);
    var source = Encoding.UTF8.GetString(sourceBytes);
    var assembled = Assembler.AssembleText(source, file: sourcePath);
    if (!assembled.Success)
        throw new InvalidOperationException(string.Join("; ", assembled.Diagnostics.Select(d => d.Message)));

    var overlay = assembled.Segments.OrderBy(segment => segment.Address).SelectMany(segment => segment.Bytes).ToArray();
    var bios = ExtractSuppliedBios(root);
    foreach (var segment in assembled.Segments)
    {
        var offset = segment.Address - 0xD300;
        if (offset < 0 || offset + segment.Bytes.Length > bios.Length)
            throw new InvalidOperationException($"Overlay {segment.Address:X4}+{segment.Bytes.Length:X} is outside D300-DAFF.");
        segment.Bytes.CopyTo(bios, offset);
    }

    var diskPath = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
    var media = Dsk.Load(File.ReadAllBytes(diskPath));
    var sectors = SystemSectors();
    for (var i = 0; i < 4; i++)
    {
        var (side, sector) = sectors[11 + i];
        var destination = media.Tracks.Single(t => t.Cylinder == 0 && t.Side == side)
            .Sectors.Single(s => s.R == sector).Data;
        bios.AsSpan(i * 512, 512).CopyTo(destination);
    }

    var biosPath = Path.Combine(output, "ram.banks.bin");
    File.WriteAllBytes(biosPath, bios);
    var outputMedia = Path.Combine(output, "CPM22-HC-ram-banks-upgrade.DSK");
    File.WriteAllBytes(outputMedia, media.Save());
    var profile = Encoding.UTF8.GetBytes("hc2000-v2-ram-banks-v1|version=1|policy=hc2000-ram-banks-8k128-v1|banks=128|bankBytes=8192|window=A000-BFFF|selector=8B|count=8D");
    Console.WriteLine($"SOURCE {Hash(sourceBytes)} {sourcePath}");
    Console.WriteLine($"BUILD {Hash(overlay)} bytes={overlay.Length} (assembled overlay bytes)");
    Console.WriteLine($"BUILDER {Hash(File.ReadAllBytes(Path.Combine(root, "tools/UpgradeBiosBuilder/Program.cs")))} tools/UpgradeBiosBuilder/Program.cs");
    Console.WriteLine($"BASE_BIOS {bios.Length} {Hash(ExtractSuppliedBios(root))}");
    Console.WriteLine($"IMAGE {bios.Length} {Hash(bios)} {biosPath}");
    Console.WriteLine($"INPUT_MEDIA {new FileInfo(diskPath).Length} {Hash(File.ReadAllBytes(diskPath))} {diskPath}");
    Console.WriteLine($"MEDIA {new FileInfo(outputMedia).Length} {Hash(File.ReadAllBytes(outputMedia))} {outputMedia}");
    Console.WriteLine($"PROFILE {Hash(profile)} {Encoding.UTF8.GetString(profile)}");
    return 0;
}

// Host-only source-stage assembly: never constructs Machine/CPU or executes guest code.
// v2 (hc2000-m-drive-8mib-v2, PO ruling-8kib-fallback-20261019): same bootstrap, pins and
// fit gate; identity and media names carry the v2 8 KiB-block DPB (SPT128 BSH6 BLM63
// EXM1 DSM1023 DRM255 ALV128 AL0 0x80 CKS0 OFF0) and the lifted 7DFD/Track+1 latches.
static int BuildMPaged(string root, string output) => BuildMPagedVariant(root, output, v2: false);

static int BuildMPagedVariant(string root, string output, bool v2)
{
    if (Directory.Exists(output) || File.Exists(output))
        throw new IOException("m-paged requires a NEW private output directory.");
    var mode = v2 ? "m-paged-v2" : "m-paged";
    var sourcePath = Path.Combine(root, $"src-library/extended/bios-upgrade/paging/ram.paged{(v2 ? ".v2" : "")}.asm");
    var sourceBytes = File.ReadAllBytes(sourcePath);
    var diskPath = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
    var input = File.ReadAllBytes(diskPath);
    var romPath = Path.Combine(root, "roms/hc2000/eprom-dump-hc2k-2/M27C512.bin");
    var rom = File.ReadAllBytes(romPath);
    if (Hash(input) != "ff5a1c541c8aa80778a0f44d3ece398013084aafd9be1d3f32d2f68b774bb008"
        || Hash(rom) != "83e3213b049c5b849ee32e04da901eba06992d5aa1f44f76564f312c79748d2b")
        throw new InvalidOperationException("Unpinned original media/whole ROM.");
    var disk = Dsk.Load(input);
    if (disk.Tracks.Count != 160 || disk.Tracks.Any(t => t.Cylinder >= 80 || t.Side >= 2
        || t.Sectors.Count != 9 || t.Sectors.Any(s => s.N != 2 || s.Data.Length != 512)
        || !t.Sectors.Select(s => (int)s.R).Order().SequenceEqual(Enumerable.Range(1, 9))))
        throw new InvalidOperationException("Expected original 80x2x9 N2 geometry.");
    var baseline = ExtractSuppliedBios(root);
    if (Hash(baseline) != "ad2bdb9053de9ec5fb971e7b660c3e07ace1fee8382cfa8f1c4f4610e48bcea4")
        throw new InvalidOperationException("Unpinned original BIOS.");
    var boot = disk.Tracks.Single(t => t.Cylinder == 0 && t.Side == 0).Sectors.Single(s => s.R == 1).Data;
    if (boot[0x13] != 0x36 || boot[0x14] != 0x0F)
        throw new InvalidOperationException("Original boot count opcode/operand mismatch (requires architecture review).");

    Directory.CreateDirectory(output);
    var report = new List<string>();
    void Record(string line) { report.Add(line); Console.WriteLine(line); }
    Record($"SOURCE {Hash(sourceBytes)} {sourcePath}");
    Record($"BUILDER {Hash(File.ReadAllBytes(Path.Combine(root, "tools/UpgradeBiosBuilder/Program.cs")))} tools/UpgradeBiosBuilder/Program.cs");
    Record($"BASE_BIOS {baseline.Length} {Hash(baseline)}");
    Record($"INPUT_MEDIA {input.Length} {Hash(input)} {diskPath}");
    Record($"WHOLE_ROM {rom.Length} {Hash(rom)} {romPath}");
    var profileName = v2 ? MachineProfile.Hc2000V2PagedMTransferV2.Name : MachineProfile.Hc2000V2PagedMTransferV1.Name;
    var policyId = v2 ? PagedRamPolicy.MTransferV2PolicyId : PagedRamPolicy.MTransferPolicyId;
    Record($"PROFILE {profileName} {policyId}");
    var prefix = v2 ? "ram.paged.v2" : "ram.paged";
    var assembly = Assembler.AssembleText(Encoding.UTF8.GetString(sourceBytes), file: sourcePath);
    File.WriteAllText(Path.Combine(output, $"{prefix}.lst"), assembly.ToListing());
    File.WriteAllText(Path.Combine(output, $"{prefix}.sym"), assembly.ToSymbolFile());
    File.WriteAllText(Path.Combine(output, $"{prefix}.hex"), assembly.ToIntelHex());
    foreach (var diagnostic in assembly.Diagnostics)
        Record($"DIAGNOSTIC {diagnostic.Line} {diagnostic.Severity} {diagnostic.Message}");
    foreach (var segment in assembly.Segments)
        Record($"SEGMENT {segment.Address:X4}-{segment.Address + segment.Bytes.Length - 1:X4} bytes={segment.Bytes.Length} sha256={Hash(segment.Bytes)}");
    foreach (var symbol in assembly.Symbols.OrderBy(s => s.Value).ThenBy(s => s.Key))
        Record($"SYMBOL {symbol.Key} {symbol.Value:X4}");
    int Finish(int exit)
    {
        Record($"RESULT exit={exit} STATIC_ONLY DB=none CPU=not-constructed runtime=not-run");
        File.WriteAllLines(Path.Combine(output, "source-fit.txt"), report);
        return exit;
    }
    if (!assembly.Success) return Finish(3);
    var start = assembly.Symbols["MCodeStart"];
    var end = assembly.Symbols["MCodeEnd"];
    Record($"CODE {start:X4}-{end - 1:X4} bytes={end - start} capacity=460 overflow={Math.Max(0, end - 0xDE80)}");
    Record("RESERVATION DPH=DB15-DB24 DPB=DB25-DB33 ALV=DB34-DBB3 bounce=DBB4-DC33 directory=DC34-DCB3 scratch=DE80-DE9F stack=DEA0-DEFF SP=DF00");
    Record("STACK_SOURCE private interval MPrivateStart..MPrivateEnd: no PUSH; CALL MCopy only, helper LD BC/LDIR/RET; depth=1 bytes=2; DI held until original SP restored; IFF2 restored via EI/RET; NMI unsupported");
    if (start != 0xDCB4 || end > 0xDE80)
    {
        Record($"FIT_REFUSED code intersects reserved data at DE80; candidate end={end:X4}; no binary/media emitted, hex/listing are FAILED candidate only");
        return Finish(3);
    }
    var vectors = new Dictionary<int, (int Original, string Target)>
    {
        [0xD301] = (0xD3A6, "MCold"), [0xD304] = (0xD3C1, "MWarm"),
        [0xD319] = (0xD52D, "MHome"), [0xD31C] = (0xD492, "MSelect"),
        [0xD31F] = (0xD52F, "MTrack"), [0xD322] = (0xD534, "MSector"),
        [0xD328] = (0xD542, "MRead"), [0xD32B] = (0xD53E, "MWrite")
    };
    var bios = new byte[3072];
    baseline.CopyTo(bios, 0);
    var changed = new HashSet<int>();
    foreach (var segment in assembly.Segments)
    {
        var vector = vectors.TryGetValue(segment.Address, out var entry);
        if (vector)
        {
            var offset = segment.Address - 0xD300;
            if (segment.Bytes.Length != 2 || baseline[offset - 1] != 0xC3
                || BitConverter.ToUInt16(baseline, offset) != entry.Original
                || BitConverter.ToUInt16(segment.Bytes) != assembly.Symbols[entry.Target])
                throw new InvalidOperationException($"Conflicting vector {segment.Address:X4}.");
            Record($"VECTOR {segment.Address:X4} {entry.Original:X4}->{assembly.Symbols[entry.Target]:X4} {entry.Target}");
        }
        else if (!((segment.Address == 0xDB15 && segment.Bytes.Length == 31)
            || (segment.Address == start && segment.Bytes.Length == end - start)))
            throw new InvalidOperationException($"Unowned segment {segment.Address:X4}.");
        for (var i = 0; i < segment.Bytes.Length; i++)
        {
            var address = segment.Address + i;
            if (!changed.Add(address)) throw new InvalidOperationException("Overlapping assembly segments.");
            bios[address - 0xD300] = segment.Bytes[i];
        }
    }
    if (vectors.Keys.Any(v => !changed.Contains(v)))
        throw new InvalidOperationException("Missing vector segment.");
    var retainedDiff = Enumerable.Range(0, baseline.Length).Where(i => baseline[i] != bios[i]).ToArray();
    if (retainedDiff.Any(i => !vectors.Keys.Any(v => i + 0xD300 == v || i + 0xD300 == v + 1)))
        throw new InvalidOperationException("Unexpected retained BIOS change.");
    Record($"RETAINED_DIFF {string.Join(',', retainedDiff.Select(i => $"{i + 0xD300:X4}:{baseline[i]:X2}>{bios[i]:X2}"))}");
    // Original format81 / DPB D396 SPT72, OFF2 reserves cylinders0-1.
    if (baseline[0x96] != 72 || baseline[0x97] != 0 || baseline[0xA3] != 2 || baseline[0xA4] != 0)
        throw new InvalidOperationException("System-sector reservation mismatch; architecture review required.");
    var sectors = SystemSectors().Concat(new[] { (Side: 1, Sector: 6), (Side: 1, Sector: 8) }).ToArray();
    for (var i = 0; i < 6; i++)
    {
        var (side, sector) = sectors[11 + i];
        var destination = disk.Tracks.Single(t => t.Cylinder == 0 && t.Side == side).Sectors.Single(s => s.R == sector).Data;
        bios.AsSpan(i * 512, 512).CopyTo(destination);
    }
    boot[0x14] = 17;
    Record("BOOTSTRAP C0/H0/R1 offset=0014 0F->11 relocated=3014 count=17 loader=original; extension=C0/H1/R6,R8 system-reserved");
    var media = disk.Save();
    var before = Dsk.Load(input);
    foreach (var track in disk.Tracks)
    foreach (var sector in track.Sectors)
    {
        var original = before.Tracks.Single(t => t.Cylinder == track.Cylinder && t.Side == track.Side).Sectors.Single(s => s.R == sector.R).Data;
        if (!original.SequenceEqual(sector.Data))
            Record($"CHANGED_SECTOR C{track.Cylinder}/H{track.Side}/R{sector.R} {Hash(original)}->{Hash(sector.Data)}");
    }
    var mediaName = v2 ? "CPM22-HC-ram-paged-v2-upgrade.DSK" : "CPM22-HC-ram-paged-upgrade.DSK";
    File.WriteAllBytes(Path.Combine(output, $"{prefix}.bin"), bios);
    File.WriteAllBytes(Path.Combine(output, mediaName), media);
    Record($"IMAGE {bios.Length} {Hash(bios)} {prefix}.bin");
    Record($"MEDIA {media.Length} {Hash(media)} {mediaName}");
    return Finish(0);
}

static int BuildCboot(string root, string output)
{
    // hc2000-direct-c-boot extended upper ROM: the faithful cpm-hc2kzip keeps
    // E000-F7FF and the faithful A: boot body at F80E verbatim; F800 becomes
    // the C: loader stub and F870 the loader body. The warm trampoline is a
    // payload patch (restored at 3080 inside the bootstrap region), emitted
    // separately for the payload capture step.
    var sourcePath = Path.Combine(root, "src-library/extended/bios-upgrade/hdd/cboot.boot.asm");
    var sourceBytes = File.ReadAllBytes(sourcePath);
    Directory.CreateDirectory(output);
    var assembly = Assembler.AssembleText(Encoding.UTF8.GetString(sourceBytes), file: sourcePath);
    File.WriteAllText(Path.Combine(output, "cboot.boot.lst"), assembly.ToListing());
    File.WriteAllText(Path.Combine(output, "cboot.boot.sym"), assembly.ToSymbolFile());
    if (!assembly.Success)
    {
        foreach (var diagnostic in assembly.Diagnostics) Console.Error.WriteLine(diagnostic.Message);
        return 3;
    }
    if (assembly.Segments.Any(s => s.Address is not (0x8100 or 0x3080 or 0xF800)))
        throw new InvalidOperationException("Unexpected cboot segment.");
    var body = assembly.Segments.Single(s => s.Address == 0x8100).Bytes;
    var warm = assembly.Segments.Single(s => s.Address == 0x3080).Bytes;
    var stub = assembly.Segments.Single(s => s.Address == 0xF800).Bytes;
    if (stub.Length != 14) throw new InvalidOperationException($"C-boot stub must be 14 bytes, got {stub.Length}.");
    if (body.Length > 0x790) throw new InvalidOperationException($"C-boot body exceeds the F870..FFFF window ({body.Length:X}).");
    if (warm.Length > 0x80) throw new InvalidOperationException("Warm trampoline exceeds the 3080-30FF window.");
    // The trampoline's CALL target: the resident HddFlush (PHASE 0x9000) from
    // the same hdd.init.asm the guest installer uses.
    var initPath = Path.Combine(root, "src-library/extended/bios-upgrade/hdd/hdd.init.asm");
    var init = Assembler.AssembleText(Encoding.UTF8.GetString(File.ReadAllBytes(initPath)), file: initPath);
    if (!init.Success)
    {
        foreach (var diagnostic in init.Diagnostics) Console.Error.WriteLine(diagnostic.Message);
        return 3;
    }
    if (!init.Symbols.TryGetValue("HddFlush", out var flush) || flush is < 0x9000 or >= 0xB800)
        throw new InvalidOperationException("Resident HddFlush symbol missing or outside the resident region.");
    if (warm[0] != 0xCD || (warm[1] | warm[2] << 8) != 0)
        throw new InvalidOperationException("Warm trampoline CALL placeholder changed.");
    warm[1] = unchecked((byte)flush);
    warm[2] = unchecked((byte)(flush >> 8));
    var romPath = Path.Combine(root, "roms/hc2000/v2/cpm-hc2kzip.bin");
    var baseline = File.ReadAllBytes(romPath);
    if (baseline.Length != 0x2000) throw new InvalidOperationException("cpm-hc2kzip.bin must be 8 KiB.");
    // Faithful cold entry: LD HL,F80E; LD DE,8000; LD BC,005F; LDIR; JP 8000.
    ReadOnlySpan<byte> faithfulStub = [0x21, 0x0E, 0xF8, 0x11, 0x00, 0x80, 0x01, 0x5F, 0x00, 0xED, 0xB0, 0xC3, 0x00, 0x80];
    if (!baseline.AsSpan(0x1800, faithfulStub.Length).SequenceEqual(faithfulStub))
        throw new InvalidOperationException("cpm-hc2kzip faithful stub at F800 changed; review required.");
    // Faithful body starts with LD SP,8000.
    if (baseline[0x180E] != 0x31 || baseline[0x180F] != 0x00 || baseline[0x1810] != 0x80)
        throw new InvalidOperationException("Faithful A: boot body at F80E changed; review required.");
    var rom = (byte[])baseline.Clone();
    stub.CopyTo(rom.AsSpan(0x1800));
    body.CopyTo(rom.AsSpan(0x1870));
    // Everything after the loader body is the faithful ROM tail: the burned-in
    // IM2 vector (0x58 0xEE at 1EFF/1F00 -> PC 0xEE58, the console/INT handler
    // the CP/M ROM relies on once its cold entry does EI) and the chip trailer.
    // Never overwrite it: with I=FE an INT vectoring through 0xFF bytes lands
    // at 0xFFFF and kills the loader mid-boot (hc2000-direct-c-boot).
    if (rom[0x1EFF] != 0x58 || rom[0x1F00] != 0xEE)
        throw new InvalidOperationException("Burned-in IM2 vector (58 EE) at 1EFF/1F00 missing from the cboot tail.");
    File.WriteAllBytes(Path.Combine(output, "cpm-hc2kzip-cboot-v1.bin"), rom);
    File.WriteAllBytes(Path.Combine(output, "cboot-warm-3080.bin"), warm);
    Console.WriteLine($"SOURCE {Hash(sourceBytes)} {sourcePath}");
    foreach (var include in new[] { "cboot.layout.inc", "cboot.boot.asm" })
    {
        var path = Path.Combine(root, "src-library/extended/bios-upgrade/hdd", include);
        Console.WriteLine($"INCLUDE {Hash(File.ReadAllBytes(path))} {path}");
    }
    Console.WriteLine($"BOOTSTUB {stub.Length} {Hash(stub)} rom@F800");
    Console.WriteLine($"BOOTBODY {body.Length} {Hash(body)} rom@F870");
    Console.WriteLine($"WARMTRAMPOLINE {warm.Length} {Hash(warm)} payload@3080 flush={flush:X4}");
    Console.WriteLine($"ROM_INPUT {baseline.Length} {Hash(baseline)} {romPath}");
    Console.WriteLine($"ROM {rom.Length} {Hash(rom)} cpm-hc2kzip-cboot-v1.bin");
    return 0;
}

static byte[] ExtractSuppliedBios(string root)
{
    var disk = Dsk.Load(File.ReadAllBytes(Path.Combine(root, "disks/cpm/CPM22-HC.DSK")));
    var sectors = SystemSectors();
    var loaded = new byte[15 * 512];
    for (var i = 0; i < sectors.Length; i++)
    {
        var (side, sector) = sectors[i];
        disk.Tracks.Single(t => t.Cylinder == 0 && t.Side == side).Sectors
            .Single(s => s.R == sector).Data.CopyTo(loaded, i * 512);
    }
    return loaded.AsSpan(0x1600, 0x800).ToArray();
}

static (int Side, int Sector)[] SystemSectors() =>
[
    (0, 3), (0, 5), (0, 7), (0, 9), (0, 2), (0, 4), (0, 6), (0, 8),
    (1, 1), (1, 3), (1, 5), (1, 7), (1, 9), (1, 2), (1, 4)
];

static string FindRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hc2000.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
