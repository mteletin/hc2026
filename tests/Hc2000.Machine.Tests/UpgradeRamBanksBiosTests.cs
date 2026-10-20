using System.Security.Cryptography;
using System.Text;
using Hc2000.App.Input;
using Hc2000.Asm;
using Hc2000.Machine;
using Hc2000.Machine.Input;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Machine.Tests;

/// <summary>End-to-end evidence for the labelled ram.banks M: BIOS reconstruction.</summary>
public sealed class UpgradeRamBanksBiosTests(ITestOutputHelper output)
{
    private const string SourceRelative = "bios/src/hc2000/upgrade/ram.banks.asm";
    private const string DiskRelative = "disks/cpm/CPM22-HC.DSK";
    private static readonly (int Side, int Sector)[] SystemSectors =
    [
        (0, 3), (0, 5), (0, 7), (0, 9), (0, 2), (0, 4), (0, 6), (0, 8),
        (1, 1), (1, 3), (1, 5), (1, 7), (1, 9), (1, 2), (1, 4)
    ];

    [Fact]
    public void RamBanksUpgradeBiosBootsSuppliedCpmDiskToPrompt()
    {
        var root = StorageAssetTests.Root();
        var media = InstallMInitializer(BuildUpgradedMedia(root), root);
        output.WriteLine($"test-media bytes={media.Length} sha256={Hash(media)}; source=CPM22-HC.DSK plus test guest utilities");
        output.WriteLine($"profile=hc2000-v2-ram-banks-v1 hash={Hash(Encoding.UTF8.GetBytes("hc2000-v2-ram-banks-v1|version=1|policy=hc2000-ram-banks-8k128-v1|banks=128|bankBytes=8192|window=A000-BFFF|selector=8B|count=8D"))}");
        var profile = RamBanksProfile();
        var boot = new MachineBootAcceptanceTests.BootHarness(new()
        {
            ProfileId = "hc2000-v2-ram-banks-v1",
            ProfileOverride = profile,
            Boot = MachineBootAcceptanceTests.BootHarness.Kind.Cpm,
            WarmBoot = true,
            DiskPath = "CPM22-HC.DSK",
            DiskImage = media,
            FdcInstant = false,
            RomProvenance = "canonical v2 ROMs, ram-banks profile; new ram.banks.asm reconstruction; CPM22-HC.DSK"
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
            $"ram.banks upgraded BIOS did not reach the CP/M A> prompt; screen={boot.ScreenText()} console={boot.ConsoleText()}");

        var bankRam = boot.Memory.Ram.Span.Slice(BankPolicy.BaseRamBytes, BankPolicy.BankCount * BankPolicy.BankBytes);
        Assert.All(bankRam.ToArray(), value => Assert.Equal(0, value));
        var queue = new InputQueue();
        var controller = new InputController(queue);
        var dirStart = boot.ConsoleText().Length;
        Send(boot, queue, controller, "DIR\n", "A>", maxFrames: 6000);
        var directoryText = boot.ConsoleText()[dirStart..];
        Assert.Contains("PIP", directoryText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FORMAT", directoryText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INITM", directoryText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CONSOLE", directoryText, StringComparison.OrdinalIgnoreCase);

        var consoleStart = boot.ConsoleOutput.Count;
        Send(boot, queue, controller, "CONSOLE\n", "A>", maxFrames: 2000);
        var consoleOutput = boot.ConsoleOutput.Skip(consoleStart).ToArray();
        Assert.True(ContainsSubsequence(consoleOutput, ConsoleSequence()),
            $"Upgraded BIOS CONOUT stream did not match step-5 guest sequence: {Convert.ToHexString(consoleOutput)}");

        Send(boot, queue, controller, "INITM\n", "A>", maxFrames: 2000);
        var pip = CpmFs.Open(Dsk.Load(File.ReadAllBytes(Path.Combine(root, DiskRelative))), "cpm-hc80").Get("PIP.COM");
        Send(boot, queue, controller, "PIP M:ROUND.COM=A:PIP.COM[OV]\n", "A>", maxFrames: 2000);
        Send(boot, queue, controller, "PIP A:RETURN.COM=M:ROUND.COM[OV]\n", "A>", maxFrames: 2000);
        var aAfter = Dsk.Load(boot.Floppy!.DriveA.ExportMedia());
        var aFs = CpmFs.Open(aAfter, "cpm-hc80");
        Assert.True(aFs.List().Any(file => file.Name == "RETURN.COM"),
            $"RETURN.COM missing; A files={string.Join(',', aFs.List().Select(file => file.Name))}; console={boot.ConsoleText()}");
        var returned = aFs.Get("RETURN.COM");
        Assert.Equal(7424, pip.Length);
        Assert.Equal(pip, returned);
        output.WriteLine($"supplied PIP.COM bytes={pip.Length} sha256={Hash(pip)}; A-return sha256={Hash(returned)}");
        var banks = boot.Memory.Ram.Span.Slice(BankPolicy.BaseRamBytes, BankPolicy.BankCount * BankPolicy.BankBytes);
        output.WriteLine($"A-after bytes={aAfter.Save().Length} sha256={Hash(aAfter.Save())}; M-bankRAM sha256={Hash(banks.ToArray())}");
        Assert.Equal(0, banks[0]);
        Assert.Equal(Encoding.ASCII.GetBytes("ROUND   COM"), banks.Slice(1, 11).ToArray());
        Assert.All(banks[32..8192].ToArray(), value => Assert.Equal(0xE5, value));
        Assert.Contains(banks[8192..].ToArray(), value => value != 0);
        Assert.Equal(BankPolicy.Disabled, boot.Memory.Latches.BankSelector);
    }

    internal static MachineProfile RamBanksProfile() => new()
    {
        Name = "hc2000-v2-ram-banks-v1",
        Memory = new(HcRomLayout.V2, RamBytes: BankPolicy.RamBytes,
            RamBanks: new RamBankDevice(BankPolicy.PolicyId, BankPolicy.BankCount, BankPolicy.BankBytes))
    };

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

    private static byte[] ConsoleSequence() =>
    [
        0x18, .. Encoding.ASCII.GetBytes("HC2000 CONSOLE CODE TEST"), 0x0D, 0x0A,
        0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x18, 0x1A, 0x1B, 0x1E,
        0x1B, 0x13, 0x1B, 0x1A, 0x1B, (byte)'A', 0x22, 0x24, 0x1B, (byte)'B',
        0x1B, (byte)'Y', 0x1B, (byte)'I', 0x03, 0x1B, (byte)'P', 0x05,
        0x1B, (byte)'Q', 0x06, 0x1B, (byte)'R', 0x17,
        0x1B, (byte)'I', 0x0B, 0x1B, (byte)'P', 0x0D,
        0x1B, (byte)'Q', 0x0E, 0x1B, (byte)'R', 0x3F,
        0x1B, (byte)'j', 0x1B, (byte)'k', 0x1B, (byte)'p', 0x1B, (byte)'q',
        0x1B, (byte)'r', 0x1B, (byte)'s', 0x1B, (byte)'t', 0x1B, (byte)'u',
        0x1B, (byte)'Z', 0xC1, 0x7F, 0x00, 0x01
    ];

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool ContainsSubsequence(byte[] source, byte[] expected)
    {
        for (var start = 0; start <= source.Length - expected.Length; start++)
            if (source.AsSpan(start, expected.Length).SequenceEqual(expected)) return true;
        return false;
    }

    internal static byte[] BuildUpgradedMedia(string root)
    {
        var bios = ExtractSuppliedBios(root);
        var sourcePath = Path.Combine(root, SourceRelative);
        var source = Assembler.AssembleText(File.ReadAllText(sourcePath), file: sourcePath);
        Assert.True(source.Success, string.Join("; ", source.Diagnostics.Select(d => d.Message)));
        foreach (var segment in source.Segments)
        {
            var offset = segment.Address - 0xD300;
            Assert.InRange(offset, 0, bios.Length - segment.Bytes.Length);
            segment.Bytes.CopyTo(bios, offset);
        }

        var disk = Dsk.Load(File.ReadAllBytes(Path.Combine(root, DiskRelative)));
        for (var i = 0; i < 4; i++)
        {
            var (side, sector) = SystemSectors[11 + i];
            bios.AsSpan(i * 512, 512).CopyTo(disk.Tracks.Single(t => t.Cylinder == 0 && t.Side == side)
                .Sectors.Single(s => s.R == sector).Data);
        }
        return disk.Save();
    }

    private static byte[] InstallMInitializer(byte[] media, string root)
    {
        var sourcePath = Path.Combine(root, "tests/Hc2000.Machine.Tests/Fixtures/ram-banks/init-m-directory.asm");
        var assembled = Assembler.AssembleText(File.ReadAllText(sourcePath), file: sourcePath);
        Assert.True(assembled.Success, string.Join("; ", assembled.Diagnostics.Select(d => d.Message)));
        var program = Assert.Single(assembled.Segments).Bytes;
        Console.WriteLine($"INITM source sha256={Hash(File.ReadAllBytes(sourcePath))}; guest COM bytes={program.Length} sha256={Hash(program)}");
        var disk = Dsk.Load(media);
        var fs = CpmFs.Open(disk, "cpm-hc80");
        fs.Put("INITM.COM", program);
        var consolePath = Path.Combine(root, "tests/assets/cpm/console-codes.asm");
        var console = Assembler.Assemble(consolePath);
        Assert.True(console.Success, string.Join("; ", console.Diagnostics.Select(d => d.Message)));
        fs.Put("CONSOLE.COM", console.ToBinary());
        return disk.Save();
    }

    private static byte[] ExtractSuppliedBios(string root)
    {
        var disk = Dsk.Load(File.ReadAllBytes(Path.Combine(root, DiskRelative)));
        var loaded = new byte[15 * 512];
        for (var i = 0; i < SystemSectors.Length; i++)
        {
            var (side, sector) = SystemSectors[i];
            disk.Tracks.Single(t => t.Cylinder == 0 && t.Side == side).Sectors
                .Single(s => s.R == sector).Data.CopyTo(loaded, i * 512);
        }
        return loaded.AsSpan(0x1600, 0x800).ToArray();
    }
}
