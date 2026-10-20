using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class RawFloppyAdapterTests
{
    [Theory]
    [InlineData(40, 1, 9, 512)]
    [InlineData(80, 2, 9, 512)]
    [InlineData(1, 1, 1, 128)]
    [InlineData(1, 2, 29, 8192)]
    public void LiteralRawRoundTripAndNativeCylinderSideSectorOrdering(int c, int h, int s, int size)
    {
        var descriptor = MediaImageDescriptor.RawFloppy(new(c, h, s, size));
        var bytes = Enumerable.Range(0, descriptor.Geometry!.Length).Select(i => (byte)(i * 17 + i / size)).ToArray();
        var before = bytes.ToArray();
        var disk = RawFloppyAdapter.Load(bytes, descriptor, new());
        Assert.Equal(DiskFormat.Raw, disk.Format);
        Assert.Equal(before, RawFloppyAdapter.Save(disk, descriptor, new()));
        foreach (var track in disk.Tracks)
            foreach (var sector in track.Sectors)
            {
                var offset = ((track.Cylinder * h + track.Side) * s + sector.R - 1) * size;
                Assert.Equal(before.AsSpan(offset, size).ToArray(), sector.Data);
                Assert.Equal(track.Cylinder, (int)sector.C);
                Assert.Equal(track.Side, (int)sector.H);
            }
        var last = disk.Tracks[^1].Sectors[^1];
        last.Data[^1] ^= 0xff;
        var expected = before.ToArray(); expected[^1] ^= 0xff;
        Assert.Equal(expected, RawFloppyAdapter.Save(disk, descriptor, new()));
        Assert.Equal(before, bytes); // No mutation of the caller's source bytes.
    }

    [Theory]
    [InlineData(DiskFormat.Standard)]
    [InlineData(DiskFormat.Extended)]
    public void ExistingDskRepresentationAndMetadataArePreserved(DiskFormat format)
    {
        var bytes = Dsk.Create(new(40, 2, 9, 512), format).Save();
        bytes[34] = 0x7a; // Creator metadata and trailing bytes must survive, not be regenerated.
        bytes = [.. bytes, 0x11, 0x22];
        var descriptor = MediaImageDescriptor.DskContainer;
        var disk = RawFloppyAdapter.Load(bytes, descriptor, new());
        Assert.Equal(format, disk.Format);
        Assert.Equal(bytes, RawFloppyAdapter.Save(disk, descriptor, new()));
        disk.Tracks[0].Sectors[0].Data[0] = 0x12;
        var expected = Dsk.Load(bytes); expected.Tracks[0].Sectors[0].Data[0] = 0x12;
        Assert.Equal(expected.Save(), RawFloppyAdapter.Save(disk, descriptor, new()));
    }

    [Theory]
    [InlineData("EXTENDED CPC DSK File\r\nDisk-Info\r\n")]
    [InlineData("MV - CPCEMU Disk-File\r\nDisk-Info\r\n")]
    public void RawDescriptorRejectsContainerHeaderEvenAtExactRawLength(string header)
    {
        var bytes = new byte[512];
        System.Text.Encoding.ASCII.GetBytes(header).CopyTo(bytes, 0);
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load(bytes,
            MediaImageDescriptor.RawFloppy(new(1, 1, 1, 512)), new()));
    }

    [Fact]
    public void NoGeometryInferenceOrImplicitFormatConversion()
    {
        var raw = new byte[512];
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load(raw, MediaImageDescriptor.DskContainer, new()));
        Assert.Throws<ArgumentNullException>(() => RawFloppyAdapter.Load(raw, null!, new()));
        var descriptor = MediaImageDescriptor.RawFloppy(new(1, 1, 1, 512));
        var disk = RawFloppyAdapter.Load(raw, descriptor, new());
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Save(disk, MediaImageDescriptor.DskContainer, new()));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Save(disk,
            MediaImageDescriptor.RawFloppy(new(1, 1, 2, 256)), new())); // Equal byte size is not equal geometry.
        var container = Dsk.Create(new(1, 1, 1, 512));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Save(container, descriptor, new()));
    }

    [Fact]
    public void BoundsWrongDeviceMalformedBytesAndHddAreRefused()
    {
        var descriptor = MediaImageDescriptor.RawFloppy(new(1, 1, 1, 128));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load(new byte[127], descriptor, new()));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load(new byte[129], descriptor, new()));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load([], MediaImageDescriptor.DskContainer, new()));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load(new byte[MediaFiles.MaxDiskBytes + 1], MediaImageDescriptor.DskContainer, new()));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load([], MediaImageDescriptor.HddRaw8MiB, new()));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load([], MediaImageDescriptor.RawFloppy(new(81, 1, 1, 128)), new()));
        var twoSides = Dsk.Create(new(40, 2, 1, 128)).Save();
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load(twoSides, MediaImageDescriptor.DskContainer, new(40, 1)));
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Load("EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8, MediaImageDescriptor.DskContainer, new()));
    }

    [Fact]
    public void SaveRechecksHeaderAmbiguityAndDoesNotRewriteSource()
    {
        var descriptor = MediaImageDescriptor.RawFloppy(new(1, 1, 1, 128));
        var disk = RawFloppyAdapter.Load(new byte[128], descriptor, new());
        "EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8.CopyTo(disk.Tracks[0].Sectors[0].Data);
        Assert.Throws<InvalidDataException>(() => RawFloppyAdapter.Save(disk, descriptor, new()));
        Assert.Equal((byte)'E', disk.Tracks[0].Sectors[0].Data[0]);
    }
}
