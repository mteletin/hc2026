using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class MbasicRawSliceTests
{
    [Fact]
    public void LaterSendRawSliceExcludesPriorCrLfBytesAndKeepsHighBit()
    {
        var console = new ConcurrentQueue<byte>(Encoding.ASCII.GetBytes("prior\r\nOk\r\n"));
        var normalizedLength = "prior\nOk\n".Length;
        var slice = new HeadlessOracle.MbasicRawSlice(console);
        Assert.Equal(11, slice.Start);
        Assert.True(normalizedLength < slice.Start);
        Assert.Empty(slice.From(console.ToArray()).ToArray()); // no new bytes yet

        byte[] next = [0xD3, 0x0D, 0x0A];
        foreach (var value in next) console.Enqueue(value);
        var raw = console.ToArray();
        Assert.Equal(next, slice.From(raw).ToArray());
        Assert.NotEqual(next, raw.AsSpan(normalizedLength).ToArray()); // old index leaks prior CRLF

        // A further Send snapshots the new raw boundary, not the previous slice's start.
        var later = new HeadlessOracle.MbasicRawSlice(console);
        console.Enqueue(0x41);
        Assert.Equal(new byte[] { 0x41 }, later.From(console.ToArray()).ToArray());
        Assert.Empty(later.From([]).ToArray()); // out-of-range snapshot stays bounded
    }
}
