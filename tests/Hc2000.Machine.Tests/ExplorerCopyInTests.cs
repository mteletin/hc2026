using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class ExplorerCopyInTests
{
    private static (Dsk Disk, CpmFs Fs, Dpb Profile) Open(string profileName = "cpm-hc80")
    {
        var profile = Dpb.Presets().Single(p => p.Name == profileName);
        var disk = Dsk.Create(profile.Geometry);
        return (disk, new CpmFs(disk, profile), profile);
    }

    [Fact]
    public void CopyInWritesFileReadableBackThroughCpmFs()
    {
        var (disk, fs, _) = Open();
        var data = Enumerable.Range(0, 130).Select(n => (byte)(n * 7)).ToArray();
        var result = new ExplorerCopyIn(fs).CopyIn("FILE.BIN", data, 3);
        Assert.Equal("FILE.BIN", result.Name); Assert.Equal(3, result.User); Assert.Equal(2, result.Records); Assert.False(result.Replaced);
        var output = fs.Get("FILE.BIN", 3);
        Assert.Equal(data, output[..130]);
        Assert.True(output.Length is 256);
        Assert.All(output[130..], b => Assert.Equal(0x1a, b)); // text EOF padding to the 128-byte record boundary
        var reloaded = CpmFs.Open(Dsk.Load(disk.Save()));
        Assert.Equal(data, reloaded.Get("FILE.BIN", 3)[..130]);
    }

    [Fact]
    public void EmptyFileKeepsOneDirectoryEntryWithNoRecords()
    {
        var (disk, fs, _) = Open();
        var result = new ExplorerCopyIn(fs).CopyIn("EMPTY.BIN", []);
        Assert.Equal(0, result.Records);
        Assert.Empty(fs.Get("EMPTY.BIN"));
        Assert.Single(CpmFs.Open(Dsk.Load(disk.Save())).List(), f => f.Name == "EMPTY.BIN");
    }

    [Fact]
    public void LowercaseHostNamesUseInvariantUppercase()
    {
        var (_, fs, _) = Open();
        var result = new ExplorerCopyIn(fs).CopyIn("file.bin", [1, 2, 3]);
        Assert.Equal("FILE.BIN", result.Name);
        Assert.Contains(fs.List(), f => f.Name == "FILE.BIN");
        Assert.DoesNotContain(fs.List(), f => f.Name == "file.bin");
    }

    [Theory]
    [InlineData(1)] [InlineData(127)] [InlineData(128)] [InlineData(129)]
    [InlineData(16384)] [InlineData(16385)] [InlineData(32768)] [InlineData(32769)]
    public void RecordAndExtentBoundariesPreserveBytesAndOnlyPadTheLastRecord(int length)
    {
        var (disk, fs, _) = Open();
        var data = Enumerable.Range(0, length).Select(n => (byte)(n * 13)).ToArray();
        var result = new ExplorerCopyIn(fs).CopyIn("BOUNDS.BIN", data);
        var records = (length + 127) / 128;
        Assert.Equal(records, result.Records);
        var output = CpmFs.Open(Dsk.Load(disk.Save())).Get("BOUNDS.BIN");
        Assert.Equal(records * 128, output.Length);
        Assert.Equal(data, output[..length]);
        Assert.All(output[length..], b => Assert.Equal(0x1a, b));
    }

    [Theory]
    [InlineData("FILE", "file.")] [InlineData("file.", "FILE")]
    public void ExtensionlessAliasesShareReadbackCollisionAndExplicitReplace(string first, string alias)
    {
        var (disk, fs, _) = Open();
        var copy = new ExplorerCopyIn(fs);
        var result = copy.CopyIn(first, [1, 2, 3]);
        Assert.Equal("FILE", result.Name);
        var before = disk.Save();
        var failure = Assert.Throws<InvalidDataException>(() => copy.CopyIn(alias, [9]));
        Assert.Contains("File already exists", failure.Message);
        Assert.Equal(before, disk.Save());
        var replaced = copy.CopyIn(alias, [9], replace: true);
        Assert.True(replaced.Replaced);
        Assert.Equal("FILE", replaced.Name);
        Assert.Single(fs.List());
        Assert.Equal(9, CpmFs.Open(Dsk.Load(disk.Save())).Get("FILE")[0]);
    }

    [Theory]
    [InlineData("")] [InlineData("   ")]
    [InlineData(".BIN")] [InlineData("A.B.C")]
    [InlineData("123456789.BIN")] [InlineData("FILE.1234")]
    [InlineData("MY FILE.BIN")] [InlineData("FILÉ.BIN")] [InlineData("ſILE.BIN")]
    [InlineData("A\0B.BIN")] [InlineData("A\tB.BIN")]
    [InlineData("A<B.BIN")] [InlineData("A>B.BIN")] [InlineData("A/B.C")] [InlineData("A\\B.C")]
    [InlineData("A:B")] [InlineData("A;B")] [InlineData("A,B")] [InlineData("A=B")]
    [InlineData("A?B")] [InlineData("A*B")] [InlineData("A[B")] [InlineData("A]B")]
    [InlineData("A%B")] [InlineData("A|B")] [InlineData("A(B")] [InlineData("A)B")]
    [InlineData("A\"B")]
    public void NonManualCompatibleNamesAreRefused(string name)
    {
        var (disk, fs, _) = Open();
        var before = disk.Save();
        Assert.Throws<InvalidDataException>(() => new ExplorerCopyIn(fs).CopyIn(name, [1, 2, 3]));
        Assert.Equal(before, disk.Save());
    }

    [Theory]
    [InlineData(-1)] [InlineData(16)] [InlineData(31)]
    public void UserAreasOutsideCpmRangeAreRefused(int user)
    {
        var (disk, fs, _) = Open();
        var before = disk.Save();
        Assert.Throws<InvalidDataException>(() => new ExplorerCopyIn(fs).CopyIn("FILE.BIN", [1, 2, 3], user));
        Assert.Equal(before, disk.Save());
    }

    [Fact]
    public void CollisionIsRefusedUnlessReplaceIsAsked()
    {
        var (disk, fs, _) = Open();
        var copy = new ExplorerCopyIn(fs);
        copy.CopyIn("FILE.BIN", [1, 2, 3, 4]);
        var before = disk.Save();
        var typed = Assert.Throws<InvalidDataException>(() => copy.CopyIn("FILE.BIN", [9]));
        Assert.Contains("File already exists", typed.Message);
        Assert.Equal(before, disk.Save());
        // The collision is on the normalized name, not the host spelling.
        Assert.Throws<InvalidDataException>(() => copy.CopyIn("file.bin", [9]));
        var replaced = copy.CopyIn("FILE.BIN", [9], 0, replace: true);
        Assert.True(replaced.Replaced);
        var after = fs.Get("FILE.BIN");
        Assert.True(after.Length is 128); Assert.Equal(9, after[0]);
        foreach (var b in after.AsSpan(1)) Assert.Equal(0x1a, b);
        Assert.Single(fs.List(), f => f.Name == "FILE.BIN");
    }

    [Fact]
    public void MultiExtentFileRoundTrips()
    {
        var (disk, fs, _) = Open(); // one 16 KiB logical extent per entry
        var data = Enumerable.Range(0, 16385 + 16384).Select(n => (byte)(n * 31 & 0xff)).ToArray();
        var result = new ExplorerCopyIn(fs).CopyIn("BIG.BIN", data);
        Assert.Equal((16385 + 16384 + 127) / 128, result.Records);
        var output = CpmFs.Open(Dsk.Load(disk.Save())).Get("BIG.BIN");
        Assert.Equal(data, output[..data.Length]);
    }

    [Fact]
    public void DiskFullIsRefusedWithItsOwnMessageAndAnUnchangedImage()
    {
        var (disk, fs, _) = Open();
        var copy = new ExplorerCopyIn(fs);
        var payload = new byte[16384];
        var written = 0; string message = "";
        var before = disk.Save();
        try
        {
            for (var i = 0; ; i++)
            {
                before = disk.Save();
                copy.CopyIn($"F{i:D4}.BIN", payload); written++;
            }
        }
        catch (InvalidDataException ex) { message = ex.Message; }
        Assert.True(written >= 40, $"only {written} 16 KiB files fit");
        Assert.Equal("Disk is full.", message);
        Assert.Equal(before, disk.Save());
        // Re-opening re-validates the whole directory and allocation: a torn write cannot survive.
        var after = CpmFs.Open(Dsk.Load(disk.Save())).List();
        Assert.True(after.Count == written);
        Assert.True(after.DistinctBy(f => f.Name).Count() == written);
    }

    [Fact]
    public void DirectoryFullIsRefusedWithItsOwnMessageAndAnUnchangedImage()
    {
        var (disk, fs, _) = Open(); // 128 entries total, 1 block per 1-byte file
        var copy = new ExplorerCopyIn(fs);
        var written = 0; string message = "";
        var before = disk.Save();
        try
        {
            for (var i = 0; ; i++)
            {
                before = disk.Save();
                copy.CopyIn($"D{i:D4}.BIN", [0x42]); written++;
            }
        }
        catch (InvalidDataException ex) { message = ex.Message; }
        Assert.True(written >= 120, $"only {written} files fit the directory");
        Assert.Equal("Directory is full.", message);
        Assert.Equal(before, disk.Save());
        Assert.True(CpmFs.Open(Dsk.Load(disk.Save())).List().Count == written);
    }

    [Fact]
    public void FailedLargerReplacementPreservesOriginalAndUnrelatedFiles()
    {
        var (disk, fs, profile) = Open();
        var copy = new ExplorerCopyIn(fs);
        copy.CopyIn("ORIGINAL.BIN", [1, 2, 3]);
        copy.CopyIn("OTHER.BIN", [4, 5]);
        var before = disk.Save();
        var tooLarge = new byte[(profile.Dsm + 1) * profile.BlockSize];
        var failure = Assert.Throws<InvalidDataException>(() => copy.CopyIn("ORIGINAL.BIN", tooLarge, replace: true));
        Assert.Equal("Disk is full.", failure.Message);
        Assert.Equal(before, disk.Save());
        var reloaded = CpmFs.Open(Dsk.Load(disk.Save()));
        Assert.Equal([1, 2, 3], reloaded.Get("ORIGINAL.BIN")[..3]);
        Assert.Equal([4, 5], reloaded.Get("OTHER.BIN")[..2]);
    }

    [Fact]
    public void AnyRefusedCopyLeavesTheImageByteUnchanged()
    {
        var (disk, fs, _) = Open();
        var copy = new ExplorerCopyIn(fs);
        copy.CopyIn("A.BIN", [1, 2, 3]); copy.CopyIn("B.BIN", [4, 5]);
        var before = disk.Save(); var beforeList = CpmFs.Open(Dsk.Load(disk.Save())).List();
        Assert.Throws<InvalidDataException>(() => copy.CopyIn("A.BIN", [9])); // collision
        Assert.Throws<InvalidDataException>(() => copy.CopyIn("A?B.C", [9])); // name
        Assert.Throws<InvalidDataException>(() => copy.CopyIn("C.BIN", [9], 17)); // user
        Assert.Equal(before, disk.Save());
        Assert.Equal(beforeList, CpmFs.Open(Dsk.Load(disk.Save())).List(),
            new AssertingEqualityComparer());
    }

    private sealed class AssertingEqualityComparer : IEqualityComparer<CpmFile>
    {
        public bool Equals(CpmFile? x, CpmFile? y) => x!.Name == y!.Name && x.User == y.User && x.Records == y.Records && x.AllocatedBytes == y.AllocatedBytes;
        public int GetHashCode(CpmFile obj) => HashCode.Combine(obj.Name, obj.User);
    }

    [Fact]
    public void HcHeaderDisksKeepLiteralNamesThroughTheSamePolicy()
    {
        var (disk, fs, _) = Open("hcfs80");
        var copy = new ExplorerCopyIn(fs);
        var result = copy.CopyIn("file.COM", [1, 2, 3], 3);
        Assert.Equal("file.COM", result.Name); // literal 11-byte names are case-sensitive, no 8.3 rewriting
        Assert.Equal([1, 2, 3], fs.Get("file.COM", 3)[..3]);
        Assert.Throws<InvalidDataException>(() => copy.CopyIn("file.COM", [9], 3)); // collision still refused
        Assert.Throws<InvalidDataException>(() => copy.CopyIn("NAME.WITH.TOO.MANY.DOTS", [1], 3)); // > 11 bytes
    }
}
