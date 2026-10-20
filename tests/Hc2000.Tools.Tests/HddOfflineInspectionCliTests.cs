using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Hc2000.Tools.Tests;

// Independent raw HCVD/HCVM1END literals: no production encoder, parser or formatter oracle.
// Private inputs and preservation receipts are intentionally retained, never owner assets.
public sealed class HddOfflineInspectionCliTests(ITestOutputHelper log)
{
    private const int Arena = 16_777_216;
    private const string IndexHex = "4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce";
    private const string FooterHex = "4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7";
    private const string Header = "hdd-container version=1 id=0102030405060708090a0b0c0d0e0f10 revision=1 entries=2 arenaBytes=16777216 indexBytes=60 indexCrc=ce4582da indexStart=16777216 indexEnd=16777276 fileBytes=16777340 integrity=metadata-only";
    private const string Row0 = "hdd-volume row=0 ordinal=0 name=\"VOL1.DSK\" protection=rw offset=0 bytes=8388608 storedImageCrc=0362a35b";
    private const string Row1 = "hdd-volume row=1 ordinal=1 name=\"VOL2.DSK\" protection=ro offset=8388608 bytes=8388608 storedImageCrc=25cfecb9";
    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
    private static readonly Lazy<string> DefaultProofRoot = new(() => ResolveProofRoot(null));

    internal static string ResolveProofRoot(string? requestedRoot)
        => requestedRoot ?? Directory.CreateTempSubdirectory("hc-hdd-inspection-proof-").FullName;

    [Fact]
    public void LiteralTwoVolumeInspectionReportsMetadata()
    {
        var path = Image("literal");
        var result = Inspect(path);
        Assert.True(result.Code == 0, Evidence(result));
        Assert.Equal("", result.Error);
        Assert.Equal(Lines(Header, Row0, Row1), result.Output);
    }

