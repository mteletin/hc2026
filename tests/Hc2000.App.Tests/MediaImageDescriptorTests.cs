using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class MediaImageDescriptorTests
{
    [Fact]
    public void ExplicitChoicesHaveImmutableValueSemantics()
    {
        Assert.Equal(MediaImageKind.DskContainer, MediaImageDescriptor.DskContainer.Kind);
        Assert.Equal(MediaImageKind.HddRaw8MiB, MediaImageDescriptor.HddRaw8MiB.Kind);
        Assert.Null(MediaImageDescriptor.DskContainer.Geometry);
        Assert.Null(MediaImageDescriptor.HddRaw8MiB.Geometry);
        Assert.Equal(MediaImageDescriptor.RawFloppy(new(40, 1, 9, 512)),
            MediaImageDescriptor.RawFloppy(new(40, 1, 9, 512)));
        Assert.Throws<ArgumentNullException>(() => MediaImageDescriptor.RawFloppy(null!));
    }

    [Theory]
    [InlineData(1, 1, 1, 128)]
    [InlineData(102, 2, 1, 128)]
    [InlineData(1, 1, 29, 8192)]
    [InlineData(64, 2, 4, 8192)] // Exactly 4 MiB.
    public void NativeGeometryBoundsAndExactLength(int c, int h, int s, int size)
    {
        var geometry = new DiskGeometry(c, h, s, size);
        var descriptor = MediaImageDescriptor.RawFloppy(geometry);
        Assert.Equal(geometry, descriptor.Geometry);
        descriptor.ValidateLength(geometry.Length);
        Assert.Throws<InvalidDataException>(() => descriptor.ValidateLength(geometry.Length - 1));
        Assert.Throws<InvalidDataException>(() => descriptor.ValidateLength(geometry.Length + 1L));
    }

    [Theory]
    [InlineData(0, 1, 1, 128)]
    [InlineData(103, 1, 1, 128)]
    [InlineData(1, 0, 1, 128)]
    [InlineData(1, 3, 1, 128)]
    [InlineData(1, 1, 0, 128)]
    [InlineData(1, 1, 30, 128)]
    [InlineData(1, 1, 1, 64)]
    [InlineData(1, 1, 1, 256 + 128)]
    [InlineData(1, 1, 1, 16384)]
    [InlineData(65, 2, 4, 8192)] // Native geometry valid, exceeds App bound.
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, 2, 29, 8192)]
    public void InvalidOversizeAndOverflowInputsRefused(int c, int h, int s, int size)
        => Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.RawFloppy(new(c, h, s, size)));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4194305)]
    [InlineData(long.MaxValue)]
    public void ContainerLengthBound(long length)
        => Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.DskContainer.ValidateLength(length));

    [Fact]
    public void HddLengthIsExactButNotAFloppyOrAnAttachmentGrant()
    {
        MediaImageDescriptor.HddRaw8MiB.ValidateLength(HddMediaAdapter.ImageLength);
        Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.HddRaw8MiB.ValidateLength(8388607));
        Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.HddRaw8MiB.ValidateLength(8388609));
        Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.HddRaw8MiB.ValidateForFloppy(new()));
        MediaImageDescriptor.DskContainer.ValidateLength(MediaFiles.MaxDiskBytes);
    }

    [Fact]
    public void DeviceCompatibilityIsSeparateFromNativeGeometryValidity()
    {
        MediaImageDescriptor.RawFloppy(new(40, 1, 9, 512)).ValidateForFloppy(new(80, 2));
        Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.RawFloppy(new(81, 1, 1, 128)).ValidateForFloppy(new()));
        Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.RawFloppy(new(80, 2, 9, 512)).ValidateForFloppy(new(40, 2)));
        Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.RawFloppy(new(40, 2, 9, 512)).ValidateForFloppy(new(40, 1)));
        Assert.Throws<InvalidDataException>(() => MediaImageDescriptor.DskContainer.ValidateForFloppy(new(102, 2)));
        Assert.Throws<ArgumentNullException>(() => MediaImageDescriptor.DskContainer.ValidateForFloppy(null!));
    }
}
