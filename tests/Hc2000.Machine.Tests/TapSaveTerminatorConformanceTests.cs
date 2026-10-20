using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapSaveTerminatorConformanceTests
{
    // Independent conformance fixture, NOT a generated/captured SAVE or guest proof.
    // Widths are fixed reference ticks from the accepted contract, never codec output.
    // Doubled ticks represent the exact inclusive 769.5..940.5 zero-window boundaries.
    private static List<TapeRun> Block(int doubledTerminatorTicks, int? gapTicks, bool highGap = true)
    {
        var runs = new List<TapeRun>(); bool level = false;
        void Pulse(int ticks) { runs.Add(new(level, new(ticks, 3_500_000))); level = !level; }
        // Even pilot count leaves the exit edge low, followed by post-inverter D6 high.
        // Odd count instead supplies the deliberately invalid low-gap fixture.
        for (int i = 0; i < (highGap ? 3200 : 3201); i++) Pulse(2168);
        Pulse(667); Pulse(735);
        foreach (byte b in new byte[] { 255, 3, 252 }) // complete flag/data/checksum, XOR 0
            for (int bit = 7; bit >= 0; bit--)
            {
                int ticks = (b & (1 << bit)) == 0 ? 855 : 1710;
                Pulse(ticks); Pulse(ticks);
            }
        Assert.Equal(!highGap, level);
        runs.Add(new(level, new(doubledTerminatorTicks, 7_000_000))); level = !level;
        if (gapTicks is int gap) runs.Add(new(level, new(gap, 3_500_000)));
        return runs;
    }

    private static TapeResult<TapeWriteResult> Export(IEnumerable<TapeRun> runs)
    {
        // Exact capture end: no automatic blank padding that could invent a missing gap.
        var duration = runs.Aggregate(default(TapeTime), (sum, run) => sum + run.Duration);
        var image = new TapeSideImage(duration, duration, [new(1, runs)], [], [], [],
            new(TapeFormat.Blank, null, true, [], null));
        return new TapCodec().Write(image, new(TapeFormat.Tap, TapeSide.A));
    }

    private static void Refused(TapeResult<TapeWriteResult> result)
    {
        Assert.False(result.Success);
        Assert.Equal(TapeError.ExactEncodingUnavailable, result.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Theory]
    [InlineData(1539, 2800)] // exactly 855 - 10%
    [InlineData(1540, 2800)]
    [InlineData(1710, 2800)]
    [InlineData(1866, 3_479_836)] // recorded MIC6 exit/gap widths, not a trace recapture
    [InlineData(1880, 2800)]
    [InlineData(1881, 2800)] // exactly 855 + 10%
    [InlineData(1866, 1_000_000_000)] // gap has no recognizer upper bound
    public void ZeroWindowEdgeWithSufficientHighGapPreservesDecodedBytes(int edge, int gap)
    {
        var result = Export(Block(edge, gap));
        Assert.True(result.Success, result.Failure?.Detail);
        Assert.Equal(new byte[] { 3, 0, 255, 3, 252 }, result.Value.Bytes.ToArray());
        Assert.Equal(TapeSignalFidelity.DataOnly, result.Value.Report.Signal);
    }

    [Theory]
    [InlineData(1538)] // just below zero window
    [InlineData(1882)] // just above zero window
    [InlineData(3078)] // one-window lower boundary is NOT a terminator
    [InlineData(3420)]
    [InlineData(3762)] // one-window upper boundary
    public void LoneWidthsOutsideZeroWindowAreRefusedEvenWithLongHighGap(int edge) =>
        Refused(Export(Block(edge, 3_479_836)));

    [Theory]
    [InlineData(1710)]
    [InlineData(1866)]
    [InlineData(3420)]
    public void EndOfCaptureCannotSubstituteForTheFollowingHighGap(int edge) =>
        Refused(Export(Block(edge, null)));

    [Theory]
    [InlineData(2800)]
    [InlineData(3_479_836)]
    public void LowGapCannotTerminateAnOtherwiseCompleteBlock(int gap) =>
        Refused(Export(Block(1866, gap, highGap: false)));

    [Theory]
    [InlineData(2000)]
    [InlineData(2799)]
    public void HighGapBelow2800ReferenceTicksIsRefused(int gap) =>
        Refused(Export(Block(1866, gap)));

    [Fact]
    public void AnotherBitWidthRetainsTheExistingHalfBitMismatchRule()
    {
        var runs = Block(1866, null);
        runs.Add(new(true, new(1710, 3_500_000)));
        runs.Add(new(false, new(3_479_836, 3_500_000)));
        var result = Export(runs);
        Refused(result);
        Assert.Equal("Mismatched/incomplete half-bit pair.", result.Failure!.Detail);
    }

    [Fact]
    public void WitnessedExitAndHighGapCanSeparateTwoCompleteBlocks()
    {
        var runs = Block(1866, 3_479_836);
        runs.AddRange(Block(1866, 3_479_836));
        var result = Export(runs);
        Assert.True(result.Success, result.Failure?.Detail);
        Assert.Equal(new byte[] { 3, 0, 255, 3, 252, 3, 0, 255, 3, 252 }, result.Value.Bytes.ToArray());
    }
}
