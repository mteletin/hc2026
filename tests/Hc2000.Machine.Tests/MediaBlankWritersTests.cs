using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>Shared blank-media writers, KB hc2000-media-home-scan-admission-v1 sec 4.</summary>
public sealed class MediaBlankWritersTests
{
    private const int VolumeBytes = 8_388_608;

    [Fact]
    public void TargetsAreTheThreeNamedWriters()
    {
        var targets = MediaBlanks.Targets();
        Assert.Equal(3, targets.Count);
        Assert.Equal(new[] { "floppy-720", "hdd-volume-8m", "tape-blank" }, targets.Select(t => t.Name));
        Assert.Equal(MediaBlanks.Floppy720().Length, targets[0].Write().Length);
        Assert.Equal(MediaBlanks.HddVolume8M().Length, targets[1].Write().Length);
        Assert.Equal(MediaBlanks.TapeBlank().Length, targets[2].Write().Length);
    }

    [Fact]
    public void Floppy720IsByteIdenticalToHcFsNewForCpmHc80()
    {
        var preset = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        // The exact bytes hc fs new writes today (StorageCommand.Filesystem, default extended container).
        Assert.Equal(Dsk.Create(preset.Geometry, DiskFormat.Extended).Save(), MediaBlanks.Floppy720());
        // Presets() stays four floppy DPBs; the HDD DPB is never added to the floppy preset list.
        Assert.Equal(4, Dpb.Presets().Count);
        Assert.DoesNotContain(Dpb.Presets(), p => p.Name == Dpb.HddVolume().Name);
    }

    [Fact]
    public void HddVolume8MIsEightMiBOfE5AcceptedByOpenHddVolume()
    {
        var bytes = MediaBlanks.HddVolume8M();
        Assert.Equal(VolumeBytes, bytes.Length);
        Assert.True(bytes.All(b => b == 0xe5));
        var fs = CpmFs.OpenHddVolume(bytes);
        Assert.Equal(Dpb.HddVolume().Name, fs.Parameters.Name);
        Assert.Empty(fs.List());
    }

    [Fact]
    public void HddVolume8MIsNotTheZeroFillAndTheFormatPathIsStructurallyClosed()
    {
        var bytes = MediaBlanks.HddVolume8M();
        Assert.NotEqual(new byte[VolumeBytes], bytes);
        // The Dpb.HddVolume() geometry (1024 cylinders) is not a Dsk geometry: the Format path's
        // Dsk.Create refuses it, so the sibling writer is the only path to this image.
        Assert.Throws<InvalidDataException>(() => CpmFs.Format(Dpb.HddVolume(), DiskFormat.Extended));
    }

    [Fact]
    public void TapeBlankIsZeroBytesAndLoadable()
    {
        var bytes = MediaBlanks.TapeBlank();
        Assert.Empty(bytes);
        var result = Tap.LoadBounded(bytes, 64 * 1024 * 1024, 4096);
        Assert.True(result.Success);
        Assert.Empty(result.Value.CopyRawBlocks());
    }
}
