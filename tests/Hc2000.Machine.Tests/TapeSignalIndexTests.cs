using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapeSignalIndexTests
{
    private static TapeSideImage Image(TapeTime duration, IEnumerable<TapeSignalChunk> chunks,
        IEnumerable<TapeStopMarker>? markers = null) =>
        new(duration, duration, chunks, markers ?? [], [], [], new(TapeFormat.Blank, null, true, [], null));
    private static ITapeSignalIndex Index(TapeSideImage image) =>
        new TapeSignalIndexFactory().Build(image, new()).Value;

    [Fact]
    public void ExactEdgeUsesNewLevelAndNextEdgeIsStrictlyLater()
    {
        var index = Index(Image(new(2, 7), [new(1, [new(false, new(1, 7)), new(true, new(1, 7))])]));
        Assert.False(index.Sample(default));
        Assert.True(index.Sample(new(1, 7)));
        Assert.Equal(new TapeTime(1, 7), index.NextEdge(default));
        Assert.Null(index.NextEdge(new(1, 7)));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Sample(new(2, 7)));
    }

    [Fact]
    public void RepetitionBoundaryUsesExplicitLevelsNotImplicitToggle()
    {
        var index = Index(Image(new(6, 1), [new(2, [new(false, new(1, 1)), new(true, new(1, 1)), new(false, new(1, 1))])]));
        Assert.False(index.Sample(new(3, 1)));
        Assert.Equal(new TapeTime(4, 1), index.NextEdge(new(2, 1)));
    }

    [Fact]
    public void MillionRepeatsSeekAndSliceStayCompact()
    {
        var index = Index(Image(new(2, 1), [new(1_000_000, [new(false, new(1, 1_000_000)), new(true, new(1, 1_000_000))])]));
        Assert.True(index.Sample(new(1_999_999, 1_000_000)));
        var slice = index.Slice(new(1, 2), new(3, 2), new()).Value;
        Assert.Equal(new TapeTime(1, 1), slice.Duration);
        Assert.True(slice.Chunks.Length <= 3);
        Assert.Contains(slice.Chunks, c => c.Repeat > 100_000);
    }

    [Fact]
    public void ReplaceIsHalfOpenPreservesOutsideAndEndMarker()
    {
        var index = Index(Image(new(3, 1), [new(1, [new(false, new(3, 1))])],
            [new(new(1, 1), 0, "remove", null, null), new(new(2, 1), 1, "retain", null, null)]));
        var replacement = Image(new(1, 1), [new(1, [new(true, new(1, 1))])]);
        var edited = index.Replace(new(1, 1), new(2, 1), replacement, new()).Value;
        var result = Index(edited);
        Assert.False(result.Sample(default));
        Assert.True(result.Sample(new(1, 1)));
        Assert.False(result.Sample(new(2, 1)));
        Assert.Single(edited.Markers);
        Assert.Equal("retain", edited.Markers[0].Label);
        Assert.False(index.Sample(new(1, 1)));
    }

    [Fact]
    public void ConstantOverwriteCoalescesJoinsWithoutExpandingRepeatedPeriods()
    {
        var low = Image(new(1800, 1), [new(1, [new(false, new(1800, 1))])]);
        var replacement = Image(new(1, 1), [new(1, [new(false, new(1, 1))])]);
        var constant = Index(low).Replace(new(1, 1), new(2, 1), replacement, new()).Value;
        Assert.Single(Assert.Single(constant.Chunks).Runs);
        var compact = Image(new(3, 1), [new(1_000_000, [new(false, new(1, 1_000_000)), new(true, new(1, 1_000_000)), new(false, new(1, 1_000_000))])]);
        var canonical = Index(compact).Replace(default, compact.Duration, compact, new()).Value;
        Assert.True(canonical.Chunks.Length <= 3);
        var index = Index(canonical);
        Assert.False(index.Sample(new(3, 1_000_000)));
        Assert.Equal(new TapeTime(4, 1_000_000), index.NextEdge(new(2, 1_000_000)));
    }

    [Fact]
    public void InvalidCoverageRepeatBombAndOutOfOrderMarkersFailDetached()
    {
        var factory = new TapeSignalIndexFactory();
        Assert.Equal(TapeError.InvalidFormat, factory.Build(Image(new(2, 1), [new(1, [new(false, new(1, 1))])]), new()).Failure!.Code);
        Assert.Equal(TapeError.BoundsExceeded, factory.Build(Image(new(1, 1), [new(ulong.MaxValue, [new(false, new(1, 1))])]), new()).Failure!.Code);
        Assert.False(factory.Build(Image(new(2, 1), [new(1, [new(false, new(2, 1))])],
            [new(new(1, 1), 1, "b", null, null), new(default, 0, "a", null, null)]), new()).Success);
    }
}
