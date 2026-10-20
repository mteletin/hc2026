using System.Buffers.Binary;
using System.Collections.Immutable;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Xunit;

namespace Hc2000.Machine.Tests;

// In-house synthetic PCM and hand-computed D6 timelines; no physical analogue oracle.
public sealed class WavTapeCodecTests
{
    private readonly WavTapeCodec codec = new();
    private static TapeImportOptions Import => new(TapeFormat.Wav, TapeSide.A, TapeCapacity.C60);
    private static TapeExportOptions Export => new(TapeFormat.Wav, TapeSide.A);

    private static byte[] Wave(uint rate, ushort bits, ushort channels, params short[] samples)
    {
        var bytes = new byte[44 + samples.Length * (bits / 8) + (samples.Length * (bits / 8) % 2)];
        "RIFF"u8.CopyTo(bytes); Put32(bytes, 4, (uint)bytes.Length - 8); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        Put32(bytes, 16, 16); Put16(bytes, 20, 1); Put16(bytes, 22, channels); Put32(bytes, 24, rate);
        Put32(bytes, 28, checked(rate * channels * (uint)(bits / 8))); Put16(bytes, 32, (ushort)(channels * bits / 8));
        Put16(bytes, 34, bits); "data"u8.CopyTo(bytes.AsSpan(36)); Put32(bytes, 40, (uint)(samples.Length * (bits / 8)));
        for (var i = 0; i < samples.Length; i++)
            if (bits == 8) bytes[44 + i] = (byte)(samples[i] / 256 + 128);
            else BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + i * 2), samples[i]);
        return bytes;
    }
    private static void Put16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static TapeSideImage Image(params TapeRun[] runs)
    {
        var duration = runs.Aggregate(default(TapeTime), (p, r) => p + r.Duration);
        return new(duration, duration, runs.Length == 0 ? [] : [new TapeSignalChunk(1, runs)], [], [], [],
            new(TapeFormat.Blank, null, true, [], null));
    }
    private static void Runs(TapeReadResult result, uint rate, params (bool Level, int Samples)[] expected)
    {
        Assert.Equal(1UL, Assert.Single(result.Image.Chunks).Repeat);
        var runs = result.Image.Chunks[0].Runs;
        // Last run pads the selected C60 side with low material.
        Assert.Equal(expected.Length + 1, runs.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Level, runs[i].Level);
            Assert.Equal(new TapeTime(expected[i].Samples, rate), runs[i].Duration);
        }
        Assert.False(runs[^1].Level);
        Assert.Equal(new TapeTime(1800, 1) - result.Image.SourceDuration, runs[^1].Duration);
    }

    [Theory]
    [InlineData(11025, 8)] [InlineData(22050, 16)] [InlineData(44100, 8)]
    [InlineData(48000, 16)] [InlineData(12347, 16)]
    public void PositiveNegativeAndZeroHaveExactSampleBoundaries(int rate, int bits)
    {
        // +1/2 persists low, -1/2 high; returning to zero has positive AC transient then -1/2 high.
        var result = codec.Read(Wave((uint)rate, (ushort)bits, 1, 16384, 16384, -16384, -16384, 0, -16384), Import);
        Assert.True(result.Success, result.Failure?.Detail);
        Runs(result.Value, (uint)rate, (false, 2), (true, 2), (false, 1), (true, 1));
        Assert.Equal(new TapeTime(6, rate), result.Value.Image.SourceDuration);
        Assert.Equal(new TapeTime(1800, 1), result.Value.Image.Duration);
        Assert.Equal(WavTapeCodec.ComparatorModel, result.Value.Image.ImportOptions.ComparatorModel);
        Assert.Equal(TapeChannel.Left, result.Value.Image.ImportOptions.Channel);
    }

    [Fact]
    public void OppositeStereoNeverAveragesAndRightIsExplicit()
    {
        var bytes = Wave(100, 16, 2, 16384, -16384, -16384, 16384, 16384, -16384);
        var left = codec.Read(bytes, Import); var right = codec.Read(bytes, Import with { Channel = TapeChannel.Right });
        Assert.True(left.Success); Assert.True(right.Success);
        // Left ends low, so its terminal low material is coalesced; right ends high.
        Assert.Equal(new TapeTime(1, 100), left.Value.Image.Chunks[0].Runs[0].Duration);
        Assert.False(left.Value.Image.Chunks[0].Runs[0].Level);
        Runs(right.Value, 100, (true, 1), (false, 1), (true, 1));
        Assert.Equal(TapeChannel.Right, right.Value.Image.ImportOptions.Channel);
        Assert.Equal(bytes, Assert.Single(right.Value.SourceAssets).Bytes.ToArray());
        bytes[44] = 99;
        Assert.NotEqual(bytes[44], right.Value.SourceAssets[0].Bytes[44]);
    }

    [Fact]
    public void SmallSignalHasNoNoiseThresholdAndDcSettlesWithoutHysteresis()
    {
        var result = codec.Read(Wave(1, 16, 1, 0, -1, -1, -1), Import);
        Assert.True(result.Success);
        // alpha=611/2500611. -1 normalized = -131072; first y=-32, next division truncates to zero.
        // Zero retains high (not a positive threshold or an implicit idle-low reset).
        Runs(result.Value, 1, (false, 1), (true, 3));
    }

    [Fact]
    public void BackwardEulerTowardZeroIsPinnedAtAnIndependentCrossing()
    {
        // At rate 1 alpha=611/2500611. Independently calculated Q32.32 outputs:
        // -1049433, -224, +256, -288 (division toward zero).
        var result = codec.Read(Wave(1, 16, 1, -32768, -32767, -32759, -32768), Import);
        Assert.True(result.Success);
        Runs(result.Value, 1, (true, 2), (false, 1), (true, 1));
    }

    [Fact]
    public void IntegerRoundingDistinguishesFloorAtTheLastTinyPositiveCrossing()
    {
        // Independently evaluated integer oracle at n19: toward-zero gives +2, floor gives 0
        // retaining the previous high D6. Values are PCM LSBs, not an invented noise threshold.
        var result = codec.Read(Wave(11025, 16, 1, -2, -5, -9, -6, -4, 8, 3, -1, 7, -1, -2, -7, 7, 4, -5, 8, 9, -5, -4, 0, -10), Import);
        Assert.True(result.Success);
        Runs(result.Value, 11025, (true, 4), (false, 3), (true, 1), (false, 1), (true, 3), (false, 2), (true, 1), (false, 2), (true, 2), (false, 1), (true, 1));
    }

    [Fact]
    public void EmptyAndSilenceRetainInitialLowAndSourceEof()
    {
        foreach (var bytes in new[] { Wave(44100, 8, 1), Wave(44100, 16, 1, 0, 0) })
        {
            var result = codec.Read(bytes, Import); Assert.True(result.Success);
            var run = Assert.Single(Assert.Single(result.Value.Image.Chunks).Runs);
            Assert.False(run.Level); Assert.Equal(new TapeTime(1800, 1), run.Duration);
            Assert.Contains(result.Value.Image.Annotations, a => a.Kind == TapeAnnotationKind.SourceEof && a.Position == result.Value.Image.SourceDuration);
        }
    }

    [Theory]
    [InlineData(0, TapeError.InvalidFormat)] [InlineData(1, TapeError.InvalidFormat)]
    [InlineData(2, TapeError.InvalidFormat)] [InlineData(3, TapeError.UnsupportedFormat)]
    [InlineData(4, TapeError.UnsupportedCompression)] [InlineData(5, TapeError.UnsupportedFormat)]
    [InlineData(6, TapeError.InvalidFormat)] [InlineData(7, TapeError.InvalidFormat)]
    [InlineData(8, TapeError.UnsupportedFormat)] [InlineData(9, TapeError.UnsupportedFormat)]
    public void MalformedAndUnsupportedHeadersAreStructured(int mutation, TapeError expected)
    {
        var bytes = Wave(44100, 16, 1, 1, 2);
        switch (mutation)
        {
            case 0: Put32(bytes, 4, (uint)bytes.Length); break;
            case 1: Put16(bytes, 32, 1); break;
            case 2: Put32(bytes, 28, 1); break;
            case 3: Put16(bytes, 34, 24); break;
            case 4: Put16(bytes, 20, 3); break;
            case 5: Put16(bytes, 22, 3); break;
            case 6: Put32(bytes, 24, 0); break;
            case 7: Put32(bytes, 40, 3); break;
            case 8: "RIFX"u8.CopyTo(bytes); break;
            case 9: "RF64"u8.CopyTo(bytes); break;
        }
        var result = codec.Read(bytes, Import); Assert.False(result.Success); Assert.Equal(expected, result.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void UnknownOddChunkHasPaddingAndIsPreserved()
    {
        var original = Wave(8, 8, 1, -16384);
        var bytes = new byte[original.Length + 10]; original.AsSpan(0, 12).CopyTo(bytes);
        "JUNK"u8.CopyTo(bytes.AsSpan(12)); Put32(bytes, 16, 1); bytes[20] = 77;
        original.AsSpan(12).CopyTo(bytes.AsSpan(22)); Put32(bytes, 4, (uint)bytes.Length - 8);
        var result = codec.Read(bytes, Import); Assert.True(result.Success);
        Assert.Equal(bytes, result.Value.SourceAssets[0].Bytes.ToArray());
        var truncated = bytes[..^1]; Put32(truncated, 4, (uint)truncated.Length - 8);
        Assert.Equal(TapeError.InvalidFormat, codec.Read(truncated, Import).Failure!.Code);
    }

    [Fact]
    public void ReaderHonorsBytesTransitionsBlocksCapacityAndRouteBudgets()
    {
        var bytes = Wave(1, 16, 1, -16384, 16384, -16384);
        Assert.Equal(TapeError.BoundsExceeded, codec.Read(bytes, Import with { Limits = new() { InputBytes = 1 } }).Failure!.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.Read(bytes, Import with { Limits = new() { DecodedBytes = bytes.Length } }).Failure!.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.Read(bytes, Import with { Limits = new() { VirtualTransitions = 1 } }).Failure!.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.Read(bytes, Import with { Limits = new() { SourceBlocks = 1 } }).Failure!.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.Read(bytes, Import with { Limits = new() { MaxDuration = new(1, 1) } }).Failure!.Code);
        Assert.Equal(TapeError.InvalidRoute, codec.Read(bytes, Import with { Selections = [new(0, 0)] }).Failure!.Code);
        Assert.Equal(TapeError.InvalidFormat, codec.Read(bytes, Import with { Channel = (TapeChannel)8 }).Failure!.Code);
        Assert.Equal(TapeError.CapacityExceeded, codec.Read(Wave(1, 8, 1, new short[1801]), Import).Failure!.Code);
        Assert.True(codec.Read(Wave(1, 8, 1, new short[1801]), Import with { Capacity = TapeCapacity.C90 }).Success);
        Assert.Equal(TapeError.CapacityExceeded, codec.Read(Wave(1, 8, 1, new short[2701]), Import with { Capacity = TapeCapacity.C90 }).Failure!.Code);
    }

    [Theory]
    [InlineData(8)] [InlineData(16)]
    public void WriterHasIndependentHeaderAmplitudeAndExactRoundtripOracle(int bits)
    {
        var image = Image(new(false, new(2, 100)), new(true, new(3, 100)));
        var result = codec.Write(image, Export with { Rate = 100, WavBitsPerSample = (uint)bits }); Assert.True(result.Success);
        var bytes = result.Value.Bytes.ToArray();
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]); Assert.Equal("WAVEfmt "u8.ToArray(), bytes[8..16]);
        Assert.Equal(100U, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal((uint)(5 * bits / 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        if (bits == 8) Assert.Equal(new byte[] { 192, 192, 64, 64, 64, 0 }, bytes[44..]);
        else Assert.Equal(new short[] { 16384, 16384, -16384, -16384, -16384 }, Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + 2 * i))));
        Assert.Equal(TapeSignalFidelity.ExactSignal, result.Value.Report.Signal);
        Assert.Equal(TapeAmplitudeFidelity.Synthesized, result.Value.Report.Amplitude);
        Assert.Equal(default, result.Value.Report.MaxEdgeDisplacement);
        var read = codec.Read(bytes, Import); Assert.True(read.Success);
        Runs(read.Value, 100, (false, 2), (true, 3));
    }

    [Fact]
    public void WriterProjectsAbsoluteEdgesAndReportsErrorAndCollisions()
    {
        var image = Image(new(false, new(1, 7)), new(true, new(1, 7)), new(false, new(5, 7)));
        var result = codec.Write(image, Export with { Rate = 3, WavBitsPerSample = 8 }); Assert.True(result.Success);
        // Ceil cumulative boundaries 3/7,6/7,3 => 1,1,3; the high interval disappears.
        Assert.Equal(new byte[] { 192, 192, 192, 0 }, result.Value.Bytes.ToArray()[44..]);
        Assert.Equal(TapeSignalFidelity.Quantized, result.Value.Report.Signal);
        Assert.Equal(new TapeTime(4, 21), result.Value.Report.MaxEdgeDisplacement);
        Assert.Equal(2UL, result.Value.Report.EliminatedTransitions); // Both input edges disappear, not merely k-1.
    }

    [Fact]
    public void DefaultsAndMetadataLossAreReportedNotOriginalOrAnalogueIdentity()
    {
        var image = Image(new TapeRun(true, new(1, 44100)));
        var annotated = new TapeSideImage(image.Duration, image.SourceDuration, image.Chunks,
            [new(default, 0, "stop", null, null)], [new(default, 0, TapeAnnotationKind.Text, "title", null, null)], [], image.ImportOptions);
        var result = codec.Write(annotated, Export); Assert.True(result.Success);
        Assert.Equal(44100U, result.Value.Report.Rate);
        Assert.Equal(TapeByteFidelity.Transformed, result.Value.Report.Bytes);
        Assert.Equal(2, result.Value.Report.ChangedAnnotations.Length);
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(result.Value.Bytes.AsSpan()[34..]));
    }

    [Fact]
    public void CompactRepeatsRetainSameLevelAtRepeatBoundary()
    {
        var image = new TapeSideImage(new(4, 10), new(4, 10), [new TapeSignalChunk(2, [new(true, new(1, 10)), new(true, new(1, 10))])], [], [], [], new(TapeFormat.Blank, null, true, [], null));
        var result = codec.Write(image, Export with { Rate = 10, WavBitsPerSample = 8 }); Assert.True(result.Success);
        Assert.Equal(new byte[] { 64, 64, 64, 64 }, result.Value.Bytes.ToArray()[44..]);
        Assert.Equal(0UL, result.Value.Report.EliminatedTransitions);
    }

    [Fact]
    public void WriterRefusesOverflowBadCoverageAndResourceBudgets()
    {
        var image = Image(new TapeRun(false, new(1, 1)));
        Assert.Equal(TapeError.InvalidFormat, codec.Write(image, Export with { Rate = 0 }).Failure!.Code);
        Assert.Equal(TapeError.UnsupportedFormat, codec.Write(image, Export with { WavBitsPerSample = 24 }).Failure!.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.Write(image, Export with { Rate = uint.MaxValue }).Failure!.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.Write(image, Export with { Limits = new() { DecodedBytes = 4 } }).Failure!.Code);
        Assert.Equal(TapeError.BoundsExceeded, codec.Write(image, Export with { Limits = new() { VirtualTransitions = 0 } }).Failure!.Code);
        var bad = new TapeSideImage(new(2, 1), new(1, 1), image.Chunks, [], [], [], image.ImportOptions);
        Assert.Equal(TapeError.InvalidFormat, codec.Write(bad, Export).Failure!.Code);
    }

    [Fact]
    public void AllTruncatedPrefixesAndOversizedChunkLengthsFailWithoutPartialMedia()
    {
        var bytes = Wave(44100, 8, 1, -32768, 32512, -256);
        for (var length = 0; length < bytes.Length; length++)
        {
            var prefix = bytes[..length];
            if (length >= 8) Put32(prefix, 4, (uint)length - 8);
            var result = codec.Read(prefix, Import);
            Assert.False(result.Success);
            Assert.Equal(TapeError.InvalidFormat, result.Failure!.Code);
        }
        foreach (var offset in new[] { 16, 40 })
        {
            var corrupt = (byte[])bytes.Clone(); Put32(corrupt, offset, uint.MaxValue);
            Assert.Equal(TapeError.InvalidFormat, codec.Read(corrupt, Import).Failure!.Code);
        }
    }

    [Fact]
    public void DataBeforeFmtWorksButDuplicateDataFmtAndAbsentChunksFail()
    {
        var normal = Wave(44100, 16, 1, -16384);
        var reordered = new byte[normal.Length]; normal.AsSpan(0, 12).CopyTo(reordered);
        normal.AsSpan(36).CopyTo(reordered.AsSpan(12)); normal.AsSpan(12, 24).CopyTo(reordered.AsSpan(22));
        var result = codec.Read(reordered, Import); Assert.True(result.Success);
        Assert.Equal(0U, Assert.Single(result.Value.Image.SourceRoute).BlockIndex);
        foreach (var (start, count) in new[] { (12, 24), (36, 10) })
        {
            var duplicate = new byte[normal.Length + count]; normal.CopyTo(duplicate, 0);
            normal.AsSpan(start, count).CopyTo(duplicate.AsSpan(normal.Length)); Put32(duplicate, 4, (uint)duplicate.Length - 8);
            Assert.Equal(TapeError.InvalidFormat, codec.Read(duplicate, Import).Failure!.Code);
            var absent = new byte[normal.Length - count]; normal.AsSpan(0, start).CopyTo(absent);
            normal.AsSpan(start + count).CopyTo(absent.AsSpan(start)); Put32(absent, 4, (uint)absent.Length - 8);
            Assert.Equal(TapeError.InvalidFormat, codec.Read(absent, Import).Failure!.Code);
        }
    }

    [Fact]
    public void PcmFmtExtensionSpanIsCheckedAndExtensibleIsExplicitlyUnsupported()
    {
        var normal = Wave(44100, 16, 1, -16384);
        var extended = new byte[normal.Length + 2]; normal.AsSpan(0, 36).CopyTo(extended);
        normal.AsSpan(36).CopyTo(extended.AsSpan(38)); Put32(extended, 4, (uint)extended.Length - 8);
        Put32(extended, 16, 18); // PCM WAVEFORMATEX with cbSize0
        Assert.True(codec.Read(extended, Import).Success);
        Put16(extended, 36, 1);
        Assert.Equal(TapeError.InvalidFormat, codec.Read(extended, Import).Failure!.Code);
        Put16(extended, 36, 0); Put16(extended, 20, 0xfffe);
        Assert.Equal(TapeError.UnsupportedFormat, codec.Read(extended, Import).Failure!.Code);
        Put16(extended, 20, 6);
        Assert.Equal(TapeError.UnsupportedCompression, codec.Read(extended, Import).Failure!.Code);
        foreach (var size in new uint[] { 15, 17 })
        {
            Put16(extended, 20, 1); Put32(extended, 16, size);
            Assert.Equal(TapeError.InvalidFormat, codec.Read(extended, Import).Failure!.Code);
        }
    }

    [Fact]
    public void HighestU32InputRateAndSignedEndpointsStayExactWithoutFloatingClock()
    {
        var result = codec.Read(Wave(uint.MaxValue, 8, 1, -32768, 32512, -32768), Import);
        Assert.True(result.Success);
        Runs(result.Value, uint.MaxValue, (true, 1), (false, 1), (true, 1));
        Assert.Equal(TapeError.InvalidFormat, codec.Read(Wave(1, 8, 1, 0), Import with { Channel = TapeChannel.Right }).Failure!.Code);
    }

    [Fact]
    public void CapacityBoundariesKeepSourceHighWithoutAddingTimeOrClipping()
    {
        var samples = new short[1800]; Array.Fill(samples, (short)-16384);
        var result = codec.Read(Wave(1, 16, 1, samples), Import); Assert.True(result.Success);
        var run = Assert.Single(result.Value.Image.Chunks[0].Runs);
        Assert.True(run.Level); Assert.Equal(new TapeTime(1800, 1), run.Duration);
        Assert.Equal(result.Value.Image.Duration, result.Value.Image.SourceDuration);
    }

    [Fact]
    public void NonEdgeSubdivisionsDoNotFalselyQuantizeTheSignal()
    {
        var image = Image(new(false, new(1, 7)), new(false, new(6, 7)));
        var result = codec.Write(image, Export with { Rate = 1, WavBitsPerSample = 8 }); Assert.True(result.Success);
        Assert.Equal(new byte[] { 192, 0 }, result.Value.Bytes.ToArray()[44..]);
        Assert.Equal(TapeSignalFidelity.ExactSignal, result.Value.Report.Signal);
        Assert.Equal(default, result.Value.Report.MaxEdgeDisplacement);
    }

    [Fact]
    public void WriterReportsTerminalProjectionAndRefusesHugeCompactExpansion()
    {
        var result = codec.Write(Image(new TapeRun(true, new(1, 7))), Export with { Rate = 3, WavBitsPerSample = 8 });
        Assert.True(result.Success); Assert.Equal(new TapeTime(4, 21), result.Value.Report.MaxEdgeDisplacement);
        Assert.Equal(new byte[] { 64, 0 }, result.Value.Bytes.ToArray()[44..]);
        var huge = new TapeSideImage(new(1, 1), new(1, 1),
            [new TapeSignalChunk(ulong.MaxValue, [new(true, new(1, 10)), new(false, new(1, 10))])], [], [], [],
            new(TapeFormat.Blank, null, true, [], null));
        Assert.Equal(TapeError.BoundsExceeded, codec.Write(huge, Export).Failure!.Code);
        var overflow = Image(new TapeRun(false, new(Int128.MaxValue, 1)));
        Assert.Equal(TapeError.BoundsExceeded, codec.Write(overflow, Export with { Limits = new() { MaxDuration = new(Int128.MaxValue, 1) } }).Failure!.Code);
        var empty = codec.Write(Image(), Export); Assert.True(empty.Success);
        Assert.Equal(44, empty.Value.Bytes.Length);
    }

    [Theory]
    [InlineData(2, false, false, 2)] [InlineData(3, false, false, 2)]
    [InlineData(1, false, false, 0)] [InlineData(1, false, true, 1)]
    [InlineData(2, false, true, 2)] [InlineData(2, true, false, 2)]
    [InlineData(3, true, false, 2)]
    public void QuantizedEdgeGroupReportsMatchNormativePhysicalEdgeOracle(int edges, bool initialHigh, bool shortExtent, int eliminated)
    {
        // ADR amendment v1: 1/50,2/50,3/50 at R10 all map to q1. An output-end
        // group cannot create a sample; interior groups emit one edge only for odd parity.
        var extent = shortExtent ? new TapeTime(1, 10) : new TapeTime(1, 1);
        var runs = new List<TapeRun>(); var level = initialHigh;
        for (var i = 0; i < edges; i++)
        {
            // Arbitrary same-level joins must not influence physical edge counts/error.
            runs.Add(new(level, new(1, 100))); runs.Add(new(level, new(1, 100)));
            level = !level;
        }
        runs.Add(new(level, extent - new TapeTime(edges, 50)));
        var result = codec.Write(Image(runs.ToArray()), Export with { Rate = 10, WavBitsPerSample = 8 });
        Assert.True(result.Success);
        var expected = new byte[shortExtent ? 2 : 10]; // odd PCM count has one RIFF pad byte
        expected[0] = initialHigh ? (byte)64 : (byte)192;
        if (!shortExtent) Array.Fill(expected, level ? (byte)64 : (byte)192, 1, 9);
        Assert.Equal(expected, result.Value.Bytes.ToArray()[44..]);
        Assert.Equal((ulong)eliminated, result.Value.Report.EliminatedTransitions);
        Assert.Equal(new TapeTime(2, 25), result.Value.Report.MaxEdgeDisplacement);
    }

    [Fact]
    public void MultipleQuantizationGroupsSumAbsentEdgesWithoutCreatingBlips()
    {
        // Initial low: first pair cancels at q1, later single survives at q3.
        var image = Image(new(false, new(1, 50)), new(true, new(1, 50)), new(false, new(17, 100)), new(true, new(79, 100)));
        var result = codec.Write(image, Export with { Rate = 10, WavBitsPerSample = 8 }); Assert.True(result.Success);
        Assert.Equal(new byte[] { 192, 192, 192, 64, 64, 64, 64, 64, 64, 64 }, result.Value.Bytes.ToArray()[44..]);
        Assert.Equal(2UL, result.Value.Report.EliminatedTransitions);
        Assert.Equal(new TapeTime(9, 100), result.Value.Report.MaxEdgeDisplacement);
    }
}
