using System.Buffers.Binary;
using Xunit;

namespace Hc2000.App.Tests;

public sealed partial class HeadlessOracle
{
    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(0xbeef)]
    public void BaudDivisorUsesPhysicalWordAcrossLogicalAliasChanges(ushort alias)
    {
        var (_, memory) = BaudFixture();
        memory.SetCpm(true); // BAUD's logical store in CP/M selects physical FF0C/FF0D.
        memory.Write(0xdf0c, 0xc5);
        memory.Write(0xdf0d, 0x04);
        Assert.Equal((ushort)1221, BinaryPrimitives.ReadUInt16LittleEndian(memory.Ram.Span[0xff0c..]));
        BinaryPrimitives.WriteUInt16LittleEndian(memory.Ram.Span[0xdf0c..], alias);
        var before = memory.Ram.ToArray();

        foreach (var cpm in new[] { false, true, false, true })
        {
            memory.SetCpm(cpm);
            var latches = memory.Latches;
            var logical = (ushort)(memory.Read(0xdf0c) | memory.Read(0xdf0d) << 8);
            Assert.Equal(cpm ? (ushort)1221 : alias, logical);
            Assert.Equal((ushort)1221, ReadBaudDivisor(memory));
            Assert.Equal(latches, memory.Latches);
            Assert.Equal(before, memory.Ram.ToArray());
        }
    }

    [Fact]
    public void BaudDivisorObservesNewPhysicalValueRatherThanCachedOrAliasValue()
    {
        var (_, memory) = BaudFixture();
        memory.SetCpm(false);
        BinaryPrimitives.WriteUInt16LittleEndian(memory.Ram.Span[0xdf0c..], 1221);
        foreach (ushort divisor in new ushort[] { 12, 1221, 107, 0, 12 })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(memory.Ram.Span[0xff0c..], divisor);
            Assert.Equal(divisor, ReadBaudDivisor(memory));
        }
    }
}
