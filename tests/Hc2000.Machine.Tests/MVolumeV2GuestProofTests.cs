using System.Security.Cryptography;
using Hc2000.App.Input;
using Hc2000.Asm;
using Hc2000.Machine;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Machine.Tests;

/// <summary>AC6 of hc2000-m-drive-8mib-v2: guest proof on the v2 paged profile.
/// One guest COM (m-volume-probe.asm) proves, on the Z80 through the v2 BIOS:
/// the paged ports are present and unlocked; M: initialises with the E5
/// directory block of the pinned 8 KiB geometry (track 0, sectors 0..63,
/// SPT 128 / BSH 6); and a file larger than 4 MiB (BIGFILE.BIN, 32,769 x
/// 128 B = 4,194,432 bytes) is created, written and read back with the
/// standard CP/M 2.2 BDOS file functions on the M: drive (FCB drive 13),
/// crossing the page group 0/1 boundary at track 256 (absolute sector
/// 32768) with the 0x7DFD group latch. The host then reads the RAM store
/// through CpmFs.OpenMVolumeV2 and verifies the directory allocation and
/// every byte of the file.</summary>
public sealed class MVolumeV2GuestProofTests(RamPagedV2PrivateOutput artifact, ITestOutputHelper output)
    : IClassFixture<RamPagedV2PrivateOutput>
{
    private const string GuestRelative = "tests/Hc2000.Machine.Tests/Fixtures/ram-paged-v2/m-volume-probe.asm";
    private const int StoreBytes = 8 * 1024 * 1024;
    private const int StoreBase = 0x10000;

    [RamPagedV2AssetFact]
    public void V2GuestWritesAndReadsBackOverFourMibOnMVolume() => RunGuestProof(32769);

    [RamPagedV2AssetFact]
    public void V2GuestFileIoSmoke() => RunGuestProof(3);

    private void RunGuestProof(int records)
    {
        var fileBytes = records * 128;
        var root = StorageAssetTests.Root();
        var sourcePath = Path.Combine(root, GuestRelative);
        var source = File.ReadAllText(sourcePath).Replace("DW 32769", $"DW {records}", StringComparison.Ordinal);
        var assembled = Assembler.AssembleText(source, file: sourcePath);
        Assert.True(assembled.Success, string.Join("; ", assembled.Diagnostics.Select(d => d.Message)));
        var program = Assert.Single(assembled.Segments).Bytes;
        var disk = Dsk.Load(artifact.Media);
        var fs = CpmFs.Open(disk, "cpm-hc80");
        fs.Put("M2PROBE.COM", program);
        var media = disk.Save();
        output.WriteLine($"M2PROBE source sha256={Hash(source)}; guest COM bytes={program.Length} sha256={Hash(program)}");
        output.WriteLine($"media bytes={media.Length} sha256={Hash(media)}; profile={MachineProfile.Hc2000V2PagedMTransferV2.Name}");

        var boot = new MachineBootAcceptanceTests.BootHarness(new()
        {
            ProfileId = "hc2000-v2-paged-m-v2",
            ProfileOverride = MachineProfile.Hc2000V2PagedMTransferV2,
            Boot = MachineBootAcceptanceTests.BootHarness.Kind.Cpm,
            WarmBoot = true,
            DiskPath = "CPM22-HC-ram-paged-v2-upgrade.DSK",
            DiskImage = media,
            FdcInstant = false,
            RomProvenance = "canonical v2 ROMs, v2 paged M: profile; m-volume-probe guest; CPM22-HC-ram-paged-v2-upgrade.DSK"
        });

        Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000),
            "BASIC did not reach the editor before the M: BIOS warm boot.");
        for (var i = 0; i < 30; i++) boot.RunFrame();
        var basic7e = boot.Memory.Latches.Port7E;
        boot.TypeKey(HcKey.P);
        boot.TypeKey(HcKey.Space);
        boot.TypeExtendedChord();
        boot.TypeKey(HcKey.L);
        boot.TypeKey(HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6 }) boot.TypeKey(key);
        boot.ArmConsoleHook();
        boot.TypeKey(HcKey.Enter);

        var deadline = boot.MasterTick + 10 * FrameTiming.MasterHz;
        for (var frame = 0; frame < 2000 && boot.MasterTick <= deadline; frame++)
        {
            boot.RunFrame();
            if (boot.Memory.Latches.Port7E != basic7e && boot.PhysicalBitmapShowsAPrompt()) break;
        }
        Assert.True(boot.Memory.Latches.Port7E != basic7e && boot.PhysicalBitmapShowsAPrompt(),
            $"v2 paged M: BIOS did not reach the CP/M A> prompt; screen={boot.ScreenText()} console={boot.ConsoleText()}");

        var queue = new InputQueue();
        var controller = new InputController(queue);

        var probeStart = boot.ConsoleText().Length;
        try
        {
            Send(boot, queue, controller, "M2PROBE\n", "A>", maxFrames: records == 3 ? 2000 : 120000);
        }
        finally
        {
            foreach (var name in new[] { "PortControl", "PortGroup", "Stage", "Error" })
                output.WriteLine($"{name}={boot.Memory.Read((ushort)assembled.Symbols[name]):X2}");
            var recordAddress = (ushort)assembled.Symbols["Record"];
            output.WriteLine($"Record={boot.Memory.Read(recordAddress) | boot.Memory.Read((ushort)(recordAddress + 1)) << 8}");
            var fcb = Enumerable.Range(0, 36).Select(i => boot.Memory.Read((ushort)(assembled.Symbols["FCB"] + i))).ToArray();
            output.WriteLine($"FCB={Convert.ToHexString(fcb)}");
            var directory = boot.Memory.Ram.Slice(StoreBase, 8192).ToArray();
            var allocated = Enumerable.Range(0, 256).Where(i => directory[i * 32] != 0xE5).ToArray();
            output.WriteLine($"Directory entries={allocated.Length}");
            foreach (var i in allocated.Take(3).Concat(allocated.TakeLast(3)).Distinct())
                output.WriteLine($"Entry {i}={Convert.ToHexString(directory.AsSpan(i * 32, 32))}");
            output.WriteLine($"latches after probe={boot.Memory.Latches}; PC={boot.Cpu.Cpu.R.PC:X4}; console={boot.ConsoleText()}");
        }
        var probeText = boot.ConsoleText()[probeStart..];
        Assert.DoesNotContain("M2 NG", probeText, StringComparison.Ordinal);
        Assert.Contains("M2 OK", probeText, StringComparison.Ordinal);
        output.WriteLine($"M2PROBE console tail={TrailingLines(probeText)}");

        // Canonical CP/M DIR lists names only; STAT DSK reports the DPB capacity.
        var statStart = boot.ConsoleText().Length;
        Send(boot, queue, controller, "STAT M: DSK:\n", "A>", maxFrames: 30000);
        var statText = boot.ConsoleText()[statStart..];
        output.WriteLine($"STAT M: DSK: output={statText}");
        Assert.Matches(@"65536:\s+128 Byte Record Capacity", statText);
        Assert.Matches(@"8192:\s+Kilobyte Drive\s+Capacity", statText);
        Assert.Matches(@"256:\s+32\s+Byte Directory Entries", statText);
        Assert.Matches(@"256:\s+Records/ Extent", statText);
        Assert.Matches(@"64:\s+Records/ Block", statText);
        Assert.Matches(@"128:\s+Sectors/ Track", statText);
        Assert.Matches(@"0:\s+Reserved Tracks", statText);
        var dirStart = boot.ConsoleText().Length;
        Send(boot, queue, controller, "DIR M:\n", "A>", maxFrames: 30000);
        var dirText = boot.ConsoleText()[dirStart..];
        output.WriteLine($"DIR M: tail={TrailingLines(dirText)}");
        Assert.Contains("BIGFILE  BIN", dirText, StringComparison.Ordinal);

        // Host-side verification of the guest's work over the RAM store.
        var store = boot.Memory.Ram.Span.Slice(StoreBase, StoreBytes).ToArray();
        output.WriteLine($"M-store raw sha256={Hash(store)}");
        var storeFs = CpmFs.OpenMVolumeV2(store);
        var files = storeFs.List();
        output.WriteLine($"M-store directory: {string.Join("; ", files.Select(f => $"{f.Name} u={f.User} rec={f.Records} bytes={f.AllocatedBytes} idx={f.DirectoryIndex}"))}");
        var file = Assert.Single(files);
        Assert.Equal("BIGFILE.BIN", file.Name);
        Assert.Equal(records, file.Records);
        Assert.Equal((fileBytes + 8191) / 8192 * 8192, file.AllocatedBytes);
        var data = storeFs.Get(file.Name);
        Assert.Equal(fileBytes, data.Length);
        for (var r = 0; r < records; r++)
        {
            for (var i = 0; i < 128; i++)
            {
                if (data[r * 128 + i] != (byte)((r + i) & 0xFF))
                {
                    Assert.Fail($"pattern mismatch at record {r} byte {i}: got {data[r * 128 + i]:X2} expected {(r + i) & 0xFF:X2}");
                }
            }
        }
        output.WriteLine($"M-store verify: {file.Name} {records} records / {fileBytes} bytes, pattern (r+i) mod 256 exact; allocation {file.AllocatedBytes} bytes");
    }

    private static string TrailingLines(string text)
    {
        var lines = text.Split('\n');
        return string.Join(" | ", lines.TakeLast(6).Select(l => l.TrimEnd('\r')));
    }

    private static void Send(MachineBootAcceptanceTests.BootHarness boot, InputQueue queue,
        InputController controller, string text, string expected, int maxFrames = 6000)
    {
        Assert.False(controller.IsPasting);
        foreach (var ch in text) Assert.NotNull(KeyMap.Character(ch));
        var start = boot.ConsoleText().Length;
        queue.Enqueue(new PasteText(text, 16 * 355680L));
        for (var frame = 0; frame < maxFrames; frame++)
        {
            boot.RunFrame();
            controller.ApplyPending(boot.MasterTick, boot.Keyboard);
            if (boot.ConsoleText()[start..].Contains("Bdos Err", StringComparison.OrdinalIgnoreCase))
            {
                for (var settle = 0; settle < 40; settle++) boot.RunFrame();
                Assert.Fail($"BDOS error: {boot.ConsoleText()[start..]}; BIOS DMA={boot.Memory.Read(0xD876):X2}{boot.Memory.Read(0xD875):X2}");
            }
            if (!controller.IsPasting && boot.ConsoleText()[start..].Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                for (var settle = 0; settle < 40; settle++)
                {
                    boot.RunFrame();
                    controller.ApplyPending(boot.MasterTick, boot.Keyboard);
                }
                return;
            }
        }
        Assert.Fail($"Expected {expected}; console={boot.ConsoleText()} screen={boot.ScreenText()}");
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
