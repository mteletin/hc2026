using Hc2000.Asm;
using Hc2000.Machine.Input;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Static provenance/preservation gate for the labelled disk.blockport BIOS reconstruction.</summary>
public sealed class UpgradeBiosTests
{
    private const string DiskPath = "disks/cpm/CPM22-HC.DSK";
    private const string SourcePath = "bios/src/hc2000/upgrade/disk.blockport.asm";
    private static readonly (byte Side, byte R)[] SystemSectors =
    [
        (0, 3), (0, 5), (0, 7), (0, 9), (0, 2), (0, 4), (0, 6), (0, 8),
        (1, 1), (1, 3), (1, 5), (1, 7), (1, 9), (1, 2), (1, 4)
    ];

    [Fact]
    public void DiskBlockportBiosBuildPreservesSuppliedJumpTableAndDpbs()
    {
        var root = StorageAssetTests.Root();
        var diskBytes = File.ReadAllBytes(Path.Combine(root, DiskPath));
        var disk = Dsk.Load(diskBytes);
        using var loaded = new MemoryStream();
        foreach (var (side, r) in SystemSectors)
            loaded.Write(disk.Tracks.Single(t => t.Cylinder == 0 && t.Side == side)
                .Sectors.Single(s => s.R == r).Data);
        var baseline = loaded.ToArray();
        Assert.Equal(7680, baseline.Length);

        var source = File.ReadAllText(Path.Combine(root, SourcePath));
        var assembled = Assembler.AssembleText(source);
        Assert.True(assembled.Success, string.Join("; ", assembled.Diagnostics.Select(d => d.Message)));
        var image = assembled.ToBinary();
        Assert.Equal(0x800, image.Length);

        var biosOffset = 0xd300 - 0xbd00;
        var originalBios = baseline.AsSpan(biosOffset, 0x800).ToArray();
        Assert.Equal(originalBios.AsSpan(0, 0x33).ToArray(), image.AsSpan(0, 0x33).ToArray());
        Assert.Equal(originalBios.AsSpan(0x66, 64).ToArray(), image.AsSpan(0x66, 64).ToArray());
        Assert.Equal(new byte[] { 0xcd, 0x90, 0xd6 }, image.AsSpan(0xd67e - 0xd300, 3).ToArray());
        Assert.Equal(new byte[] { 0xcd, 0x84, 0xd6, 0xd0, 0x3a, 0x8e, 0xd8, 0xc9 }, image.AsSpan(0xd7b7 - 0xd300, 8).ToArray());
        Assert.Equal(new byte[23], image.AsSpan(0xd7d7 - 0xd300, 23).ToArray());
    }

    [Fact]
    public void BlockportProfileBootsCpmAndRunsDirThroughTheUpgradedBios()
    {
        var root = StorageAssetTests.Root();
        var source = File.ReadAllText(Path.Combine(root, SourcePath));
        var assembled = Assembler.AssembleText(source);
        Assert.True(assembled.Success, string.Join("; ", assembled.Diagnostics.Select(d => d.Message)));
        var bios = assembled.ToBinary();
        var disk = Dsk.Load(File.ReadAllBytes(Path.Combine(root, DiskPath)));
        for (var i = 11; i < SystemSectors.Length; i++)
        {
            var (side, r) = SystemSectors[i];
            bios.AsSpan((i - 11) * 512, 512).CopyTo(disk.Tracks.Single(t => t.Cylinder == 0 && t.Side == side)
                .Sectors.Single(s => s.R == r).Data);
        }

        var config = MachineBootAcceptanceTests.BootHarness.Config.ForCpmWarmBoot("hc2000-v2-blockport-v1", "CPM22-HC.DSK");
        config.DiskImage = disk.Save();
        var boot = new MachineBootAcceptanceTests.BootHarness(config);
        Assert.True(MachineBootAcceptanceTests.BootHarness.ReachesEditor(boot, maxFrames: 1000),
            "BASIC did not reach its input prompt before the upgraded CP/M boot.");
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
        var prompt = false;
        for (var frame = 0; frame < 2000 && boot.MasterTick <= deadline; frame++)
        {
            boot.RunFrame();
            if (boot.Memory.Latches.Port7E != basic7e && boot.PhysicalBitmapShowsAPrompt())
            {
                prompt = true;
                break;
            }
        }
        Assert.True(prompt, $"Blockport CP/M did not reach A> within 10 emulated seconds (tick {boot.MasterTick}, block ops {boot.BlockDiskCommands}, statuses {string.Join(',', boot.BlockDiskStatuses)}). Screen: {boot.ScreenText()} Console: {boot.ConsoleText()}");
        Assert.True(boot.BlockDiskCommands > 0, "The upgraded BIOS boot did not issue block-port commands.");
        Assert.All(boot.BlockDiskStatuses, status => Assert.Equal(0, status));

        var outputStart = boot.ConsoleOutput.Count;
        boot.TypeString("DIR", framesPerKey: 8);
        boot.TypeKey(HcKey.Enter, frames: 12);
        deadline = boot.MasterTick + 10 * FrameTiming.MasterHz;
        for (var frame = 0; frame < 3000 && boot.MasterTick <= deadline; frame++)
        {
            boot.RunFrame();
            if (frame > 100 && boot.PhysicalBitmapShowsAPrompt()) break;
        }
        var listing = string.Concat(boot.ConsoleOutput.Skip(outputStart)
            .Select(b => b is >= 32 and < 127 ? (char)b : ' '));
        Assert.Contains("BW", listing);
        Assert.Contains("PIP", listing);
        Assert.True(boot.BlockDiskCommands > 1, "DIR did not use the block-port BIOS read path.");
        Assert.All(boot.BlockDiskStatuses, status => Assert.Equal(0, status));
    }
}