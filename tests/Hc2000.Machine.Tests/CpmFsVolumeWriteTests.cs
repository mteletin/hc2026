using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class CpmFsVolumeWriteTests
{
    private static byte[] BlankVolume()
    {
        var p = Dpb.HddVolume();
        return Enumerable.Repeat((byte)0xe5, p.Geometry.Length).ToArray();
    }

    [Fact]
    public void PutOnVolumeAcceptsAndReadsBackThroughOpenHddVolume()
    {
        var fs = CpmFs.OpenHddVolume(BlankVolume());
        var data = Enumerable.Range(1, 300).Select(v => (byte)v).ToArray();
        fs.Put("DATA.BIN", data);
        var exported = fs.ExportVolumeBytes();
        Assert.Equal(Dpb.HddVolume().Geometry.Length, exported.Length);
        Assert.Equal(8_388_608, exported.Length);
        Assert.NotEqual(new byte[8_388_608], exported); // written volume differs from hc hdd create's zero-fill
        var reopened = CpmFs.OpenHddVolume(exported);
        var readBack = reopened.Get("DATA.BIN"); // records are padded to 128-byte records
        Assert.Equal(data, readBack.AsSpan(0, data.Length).ToArray());
        Assert.Equal(0, readBack.Length % 128);
        Assert.Contains(reopened.List(), f => f.Name == "DATA.BIN");
    }

    [Fact]
    public void DeleteOnVolumeLeavesAReadableVolume()
    {
        var fs = CpmFs.OpenHddVolume(BlankVolume());
        fs.Put("TWO.BIN", [1, 2, 3]);
        fs.Put("THREE.BIN", [4, 5, 6]);
        fs.Delete("TWO.BIN");
        var reopened = CpmFs.OpenHddVolume(fs.ExportVolumeBytes());
        var names = reopened.List().Select(f => f.Name).ToArray();
        Assert.DoesNotContain("TWO.BIN", names);
        Assert.Equal([4, 5, 6], reopened.Get("THREE.BIN").AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void ExportVolumeBytesOnFloppyRefuses()
    {
        var p = Dpb.Presets().Single(x => x.Name == "cpm-hc80");
        var floppy = CpmFs.Open(Dsk.Create(p.Geometry));
        Assert.Throws<InvalidOperationException>(() => floppy.ExportVolumeBytes());
    }
}
