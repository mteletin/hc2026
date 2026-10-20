using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TapRecordingExportTests
{
    // Independently authored pulse-family fixture, not codec-generated SAVE or guest proof.
    private static TapeSideImage Signal(byte[] frame, int pilot = 3201, int hz = 3_546_895,
        int zero = 855, int one = 1710, bool extraHighTail = false, bool mismatch = false, bool steadyHighGap = false)
    {
        var runs = new List<TapeRun>(); bool level = false;
        void Pulse(int ticks) { runs.Add(new(level, new(ticks, hz))); level = !level; }
        for (int i = 0; i < pilot; i++) Pulse(2168);
        Pulse(667); Pulse(735);
        foreach (byte b in frame)
            for (int bit = 7; bit >= 0; bit--)
            {
                int width = (b & (1 << bit)) == 0 ? zero : one;
                Pulse(width); Pulse(mismatch ? (width == zero ? one : zero) : width);
            }
        // For the odd pilot chosen above the final half-bit ends into high.
        var elapsed = runs.Aggregate(default(TapeTime), (sum, run) => sum + run.Duration);
        // A short high stretch after the block is neither a bit nor a gap: unexplained material.
        if (level) { Pulse(extraHighTail ? 1200 : steadyHighGap ? 35_000 : 3500); elapsed = runs.Aggregate(default(TapeTime), (sum, run) => sum + run.Duration); }
        runs.Add(new(false, new TapeTime(1800, 1) - elapsed));
        return new(new(1800, 1), elapsed, [new(1, runs)], [], [], [], new(TapeFormat.Blank, null, true, [], null));
    }

    [Fact]
    public void RecognizesNonNominalPilotCountOnNativeHcClockAndReportsOnlyDecodedData()
    {
        var codec = new TapCodec(); Assert.True(codec.Capabilities.CanWrite);
        var result = codec.Write(Signal([255, 3, 252]), new(TapeFormat.Tap, TapeSide.A));
        Assert.True(result.Success, result.Failure?.Detail);
        Assert.Equal(new byte[] { 3, 0, 255, 3, 252 }, result.Value.Bytes.ToArray());
        Assert.Equal(TapeSignalFidelity.DataOnly, result.Value.Report.Signal);
        Assert.Equal(TapeByteFidelity.Transformed, result.Value.Report.Bytes);
        Assert.Null(result.Value.Report.Rate);
    }

    [Theory]
    [InlineData(2999, 855, 1710, false, false)]
    [InlineData(3201, 700, 1710, false, false)]
    [InlineData(3201, 855, 2100, false, false)]
    [InlineData(3201, 855, 1710, true, false)]
    [InlineData(3201, 855, 1710, false, true)]
    public void RefusesShortPilotTurboNonblankTailAndMismatchedHalfBits(int pilot, int zero, int one, bool tail, bool mismatch)
    {
        var result = new TapCodec().Write(Signal([255, 3, 252], pilot, zero: zero, one: one, extraHighTail: tail, mismatch: mismatch), new(TapeFormat.Tap, TapeSide.A));
        Assert.False(result.Success);
        Assert.Equal(TapeError.ExactEncodingUnavailable, result.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ASteadyHighGapAfterTheBlockIsBlankLikeTheRomLeavesIt()
    {
        // A real ROM SAVE idles MIC low after the block, held as D6 high until the deck stops (hc2000-tape-record trace).
        var result = new TapCodec().Write(Signal([255, 3, 252], steadyHighGap: true), new(TapeFormat.Tap, TapeSide.A));
        Assert.True(result.Success, result.Failure?.Detail);
        Assert.Equal(new byte[] { 3, 0, 255, 3, 252 }, result.Value.Bytes);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(254)]
    public void SignalChecksumOrIncompleteFrameIsNeverRepaired(byte checksum)
    {
        var result = new TapCodec().Write(Signal(checksum == 255 ? [255] : [255, 3, checksum]), new(TapeFormat.Tap, TapeSide.A));
        Assert.False(result.Success); Assert.Equal(TapeError.ExactEncodingUnavailable, result.Failure!.Code);
    }

    [Fact]
    public void EveryBlockIsDecodedAndLowBlankCanBeExportedAsEmptyWithoutOriginalByteClaim()
    {
        byte[] bytes = [2, 0, 0, 0, 3, 0, 255, 3, 252];
        var codec = new TapCodec();
        var read = codec.Read(bytes, new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60)).Value;
        var write = codec.Write(read.Image, new(TapeFormat.Tap, TapeSide.A));
        Assert.True(write.Success, write.Failure?.Detail); Assert.Equal(bytes, write.Value.Bytes.ToArray());
        var blank = new TapeSideImage(new(1800, 1), default, [new(1, [new(false, new(1800, 1))])], [], [], [], new(TapeFormat.Blank, null, true, [], null));
        Assert.Empty(codec.Write(blank, new(TapeFormat.Tap, TapeSide.A)).Value.Bytes);
    }

    [Fact]
    public void ExportBoundsAndOptionsRefuseWithoutPartialBytes()
    {
        var codec = new TapCodec(); var signal = Signal([255, 3, 252]);
        Assert.Equal(TapeError.BoundsExceeded, codec.Write(signal, new(TapeFormat.Tap, TapeSide.A) { Limits = new() { InputBytes = 4 } }).Failure!.Code);
        Assert.Equal(TapeError.InvalidFormat, codec.Write(signal, new(TapeFormat.Tap, TapeSide.A) { Rate = 44100 }).Failure!.Code);
    }
}
