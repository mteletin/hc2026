using System.Globalization;
using System.Text.RegularExpressions;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class StorageAssetTests
{
    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hc2000.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    public static IEnumerable<object[]> Disks() => Directory.GetFiles(Path.Combine(Root(), "disks"), "*", SearchOption.AllDirectories)
        .Where(p => Path.GetExtension(p).Equals(".dsk", StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p, StringComparer.Ordinal).Select(p => new object[] { Path.GetRelativePath(Root(), p) });
    [StorageDisksTheory]
    [MemberData(nameof(Disks))]
    public void ImageRoundTripAndDirectoryMatchCatalog(string relative)
    {
        var input = File.ReadAllBytes(Path.Combine(Root(), relative)); var disk = Dsk.Load(input);
        Assert.Equal(input, disk.Save());
        var fs = CpmFs.Open(disk); var files = fs.List();
        var catalog = File.ReadAllLines(Path.Combine(Root(), "disks", "catalog.txt"));
        var start = Array.FindIndex(catalog, l => l.Trim().Equals(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            // The only renamed lowercase-extension fixture has no listing in the pack;
            // catalog.txt records that its old reader could not recognize this disk.
            Assert.Equal("RARESIndiJonesTemple.dsk", Path.GetFileName(relative));
            Assert.Equal("805cb0ed04b0cffa8f5db317034b71df2839ea3fa7e3c145491b950946a70044", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant());
            Assert.Contains(catalog, l => l.Contains("RARESINDIANA is not a recognized disk/file system."));
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                Assert.Equal(file.Records * 128, fs.Get(file.Name, file.User).Length);
                Assert.Equal(11,file.Name.Length);
                var hc = new HcFs(fs); var inspection = hc.Inspect(file);
                if (inspection.Header is not null) { Assert.Null(inspection.Diagnostic); Assert.Equal(inspection.Header.Length, hc.Get(file.Name,file.User).Length); }
            }
            Assert.Equal(input,disk.Save()); return;
        }
        var rows = catalog.Skip(start + 1).TakeWhile(l => !l.Trim().EndsWith(".DSK", StringComparison.OrdinalIgnoreCase))
            .Where(l => Regex.IsMatch(l, @"^\s+\d+\t"))
            .Select(l => l.Split('\t').Where(s => s.Length > 0).ToArray()).ToArray();
        Assert.Equal(rows.Length, files.Count);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; var file = files[i];
            Assert.Equal(int.Parse(row[1], CultureInfo.InvariantCulture), file.User);
            var catalogName = row[2].TrimEnd();
            var expectedName = fs.Parameters.HcHeader ? catalogName : catalogName.Replace(" ", "");
            Assert.Equal(expectedName, fs.Parameters.HcHeader ? Regex.Replace(file.Name.TrimEnd(' '), @"[/*?\\\x00-\x1f]", "_") : file.Name);
            Assert.Equal((int)(double.Parse(row[3], CultureInfo.InvariantCulture) * 1024), file.AllocatedBytes);
            Assert.Equal(row[4].Contains('R'), file.ReadOnly); Assert.Equal(row[4].Contains('S'), file.System);
            Assert.Equal(file.Records * 128, fs.Get(file.Name, file.User).Length);
            if (fs.Parameters.HcHeader)
            {
                Assert.Equal(11,file.Name.Length);
                var hc = new HcFs(fs); var inspection = hc.Inspect(file);
                if (row[5].Trim() == "Untyped")
                {
                    Assert.Null(inspection.Header); Assert.Contains("Untyped", inspection.Diagnostic);
                    Assert.True(inspection.IsUntyped);
                    Assert.Throws<InvalidDataException>(() => hc.Get(file.Name, file.User));
                    continue;
                }
                Assert.Null(inspection.Diagnostic);
                var header = hc.Header(file.Name, file.User);
                Assert.Equal(header.Length, hc.Get(file.Name, file.User).Length);
                var expectedType = row[5].Trim() switch { "Program" => 0, "Bytes" => 3, "CharArray" => 2, "NumberArray" => 1, _ => (int?)null };
                if (expectedType.HasValue) Assert.Equal(expectedType.Value, header.Type);
                if (int.TryParse(row[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startValue))
                    Assert.Equal(startValue, header.Type == 0 ? header.Autostart : header.Start);
                if (int.TryParse(row[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lengthValue))
                    Assert.Equal(lengthValue, header.Length);
            }
        }
        Assert.Equal(input, disk.Save()); // listing never repairs the original container/directory
    }

    [StorageAssetFact("disks/basic/ANDREI1.DSK")]
    public void Hc40MultiExtentFixtureProvesWordAllocationsAndCaseSensitiveNames()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "disks", "basic", "ANDREI1.DSK"));
        Assert.Equal("c4bee9fa10feeca65f7bac0aabc81c248b380e5dc2d0511f1cef5276314b307a", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        // Directory logical offsets 64 and 96: physical offsets 0x240 and 0x260.
        Assert.Equal("00414C49454E202020A0A020000000800400050006000700080009000A000B00", Convert.ToHexString(bytes.AsSpan(0x240, 32)));
        Assert.Equal("00414C49454E202020A0A020010000480C000D000E000F001000000000000000", Convert.ToHexString(bytes.AsSpan(0x260, 32)));
        var disk = Dsk.Load(bytes); var fs = CpmFs.Open(disk); var hc = new HcFs(fs);
        Assert.Equal("hcfs40", fs.Parameters.Name); Assert.True(fs.Parameters.WordAllocations);
        Assert.Equal(25536, hc.Get("ALIEN").Length); Assert.Equal(3, hc.Header("ALIEN").Type); Assert.Equal(40000, hc.Header("ALIEN").Start);
        Assert.Equal(0, hc.Header("alien").Type); Assert.Equal(182, hc.Header("alien").Variables);
        Assert.Throws<InvalidDataException>(() => new CpmFs(disk, fs.Parameters with { WordAllocations = false, Exm = 1 }));
    }
}

public sealed class StorageDisksTheoryAttribute : TheoryAttribute
{
    public StorageDisksTheoryAttribute()
    {
        if (StorageAssetTests.Disks().Count() != 77) Skip = "Complete copyrighted disk fixture set is absent.";
    }
}
public sealed class StorageAssetFactAttribute : FactAttribute
{
    public StorageAssetFactAttribute(string relative)
    {
        if (!File.Exists(Path.Combine(StorageAssetTests.Root(), relative))) Skip = "Copyrighted asset is absent: " + relative;
    }
}
