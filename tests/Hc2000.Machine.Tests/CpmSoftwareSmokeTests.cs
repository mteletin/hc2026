using System.Security.Cryptography;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Input;
using Hc2000.Machine.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Machine.Tests;

public sealed class CpmSoftwareSmokeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("CPMZ80.DSK", "DDT")]
    [InlineData("CPM22-HC.DSK", "D")]
    public void LaunchesSelectedCpmTitleOnFaithfulDisk(string disk, string command)
    {
        var config = MachineBootAcceptanceTests.BootHarness.Config.ForCpmWarmBoot("hc2000-v2", disk);
        var sourcePath = Path.Combine(StorageAssetTests.Root(), "disks/cpm", disk);
        var source = File.ReadAllBytes(sourcePath);
        // The controller receives a parsed in-memory copy, never a writable source path.
        config.DiskImage = (byte[])source.Clone();
        if (command == "DDT")
        {
            Assert.Equal("66b8e4dedc08ddd872607efaecd2c2a4bf4ec8cb3869e7f3e468f966ccf12b87", Hash(source));
            var media = Dsk.Load(source);
            var title = CpmFs.Open(media).Get("DDT.COM", user: 0);
            Assert.Equal(4864, title.Length);
            Assert.Equal("bc4b0f085f8d1e37caf2a13850c70afa07acb626b22b1a7cb63368b3334dd448", Hash(title));
            output.WriteLine($"source geometry={media.UniformGeometry()}; DDT.COM user=0 bytes={title.Length} sha256={Hash(title)}");
        }
        var boot = new MachineBootAcceptanceTests.BootHarness(config);
        Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000),
            $"BASIC editor not reached for {disk}.");
        for (int f = 0; f < 30; f++) boot.RunFrame();
        boot.TypeKey(HcKey.P);
        boot.TypeKey(HcKey.Space);
        boot.TypeExtendedChord();
        boot.TypeKey(HcKey.L);
        boot.TypeKey(HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6 }) boot.TypeKey(key);
        boot.TypeKey(HcKey.Enter);
        boot.ArmConsoleHook();
        long deadline = boot.MasterTick + 10 * FrameTiming.MasterHz;
        for (int f = 0; f < 2000 && boot.MasterTick < deadline; f++) boot.RunFrame();
        var bootOutput = boot.ConsoleText();
        // The CPMZ80 image boots its own Z80 CCP, which labels its prompt A0>; the HC disk uses A>.
        var prompt = disk == "CPMZ80.DSK" ? "A0>" : "A>";
        Assert.Contains(prompt, bootOutput);
        if (disk == "CPM22-HC.DSK") Assert.True(boot.PhysicalBitmapShowsAPrompt(), "HC CP/M prompt absent from physical bitmap.");

        foreach (char c in command)
            boot.TypeKey(c switch
            {
                'A' => HcKey.A, 'D' => HcKey.D, 'T' => HcKey.T,
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            });
        boot.TypeKey(HcKey.Enter);
        for (int f = 0; f < 300; f++) boot.RunFrame();
        var console = boot.ConsoleText();
        var screen = boot.ScreenText();
        output.WriteLine($"disk={disk} command={command} tick={boot.MasterTick}");
        output.WriteLine($"CONOUT ({console.Length} chars): {console}");
        output.WriteLine($"SCREEN: {screen}");
        Assert.Contains(command, console);
        Assert.Contains(disk == "CPMZ80.DSK" ? "A0>" : "A>", console);
        if (command == "DDT")
        {
            Assert.Contains("DDT VERS 2.2", console);
            Assert.Contains("-", console);
            // hc-cpm PDF pages 4-5 (printed 7-8), source-manuals section 9:
            // Fs,f,bc fills memory; D{s,{f}} displays HEX/ASCII. No guest code,
            // registers or BDOS calls are injected. Only DDT changes scratch TPA RAM.
            byte before = boot.Memory.Read(0x00FF), after = boot.Memory.Read(0x0110);
            TypeDdtLine(boot, "F0100,010F,5A");
            for (ushort address = 0x0100; address <= 0x010F; address++)
                Assert.Equal((byte)0x5A, boot.Memory.Read(address));
            Assert.Equal(before, boot.Memory.Read(0x00FF));
            Assert.Equal(after, boot.Memory.Read(0x0110));
            TypeDdtLine(boot, "D0100,010F");
            var interaction = boot.ConsoleText()[console.Length..];
            output.WriteLine($"DDT interaction (expected 0100..010F = sixteen 5A bytes; adjacent bytes unchanged): {interaction}");
            Assert.Contains("0100 " + string.Join(" ", Enumerable.Repeat("5A", 16)), interaction);
            Assert.Equal(Hash(source), Hash(File.ReadAllBytes(sourcePath)));
            output.WriteLine($"source preserved sha256={Hash(File.ReadAllBytes(sourcePath))}");
        }
        else
        {
            Assert.Contains("File Count: 32", console);
            Assert.Contains("Free R/W Space: 102K", console);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TypeDdtLine(MachineBootAcceptanceTests.BootHarness boot, string line)
    {
        foreach (char c in line)
        {
            if (c == ',') boot.TypeShiftedKey(HcKey.SymbolShift, HcKey.N);
            else boot.TypeKey(c switch
            {
                'F' => HcKey.F, 'D' => HcKey.D, 'A' => HcKey.A,
                '0' => HcKey.D0, '1' => HcKey.D1, '5' => HcKey.D5,
                _ => throw new ArgumentOutOfRangeException(nameof(line))
            });
        }
        boot.TypeKey(HcKey.Enter);
        for (int frame = 0; frame < 100; frame++) boot.RunFrame();
    }

    [Fact]
    public void LaunchesDOnRamBanksUpgradeProfile()
    {
        var root = StorageAssetTests.Root();
        var config = MachineBootAcceptanceTests.BootHarness.Config.ForCpmWarmBoot("hc2000-v2", "CPM22-HC.DSK");
        config.ProfileId = "hc2000-v2-ram-banks-v1";
        config.ProfileOverride = UpgradeRamBanksBiosTests.RamBanksProfile();
        config.DiskImage = UpgradeRamBanksBiosTests.BuildUpgradedMedia(root);
        config.RomProvenance = "canonical v2 ROMs; labelled ram.banks BIOS reconstruction; CPM22-HC.DSK";
        var boot = new MachineBootAcceptanceTests.BootHarness(config);
        Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000));
        for (var frame = 0; frame < 30; frame++) boot.RunFrame();
        boot.TypeKey(HcKey.P); boot.TypeKey(HcKey.Space); boot.TypeExtendedChord();
        boot.TypeKey(HcKey.L); boot.TypeKey(HcKey.Space);
        foreach (var key in new[] { HcKey.D1, HcKey.D4, HcKey.D4, HcKey.D4, HcKey.D6 }) boot.TypeKey(key);
        boot.TypeKey(HcKey.Enter); boot.ArmConsoleHook();
        var deadline = boot.MasterTick + 10 * FrameTiming.MasterHz;
        while (boot.MasterTick < deadline && !boot.ConsoleText().Contains("A>", StringComparison.Ordinal)) boot.RunFrame();
        Assert.Contains("A>", boot.ConsoleText());
        Assert.True(boot.PhysicalBitmapShowsAPrompt());
        boot.TypeKey(HcKey.D); boot.TypeKey(HcKey.Enter);
        for (var frame = 0; frame < 300; frame++) boot.RunFrame();
        var console = boot.ConsoleText();
        output.WriteLine($"profile=hc2000-v2-ram-banks-v1; command=D; console={console}");
        Assert.Contains("File Count: 32", console);
        Assert.Contains("Free R/W Space: 102K", console);
        Assert.Contains("A>", console);
    }
}
