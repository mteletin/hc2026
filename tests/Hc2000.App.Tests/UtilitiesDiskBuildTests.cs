using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class UtilitiesDiskBuildTests
{
    [Fact]
    public void AppBuildBundlesUtilitiesForFloppyAndMGeometryAndTheQualifiedV1BootDisk()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "disks");
        foreach (var name in new[] { "HC2000-UTIL.DSK", "HC2000-UTIL-8M.DSK", "CPM22-HC-ram-paged-upgrade.DSK", "utilities-manifest.json" })
            Assert.True(File.Exists(Path.Combine(directory, name)), "Build must generate " + name);
        var floppy = CpmFs.Open(Dsk.Load(File.ReadAllBytes(Path.Combine(directory, "HC2000-UTIL.DSK"))));
        var raw = File.ReadAllBytes(Path.Combine(directory, "HC2000-UTIL-8M.DSK"));
        Assert.Equal(8388608, raw.Length);
        var volume = CpmFs.OpenMVolumeV2(raw);
        Assert.Equal(Dpb.MVolumeV2(), volume.Parameters);
        string[] names = ["D.COM", "DDT.COM", "DRVSEL.COM", "HDFMT.COM", "LOAD.COM", "MBASIC.COM", "MFMT.COM", "PIP.COM", "RAMSTAT.COM", "STAT.COM", "SUBMIT.COM", "TAPEINIT.COM", "XSUB.COM"];
        Assert.Equal(names, floppy.List().Select(file => file.Name).Order());
        Assert.Equal(names, volume.List().Select(file => file.Name).Order());
        foreach (var name in names)
            Assert.Equal(floppy.Get(name), volume.Get(name));
    }
}
