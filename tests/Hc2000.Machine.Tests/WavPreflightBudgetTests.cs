using System.Buffers.Binary;
using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class WavPreflightBudgetTests
{
    // Independent canonical header only: no production writer, no PCM or side-sized allocation.
    private static byte[] Header(uint frames, uint rate = 44100)
    {
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes); Put32(bytes, 4, checked(36 + frames * 2));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); Put32(bytes, 16, 16);
        Put16(bytes, 20, 1); Put16(bytes, 22, 1); Put32(bytes, 24, rate);
        Put32(bytes, 28, checked(rate * 2)); Put16(bytes, 32, 2); Put16(bytes, 34, 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); Put32(bytes, 40, checked(frames * 2));
        return bytes;
    }
    private static void Put16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static TapeImportOptions Options(TapeCapacity capacity = TapeCapacity.C60) =>
        new(TapeFormat.Wav, TapeSide.A, capacity) { Limits = new() { InputBytes = 268_435_456 } };
    private static (WavPcmPreflight Probe, int ReadBytes) Probe(byte[] header, long length, TapeImportOptions options)
    {
        var probe = new WavPcmPreflight(length, options);
        var read = 0;
        while (probe.ReadLength != 0)
        {
            Assert.InRange(probe.ReadLength, 1, 18);
            Assert.InRange(probe.ReadOffset, 0L, header.Length - probe.ReadLength);
            read += probe.ReadLength;
            probe.Feed(header.AsSpan((int)probe.ReadOffset, probe.ReadLength));
        }
        return (probe, read);
    }

    [Theory]
    [InlineData(TapeCapacity.C60, 79_380_000U, 158_760_044L, 1800)]
    [InlineData(TapeCapacity.C90, 119_070_000U, 238_140_044L, 2700)]
    public void FullSidePreflightReadsOnly44MetadataBytes(TapeCapacity capacity, uint frames, long length, int seconds)
    {
        var (probe, read) = Probe(Header(frames), length, Options(capacity));
        Assert.True(probe.Complete, probe.Failure?.Detail); Assert.Equal(44, read);
        Assert.Equal(new TapeTime(seconds, 1), probe.SourceDuration);
        Assert.Equal(44, probe.DataOffset); Assert.Equal((int)frames, probe.Frames);
    }

    [Fact]
    public void Explicit64MiBCapRefusesBeforeEvenReadingHeader()
    {
        var (probe, read) = Probe(Header(79_380_000), 158_760_044, Options() with { Limits = new() });
        Assert.Equal(0, read); Assert.False(probe.Complete); Assert.Equal(TapeError.BoundsExceeded, probe.Failure!.Code);
    }

    [Fact]
    public void OneExtraCompleteFrameRefusesSelectedSideWithoutReadingPayload()
    {
        var (probe, read) = Probe(Header(79_380_001), 158_760_046, Options());
        Assert.Equal(44, read); Assert.Equal(TapeError.CapacityExceeded, probe.Failure!.Code);
    }

    [Theory]
    [InlineData(24, 0U)] // Zero rate.
    [InlineData(28, 1U)] // Wrong byteRate.
    [InlineData(40, 159U)] // Incomplete PCM16 frames (160-byte padded payload).
    [InlineData(40, 162U)] // Payload beyond the complete container.
    [InlineData(4, 197U)] // RIFF size not equal to file size.
    public void MalformedHeaderFailsBeforePayload(int at, uint value)
    {
        var header = Header(80); Put32(header, at, value);
        var (probe, _) = Probe(header, 204, Options());
        Assert.False(probe.Complete); Assert.Equal(TapeError.InvalidFormat, probe.Failure!.Code);
    }

    [Fact]
    public void JointDecodedBudgetIncludesRetainedSourceBeforeCopies()
    {
        var options = Options() with { Limits = Options().Limits with { DecodedBytes = 158_760_044 + 575 } };
        var (probe, read) = Probe(Header(79_380_000), 158_760_044, options);
        Assert.Equal(44, read); Assert.Equal(TapeError.BoundsExceeded, probe.Failure!.Code);
    }

    [Fact]
    public void SourceBlockLimitAppliesToMetadataWalkNotJustDecodedSignal()
    {
        var (probe, read) = Probe(Header(80), 204, Options() with { Limits = Options().Limits with { SourceBlocks = 1 } });
        Assert.Equal(36, read); Assert.Equal(TapeError.BoundsExceeded, probe.Failure!.Code);
    }
}
