using System.Security.Cryptography;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddColdVolumeTests : IDisposable
{
    private const int S = 8_388_608;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-cold-" + Guid.NewGuid().ToString("N"));
    internal string PathName => Path.Combine(directory, "volumes.hdd");
    private readonly IDisposable domain;
    private static byte[] Index() => Convert.FromHexString("4843564401000200564f4c312020202044534b0000008000000000005ba36203564f4c322020202044534b010000800001000000b9eccf25da8245ce");
    private static byte[] Footer() => Convert.FromHexString("4843564d31454e4401000000000000000102030405060708090a0b0c0d0e0f10010000000000000000000001000000003c000000da8245ce00000000b7cd3ab7");
    private static byte[] Payload(byte content)
    {
        var bytes = new byte[S]; bytes.AsSpan(0, 32768).Fill(0xe5);
        Convert.FromHexString("0053454544202020205458540000000108000000000000000000000000000000").CopyTo(bytes, 0);
        bytes.AsSpan(32768, 4096).Fill(content); return bytes;
    }
    public HddColdVolumeTests()
    {
        Directory.CreateDirectory(directory);
        var admissionDirectory = Path.Combine(directory, "admission");
        Directory.CreateDirectory(admissionDirectory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(admissionDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        domain = Hc2000.HostFiles.NativeImageAdmission.EnterPrivateTestDomain(admissionDirectory);
        using var file = File.Create(PathName);
        file.Write(Payload(0x41)); file.Write(Payload(0x42)); file.Write(Index()); file.Write(Footer());
    }
    [Theory]
    [InlineData("vol1.dsk", 0x41, "9D0ABB36878273883169C3756407D0658B4C8EC8E6B31CE35BC70F188BEE987F")]
    [InlineData("VOL2.DSK", 0x42, "F484C7D32E06CBA31D0DD508A1EBC7C26CC4ECDC18A738E040CC3D6BEF7F1EC3")]
    public void LiteralNamedVolumeIsAnExactProtectedCopy(string name, byte content, string sha)
    {
        var before = SHA256.HashData(File.ReadAllBytes(PathName));
        using (var selected = HddColdVolume.Open(PathName, name))
        {
            var media = selected.Media(2);
            Assert.Equal(S, media.Bytes.Length); Assert.True(media.WriteProtected);
            Assert.Equal(content, media.Bytes[32768]);
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(media.Bytes.AsSpan())));
            Assert.Equal(name.ToUpperInvariant(), selected.Identity.Name);
            Assert.Throws<ArgumentOutOfRangeException>(() => selected.Media(1));
        }
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(PathName)));
    }
    [Fact]
    public void PreparedSelectionCannotSurviveChangedPayloadOrMetadata()
    {
        Hc2000.Machine.Storage.HddVolumeIdentity expected;
        using (var first = HddColdVolume.Open(PathName, "VOL2.DSK")) expected = first.Identity;
        using (var unchanged = HddColdVolume.Open(PathName, "VOL2.DSK", expected)) Assert.True(unchanged.Media(2).WriteProtected);
        using (var file = File.OpenWrite(PathName)) { file.Position = S + 32768; file.WriteByte(0x43); }
        var changed = File.ReadAllBytes(PathName);
        Assert.Throws<InvalidDataException>(() => HddColdVolume.Open(PathName, "VOL2.DSK", expected));
        Assert.Equal(changed, File.ReadAllBytes(PathName));
    }
    [Theory]
    [InlineData("MISSING.DSK")]
    [InlineData("../VOL2.DSK")]
    public void InvalidOrMissingNamesPreserveSource(string name)
    {
        var before = File.ReadAllBytes(PathName);
        Assert.Throws<InvalidDataException>(() => HddColdVolume.Open(PathName, name));
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }
    [Fact]
    public void InvalidFooterAndTruncatedExtentRefuseWithoutRepair()
    {
        using (var file = File.OpenWrite(PathName)) file.SetLength(2L * S + 123);
        var before = File.ReadAllBytes(PathName);
        Assert.Throws<InvalidDataException>(() => HddColdVolume.Open(PathName, "VOL1.DSK"));
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }
    [Fact]
    public void SymlinkAdmissionRefuses()
    {
        if (OperatingSystem.IsWindows()) return;
        var link = Path.Combine(directory, "alias.hdd"); File.CreateSymbolicLink(link, PathName);
        Assert.Throws<InvalidDataException>(() => HddColdVolume.Open(link, "VOL1.DSK"));
    }
    public void Dispose() { domain.Dispose(); Directory.Delete(directory, true); }
}
