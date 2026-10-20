using System.Security.Cryptography;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class LegacySnapshotTests
{
    public static TheoryData<string, int, string> Fixtures => new()
    {
        { "legacy48-pattern-v1.z80", 49182, "4233507a1d400338657c167e97682414f5db2885111756f9c7bb37ad25fc0c56" },
        { "legacy48-stack.sna", 49179, "c81405cd8bd4b18de2037d6bbecccf023f836fa6598653a66af72473b381810d" },
        { "legacy48-zero-v1-rle.z80", 806, "b5d0799537c47d560c6333ef0a2ba2af479e5301fc55d2f1e8248b89cbf2a4bf" },
        { "legacy48-literal-ed-v1-rle.z80", 812, "a71dc4ded7ffe592f69d5795b2328d9b49e5257dea4a28e6a93898388a7bd03f" },
        { "legacy48-byte12-ff-v1.z80", 49182, "f70d3504d22c1d508c100cb6f9f8cb9269893808842a15dd3e04d6c6d3e0deb7" }
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void NamedSyntheticFixturesDecodeAllArchitecturalFields(string name, int length, string hash)
    {
        var bytes = Fixture(name);
        Assert.Equal(length, bytes.Length);
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var snapshot = name.EndsWith(".sna", StringComparison.Ordinal)
            ? LegacySnapshotDecoder.DecodeSna(bytes) : LegacySnapshotDecoder.DecodeZ80(bytes);
        Assert.Equal(new LegacyRegisters(0x1234, 0x2345, 0x3456, 0x4567,
            0x5678, 0x6789, 0x789a, 0x89ab, 0x9abc, 0xabcd, 0x8123, 0x9002,
            0x3f, 0xd6, true, true, 1), snapshot.Registers);
        Assert.Equal(name.Contains("byte12-ff", StringComparison.Ordinal) ? 0 : 6, snapshot.Border);
        Assert.Equal(0x12, snapshot.Registers.A);
        Assert.Equal(0x34, snapshot.Registers.F);
        Assert.Equal(0x56, snapshot.Registers.APrime);
        Assert.Equal(0x78, snapshot.Registers.FPrime);
        Assert.Equal(LegacySnapshotWarnings.None, snapshot.Warnings);
        var ram = snapshot.CopyRam();
        Assert.Equal(49152, ram.Length);
        if (name.Contains("-rle", StringComparison.Ordinal))
        {
            Assert.Equal(name.Contains("literal-ed", StringComparison.Ordinal) ? 0xed : 0, ram[0]);
            Assert.All(ram[1..], value => Assert.Equal(0, value));
        }
        else
        {
            Assert.Equal("9155f21b1fa64f70c61f5c43e1b05b1b001beaab6bb51c50a7e69d09c551fe03",
                Convert.ToHexStringLower(SHA256.HashData(ram)));
        }
        Assert.Equal(bytes, Fixture(name)); // SNA stacked PC is never cleared
        Array.Fill(bytes, (byte)0xff);
        ram[0] ^= 0xff;
        Assert.NotEqual(ram[0], snapshot.CopyRam()[0]); // neither input nor output shares storage
    }

    [Fact]
    public void Z80RHighBitComesOnlyFromNormalizedFlagsAndIffsAreIndependent()
    {
        var bytes = Fixture("legacy48-pattern-v1.z80");
        bytes[11] = 0xd6;
        bytes[12] = 0x0c;
        bytes[27] = 0;
        bytes[28] = 0xff;
        var parsed = LegacySnapshotDecoder.DecodeZ80(bytes);
        Assert.Equal(0x56, parsed.Registers.R);
        Assert.False(parsed.Registers.IFF1);
        Assert.True(parsed.Registers.IFF2);
        Assert.Equal(6, parsed.Border);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x80, 0xd6)]
    [InlineData(4, 0xd6)]
    public void SnaUsesOnlySavedIff2AndPreservesFullR(byte iffByte, byte refresh)
    {
        var bytes = Fixture("legacy48-stack.sna");
        bytes[19] = iffByte;
        bytes[20] = refresh;
        var parsed = LegacySnapshotDecoder.DecodeSna(bytes);
        Assert.Equal((iffByte & 4) != 0, parsed.Registers.IFF1);
        Assert.Equal(parsed.Registers.IFF1, parsed.Registers.IFF2);
        Assert.Equal(refresh, parsed.Registers.R);
        Assert.Equal((iffByte & ~4) != 0 ? LegacySnapshotWarnings.IgnoredSnaInterruptBits : LegacySnapshotWarnings.None, parsed.Warnings);
    }

    [Fact]
    public void Z80HardwareHintsWarnButMeaninglessFlagBitsDoNotChangeFields()
    {
        var bytes = Fixture("legacy48-pattern-v1.z80");
        var original = LegacySnapshotDecoder.DecodeZ80(bytes);
        bytes[12] |= 0xc0;
        bytes[29] |= 0xfc;
        var parsed = LegacySnapshotDecoder.DecodeZ80(bytes);
        Assert.Equal(original.Registers, parsed.Registers);
        Assert.Equal(original.Border, parsed.Border);
        Assert.Equal(original.CopyRam(), parsed.CopyRam());
        Assert.Equal(LegacySnapshotWarnings.IgnoredZ80HardwareHints, parsed.Warnings);
    }

    [Theory]
    [InlineData(0x4000)]
    [InlineData(0xfffe)]
    public void SnaStackBoundaryExtractsPcAndWrapsSpWithoutChangingRam(int sp)
    {
        var bytes = Fixture("legacy48-stack.sna");
        bytes[23] = (byte)sp;
        bytes[24] = (byte)(sp >> 8);
        bytes[27 + sp - 0x4000] = 0x23;
        bytes[27 + sp - 0x4000 + 1] = 0x81;
        var before = (byte[])bytes.Clone();
        var parsed = LegacySnapshotDecoder.DecodeSna(bytes);
        Assert.Equal(0x8123, parsed.Registers.PC);
        Assert.Equal(unchecked((ushort)(sp + 2)), parsed.Registers.SP);
        Assert.Equal(before[27..], parsed.CopyRam());
        Assert.Equal(before, bytes);
    }

    [Theory]
    [InlineData("sp-3fff", LegacySnapshotError.InvalidStackPointer)]
    [InlineData("sp-ffff", LegacySnapshotError.InvalidStackPointer)]
    [InlineData("im3", LegacySnapshotError.InvalidHeader)]
    [InlineData("im255", LegacySnapshotError.InvalidHeader)]
    [InlineData("border8", LegacySnapshotError.InvalidHeader)]
    [InlineData("extra-byte", LegacySnapshotError.InvalidLength)]
    [InlineData("short", LegacySnapshotError.InvalidLength)]
    [InlineData("banked", LegacySnapshotError.InvalidLength)]
    [InlineData("empty", LegacySnapshotError.InvalidLength)]
    public void SnaRejectsUnsupportedAndMalformedLayouts(string mutation, LegacySnapshotError error)
    {
        var bytes = Fixture("legacy48-stack.sna");
        switch (mutation)
        {
            case "sp-3fff": bytes[23] = 0xff; bytes[24] = 0x3f; break;
            case "sp-ffff": bytes[23] = 0xff; bytes[24] = 0xff; break;
            case "im3": bytes[25] = 3; break;
            case "im255": bytes[25] = 255; break;
            case "border8": bytes[26] = 8; break;
            case "extra-byte": bytes = [.. bytes, 0]; break;
            case "short": bytes = bytes[..^1]; break;
            case "banked": bytes = [.. bytes, .. new byte[32772]]; break;
            case "empty": bytes = []; break;
        }
        var before = (byte[])bytes.Clone();
        Assert.Equal(error, Assert.Throws<LegacySnapshotException>(() => LegacySnapshotDecoder.DecodeSna(bytes)).Error);
        Assert.Equal(before, bytes);
    }

    [Theory]
    [InlineData("extended-pc-zero", LegacySnapshotError.UnsupportedVersion)]
    [InlineData("extended-header", LegacySnapshotError.UnsupportedVersion)]
    [InlineData("samrom", LegacySnapshotError.UnsupportedHardware)]
    [InlineData("im3", LegacySnapshotError.InvalidHeader)]
    [InlineData("extra-byte", LegacySnapshotError.InvalidLength)]
    [InlineData("short", LegacySnapshotError.InvalidLength)]
    [InlineData("header-short", LegacySnapshotError.InvalidLength)]
    [InlineData("empty", LegacySnapshotError.InvalidLength)]
    [InlineData("unbounded", LegacySnapshotError.InputTooLarge)]
    public void Z80RejectsUnsupportedAndMalformedLayouts(string mutation, LegacySnapshotError error)
    {
        var bytes = Fixture("legacy48-pattern-v1.z80");
        switch (mutation)
        {
            case "extended-pc-zero": bytes[6] = 0; bytes[7] = 0; break;
            case "extended-header": bytes[6] = 0; bytes[7] = 0; bytes = [.. bytes, 23, 0, .. new byte[23]]; break;
            case "samrom": bytes[12] |= 0x10; break;
            case "im3": bytes[29] = 3; break;
            case "extra-byte": bytes = [.. bytes, 0]; break;
            case "short": bytes = bytes[..^1]; break;
            case "header-short": bytes = bytes[..29]; break;
            case "empty": bytes = []; break;
            case "unbounded": bytes = new byte[LegacySnapshotDecoder.MaximumZ80Bytes + 1]; break;
        }
        var before = (byte[])bytes.Clone();
        Assert.Equal(error, Assert.Throws<LegacySnapshotException>(() => LegacySnapshotDecoder.DecodeZ80(bytes)).Error);
        Assert.Equal(before, bytes);
    }

    [Theory]
    [InlineData("zero-count")]
    [InlineData("overflow")]
    [InlineData("underflow")]
    [InlineData("truncated-token")]
    [InlineData("truncated-count")]
    [InlineData("lone-ed-eof")]
    [InlineData("no-terminator")]
    [InlineData("truncated-terminator")]
    [InlineData("wrong-terminator")]
    [InlineData("trailing")]
    [InlineData("empty")]
    public void RleRejectsMalformedStreams(string mutation)
    {
        var bytes = Fixture("legacy48-zero-v1-rle.z80");
        var header = bytes[..30];
        bytes = mutation switch
        {
            "zero-count" => [.. header, 0xed, 0xed, 0, 0x7f, .. bytes[30..]],
            "overflow" => [.. header, .. ZeroRun(49153), 0, 0xed, 0xed, 0],
            "underflow" => [.. header, .. ZeroRun(49151), 0, 0xed, 0xed, 0],
            "truncated-token" => [.. header, 0xed, 0xed, 1],
            "truncated-count" => [.. header, 0xed, 0xed],
            "lone-ed-eof" => [.. header, 0xed],
            "no-terminator" => bytes[..^4],
            "truncated-terminator" => bytes[..^1],
            "wrong-terminator" => [.. bytes[..^1], 1],
            "trailing" => [.. bytes, 0],
            _ => header
        };
        var before = (byte[])bytes.Clone();
        Assert.Equal(LegacySnapshotError.InvalidCompression,
            Assert.Throws<LegacySnapshotException>(() => LegacySnapshotDecoder.DecodeZ80(bytes)).Error);
        Assert.Equal(before, bytes);
    }

    [Fact]
    public void RleAcceptsCountOneAtMaximumInputBound()
    {
        var header = Fixture("legacy48-zero-v1-rle.z80")[..30];
        var bytes = new byte[LegacySnapshotDecoder.MaximumZ80Bytes];
        header.CopyTo(bytes, 0);
        var expected = new byte[49152];
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)(i % 256);
            bytes[30 + 4 * i] = 0xed;
            bytes[31 + 4 * i] = 0xed;
            bytes[32 + 4 * i] = 1;
            bytes[33 + 4 * i] = expected[i];
        }
        bytes[^3] = 0xed;
        bytes[^2] = 0xed;
        Assert.Equal(expected, LegacySnapshotDecoder.DecodeZ80(bytes).CopyRam());
    }

    [Fact]
    public void RleAcceptsFinalSolitaryEdAndOrdinaryLiteralPayload()
    {
        var header = Fixture("legacy48-zero-v1-rle.z80")[..30];
        var ram = Fixture("legacy48-pattern-v1.z80")[30..]; // no adjacent EDs
        ram[^1] = 0xed;
        byte[] bytes = [.. header, .. ram, 0, 0xed, 0xed, 0];
        Assert.Equal(ram, LegacySnapshotDecoder.DecodeZ80(bytes).CopyRam());
    }

    private static byte[] ZeroRun(int count)
    {
        var output = new List<byte>();
        while (count > 0)
        {
            var run = Math.Min(count, 255);
            output.AddRange([0xed, 0xed, (byte)run, 0]);
            count -= run;
        }
        return output.ToArray();
    }

    private static byte[] Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hc2000.slnx")))
            directory = directory.Parent;
        return File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "assets", "legacy48", name));
    }
}
