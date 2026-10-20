using System.Collections.Immutable;
using System.Text;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class TzxTapeCodecTests
{
    private ITapeCodec Codec => new TzxTapeCodec();
    private static TapeImportOptions Options => new(TapeFormat.Tzx, TapeSide.A, TapeCapacity.C60);
    private static byte[] Tape(params byte[][] blocks) => "ZXTape!\x1a"u8.ToArray().Concat(new byte[] { 1, 20 }).Concat(blocks.SelectMany(x => x)).ToArray();
    private static byte[] Block(byte id, Action<BinaryWriter> body)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); w.Write(id); body(w); return m.ToArray(); }
    private static void U24(BinaryWriter w, uint v) { w.Write((byte)v); w.Write((byte)(v >> 8)); w.Write((byte)(v >> 16)); }
    private static byte[] Sized(byte id, Action<BinaryWriter> body)
    { var raw = Block(0, body)[1..]; return Block(id, w => { w.Write(raw.Length); w.Write(raw); }); }
    private TapeSideImage Read(params byte[][] blocks)
    { var result = Codec.Read(Tape(blocks), Options); Assert.True(result.Success, result.Failure?.Detail); return result.Value.Image; }
    private static byte[] Tone(ushort ticks, ushort count = 1) => Block(0x12, w => { w.Write(ticks); w.Write(count); });
    private static byte[] Text => [0x30, 1, (byte)'x'];
    private static byte[] Pure => Block(0x14, w => { w.Write((ushort)2); w.Write((ushort)4); w.Write((byte)2); w.Write((ushort)0); U24(w, 1); w.Write((byte)0x80); });

    [Fact]
    public void PulseAndPartialDataHaveIndependentExactTimeline()
    {
        var image = Read(Tone(3, 3), [0x13, 3, 0, 0, 5, 0, 7, 0], Pure);
        Assert.Equal(new TapeTime(33, 3_500_000), image.SourceDuration); // 9 + zero + 5 + 7 + 8 + 4
        var runs = image.Chunks.Take(image.Chunks.Length - 1).SelectMany(c => Enumerable.Range(0, (int)c.Repeat).SelectMany(_ => c.Runs)).ToArray();
        Assert.Equal(new bool[] { false, true, false, true, false, true, false, true }, runs.Select(r => r.Level));
        Assert.Equal(new long[] { 3, 3, 8, 7, 4, 4, 2, 2 }, runs.Select(r => (long)(r.Duration.Numerator * 3_500_000 / r.Duration.Denominator)));
        Assert.Equal(new TapeTime(1800, 1), image.Duration);
        Assert.Single(image.Annotations.Where(a => a.Kind == TapeAnnotationKind.SourceEof));
    }

    [Fact]
    public void DirectRecordingUsesAbsoluteSamplesAndLeavesLastLevel()
    {
        var direct = Block(0x15, w => { w.Write((ushort)10); w.Write((ushort)0); w.Write((byte)3); U24(w, 1); w.Write((byte)0xC0); });
        var image = Read(direct, Tone(7));
        Assert.Equal(new TapeTime(37, 3_500_000), image.SourceDuration);
        Assert.Equal(new bool[] { true, false }, image.Chunks.Take(2).Select(c => c.Runs[0].Level));
        Assert.Equal(new TapeTime(20, 3_500_000), image.Chunks[0].Runs[0].Duration);
        Assert.Equal(new TapeTime(17, 3_500_000), image.Chunks[1].Runs[0].Duration);
    }

    [Fact]
    public void TurboFieldsAndStandardPilotAreNotRegenerated()
    {
        var turbo = Block(0x11, w => { foreach (ushort n in new ushort[] { 3, 5, 7, 11, 13, 2 }) w.Write(n); w.Write((byte)1); w.Write((ushort)0); U24(w, 1); w.Write((byte)0x80); });
        Assert.Equal(new TapeTime(44, 3_500_000), Read(turbo).SourceDuration);
        var standard = Block(0x10, w => { w.Write((ushort)0); w.Write((ushort)1); w.Write((byte)0xFF); });
        var image = Read(standard);
        Assert.Equal(new TapeTime(3223L * 2168 + 667 + 735 + 16 * 1710, 3_500_000), image.SourceDuration);
        Assert.True(image.Chunks[0].Repeat > 1000); // Compact pilot, not thousands of objects.
    }

    [Fact]
    public void PauseIncludesTerminalMillisecondAndLevelAssignmentsTakeNoTime()
    {
        var image = Read([0x2B, 1, 0, 0, 0, 1], Tone(7, 2), [0x20, 2, 0], [0x20, 0, 0], [0x2A, 0, 0, 0, 0]);
        Assert.Equal(new TapeTime(14 + 7000, 3_500_000), image.SourceDuration);
        Assert.True(image.Chunks[0].Runs[0].Level);
        Assert.True(image.Chunks[1].Runs[0].Level);
        Assert.Equal(new TapeTime(1, 1000), image.Chunks[1].Runs[0].Duration);
        Assert.Equal(2, image.Markers.Length);
        var other = Codec.Read(Tape([0x2A, 0, 0, 0, 0]), Options with { Target48K = false });
        Assert.Empty(other.Value.Image.Markers);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CswUsesOwnRateAndLastPlayedPhase(int compression)
    {
        byte[] payload = compression == 1 ? [1, 255] : [0x78, 1, 1, 2, 0, 0xFD, 0xFF, 1, 255, 1, 3, 1, 1];
        var csw = Sized(0x18, w => { w.Write((ushort)0); U24(w, 44100); w.Write((byte)compression); w.Write(2U); w.Write(payload); });
        var image = Read(csw, Tone(7));
        Assert.Equal(new TapeTime(256L * 3_500_000 + 7L * 44100, 44100L * 3_500_000), image.SourceDuration);
        Assert.False(image.Chunks[0].Runs[0].Level); Assert.True(image.Chunks[1].Runs[0].Level);
        Assert.Equal(new TapeTime(255L * 3_500_000 + 7L * 44100, 44100L * 3_500_000), image.Chunks[1].Runs[0].Duration);
    }

    private static byte[] Generalized(byte alphabet, byte[] flags, ushort[][] widths, uint total, byte[] packed) =>
        Sized(0x19, w => { w.Write((ushort)0); w.Write(0U); w.Write((byte)0); w.Write((byte)0); w.Write(total); w.Write((byte)widths[0].Length); w.Write(alphabet); for (int i = 0; i < flags.Length; i++) { w.Write(flags[i]); foreach (ushort n in widths[i]) w.Write(n); } w.Write(packed); });

    [Fact]
    public void GeneralizedAllPolarityFlagsShortSymbolsAndPackedIndices()
    {
        var block = Generalized(4, [0, 1, 2, 3], [[2, 0], [3, 0], [5, 0], [7, 11]], 4, [0x1B]);
        var image = Read(block);
        Assert.Equal(new TapeTime(28, 3_500_000), image.SourceDuration);
        Assert.Equal(new bool[] { false, true, false }, image.Chunks.Take(3).Select(c => c.Runs[0].Level));
        Assert.Equal(new TapeTime(10, 3_500_000), image.Chunks[0].Runs[0].Duration);
        var singleton = Generalized(1, [2], [[2]], 3, []);
        Assert.Equal(new TapeTime(6, 3_500_000), Read(singleton).SourceDuration);
        var bad = Generalized(3, [0, 0, 0], [[2], [3], [4]], 1, [0xC0]);
        Assert.Equal(TapeError.InvalidFormat, Codec.Read(Tape(bad), Options).Failure?.Code);
    }

    [Fact]
    public void GeneralizedZeroAlphabetMeans256AndPilotRepetitionsAreCompact()
    {
        var alphabet = Generalized(0, new byte[256], Enumerable.Repeat(new ushort[] { 2 }, 256).ToArray(), 1, [255]);
        Assert.Equal(new TapeTime(2, 3_500_000), Read(alphabet).SourceDuration);
        var pilot = Sized(0x19, w => { w.Write((ushort)0); w.Write(1U); w.Write((byte)1); w.Write((byte)1); w.Write(0U); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((ushort)3); w.Write((byte)0); w.Write((ushort)60000); });
        var image = Read(pilot); Assert.Equal(new TapeTime(180000, 3_500_000), image.SourceDuration); Assert.Equal(2, image.Chunks.Length);
    }

    [Fact]
    public void LoopJumpCallsCountMetadataBlocksAndDoNotSpin()
    {
        var image = Read([0x24, 3, 0], Tone(2), Text, [0x25], [0x23, 2, 0], Tone(99), Tone(3));
        Assert.Equal(new TapeTime(9, 3_500_000), image.SourceDuration);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 4, 6 }, image.SourceRoute.Select(v => v.BlockIndex));
        var calls = Block(0x26, w => { w.Write((ushort)2); w.Write((short)3); w.Write((short)5); });
        image = Read(calls, Tone(7), [0x23, 5, 0], Tone(2), [0x27], Tone(3), [0x27], Text);
        Assert.Equal(new TapeTime(12, 3_500_000), image.SourceDuration);
        Assert.Equal(TapeError.BoundsExceeded, Codec.Read(Tape([0x23, 0, 0]), Options with { Limits = new() { RouteSteps = 8 } }).Failure?.Code);
        Assert.Equal(TapeError.InvalidRoute, Codec.Read(Tape([0x27]), Options).Failure?.Code);
        Assert.Equal(TapeError.InvalidRoute, Codec.Read(Tape([0x24, 2, 0]), Options).Failure?.Code);
    }

    [Fact]
    public void SelectionIsRequiredPerOccurrenceAndUnusedChoicesFail()
    {
        var select = Block(0x28, w => { w.Write((ushort)5); w.Write((byte)1); w.Write((short)1); w.Write((byte)1); w.Write((byte)'A'); });
        Assert.Equal(TapeError.RouteSelectionRequired, Codec.Read(Tape(select, Tone(2)), Options).Failure?.Code);
        var result = Codec.Read(Tape(select, Tone(2)), Options with { Selections = [new(0, 0)] });
        Assert.True(result.Success); Assert.Equal(new TapeTime(2, 3_500_000), result.Value.Image.SourceDuration);
        Assert.Equal(TapeError.InvalidRoute, Codec.Read(Tape(Tone(2)), Options with { Selections = [new(0, 0)] }).Failure?.Code);
    }

    private static byte[] C64(byte id, bool inclusive)
    {
        var data = Block(0, w =>
        {
            if (id == 0x16)
            {
                foreach (ushort n in new ushort[] { 2, 1, 3, 5, 7, 11, 13, 17 }) w.Write(n);
                w.Write((byte)255);
                foreach (ushort n in new ushort[] { 0, 0, 19, 23, 29, 1 }) w.Write(n);
                w.Write((byte)1); w.Write((byte)1); w.Write((ushort)0); U24(w, 1); w.Write((byte)0x80);
            }
            else
            { w.Write((ushort)2); w.Write((ushort)3); w.Write((byte)9); w.Write((ushort)1); w.Write((byte)0); w.Write((byte)1); w.Write((byte)1); w.Write((ushort)1); w.Write((byte)255); w.Write((ushort)0); U24(w, 1); w.Write((byte)0x80); }
        })[1..];
        return Block(id, w => { w.Write(data.Length + (inclusive ? 4 : 0)); w.Write(data); });
    }

    [Theory]
    // ROM: 2*2 + 2*3 + 2*5 + 2*(13+17) + 2*(19+23) + 2*29 = 222.
    // Turbo: (8*2+3)*2 lead + (3+3)*2 data + (8*3+3)*2 trail = 104.
    [InlineData(0x16, false, 222)]
    [InlineData(0x16, true, 222)]
    [InlineData(0x17, false, 104)]
    [InlineData(0x17, true, 104)]
    public void DeprecatedC64BothBoundedLengthsAndIndependentWaveArithmetic(int id, bool inclusive, long ticks)
    {
        var block = C64((byte)id, inclusive);
        Assert.Equal(id == 0x16 ? 42 : 24, block.Length);
        var image = Read(block, Text);
        Assert.Equal(new TapeTime(ticks, 3_500_000), image.SourceDuration);
        Assert.Equal(2, image.SourceRoute.Length);
        Assert.Contains(image.Annotations, a => a.Label.EndsWith(inclusive ? "LengthIncludesDword" : "ExtensionLengthExcludesDword", StringComparison.Ordinal));
        var littleEndian = block.ToArray(); littleEndian[id == 0x16 ? 0x23 : 0x0E] = 0;
        Assert.Equal(new TapeTime(ticks, 3_500_000), Read(littleEndian).SourceDuration); // The one used high bit is still 1.
        var corrupt = block.ToArray(); corrupt[1]++;
        Assert.Equal(TapeError.InvalidFormat, Codec.Read(Tape(corrupt, Text), Options).Failure?.Code);
    }

    [Fact]
    public void EveryMetadataAndDangerousActionIsPreservedWithoutCpuSideEffects()
    {
        byte[][] blocks = [ [0x21, 1, (byte)'g'], [0x22], Text, [0x31, 5, 1, (byte)'m'],
            [0x32, 4, 0, 1, 0, 1, (byte)'a'], [0x33, 1, 0, 1, 2], Block(0x34, w => w.Write(new byte[8])),
            Block(0x35, w => { w.Write(new byte[16]); w.Write(1); w.Write((byte)123); }), [0x40, 0, 1, 0, 0, 0],
            [0x5A, (byte)'X', (byte)'T', (byte)'a', (byte)'p', (byte)'e', (byte)'!', 0x1A, 1, 20] ];
        var result = Codec.Read(Tape(blocks), Options);
        Assert.True(result.Success, result.Failure?.Detail); Assert.Equal(default, result.Value.Image.SourceDuration);
        Assert.Equal(10, result.Value.Image.SourceRoute.Length);
        Assert.Equal(2, result.Value.Warnings.Length);
        Assert.Contains(result.Value.Image.Annotations, a => a.Label == "UnsupportedSnapshotAction");
    }

    [Fact]
    public void TruncationEveryByteUnknownSignalAndBudgetsRefuseTransactionally()
    {
        var bytes = Tape(C64(0x16, false), Pure);
        for (int n = 0; n < bytes.Length; n++)
        {
            if (n == 10 || n == 52) continue; // Complete preceding blocks/empty file are valid.
            var result = Codec.Read(bytes.AsSpan(0, n), Options);
            Assert.False(result.Success, $"Unexpected truncated success at {n}");
        }
        Assert.Equal(TapeError.UnsupportedFormat, Codec.Read(Tape([0x99, 0, 0, 0, 0]), Options).Failure?.Code);
        Assert.Equal(TapeError.BoundsExceeded, Codec.Read(Tape(Tone(3, 100)), Options with { Limits = new() { VirtualTransitions = 2 } }).Failure?.Code);
    }

    private static TapeSideImage Image(params TapeRun[] runs) => new(new(1, 1), new(1, 1), [new(1, runs)], [], [], [], new(TapeFormat.Blank, null, true, [], null));
    [Fact]
    public void ExactWriterIndependentFieldsAndMixedGridSignal()
    {
        var image = Image(new(false, new(1, 3_500_000)), new(true, new(1, 3_546_895)), new(false, new TapeTime(1, 1)));
        image = new(Total(image), Total(image), image.Chunks, [], [], [], image.ImportOptions);
        var result = Codec.Write(image, new(TapeFormat.Tzx, TapeSide.A));
        Assert.True(result.Success, result.Failure?.Detail);
        Assert.Equal(TapeSignalFidelity.ExactSignal, result.Value.Report.Signal);
        // Each interval is 2B(length1,level) +18(length11, pause0, rate-u24, RLE,count1).
        using var r = new BinaryReader(new MemoryStream(result.Value.Bytes.ToArray()));
        Assert.Equal("ZXTape!\x1a", System.Text.Encoding.ASCII.GetString(r.ReadBytes(8))); r.ReadBytes(2);
        foreach ((byte level, uint rate) in new (byte, uint)[] { (0, 3_500_000), (1, 3_546_895), (0, 1) })
        {
            Assert.Equal(0x2B, r.ReadByte()); Assert.Equal(1U, r.ReadUInt32()); Assert.Equal(level, r.ReadByte());
            Assert.Equal(0x18, r.ReadByte()); Assert.Equal(11U, r.ReadUInt32()); Assert.Equal(0, r.ReadUInt16());
            Assert.Equal(rate, (uint)(r.ReadByte() | r.ReadByte() << 8 | r.ReadByte() << 16)); Assert.Equal(1, r.ReadByte());
            Assert.Equal(1U, r.ReadUInt32()); Assert.Equal(1, r.ReadByte());
        }
        Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);
        var roundtrip = Codec.Read(result.Value.Bytes.AsSpan(), Options);
        Assert.True(roundtrip.Success); Assert.Equal(image.Duration, roundtrip.Value.Image.SourceDuration);
    }
    [Fact]
    public void WriterStopsInsideHoldsAndLongHoldsHaveNoInventedEdges()
    {
        var image = new TapeSideImage(new(1800, 1), new(1800, 1), [new(1, [new(true, new(1800, 1))])],
            [new(new(1, 2), 0, "stop", null, null)], [], [], new(TapeFormat.Blank, null, true, [], null));
        var written = Codec.Write(image, new(TapeFormat.Tzx, TapeSide.A) { Rate = 3_500_000 });
        Assert.True(written.Success, written.Failure?.Detail);
        var read = Codec.Read(written.Value.Bytes.AsSpan(), Options);
        Assert.True(read.Success, read.Failure?.Detail);
        Assert.Equal(new TapeTime(1800, 1), read.Value.Image.SourceDuration);
        Assert.Single(read.Value.Image.Markers);
        Assert.Equal(new TapeTime(1, 2), read.Value.Image.Markers[0].Position);
        Assert.All(read.Value.Image.Chunks.SelectMany(c => c.Runs), r => Assert.True(r.Level));
        var document = TzxDocument.Parse(written.Value.Bytes.AsSpan(), new()).Value;
        Assert.Equal(3, document.Blocks.Count(b => b.Id == 0x18)); // 0.5s, uint.MaxValue, remainder.
    }

    [Fact]
    public void QuantizationFoldsTwoCollidingTransitionsToFinalLevel()
    {
        var image = Image(new(false, new(1, 10)), new(true, new(1, 10)), new(false, new(8, 10)));
        var result = Codec.Write(image, new(TapeFormat.Tzx, TapeSide.A) { AllowQuantize = true, Rate = 1 });
        Assert.True(result.Success, result.Failure?.Detail);
        Assert.Equal(2UL, result.Value.Report.EliminatedTransitions);
        Assert.Equal(new TapeTime(9, 10), result.Value.Report.MaxEdgeDisplacement);
        var read = Codec.Read(result.Value.Bytes.AsSpan(), Options);
        Assert.True(read.Success); Assert.False(read.Value.Image.Chunks[0].Runs[0].Level);
        Assert.Equal(new TapeTime(1, 1), read.Value.Image.SourceDuration);
    }

    [Theory]
    [InlineData(false, 1, false, 0)]
    [InlineData(true, 1, false, 0)]
    [InlineData(false, 2, false, 2)]
    [InlineData(true, 2, false, 2)]
    [InlineData(false, 3, false, 2)]
    [InlineData(true, 3, false, 2)]
    [InlineData(false, 1, true, 1)]
    [InlineData(true, 1, true, 1)]
    [InlineData(false, 2, true, 2)]
    [InlineData(true, 2, true, 2)]
    public void AmendmentPhysicalEdgesBothPolaritiesAndExcludedOutputEnd(bool initial, int edges, bool terminal, ulong eliminated)
    {
        var runs = new List<TapeRun>();
        // Same-level subdivisions are deliberately not physical edges and must not move the error metric.
        runs.Add(new(initial, new(1, 100))); runs.Add(new(initial, new(1, 100)));
        for (int n = 1; n < edges; n++) runs.Add(new((n & 1) == 0 ? initial : !initial, new(1, 50)));
        var duration = new TapeTime(terminal ? 1 : 10, 10);
        runs.Add(new((edges & 1) == 0 ? initial : !initial,
            new TapeTime((terminal ? 5 : 50) - edges, 50)));
        var image = new TapeSideImage(duration, duration, [new(1, runs)], [], [], [], new(TapeFormat.Blank, null, true, [], null));
        var result = Codec.Write(image, new(TapeFormat.Tzx, TapeSide.A) { AllowQuantize = true, Rate = 10 });
        Assert.True(result.Success, result.Failure?.Detail); Assert.Equal(eliminated, result.Value.Report.EliminatedTransitions);
        Assert.Equal(new TapeTime(2, 25), result.Value.Report.MaxEdgeDisplacement);
        var source = TzxDocument.Parse(result.Value.Bytes.AsSpan(), new()).Value;
        var levels = source.Blocks.Where(b => b.Id == 0x2B).Select(b => b.Body[4]).ToArray();
        Assert.Equal(initial ? (byte)1 : (byte)0, levels[0]);
        // An odd interior group changes output at sample1; terminal groups do not append a sample.
        if (!terminal && (edges & 1) != 0) Assert.Equal(initial ? (byte)0 : (byte)1, levels[^1]);
        else Assert.All(levels, l => Assert.Equal(initial ? (byte)1 : (byte)0, l));
        var counts = source.Blocks.Where(b => b.Id == 0x18).Select(b => b.Body[14]).ToArray();
        Assert.Equal(terminal ? new byte[] { 1 } : new byte[] { 1, 9 }, counts);
    }

    [Theory]
    [InlineData(new byte[] { 0x12, 0, 0, 255, 255 }, TapeError.BoundsExceeded)]
    [InlineData(new byte[] { 0x23, 255, 127 }, TapeError.InvalidRoute)]
    [InlineData(new byte[] { 0x24, 2, 0, 0x24, 2, 0, 0x25, 0x25 }, TapeError.InvalidRoute)]
    [InlineData(new byte[] { 0x2B, 1, 0, 0, 0, 2 }, TapeError.InvalidFormat)]
    [InlineData(new byte[] { 0x2A, 1, 0, 0, 0, 0 }, TapeError.InvalidFormat)]
    public void AdditionalMalformedAndZeroTimeExpansionOracles(byte[] blocks, TapeError expected)
    {
        Assert.Equal(expected, Codec.Read(Tape(blocks), Options with { Limits = new() { VirtualTransitions = 100 } }).Failure?.Code);
    }

    [Fact]
    public void CallsInsideLoopsAndLoopsInsideCallsRemainFinite()
    {
        var call = Block(0x26, w => { w.Write((ushort)1); w.Write((short)3); });
        var image = Read([0x24, 2, 0], call, [0x25], [0x23, 3, 0], Tone(3), [0x27], Text);
        Assert.Equal(new TapeTime(6, 3_500_000), image.SourceDuration);
        call = Block(0x26, w => { w.Write((ushort)1); w.Write((short)2); });
        image = Read(call, [0x23, 5, 0], [0x24, 3, 0], Tone(3), [0x25], [0x27], Text);
        Assert.Equal(new TapeTime(9, 3_500_000), image.SourceDuration);
    }

    private static TapeTime Total(TapeSideImage image)
    {
        // Independent fixture oracle: the test image is 1/3.5MHz + 1/3.546895MHz + 1 second.
        return new TapeTime(3_500_000L * 3_546_895 + 3_500_000 + 3_546_895, 3_500_000L * 3_546_895);
    }

    [Fact]
    public void OffGridExactRefusalAndExplicitCumulativeQuantization()
    {
        var image = Image(new(false, new(1, 33_554_431)), new(true, new(33_554_430, 33_554_431)));
        Assert.Equal(TapeError.ExactEncodingUnavailable, Codec.Write(image, new(TapeFormat.Tzx, TapeSide.A)).Failure?.Code);
        var result = Codec.Write(image, new(TapeFormat.Tzx, TapeSide.A) { AllowQuantize = true, Rate = 1000 });
        Assert.True(result.Success); Assert.Equal(TapeSignalFidelity.Quantized, result.Value.Report.Signal);
        Assert.Equal(new TapeTime(33_554_431 - 1000, 33_554_431_000), result.Value.Report.MaxEdgeDisplacement);
        Assert.Equal(0UL, result.Value.Report.EliminatedTransitions);
        Assert.Equal(new TapeTime(1, 1), Codec.Read(result.Value.Bytes.AsSpan(), Options).Value.Image.SourceDuration);
    }
}
