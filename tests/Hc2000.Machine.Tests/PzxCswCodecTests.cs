using System.Buffers.Binary;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Hc2000.Machine.Tape.Encoding;
using Xunit;
namespace Hc2000.Machine.Tests;
public sealed class PzxCswCodecTests
{
    static TapeImportOptions Import(TapeFormat format) => new(format, TapeSide.A, TapeCapacity.C60);
    static byte[] Csw(byte major, byte minor, byte compression, byte[] payload, uint count = 3)
    {
        byte[] b = new byte[(major == 1 ? 32 : 52) + payload.Length];
        "Compressed Square Wave"u8.CopyTo(b); b[22] = 26; b[23] = major; b[24] = minor;
        if (major == 1) { BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(25), 1000); b[27] = compression; b[28] = 1; }
        else { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(25), 1000); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(29), count); b[33] = compression; b[34] = 1; }
        payload.CopyTo(b, major == 1 ? 32 : 52); return b;
    }
    static byte[] Pzx(params (string Tag, byte[] Body)[] blocks)
    {
        var b = new List<byte>();
        foreach (var (tag, body) in blocks) { b.AddRange(System.Text.Encoding.ASCII.GetBytes(tag)); byte[] len = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)body.Length); b.AddRange(len); b.AddRange(body); }
        return b.ToArray();
    }
    [Theory] [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(2, 0)]
    public void CswHeaderRatePolarityAndDuration(int major, int minor)
    {
        var r = new CswTapeCodec(new TapeRunEncoding()).Read(Csw((byte)major, (byte)minor, 1, [1, 2, 3]), Import(TapeFormat.Csw));
        Assert.True(r.Success, r.Failure?.Detail); Assert.Equal(new TapeTime(3, 500), r.Value.Image.SourceDuration);
        Assert.Equal(new TapeTime(1800, 1), r.Value.Image.Duration); Assert.Equal(major != 1 || minor != 0, r.Value.Image.Chunks[0].Runs[0].Level);
    }
    [Fact] public void IndependentStoredZlibOracle()
    {
        var r = new CswTapeCodec(new TapeRunEncoding()).Read(Csw(2, 0, 2, [0x78, 1, 1, 3, 0, 0xfc, 0xff, 1, 2, 3, 0, 0x0d, 0, 7]), Import(TapeFormat.Csw));
        Assert.True(r.Success, r.Failure?.Detail); Assert.Equal(new TapeTime(3, 500), r.Value.Image.SourceDuration);
    }
    [Theory] [InlineData(1)] [InlineData(2)]
    public void AllHeaderAndPayloadTruncationsReject(int major)
    {
        var codec = new CswTapeCodec(new TapeRunEncoding());
        var bytes = Csw((byte)major, 0, 1, [0, 0xe9, 0xcd, 0, 0], 1);
        for (int i = 0; i < bytes.Length; i++)
        {
            // v1 has no count; a complete empty stream is legal.
            if (major == 1 && i == 32) continue;
            Assert.False(codec.Read(bytes.AsSpan(0, i), Import(TapeFormat.Csw)).Success);
        }
        Assert.Equal(new TapeTime(52713, 1000), codec.Read(bytes, Import(TapeFormat.Csw)).Value.Image.SourceDuration);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CswMalformedFieldsAreTransactional(int fault)
    {
        var bytes = Csw(2, 0, 1, [1, 2, 3]);
        switch (fault) { case 0: bytes[0] = 0; break; case 1: bytes[23] = 3; break; case 2: bytes[25] = bytes[26] = 0; break; case 3: bytes[29] = 4; break; case 4: bytes[33] = 3; break; case 5: bytes[35] = 255; break; }
        var r = new CswTapeCodec(new TapeRunEncoding()).Read(bytes, Import(TapeFormat.Csw));
        Assert.False(r.Success); Assert.Throws<InvalidOperationException>(() => r.Value);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CallerBudgetsAreEnforced(int budget)
    {
        var limits = budget switch { 0 => new TapeLimits { InputBytes = 1 }, 1 => new TapeLimits { DecodedBytes = 1 }, 2 => new TapeLimits { VirtualTransitions = 2 }, _ => new TapeLimits { MaxDuration = new(1, 1000) } };
        var r = new CswTapeCodec(new TapeRunEncoding()).Read(Csw(2, 0, 1, [1, 2, 3]), Import(TapeFormat.Csw) with { Limits = limits });
        Assert.Equal(TapeError.BoundsExceeded, r.Failure!.Code);
    }
    [Fact] public void PzxDataHasIndependentBitSequenceAndTailOracle()
    {
        // bits 1,0,1; initial high; zero=[2,3], one=[5]; tail=7 => 22 cycles.
        var r = new PzxTapeCodec().Read(Pzx(("PZXT", [1, 0]), ("DATA", [3, 0, 0, 0x80, 7, 0, 2, 1, 2, 0, 3, 0, 5, 0, 0xa0])), Import(TapeFormat.Pzx));
        Assert.True(r.Success, r.Failure?.Detail); Assert.Equal(new TapeTime(22, 3500000), r.Value.Image.SourceDuration);
        Assert.Equal(new[] { true, false, true, false, true }, r.Value.Image.Chunks.SelectMany(c => c.Runs).Take(5).Select(r => r.Level));
        Assert.Equal(new[] { 5, 2, 3, 5, 7 }, r.Value.Image.Chunks.SelectMany(c => c.Runs).Take(5).Select(r => (int)(r.Duration.Numerator * 3500000 / r.Duration.Denominator)));
    }
    [Fact] public void PzxCompactRepeatAndFull31BitExtendedWidths()
    {
        var r = new PzxTapeCodec().Read(Pzx(("PZXT", [1, 0]), ("PULS", [0xff, 0xff, 10, 0, 0, 0x80, 0, 0x80, 1, 0x80, 0xff, 0xff, 0xff, 0xff])), Import(TapeFormat.Pzx));
        Assert.True(r.Success, r.Failure?.Detail);
        Assert.Equal(new TapeTime(32767L * 10 + 32768 + 2147483647, 3500000), r.Value.Image.SourceDuration);
        Assert.Equal(16383ul, r.Value.Image.Chunks[0].Repeat); Assert.True(r.Value.Image.Chunks.Length < 10);
    }
    [Theory] [InlineData(true, 3)] [InlineData(false, 2)]
    public void PzxAllMetadataAndConditionalStops(bool target, int stops)
    {
        var bytes = Pzx(("PZXT", [1, 0, 0xc3, 0xa9]), ("BRWS", [65]), ("STOP", [1, 0]), ("STOP", [0, 0]), ("STOP", [9, 0]), ("ZZZZ", [1, 2]), ("PZXT", [1, 0]));
        var r = new PzxTapeCodec().Read(bytes, Import(TapeFormat.Pzx) with { Target48K = target });
        Assert.True(r.Success, r.Failure?.Detail); Assert.Equal(stops, r.Value.Image.Markers.Length);
        Assert.Contains(r.Value.Image.Annotations, a => a.Label == "é"); Assert.Contains(r.Value.Warnings, w => w.Code == "UnsupportedBlockAction");
        Assert.Equal(bytes, r.Value.SourceAssets[0].Bytes); bytes[0] = 0; Assert.Equal((byte)'P', r.Value.SourceAssets[0].Bytes[0]);
    }
    [Theory] [InlineData("PULS", 1)] [InlineData("PAUS", 3)] [InlineData("STOP", 1)] [InlineData("DATA", 7)]
    public void PzxMalformedTagBodiesReject(string tag, int size)
    {
        Assert.False(new PzxTapeCodec().Read(Pzx(("PZXT", [1, 0]), (tag, new byte[size])), Import(TapeFormat.Pzx)).Success);
    }
    [Fact] public void PzxTruncationAndConcatenatedVersionValidation()
    {
        var bytes = Pzx(("PZXT", [1, 0]), ("PAUS", [1, 0, 0, 0]));
        for (int i = 0; i < bytes.Length; i++) { if (i == 10) continue; Assert.False(new PzxTapeCodec().Read(bytes.AsSpan(0, i), Import(TapeFormat.Pzx)).Success); }
        Assert.Equal(TapeError.UnsupportedVersion, new PzxTapeCodec().Read(Pzx(("PZXT", [1, 0]), ("PZXT", [2, 0])), Import(TapeFormat.Pzx)).Failure!.Code);
    }
    static TapeSideImage Image(params TapeRun[] runs)
    {
        var duration = runs.Aggregate(default(TapeTime), (a, r) => new TapeTime(a.Numerator * r.Duration.Denominator + r.Duration.Numerator * a.Denominator, a.Denominator * r.Duration.Denominator));
        return new(duration, duration, [new TapeSignalChunk(1, runs)], [], [], [], new(TapeFormat.Blank, null, true, [], null));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CswWriterHeaderAndRleHaveIndependentOracle(bool zrle)
    {
        var image = Image(new(true, new(1, 1000)), new(false, new(256, 1000)), new(true, new(3, 1000)));
        var r = new CswTapeCodec(new TapeRunEncoding()).Write(image, new(TapeFormat.Csw, TapeSide.A) { Rate = 1000, UseZrle = zrle });
        Assert.True(r.Success, r.Failure?.Detail); var b = r.Value.Bytes.ToArray();
        Assert.Equal(2, b[23]); Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(25))); Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(29))); Assert.Equal(1, b[34]);
        byte[] payload = b[52..];
        if (zrle) { using var compressed = new MemoryStream(payload); using var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress); using var output = new MemoryStream(); z.CopyTo(output); payload = output.ToArray(); }
        Assert.Equal(new byte[] { 1, 0, 0, 1, 0, 0, 3 }, payload); Assert.Equal(TapeSignalFidelity.ExactSignal, r.Value.Report.Signal);
        Assert.Equal(image.SourceDuration, new CswTapeCodec(new TapeRunEncoding()).Read(b, Import(TapeFormat.Csw)).Value.Image.SourceDuration);
    }
    [Fact] public void PzxWriterIndependentPauseFieldsAndRoundtrip()
    {
        var image = Image(new(true, new(10, 3500000)), new(false, new(20, 3500000)));
        var r = new PzxTapeCodec().Write(image, new(TapeFormat.Pzx, TapeSide.A)); Assert.True(r.Success, r.Failure?.Detail);
        Assert.Equal(Pzx(("PZXT", [1, 0]), ("PAUS", [10, 0, 0, 0x80]), ("PAUS", [20, 0, 0, 0])), r.Value.Bytes);
        Assert.Equal(new TapeTime(3, 350000), new PzxTapeCodec().Read(r.Value.Bytes.AsSpan(), Import(TapeFormat.Pzx)).Value.Image.SourceDuration);
    }
    [Fact] public void WritersRefuseOffGridAndQuantizeCumulativelyWithCollisionReport()
    {
        var image = Image(new(false, new(1, 10000)), new(true, new(1, 10000)), new(false, new(1, 1000)));
        var codec = new CswTapeCodec(new TapeRunEncoding()); var options = new TapeExportOptions(TapeFormat.Csw, TapeSide.A) { Rate = 1000 };
        Assert.Equal(TapeError.ExactEncodingUnavailable, codec.Write(image, options).Failure!.Code);
        var r = codec.Write(image, options with { AllowQuantize = true }); Assert.True(r.Success, r.Failure?.Detail);
        Assert.Equal(new byte[] { 2 }, r.Value.Bytes.Skip(52)); Assert.Equal(TapeSignalFidelity.Quantized, r.Value.Report.Signal);
        Assert.Equal(2ul, r.Value.Report.EliminatedTransitions); Assert.Equal(new TapeTime(9, 10000), r.Value.Report.MaxEdgeDisplacement);
    }
    [Fact] public void ExportIsEntireSideNotOriginalSourceExtent()
    {
        var image = Image(new(true, new(1, 1000)), new(false, new(1, 1000)));
        image = new(image.Duration, default, image.Chunks, [], [], [], image.ImportOptions);
        var r = new CswTapeCodec(new TapeRunEncoding()).Write(image, new(TapeFormat.Csw, TapeSide.A) { Rate = 1000 });
        Assert.True(r.Success, r.Failure?.Detail); Assert.Equal(new byte[] { 1, 1 }, r.Value.Bytes.Skip(52));
    }
    [Fact] public void LongHoldDoesNotGainEdgesAndUnrepresentableCswHoldRefuses()
    {
        var image = Image(new TapeRun(true, new(1800, 1)));
        var pzx = new PzxTapeCodec().Write(image, new(TapeFormat.Pzx, TapeSide.A)); Assert.True(pzx.Success, pzx.Failure?.Detail);
        var reread = new PzxTapeCodec().Read(pzx.Value.Bytes.AsSpan(), Import(TapeFormat.Pzx)); Assert.True(reread.Success); Assert.All(reread.Value.Image.Chunks, c => Assert.All(c.Runs, r => Assert.True(r.Level)));
        var csw = new CswTapeCodec(new TapeRunEncoding()).Write(image, new(TapeFormat.Csw, TapeSide.A) { Rate = 3500000 });
        Assert.Equal(TapeError.ExactEncodingUnavailable, csw.Failure!.Code);
    }
    [Fact] public void CompactDataAndZeroWidthWorkBudget()
    {
        byte[] data = new byte[14 + 4096];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 32768); data[6] = 2; data[7] = 1; data[8] = 10; data[10] = 20; data[12] = 1;
        var bytes = Pzx(("PZXT", [1, 0]), ("DATA", data));
        var r = new PzxTapeCodec().Read(bytes, Import(TapeFormat.Pzx)); Assert.True(r.Success, r.Failure?.Detail);
        Assert.Equal(32768ul, r.Value.Image.Chunks[0].Repeat); Assert.Equal(new TapeTime(983040, 3500000), r.Value.Image.SourceDuration); Assert.True(r.Value.Image.Chunks.Length < 4);
        var zero = Pzx(("PZXT", [1, 0]), ("PULS", [0xff, 0xff, 0, 0]));
        Assert.Equal(TapeError.BoundsExceeded, new PzxTapeCodec().Read(zero, Import(TapeFormat.Pzx) with { Limits = new() { VirtualTransitions = 3 } }).Failure!.Code);
    }
    [Fact] public void CswExtensionAndWideRateAreNotPulseBytes()
    {
        var bytes = Csw(2, 0, 1, [1, 2, 3]); var extended = new byte[bytes.Length + 2]; bytes.AsSpan(0, 52).CopyTo(extended);
        extended[35] = 2; extended[52] = 99; extended[53] = 100; bytes.AsSpan(52).CopyTo(extended.AsSpan(54));
        BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(25), 1234567);
        var r = new CswTapeCodec(new TapeRunEncoding()).Read(extended, Import(TapeFormat.Csw)); Assert.True(r.Success, r.Failure?.Detail);
        Assert.Equal(new TapeTime(6, 1234567), r.Value.Image.SourceDuration); Assert.Contains(r.Value.Image.Annotations, a => a.Label == "Extension: 6364");
    }
    [Fact] public void CorruptCompressedContainerCannotReturnPartialImage()
    {
        var codec = new CswTapeCodec(new TapeRunEncoding()); byte[] z = [0x78, 1, 1, 3, 0, 0xfc, 0xff, 1, 2, 3, 0, 0x0d, 0, 7];
        for (int length = 0; length < z.Length; length++) Assert.False(codec.Read(Csw(2, 0, 2, z[..length]), Import(TapeFormat.Csw)).Success);
        z[^1] ^= 1; Assert.Equal(TapeError.InvalidFormat, codec.Read(Csw(2, 0, 2, z), Import(TapeFormat.Csw)).Failure!.Code);
    }
    [Fact] public void PzxStopsSplitHoldsAndQuantizationReportsMarkerDisplacement()
    {
        var image = Image(new TapeRun(true, new(10, 3500000)));
        image = new(image.Duration, image.SourceDuration, image.Chunks, [new(new(3, 3500000), 0, "stop", null, null)], [], [], image.ImportOptions);
        var r = new PzxTapeCodec().Write(image, new(TapeFormat.Pzx, TapeSide.A)); Assert.True(r.Success, r.Failure?.Detail);
        Assert.Equal(Pzx(("PZXT", [1, 0]), ("PAUS", [3, 0, 0, 0x80]), ("STOP", [0, 0]), ("PAUS", [7, 0, 0, 0x80])), r.Value.Bytes);
        image = new(image.Duration, image.SourceDuration, image.Chunks, [new(new(1, 7000000), 0, "stop", null, null)], [], [], image.ImportOptions);
        Assert.Equal(TapeError.ExactEncodingUnavailable, new PzxTapeCodec().Write(image, new(TapeFormat.Pzx, TapeSide.A)).Failure!.Code);
        r = new PzxTapeCodec().Write(image, new(TapeFormat.Pzx, TapeSide.A) { AllowQuantize = true }); Assert.True(r.Success);
        Assert.Equal(TapeSignalFidelity.Quantized, r.Value.Report.Signal); Assert.Equal(new TapeTime(1, 7000000), r.Value.Report.MaxEdgeDisplacement);
    }
    [Fact] public void NoSpuriousGridRequirementsAtSameLevelChunkBoundaries()
    {
        var image = Image(new(true, new(1, 2000)), new(true, new(1, 2000)));
        var r = new CswTapeCodec(new TapeRunEncoding()).Write(image, new(TapeFormat.Csw, TapeSide.A) { Rate = 1000 });
        Assert.True(r.Success, r.Failure?.Detail); Assert.Equal(new byte[] { 1 }, r.Value.Bytes.Skip(52)); Assert.Equal(TapeSignalFidelity.ExactSignal, r.Value.Report.Signal);
    }
    [Theory] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(1, true)] [InlineData(2, true)]
    public void EliminatedTransitionsCountsAbsentPhysicalEdges(int toggles, bool atEnd)
    {
        foreach (bool initial in new[] { false, true })
        foreach (TapeFormat format in new[] { TapeFormat.Csw, TapeFormat.Pzx })
        {
            uint rate = format == TapeFormat.Csw ? 10u : 3500000u;
            var runs = new List<TapeRun>(); bool level = initial;
            for (int i = 0; i < toggles; i++) { runs.Add(new(level, new(1, 5 * rate))); level = !level; }
            runs.Add(new(level, new((atEnd ? 5 : 50) - toggles, 5 * rate)));
            var image = Image(runs.ToArray());
            ITapeCodec codec = format == TapeFormat.Csw ? new CswTapeCodec(new TapeRunEncoding()) : new PzxTapeCodec();
            var r = codec.Write(image, new(format, TapeSide.A) { Rate = rate, AllowQuantize = true }); Assert.True(r.Success, r.Failure?.Detail);
            ulong emitted = !atEnd && (toggles & 1) != 0 ? 1ul : 0;
            Assert.Equal((ulong)toggles - emitted, r.Value.Report.EliminatedTransitions);
            var b = r.Value.Bytes.ToArray();
            if (format == TapeFormat.Csw)
            {
                Assert.Equal(initial ? 1 : 0, b[34]); Assert.Equal(emitted != 0 ? new byte[] { 1, 9 } : new byte[] { atEnd ? (byte)1 : (byte)10 }, b[52..]);
            }
            else
            {
                byte[] first = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(first, (emitted != 0 || atEnd ? 1u : 10u) | (initial ? 0x80000000u : 0));
                if (emitted == 0) Assert.Equal(Pzx(("PZXT", [1, 0]), ("PAUS", first)), b);
                else { byte[] last = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(last, 9u | (!initial ? 0x80000000u : 0)); Assert.Equal(Pzx(("PZXT", [1, 0]), ("PAUS", first), ("PAUS", last)), b); }
            }
        }
    }
    [Fact] public void PzxZeroPulseParityAndPauseUnits()
    {
        var r = new PzxTapeCodec().Read(Pzx(("PZXT", [1, 0]), ("PULS", [0, 0, 10, 0]), ("PAUS", [20, 0, 0, 0x80])), Import(TapeFormat.Pzx));
        Assert.True(r.Success, r.Failure?.Detail); Assert.Equal(new TapeTime(3, 350000), r.Value.Image.SourceDuration);
        Assert.True(r.Value.Image.Chunks[0].Runs[0].Level);
    }
}
