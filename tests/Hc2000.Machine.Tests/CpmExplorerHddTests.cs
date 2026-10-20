using System.Buffers.Binary;
using System.Text;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class CpmExplorerHddTests
{
    private static byte[] Image()
    {
        var p = Dpb.HddVolume();
        var raw = Enumerable.Repeat((byte)0xe5, p.Geometry.Length).ToArray();
        Array.Clear(raw, 0, 64);
        raw[0] = 15;
        Encoding.ASCII.GetBytes("FILE    BIN").CopyTo(raw, 1);
        raw[9] |= 0x80; raw[10] |= 0x80;
        raw[15] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16), (ushort)p.DirectoryBlocks);
        raw[32] = 17;
        Encoding.ASCII.GetBytes("FAR     BIN").CopyTo(raw, 33);
        return raw;
    }

    [Fact]
    public void FlatVolumeUsesSameUserNameAttributeAndRecordPolicyWithoutMutation()
    {
        var raw = Image();
        var before = raw.ToArray();
        var explorer = CpmExplorer.OpenHddVolume(raw);
        Assert.Equal(Dpb.HddVolume(), explorer.Parameters);
        var good = Assert.Single(explorer.List(15));
        Assert.Equal("FILE.BIN", good.Name);
        Assert.Equal(128, good.Size); Assert.Equal(1, good.Records);
        Assert.True(good.ReadOnly); Assert.True(good.System); Assert.False(good.Refused);
        var refused = explorer.List().Single(e => e.User == 17);
        Assert.True(refused.Refused); Assert.Contains("0-15", refused.Refusal);
        Assert.Throws<ArgumentException>(() => explorer.List(16));
        Assert.Equal(before, raw);
        raw[0] = 0xe5;
        Assert.Single(explorer.List(15)); // owns its frozen copy, not caller storage
    }

    [Fact]
    public void NonCanonicalNamesStayVisibleAndRefused()
    {
        var raw = Image(); raw[1] = (byte)'f';
        var entry = CpmExplorer.OpenHddVolume(raw).List(15).Single();
        Assert.True(entry.Refused);
        Assert.Contains("canonical upper-case", entry.Refusal);
    }

    [Theory]
    [InlineData(-1)] [InlineData(1)]
    public void RefusesNonEightMiBVolumesInsteadOfGuessingGeometry(int delta)
        => Assert.Throws<InvalidDataException>(() => CpmExplorer.OpenHddVolume(new byte[Dpb.HddVolume().Geometry.Length + delta]));

    [Theory]
    [InlineData(0)] [InlineData(65535)]
    public void RefusesMissingOrOutOfRangeAllocation(int block)
    {
        var raw = Image();
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16), (ushort)block);
        Assert.Throws<InvalidDataException>(() => CpmExplorer.OpenHddVolume(raw));
    }
}