    [Fact]
    public void PayloadMutationDoesNotClaimPayloadIntegrity()
    {
        var path = Image("payload-only");
        // Intentional setup writes precede the sealed inspection/preservation baseline.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        using (var file = File.OpenWrite(path)) { file.Position = 32768; file.WriteByte(0xa5); }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        var result = Inspect(path);
        Assert.True(result.Code == 0, Evidence(result));
        Assert.Equal(Lines(Header, Row0, Row1), result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void ReorderedRowsKeepOrdinalOffsets()
    {
        var index = Convert.FromHexString(IndexHex);
        var first = index.AsSpan(8, 24).ToArray();
        index.AsSpan(32, 24).CopyTo(index.AsSpan(8));
        first.CopyTo(index, 32);
        var footer = Convert.FromHexString(FooterHex);
        Link(index, footer);
        var result = Inspect(Image("reordered", index, footer));
        Assert.True(result.Code == 0, Evidence(result));
        Assert.Empty(result.Error);
        var lines = result.Output.Split(Environment.NewLine);
        Assert.Equal(Row1.Replace("row=1", "row=0", StringComparison.Ordinal), lines[1]);
        Assert.Equal(Row0.Replace("row=0", "row=1", StringComparison.Ordinal), lines[2]);
        Assert.Equal(4, lines.Length);
    }

    [Theory]
    [InlineData("O'NEIL  DSK", "\"O\\u0027NEIL.DSK\"")]
    [InlineData("A&B     DSK", "\"A\\u0026B.DSK\"")]
    [InlineData("A B     DSK", "\"A B.DSK\"")]
    public void AcceptedNamesHaveLiteralJsonEscaping(string wire, string expected)
    {
        var index = Convert.FromHexString(IndexHex);
        Encoding.ASCII.GetBytes(wire).CopyTo(index, 8);
        var footer = Convert.FromHexString(FooterHex);
        Link(index, footer);
        var result = Inspect(Image("escaped", index, footer));
        Assert.True(result.Code == 0, Evidence(result));
        Assert.Empty(result.Error);
        Assert.Equal(Row0.Replace("\"VOL1.DSK\"", expected, StringComparison.Ordinal), result.Output.Split(Environment.NewLine)[1]);
        Assert.Equal(Row1, result.Output.Split(Environment.NewLine)[2]);
    }

    [Fact]
    public void ValidEmptyContainerHasOnlyMetadataHeader()
    {
        var index = Convert.FromHexString("484356440100000000000000");
        var footer = Convert.FromHexString(FooterHex);
        Put64(footer, 40, 0); Put32(footer, 48, 12); Link(index, footer);
        var result = Inspect(Image("empty", index, footer, 0));
        Assert.True(result.Code == 0, Evidence(result));
        Assert.Empty(result.Error);
        // CRC independently calculated for this raw index, not obtained from production output.
        Assert.Equal(Lines($"hdd-container version=1 id=0102030405060708090a0b0c0d0e0f10 revision=1 entries=0 arenaBytes=0 indexBytes=12 indexCrc={Crc(index.AsSpan(0, 8)):x8} indexStart=0 indexEnd=12 fileBytes=76 integrity=metadata-only"), result.Output);
    }

    [Theory]
    [InlineData("footer-magic")]
    [InlineData("footer-version")]
    [InlineData("footer-flags")]
    [InlineData("empty-id")]
    [InlineData("zero-revision")]
    [InlineData("footer-reserved")]
    [InlineData("footer-crc")]
    [InlineData("arena")]
    [InlineData("index-length")]
    [InlineData("index-magic")]
    [InlineData("index-version")]
    [InlineData("index-reserved")]
    [InlineData("index-count")]
    [InlineData("duplicate-name")]
    [InlineData("flags")]
    [InlineData("image-length")]
    [InlineData("ordinal-range")]
    [InlineData("ordinal-duplicate")]
    [InlineData("index-crc")]
    [InlineData("crc-link")]
    [InlineData("escape-name")]
    [InlineData("newline-name")]
    [InlineData("tab-name")]
    [InlineData("del-name")]
    public void InvalidMetadataRefusesWithoutPartialOutputOrRepair(string kind)
    {
        var index = Convert.FromHexString(IndexHex);
        var footer = Convert.FromHexString(FooterHex);
        switch (kind)
        {
            case "footer-magic": footer[0] ^= 1; break;
            case "footer-version": Put32(footer, 8, 2); break;
            case "footer-flags": footer[12] = 1; break;
            case "empty-id": footer.AsSpan(16, 16).Clear(); break;
            case "zero-revision": Put64(footer, 32, 0); break;
            case "footer-reserved": footer[56] = 1; break;
            case "arena": Put64(footer, 40, Arena + 1); break;
            case "index-length": Put32(footer, 48, 61); break;
            case "index-magic": index[0] ^= 1; break;
            case "index-version": index[4] = 2; break;
            case "index-reserved": index[5] = 1; break;
            case "index-count": index[6] = 1; break;
            case "duplicate-name": index.AsSpan(8, 11).CopyTo(index.AsSpan(32)); break;
            case "flags": index[19] = 2; break;
            case "image-length": Put32(index, 20, 8388607); break;
            case "ordinal-range": Put32(index, 24, 2); break;
            case "ordinal-duplicate": Put32(index, 48, 0); break;
            case "escape-name": index[8] = 27; break;
            case "newline-name": index[8] = 10; break;
            case "tab-name": index[8] = 9; break;
            case "del-name": index[8] = 127; break;
        }
        Link(index, footer);
        if (kind == "footer-crc") footer[60] ^= 1;
        if (kind == "index-crc") index[^1] ^= 1;
        if (kind == "crc-link") { footer[52] ^= 1; Put32(footer, 60, Crc(footer.AsSpan(0, 60))); }
        Refused(Inspect(Image(kind, index, footer)));
    }

    [Theory]
    [InlineData(0)] [InlineData(63)] [InlineData(75)]
    public void ShortInputsRefuse(int length)
    {
        var path = Image("short", [], [], length);
        Refused(Inspect(path));
    }

    [Theory]
    [InlineData(-1)] [InlineData(1)]
    public void TruncatedOrAppendedContainerRefuses(int delta)
    {
        var index = Convert.FromHexString("484356440100000000000000");
        var footer = Convert.FromHexString(FooterHex);
        Put64(footer, 40, 0); Put32(footer, 48, 12); Link(index, footer);
        var path = Image("length", index, footer, 0);
        // Close the writable setup handle and seal permissions before Inspect snapshots.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        using (var stream = File.OpenWrite(path)) stream.SetLength(stream.Length + delta);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        Refused(Inspect(path));
    }

    [Fact]
    public void TinyFooterHitsEntryAdmissionWithoutLargeImage()
    {
        var footer = Convert.FromHexString(FooterHex);
        Put64(footer, 40, 4_294_967_296); Put32(footer, 48, 12300);
        Put32(footer, 60, Crc(footer.AsSpan(0, 60)));
        var result = Inspect(Image("limit", new byte[12], footer, 0));
        Refused(result);
        Assert.Contains("Entry count exceeds caller limit", result.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("option")]
    [InlineData("extra")]
    public void InvalidArgumentsNeverCreateDestinations(string kind)
    {
        var path = Image("arguments", [], [], 0);
        Refused(kind switch
        {
            "missing" => Inspect(path, ["inspect", Path.Combine(Path.GetDirectoryName(path)!, "absent.img")]),
            "option" => Inspect(path, ["inspect", path, "--force"]),
            _ => Inspect(path, ["inspect", path, "destination.img"])
        });
    }

    private static void Refused((int Code, string Output, string Error) result)
    {
        Assert.True(result.Code == 1, Evidence(result));
        Assert.Empty(result.Output);
        Assert.StartsWith("hc hdd: ", result.Error);
    }

    private static string Evidence((int Code, string Output, string Error) r) =>
        $"return={r.Code}; stdout=[{r.Output}]; stderr=[{r.Error}]";

    private (int Code, string Output, string Error) Inspect(string path, string[]? args = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        var before = Snapshot(directory);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = StorageCommand.Run("hdd", args ?? ["inspect", path], output, error);
        var result = (code, output.ToString(), error.ToString());
        var after = Snapshot(directory);
        log.WriteLine(Evidence(result));
        log.WriteLine("preservation before:\n" + before + "\nafter:\n" + after);
        File.WriteAllText(Path.Combine(directory, "receipt.txt"), Evidence(result) + "\nbefore:\n" + before + "\nafter:\n" + after);
        Assert.Equal(before, after);
        return result;
    }

    private static string Snapshot(string directory) => string.Join("\n", Directory.GetFiles(directory)
        .Order(StringComparer.Ordinal).Select(path =>
        {
            using var input = File.OpenRead(path);
            var mode = OperatingSystem.IsWindows() ? "n/a" : File.GetUnixFileMode(path).ToString();
            return $"{Path.GetFileName(path)} bytes={input.Length} mode={mode} sha256={Convert.ToHexString(SHA256.HashData(input))}";
        }));

    private static string Image(string kind, byte[]? index = null, byte[]? footer = null, long arena = Arena)
    {
        var root = Environment.GetEnvironmentVariable("HC_HDD_INSPECTION_PROOF_ROOT") ?? DefaultProofRoot.Value;
        var directory = Path.Combine(root, kind + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "input.img");
        using (var file = File.Create(path))
        {
            file.SetLength(arena); file.Position = arena;
            file.Write(index ?? Convert.FromHexString(IndexHex));
            file.Write(footer ?? Convert.FromHexString(FooterHex));
        }
        File.WriteAllText(Path.Combine(directory, "sentinel.img"), "unrelated private sentinel");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        return path;
    }

    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
    // Separate test-only bitwise CRC oracle; no production CRC or encoder calls.
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        }
        return ~crc;
    }
    private static void Link(byte[] index, byte[] footer)
    {
        var crc = Crc(index.AsSpan(0, index.Length - 4));
        Put32(index, index.Length - 4, crc); Put32(footer, 52, crc);
        Put32(footer, 60, Crc(footer.AsSpan(0, 60)));
    }
}
