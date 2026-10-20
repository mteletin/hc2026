using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hc2000.App.Input;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;
using Xunit.Abstractions;
using BootHarness = Hc2000.Machine.Tests.MachineBootAcceptanceTests.BootHarness;

namespace Hc2000.Machine.Tests;

/// <summary>Step 6 guest outcomes, through the merged step5a physical paste path, never BDOS injection.</summary>
public sealed class CpmDiskToolsAcceptanceTests(ITestOutputHelper output)
{
    // Oracle: references/cpm/cpm22-manual.zip, CH1.HTM sections 1.6.1/1.6.1.1.
    // STAT d: reports free KiB; file $S reports records and allocation-rounded Bytes.
    [CpmDiskToolsFact]
    public void GuestStatReportsGeometryAndFreeSpaceBeforeAndAfterWritesOnAAndB()
    {
        using var guest = new Guest(output, "stat-ab");
        var a = guest.Boot.Floppy!.DriveA;
        var b = guest.Boot.Floppy.DriveB;
        var sourceFs = CpmFs.Open(a.Image!, "cpm-hc80");
        var payload = sourceFs.Get("PIP.COM");
        guest.Log($"STAT.COM: bytes={sourceFs.Get("STAT.COM").Length} sha256={Hash(sourceFs.Get("STAT.COM"))}");
        var aFree = FreeKiB(a.ExportMedia());
        var bFree = FreeKiB(b.ExportMedia());
        var allocationKiB = (payload.Length + sourceFs.Parameters.BlockSize - 1) /
            sourceFs.Parameters.BlockSize * sourceFs.Parameters.BlockSize / 1024;
        var mismatches = new List<string>();
        guest.BootToPrompt();
        CheckFree('A', aFree);
        CheckFree('B', bFree);
        CheckStatus(aFree, bFree);
        CheckGeometry('A');
        CheckGeometry('B');
        CheckFile('A', "PIP.COM", Assert.Single(sourceFs.List(), file => file.User == 0 && file.Name == "PIP.COM"));
        guest.Send("PIP B:STPROOF.COM=A:PIP.COM[OV]\n", "A>");
        guest.Send("PIP A:STPROOF.COM=B:STPROOF.COM[OV]\n", "A>");
        var afterA = guest.Export("a-after.dsk", a.ExportMedia());
        var afterB = guest.Export("b-after.dsk", b.ExportMedia());
        var afterAFs = CpmFs.Open(Dsk.Load(afterA), "cpm-hc80");
        var afterBFs = CpmFs.Open(Dsk.Load(afterB), "cpm-hc80");
        Assert.Equal(payload, afterAFs.Get("STPROOF.COM"));
        Assert.Equal(payload, afterBFs.Get("STPROOF.COM"));
        guest.Log($"guest-written payload: bytes={payload.Length} sha256={Hash(payload)}; A/B readback byte-identical");
        Assert.Equal(aFree - allocationKiB, FreeKiB(afterA));
        Assert.Equal(bFree - allocationKiB, FreeKiB(afterB));
        CheckFree('A', aFree - allocationKiB);
        CheckFree('B', bFree - allocationKiB);
        CheckFile('A', "STPROOF.COM", Assert.Single(afterAFs.List(), file => file.User == 0 && file.Name == "STPROOF.COM"));
        CheckFile('B', "STPROOF.COM", Assert.Single(afterBFs.List(), file => file.User == 0 && file.Name == "STPROOF.COM"));
        CheckStatus(aFree - allocationKiB, bFree - allocationKiB);
        Assert.Equal(guest.SourceHash, Hash(File.ReadAllBytes(SourcePath())));
        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));

        void CheckFree(char drive, int expected)
        {
            var reply = guest.Send($"STAT {drive}:\n", "A>");
            var expectedText = $"Bytes Remaining On {drive}: {expected}k";
            if (!reply.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"Expected {expectedText}; seen {reply}");
        }

        void CheckStatus(int freeA, int freeB)
        {
            var reply = guest.Send("STAT\n", "A>");
            Assert.Contains($"A: R/W, Space: {freeA}k", reply, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"B: R/W, Space: {freeB}k", reply, StringComparison.OrdinalIgnoreCase);
        }

        void CheckGeometry(char drive)
        {
            var reply = Regex.Replace(guest.Send($"STAT {drive}:DSK:\n", "A>"), @"\s+", " ");
            var dpb = sourceFs.Parameters;
            // HC's guest DPB counts cylinders; the host preset counts sides and overstates DSM by one.
            // Expected capacity is bounded by physical media, NOT copied from that published DSM.
            var capacity = (sourceFs.MaximumBlock + 1) * dpb.BlockSize;
            var geometry = a.Image!.UniformGeometry();
            Assert.Contains($"{drive}: Drive Characteristics", reply);
            Assert.Contains($"{capacity / 128}: 128 Byte Record Capacity", reply);
            Assert.Contains($"{capacity / 1024}: Kilobyte Drive Capacity", reply);
            Assert.Contains($"{dpb.Drm + 1}: 32 Byte Directory Entries", reply);
            Assert.Contains($"{dpb.Cks * 4}: Checked Directory Entries", reply);
            Assert.Contains($"{128 * (dpb.Exm + 1)}: Records/ Extent", reply);
            Assert.Contains($"{dpb.BlockSize / 128}: Records/ Block", reply);
            Assert.Contains($"{geometry.Sides * geometry.Sectors * geometry.SectorSize / 128}: Sectors/ Track", reply);
            Assert.Contains($"{dpb.Off / geometry.Sides}: Reserved Tracks", reply);
        }

        void CheckFile(char drive, string name, CpmFile file)
        {
            var reply = Regex.Replace(guest.Send($"STAT {drive}:{name} $S\n", "A>"), @"\s+", " ");
            Assert.Contains("Size Recs Bytes Ext Acc", reply);
            var extents = (file.Records + 127) / 128;
            Assert.Contains($"{file.Records} {file.Records} {file.AllocatedBytes / 1024}k {extents} " +
                $"{(file.ReadOnly ? "R/O" : "R/W")} {drive}:{name}", reply);
        }

        int FreeKiB(byte[] media)
        {
            var disk = Dsk.Load(media);
            Assert.Equal(new DiskGeometry(80, 2, 9, 512), disk.UniformGeometry());
            var fs = CpmFs.Open(disk, "cpm-hc80");
            foreach (var warning in fs.Warnings) guest.Log($"physical geometry warning: {warning}");
            var dpb = fs.Parameters;
            var allocated = fs.List().Sum(file => file.AllocatedBytes);
            var free = ((fs.MaximumBlock + 1 - dpb.DirectoryBlocks) * dpb.BlockSize - allocated) / 1024;
            guest.Log($"oracle: geometry={disk.UniformGeometry()} DPB={dpb} maxBlock={fs.MaximumBlock} files(all users)={fs.List().Count} allocated={allocated} freeKiB={free}");
            return free;
        }
    }

    [CpmDiskToolsFact]
    public void GuestPipCopiesBinaryFromAToBAndBackByteIdentically()
    {
        using var guest = new Guest(output, "pip-roundtrip");
        var source = CpmFs.Open(guest.Boot.Floppy!.DriveA.Image!, "cpm-hc80").Get("PIP.COM");
        guest.BootToPrompt();
        guest.Send("PIP B:ROUND.COM=A:PIP.COM[OV]\n", "A>");
        guest.Send("DIR B:\n", "A>");
        guest.Send("PIP A:RETURN.COM=B:ROUND.COM[OV]\n", "A>");
        guest.Send("DIR A:\n", "A>");
        var a = guest.Export("a-after.dsk", guest.Boot.Floppy.DriveA.ExportMedia());
        var b = guest.Export("b-after.dsk", guest.Boot.Floppy.DriveB.ExportMedia());
        var afs = CpmFs.Open(Dsk.Load(a), "cpm-hc80");
        var bfs = CpmFs.Open(Dsk.Load(b), "cpm-hc80");
        Assert.Equal(45, afs.List().Count);
        Assert.Equal("ROUND.COM", Assert.Single(bfs.List()).Name);
        var round = guest.Export("round.com", bfs.Get("ROUND.COM"));
        var returned = guest.Export("return.com", afs.Get("RETURN.COM"));
        Assert.Equal(7424, source.Length);
        Assert.Equal(source, round);
        Assert.Equal(source, returned);
        Assert.Equal(source, afs.Get("PIP.COM"));
        Assert.Contains("ROUND", guest.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RETURN", guest.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(guest.SourceHash, Hash(File.ReadAllBytes(SourcePath())));
    }

    // FORMAT-only gate: retain an un-SYSGENed image for the separate SYSGEN proof.
    [CpmDiskToolsFact]
    public void FaithfulV2GuestFormatLeavesReadableEmptyDisk()
    {
        // Zero-filled disposable media ensures the E5 directory is written by guest FORMAT.
        using var guest = new Guest(output, "format-only", blankFill: 0x00);
        guest.BootToPrompt();
        guest.Send("FORMAT B:\n", "Strike ENTER when ready");
        guest.Send("\n", "Format another", maxFrames: 12000);
        Assert.Contains("Format complete", guest.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
        guest.Send("N\n", "A>");
        guest.Send("DIR B:\n", "A>");
        Assert.Contains("NO FILE", guest.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
        guest.Send("STAT B:\n", "A>");
        guest.Send("STAT B:DSK:\n", "A>");
        var formatted = guest.Export("b-formatted.dsk", guest.Boot.Floppy!.DriveB.ExportMedia());
        Assert.Equal(160, guest.Boot.Floppy.DriveB.FormattedTracks.Count);
        var disk = Dsk.Load(formatted);
        Assert.Equal(160, disk.Tracks.Count);
        foreach (var track in disk.Tracks)
        {
            Assert.Equal(9, track.Sectors.Count);
            foreach (var sector in track.Sectors)
            {
                Assert.Equal(512, sector.Data.Length);
                Assert.All(sector.Data, value => Assert.Equal((byte)0xe5, value));
            }
        }
        Assert.Empty(CpmFs.Open(disk, "cpm-hc80").List());
        guest.Log("readback: 160 tracks, 9 sectors/track, 512 bytes/sector, all E5; cpm-hc80 directory empty");
        Assert.Equal(guest.SourceHash, Hash(File.ReadAllBytes(SourcePath())));
    }

    [CpmDiskToolsFact]
    public void GuestFormatAndSysgenProduceAnIndependentlyBootableDisk()
    {
        byte[] generated;
        using (var guest = new Guest(output, "format-sysgen"))
        {
            guest.BootToPrompt();
            guest.Send("FORMAT B:\n", "Strike ENTER when ready");
            guest.Send("\n", "Format another", maxFrames: 12000);
            Assert.Contains("Format complete", guest.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
            // The FORMAT utility's "Format another" reader echoes the char and waits for CR; send N + ENTER.
            guest.Send("N\n", "A>");
            var formatted = guest.Export("b-formatted.dsk", guest.Boot.Floppy!.DriveB.ExportMedia());
            guest.Log($"formatted tracks={guest.Boot.Floppy.DriveB.FormattedTracks.Count}");
            Assert.Equal(160, guest.Boot.Floppy.DriveB.FormattedTracks.Count);
            Assert.Empty(CpmFs.Open(Dsk.Load(formatted), "cpm-hc80").List());
            guest.Send("SYSGEN\n", "SOURCE DRIVE NAME");
            guest.Send("A", "SOURCE ON");
            guest.Send("\n", "DESTINATION DRIVE NAME");
            guest.Send("B", "DESTINATION ON");
            guest.Send("\n", "DESTINATION DRIVE NAME");
            guest.Send("\n", "A>");
            var transcript = guest.Boot.ConsoleText();
            Assert.Contains("SYSGEN VER 2.0", transcript, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, transcript.Split("FUNCTION COMPLETE", StringSplitOptions.None).Length - 1);
            generated = guest.Export("b-system.dsk", guest.Boot.Floppy.DriveB.ExportMedia());
            var disk = Dsk.Load(generated);
            Assert.Empty(CpmFs.Open(disk, "cpm-hc80").List());
            Assert.False(disk.Tracks.Take(4).SelectMany(t => t.Sectors).All(s => s.Data.All(b => b == 0xe5)),
                "SYSGEN must replace the E5-filled reserved/system sectors.");
            Assert.Equal(guest.SourceHash, Hash(File.ReadAllBytes(SourcePath())));
        }
        // Independent machine, generated disk in A, empty B: no original boot disk available to the guest.
        using var reboot = new Guest(output, "generated-reboot", generated, mountBlankB: false);
        reboot.BootToPrompt();
        reboot.Send("DIR\n", "A>");
        Assert.Contains("NO FILE", reboot.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(CpmFs.Open(Dsk.Load(reboot.Boot.Floppy!.DriveA.ExportMedia()), "cpm-hc80").List());
    }

    [CpmDiskToolsFact]
    public void RamBanksUpgradeProfilePassesFormatSysgenAndIndependentReboot()
    {
        var profile = UpgradeRamBanksBiosTests.RamBanksProfile();
        var upgraded = UpgradeRamBanksBiosTests.BuildUpgradedMedia(StorageAssetTests.Root());
        byte[] generated;
        using (var guest = new Guest(output, "ram-banks-format-sysgen", upgraded, profileOverride: profile))
        {
            guest.BootToPrompt();
            guest.Send("FORMAT B:\n", "Strike ENTER when ready");
            guest.Send("\n", "Format another", maxFrames: 12000);
            Assert.Contains("Format complete", guest.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
            guest.Send("N\n", "A>");
            var formatted = guest.Export("b-formatted.dsk", guest.Boot.Floppy!.DriveB.ExportMedia());
            Assert.Equal(160, guest.Boot.Floppy.DriveB.FormattedTracks.Count);
            Assert.Empty(CpmFs.Open(Dsk.Load(formatted), "cpm-hc80").List());
            guest.Send("SYSGEN\n", "SOURCE DRIVE NAME");
            guest.Send("A", "SOURCE ON");
            guest.Send("\n", "DESTINATION DRIVE NAME");
            guest.Send("B", "DESTINATION ON");
            guest.Send("\n", "DESTINATION DRIVE NAME");
            guest.Send("\n", "A>");
            Assert.Equal(2, guest.Boot.ConsoleText().Split("FUNCTION COMPLETE", StringSplitOptions.None).Length - 1);
            generated = guest.Export("b-system.dsk", guest.Boot.Floppy.DriveB.ExportMedia());
            var disk = Dsk.Load(generated);
            Assert.Empty(CpmFs.Open(disk, "cpm-hc80").List());
            Assert.False(disk.Tracks.Take(4).SelectMany(t => t.Sectors).All(s => s.Data.All(b => b == 0xe5)));
        }

        using var reboot = new Guest(output, "ram-banks-generated-reboot", generated, mountBlankB: false,
            profileOverride: profile);
        reboot.BootToPrompt();
        reboot.Send("DIR\n", "A>");
        Assert.Contains("NO FILE", reboot.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(CpmFs.Open(Dsk.Load(reboot.Boot.Floppy!.DriveA.ExportMedia()), "cpm-hc80").List());
    }

    [CpmDiskToolsFact]
    public void GuestConsoleWriteCloseReadPersistsOnBAndEraseReclaimsSpace()
    {
        // PIP's CON: source is guest input, not the separate A: -> B: copy leg.
        const string name = "BPROOF.TXT";
        // The physical Enter key produces CR in PIP's unbuffered CON: input.
        const string payload = "HC2000 B WRITE CLOSE READ 0178\r" +
            "SECOND RECORD PERSISTS AFTER NEW MACHINE REMOUNT\r" +
            "THIRD LINE VERIFY EXACT ASCII CONTENT NOT HOST WRITES\r";
        byte[] writtenDisk;
        byte[] writtenFile;
        int freeBefore;
        using (var guest = new Guest(output, "b-write-close"))
        {
            guest.BootToPrompt();
            var baseline = CpmFs.Open(Dsk.Load(guest.Boot.Floppy!.DriveB.ExportMedia()), "cpm-hc80");
            Assert.Empty(baseline.List());
            freeBefore = FreeBytes(baseline);
            guest.Log($"B free baseline bytes={freeBefore}");
            guest.Send("STAT B:\n", "A>");
            Assert.Contains($"Bytes Remaining On B: {freeBefore / 1024}k", guest.Boot.ConsoleText());
            // Wait for PIP to finish loading before giving it console-source data.
            guest.Send("PIP\n", "*");
            guest.Send($"B:{name}=CON:\n", "=CON:");
            // CON: has no ready prompt. Give guest directory/open I/O a bounded
            // 400-frame idle interval; exact content assertion detects lost input.
            for (var frame = 0; frame < 400; frame++) guest.Boot.RunFrame();
            guest.Log($"CON: pre-input idle=400 frames; tick={guest.Boot.MasterTick}");
            guest.Send(payload + "\u001a", "*", maxFrames: 12000);
            guest.Send("\n", "A>");
            guest.Send($"TYPE B:{name}\n", "A>");
            guest.Send("DIR B:\n", "A>");
            writtenDisk = guest.Export("b-written.dsk", guest.Boot.Floppy.DriveB.ExportMedia());
            var fs = CpmFs.Open(Dsk.Load(writtenDisk), "cpm-hc80");
            Assert.Equal(name, Assert.Single(fs.List()).Name);
            writtenFile = guest.Export("bproof-before-remount.txt", fs.Get(name));
            var expected = Encoding.ASCII.GetBytes(payload);
            Assert.True(writtenFile.Length % 128 == 0);
            Assert.Equal(expected, writtenFile[..expected.Length]);
            Assert.All(writtenFile[expected.Length..], value => Assert.Equal((byte)0x1a, value));
            Assert.Equal(2048, freeBefore - FreeBytes(fs));
            guest.Log($"B free after write bytes={FreeBytes(fs)}; records={writtenFile.Length / 128}");
            Assert.Equal(guest.SourceHash, Hash(File.ReadAllBytes(SourcePath())));
        }

        // A genuinely new faithful machine: boot pristine A and mount saved B bytes.
        using var reboot = new Guest(output, "b-restart-remount", mountBlankB: false);
        reboot.Boot.Floppy!.DriveB.Mount(Dsk.Load(writtenDisk));
        reboot.Export("b-remounted.dsk", reboot.Boot.Floppy.DriveB.ExportMedia());
        reboot.BootToPrompt();
        reboot.Send("DIR B:\n", "A>");
        Assert.Contains("BPROOF", reboot.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
        var start = reboot.Boot.ConsoleText().Length;
        reboot.Send($"TYPE B:{name}\n", "A>");
        var tail = reboot.Boot.ConsoleText()[start..];
        foreach (var line in payload.Split('\r', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(line, tail, StringComparison.Ordinal);
        var persisted = CpmFs.Open(Dsk.Load(reboot.Boot.Floppy.DriveB.ExportMedia()), "cpm-hc80");
        Assert.Equal(writtenFile, reboot.Export("bproof-after-remount.txt", persisted.Get(name)));
        reboot.Send("STAT B:\n", "A>");
        Assert.Contains($"Bytes Remaining On B: {FreeBytes(persisted) / 1024}k", reboot.Boot.ConsoleText());
        reboot.Send($"ERA B:{name}\n", "A>");
        reboot.Send("DIR B:\n", "A>");
        Assert.Contains("NO FILE", reboot.Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
        start = reboot.Boot.ConsoleText().Length;
        reboot.Send("STAT B:\n", "A>");
        Assert.Contains($"Bytes Remaining On B: {freeBefore / 1024}k", reboot.Boot.ConsoleText()[start..]);
        var erased = reboot.Export("b-erased.dsk", reboot.Boot.Floppy.DriveB.ExportMedia());
        var erasedFs = CpmFs.Open(Dsk.Load(erased), "cpm-hc80");
        Assert.Empty(erasedFs.List());
        Assert.Throws<FileNotFoundException>(() => erasedFs.Get(name));
        Assert.Equal(freeBefore, FreeBytes(erasedFs));
        reboot.Log($"B free after ERA bytes={FreeBytes(erasedFs)}; reclaimed={FreeBytes(erasedFs) - FreeBytes(persisted)}");
        Assert.Equal(reboot.SourceHash, Hash(File.ReadAllBytes(SourcePath())));
    }

    private static int FreeBytes(CpmFs fs) =>
        (fs.MaximumBlock + 1 - fs.Parameters.DirectoryBlocks) * fs.Parameters.BlockSize -
        fs.List().Sum(file => file.AllocatedBytes);

    private static string SourcePath() => Path.Combine(StorageAssetTests.Root(), "disks/cpm/CPM22-HC.DSK");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Only scheduling/evidence around the existing BootHarness and InputController, no input implementation.
    private sealed class Guest : IDisposable
    {
        public BootHarness Boot { get; }
        public string SourceHash { get; }
        private readonly ITestOutputHelper output;
        private readonly InputQueue queue = new();
        private readonly InputController controller;
        private readonly StringBuilder evidence = new();
        private readonly string dir;
        // Step5a's proven-safe 16-frame edge cadence, not a runtime input mapping change.
        private const long PasteTicks = 16 * 355680L;

        public Guest(ITestOutputHelper output, string leg, byte[]? diskA = null, bool mountBlankB = true,
            Hc2000.Machine.MachineProfile? profileOverride = null, byte blankFill = 0xe5)
        {
            this.output = output;
            dir = Path.Combine(Path.GetTempPath(), $"wi118-step6-{leg}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            controller = new InputController(queue);
            var config = BootHarness.Config.ForCpmWarmBoot("hc2000-v2", "CPM22-HC.DSK");
            if (profileOverride is not null)
            {
                config.ProfileId = "hc2000-v2-ram-banks-v1";
                config.ProfileOverride = profileOverride;
                config.RomProvenance = "canonical v2 ROMs; labelled ram.banks BIOS reconstruction; supplied CP/M media";
            }
            Boot = new BootHarness(config);
            if (diskA is not null) Boot.Floppy!.DriveA.Mount(Dsk.Load(diskA));
            if (mountBlankB) Boot.Floppy!.DriveB.Mount(Dsk.Create(new DiskGeometry(80, 2, 9, 512), fill: blankFill));
            SourceHash = Hash(File.ReadAllBytes(SourcePath()));
            Log($"utc={DateTime.UtcNow:O}; leg={leg}; profile=hc2000-v2; fdc.instant=false; DB=none; evidence={dir}");
            foreach (var rom in new[] { "basic.rom", "cpm-hc2kzip.bin", "if1.rom" })
                Log($"roms/hc2000/v2/{rom}: {Hash(File.ReadAllBytes(Path.Combine(StorageAssetTests.Root(), "roms/hc2000/v2", rom)))}");
            Export("a-before.dsk", Boot.Floppy!.DriveA.ExportMedia());
            if (mountBlankB) Export("b-before.dsk", Boot.Floppy.DriveB.ExportMedia());
        }

        public void BootToPrompt()
        {
            Assert.True(BootHarness.ReachesEditor(Boot, maxFrames: 1000), Diagnostic("BASIC editor"));
            for (var f = 0; f < 30; f++) Boot.RunFrame();
            Boot.TypeKey(HcKey.P); Boot.TypeKey(HcKey.Space); Boot.TypeExtendedChord();
            Boot.TypeKey(HcKey.L); Boot.TypeKey(HcKey.Space);
            Boot.TypeKey(HcKey.D1); Boot.TypeKey(HcKey.D4); Boot.TypeKey(HcKey.D4);
            Boot.TypeKey(HcKey.D4); Boot.TypeKey(HcKey.D6);
            Boot.ArmConsoleHook();
            Boot.TypeKey(HcKey.Enter);
            var deadline = Boot.MasterTick + 10 * FrameTiming.MasterHz;
            while (Boot.MasterTick < deadline && !Boot.ConsoleText().Contains("A>", StringComparison.Ordinal))
                Boot.RunFrame();
            Assert.Contains("A>", Boot.ConsoleText());
            Assert.Contains("CP/M ver 2.2", Boot.ConsoleText(), StringComparison.OrdinalIgnoreCase);
            Assert.True(Boot.PhysicalBitmapShowsAPrompt(), Diagnostic("physical A>"));
            for (var f = 0; f < 40; f++) Boot.RunFrame();
            Log($"boot tick={Boot.MasterTick}; banner/prompt={Boot.ConsoleText()}");
        }

        public string Send(string text, string expected, int maxFrames = 4000)
        {
            Assert.False(controller.IsPasting);
            foreach (var ch in text) Assert.NotNull(KeyMap.Character(ch));
            var start = Boot.ConsoleText().Length;
            Log($"input={text.Replace("\n", "<ENTER>")}; expect={expected}; tick={Boot.MasterTick}");
            queue.Enqueue(new PasteText(text, PasteTicks));
            for (var f = 0; f < maxFrames; f++)
            {
                Boot.RunFrame();
                controller.ApplyPending(Boot.MasterTick, Boot.Keyboard);
                var tail = Boot.ConsoleText()[start..];
                if (!controller.IsPasting && tail.Contains(expected, StringComparison.OrdinalIgnoreCase))
                {
                    for (var settle = 0; settle < 40; settle++)
                    {
                        Boot.RunFrame();
                        controller.ApplyPending(Boot.MasterTick, Boot.Keyboard);
                    }
                    var reply = Boot.ConsoleText()[start..];
                    Log($"reply={reply}; tick={Boot.MasterTick}");
                    return reply;
                }
            }
            Assert.Fail(Diagnostic(expected));
            return string.Empty;
        }

        public byte[] Export(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            Log($"{name}: bytes={bytes.Length} sha256={Hash(bytes)}");
            return bytes;
        }

        public void Log(string line)
        {
            evidence.AppendLine(line);
            File.AppendAllText(Path.Combine(dir, "progress.txt"), line + Environment.NewLine);
            output.WriteLine(line);
        }
        private string Diagnostic(string expected)
        {
            var r = Boot.Cpu.Cpu.R;
            var snap = Boot.Floppy!.Fdc.Inspect();
            var pc = r.PC;
            var bytes = new byte[48];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Boot.Memory.Read((ushort)((pc - 16 + i + 0x10000) & 0xFFFF));
            var hexDump = Convert.ToHexString(bytes);
            var asc = new string(bytes.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
            return $"Expected {expected}; PC={pc:X4} AF={r.AF:X4} BC={r.BC:X4} DE={r.DE:X4} HL={r.HL:X4} SP={r.SP:X4} I={r.I:X2} IM={r.IM} IFF1={r.IFF1} " +
                   $"tick={Boot.MasterTick}, FDC phase={snap.Phase} status={Boot.Floppy.Fdc.ReadStatus():X2} pendingInterrupts=[{string.Join(",", snap.PendingInterrupts?.Select(x => $"{x.Status:X2}/cyl{x.Cylinder}") ?? Array.Empty<string>())} " +
                   $"lastCmd=[{string.Join(",", snap.LastCommand.Select(x => x.ToString("X2")))}] st0={snap.LastSt0?.ToString("X2")} st1={snap.LastSt1?.ToString("X2")} st2={snap.LastSt2?.ToString("X2")} " +
                   $"mem@{pc - 16:X4}=[{hexDump}] ({asc}) output={Boot.ConsoleText()}; evidence={dir}";
        }

        public void Dispose()
        {
            Log($"final console={Boot.ConsoleText()}; screen={Boot.ScreenText()}");
            if (Boot.Floppy is not null)
            {
                var snap = Boot.Floppy.Fdc.Inspect();
                Log($"fdc diag: phase={snap.Phase} mstatus={snap.MainStatus:X2} lastCmd=[{string.Join(",", snap.LastCommand.Select(x => x.ToString("X2")))}] st0={snap.LastSt0?.ToString("X2")} st1={snap.LastSt1?.ToString("X2")} st2={snap.LastSt2?.ToString("X2")} statusPolls={Boot.Floppy.Fdc.DiagStatusPolls} reads={Boot.Floppy.Fdc.DiagReads} readsReturned={Boot.Floppy.Fdc.DiagReadReturns} brSet={Boot.Floppy.Fdc.DiagBrSet} brClear={Boot.Floppy.Fdc.DiagBrClear}");
                var units = new[] { Boot.Floppy.DriveA, Boot.Floppy.DriveB };
                for (var i = 0; i < units.Length; i++)
                {
                    var unit = units[i];
                    Log($"drive unit={i}: image={(unit.Image is null ? "null" : "mounted")} motorOn={unit.MotorOn} headLoaded={unit.HeadLoaded} head={unit.HeadPosition} formattedTracks={unit.FormattedTracks.Count} pcn={snap.PresentCylinders[i]}");
                }
                var diagLog = Boot.Floppy.Fdc.DiagLog;
                const int diagnosticLimit = 4096;
                Log($"fdc diagLog chars={diagLog.Length}; bounded tail={diagLog[^Math.Min(diagnosticLimit, diagLog.Length)..]}");
            }
            var frame = Boot.RunFrame();
            Log($"frame sha256={Boot.FrameHash(frame)}");
            File.WriteAllText(Path.Combine(dir, "evidence.txt"), evidence.ToString());
        }
    }
}

public sealed class CpmDiskToolsFactAttribute : FactAttribute
{
    public CpmDiskToolsFactAttribute()
    {
        foreach (var relative in new[] { "disks/cpm/CPM22-HC.DSK", "roms/hc2000/v2/basic.rom",
                     "roms/hc2000/v2/cpm-hc2kzip.bin", "roms/hc2000/v2/if1.rom" })
            if (!File.Exists(Path.Combine(StorageAssetTests.Root(), relative)))
                Skip = "Copyrighted asset absent: " + relative;
    }
}
