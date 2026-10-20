using Hc2000.Machine.Memory;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class SuppliedCpmContractTests
{
    [Fact]
    public void SuppliedV2CpmHalfIsPreservedRatherThanRejectedOrPadded()
    {
        var cpm = Enumerable.Range(0, 0x4000).Select(n => (byte)(n / 0x2000 + 42)).ToArray();
        var memory = new HcMemory(new MemoryProfile(), new byte[0x4000], cpm, new byte[0x4000]);
        Assert.Equal(cpm, memory.Rom.Span.Slice(0x4000, 0x4000).ToArray());
        memory.WritePort7E(1);
        Assert.Equal(42, memory.Read(0)); Assert.Equal(43, memory.Read(0x2000));
    }
}
