using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>
/// Explorer export-lane core (hc2000-explorer-copy-out): resolving one named entry out of an
/// open filesystem to its stored records, byte-faithful, read-only. Host naming, collision and
/// file writing are covered by Hc2000.App.Tests/ExplorerCopyOutHostTests.
/// </summary>
public sealed class ExplorerCopyOutTests
{
    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var n = 0; n < length; n++) data[n] = (byte)(n % 256); // covers every byte value, 0x00 and 0x1A included
        return data;
    }

    [Theory]
    [InlineData("CON.BAT", "hc-CON.BAT")] // reserved device names stay legal with a prefix
    [InlineData("AUX", "hc-AUX")]
    [InlineData("com1", "hc-com1")]
    [InlineData("LPT9.LOG", "hc-LPT9.LOG")]
    [InlineData("A B.C", "A B.C")] // spaces are legal host names; CP/M identity is never rewritten
    [InlineData("MY FILE.TXT", "MY FILE.TXT")]
    [InlineData("FILE.", "FILE")] // trailing dot is not legal on every host
    [InlineData("FILE. .   ", "FILE")] // literal HC fields can mix trailing dots and spaces
    [InlineData("NUL.A.B", "hc-NUL.A.B")] // Windows device identity is before the first dot
    [InlineData("\x01HID", "_HID")] // control bytes are replaced, not dropped silently
    public void HostNameSanitisesToALegalHostName(string cpmName, string expected)
        => Assert.Equal(expected, ExplorerCopyOut.HostName(cpmName));

    [Fact]
    public void ExtractIsByteFaithfulToStoredRecordsIncludingTerminalZeroAndCtrlZ()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        var data = Pattern(130); data[^2] = 0x00; data[^1] = 0x1a; // terminal bytes as stored, never trimmed
        new CpmFs(disk, profile).Put("TAIL.BIN", data);
        var fs = CpmFs.Open(Dsk.Load(disk.Save()));
        var entry = ExplorerCopyOut.Extract(fs, "TAIL.BIN", 0);
        var expected = new byte[256]; data.CopyTo(expected, 0); Array.Fill(expected, (byte)0x1a, 130, 126);
        Assert.Equal(expected, entry.Data); // stored 0x00 and 0x1A kept, allocation padding preserved
        Assert.Equal("TAIL.BIN", entry.CpmName);
        Assert.Equal(0, entry.User);
        Assert.Equal(2, entry.Records);
        Assert.Equal(256, entry.Bytes);
    }

    [Fact]
    public void ExtractMultiExtentFileIsByteIdentical()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        var data = Pattern(40000); // 16K extent: spans three directory entries
        new CpmFs(disk, profile).Put("BIG.BIN", data);
        var entry = ExplorerCopyOut.Extract(CpmFs.Open(Dsk.Load(disk.Save())), "BIG.BIN", 0);
        var expected = new byte[(40000 + 127) / 128 * 128]; data.CopyTo(expected, 0); Array.Fill(expected, (byte)0x1a, 40000, expected.Length - 40000);
        Assert.Equal(expected, entry.Data);
        Assert.Equal(313, entry.Records);
    }

    [Fact]
    public void ExtractKeepsTheOnDiskIdentityInTheEntry()
    {
        var cpm = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(cpm.Geometry);
        new CpmFs(disk, cpm).Put("lower.bin", Pattern(256), 1); // on-disk identity is case as stored
        var entry = ExplorerCopyOut.Extract(CpmFs.Open(Dsk.Load(disk.Save())), "LOWER.BIN", 1);
        Assert.Equal("lower.bin", entry.CpmName);
        Assert.Equal(1, entry.User);
        var hc = Dpb.Presets().Single(p => p.Name == "hcfs80"); // literal 11-byte 7-bit names, case kept
        var hcDisk = Dsk.Create(hc.Geometry);
        new CpmFs(hcDisk, hc).Put("EXACT.D", Pattern(256));
        var hcEntry = ExplorerCopyOut.Extract(CpmFs.Open(Dsk.Load(hcDisk.Save())), "EXACT.D", 0);
        Assert.Equal("EXACT.D".PadRight(11), hcEntry.CpmName); // the stored field, presentation unchanged
        Assert.Equal(11, hcEntry.CpmName.Length);
    }

    [Fact]
    public void ExtractIsReadOnlyWithRespectToTheFilesystem()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("RO.BIN", Pattern(256));
        var image = disk.Save();
        var opened = Dsk.Load(image);
        var fs = CpmFs.Open(opened);
        ExplorerCopyOut.Extract(fs, "RO.BIN", 0);
        Assert.Equal(image, opened.Save()); // inspect the same disk that extraction read
    }

    [Fact]
    public void ExtractOfAnAbsentEntryIsFileNotFound()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("HERE.BIN", Pattern(128));
        var fs = CpmFs.Open(Dsk.Load(disk.Save()));
        Assert.Throws<FileNotFoundException>(() => ExplorerCopyOut.Extract(fs, "ABSENT.BIN", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExplorerCopyOut.Extract(fs, "HERE.BIN", 32));
    }

    [Fact]
    public void SameNameInDifferentUserAreasExtractsIndependently()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        var fs = new CpmFs(disk, profile);
        fs.Put("TWO.BIN", Pattern(200), 0);
        fs.Put("TWO.BIN", Pattern(400), 1);
        var fs2 = CpmFs.Open(Dsk.Load(disk.Save()));
        var zero = ExplorerCopyOut.Extract(fs2, "TWO.BIN", 0);
        var one = ExplorerCopyOut.Extract(fs2, "TWO.BIN", 1);
        Assert.Equal(256, zero.Bytes); Assert.Equal(512, one.Bytes);
        Assert.NotEqual(zero.Data, one.Data);
    }
}
