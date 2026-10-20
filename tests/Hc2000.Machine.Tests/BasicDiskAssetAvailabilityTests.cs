using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class BasicDiskAssetAvailabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hc-basic-assets-" + Guid.NewGuid().ToString("N"));

    public BasicDiskAssetAvailabilityTests()
    {
        // These are existence-only prerequisites, never ROMs passed to an emulator.
        Directory.CreateDirectory(Path.Combine(root, "roms", "hc2000", "v1"));
        Directory.CreateDirectory(Path.Combine(root, "roms", "hc2000", "v2"));
        File.WriteAllBytes(Path.Combine(root, "roms", "hc2000", "v1", "basic.rom"), [0]);
        File.WriteAllBytes(Path.Combine(root, "roms", "hc2000", "v2", "cpm-hc2kzip.bin"), [0]);
        Directory.CreateDirectory(Path.Combine(root, "disks", "basic"));
    }

    public static TheoryData<string, string> Titles => new()
    {
        { "ALBA8.DSK", "run" },
        { "ALEO-UTILS.DSK", "artist2" },
        { "ALEO-GAMES1.DSK", "run" },
        { "BASDEMO35.DSK", "run" },
        { "BASIC2.DSK", "run" },
        { "CRISTIG1.DSK", "run" },
        { "GEOOLDIES.DSK", "ManicMiner" },
        { "RARESGOLDENAXE.DSK", "goldenaxe" },
    };

    [Theory]
    [MemberData(nameof(Titles))]
    public void MissingSelectedDiskExplicitlySkipsBothRowsWithRomPrerequisitesPresent(string disk, string program)
    {
        Assert.True(File.Exists(Path.Combine(root, "roms", "hc2000", "v1", "basic.rom")));
        Assert.True(File.Exists(Path.Combine(root, "roms", "hc2000", "v2", "cpm-hc2kzip.bin")));
        var reason = "Copyrighted asset is absent: " + Path.Combine("disks", "basic", disk);
        Assert.Equal(reason, new BasicDiskDataAttribute(root, disk, []).Skip);
        Assert.Equal(reason, new BasicDiskDataAttribute(root, disk, [program]).Skip);
    }

    [Theory]
    [MemberData(nameof(Titles))]
    public void PresentSelectedDiskKeepsBothRowsEvenWhenOtherDisksAreAbsent(string disk, string program)
    {
        // Even invalid bytes must reach existing assertions, not be hidden by an asset skip.
        File.WriteAllBytes(Path.Combine(root, "disks", "basic", disk), [0]);
        var cat = new BasicDiskDataAttribute(root, disk, []);
        var load = new BasicDiskDataAttribute(root, disk, [program]);
        Assert.Null(cat.Skip);
        Assert.Null(load.Skip);
        var catMethod = typeof(BasicDiskAcceptanceTests).GetMethod(nameof(BasicDiskAcceptanceTests.BasicV2CatReadsEachTitleDisk))!;
        var loadMethod = typeof(BasicDiskAcceptanceTests).GetMethod(nameof(BasicDiskAcceptanceTests.FreshBasicV2LoadsAndRunsEachTitleProgram))!;
        Assert.Equal(new object[] { disk }, Assert.Single(cat.GetData(catMethod)));
        Assert.Equal(new object[] { disk, program }, Assert.Single(load.GetData(loadMethod)));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
