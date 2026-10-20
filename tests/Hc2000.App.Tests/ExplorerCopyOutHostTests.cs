using Hc2000.App.Explorer;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// Explorer export-lane host layer (hc2000-explorer-copy-out): writing one named entry out of
/// an admitted image to a host path. The source image is opened read-only and never mutated;
/// the host name is sanitised with the CP/M identity kept in the mapping; a host collision is
/// refused unless replace is explicit.
/// </summary>
public sealed class ExplorerCopyOutHostTests : IDisposable
{
    private readonly string root;
    public ExplorerCopyOutHostTests()
    {
        root = Path.Combine(Path.GetTempPath(), "explorer-copy-out-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }
    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private string Dir(string name)
    {
        var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path;
    }
    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var n = 0; n < length; n++) data[n] = (byte)(n % 256);
        return data;
    }
    private byte[] Padded(byte[] data)
    {
        var expected = new byte[(data.Length + 127) / 128 * 128];
        data.CopyTo(expected, 0); Array.Fill(expected, (byte)0x1a, data.Length, expected.Length - data.Length);
        return expected;
    }
    private string SaveImage(Dsk disk)
    {
        var path = Path.Combine(root, "image-" + Guid.NewGuid().ToString("N") + ".dsk");
        File.WriteAllBytes(path, disk.Save()); return path;
    }

    [Fact]
    public void CopyInThenCopyOutRoundTripIsByteIdenticalForFixtureFile()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        var fixture = Pattern(10240); // record-aligned: the host fixture itself must round-trip exactly
        fixture[^2] = 0x00; fixture[^1] = 0x1a;
        new ExplorerCopyIn(new CpmFs(disk, profile)).CopyIn("fixture.bin", fixture);
        var image = SaveImage(disk);
        var before = File.ReadAllBytes(image);
        var destination = Path.Combine(Dir("out"), "fixture.bin");
        var mapping = ExplorerCopyOutHost.CopyOut(image, "FIXTURE.BIN", 0, destination);
        Assert.Equal(fixture, File.ReadAllBytes(destination));
        Assert.Equal("FIXTURE.BIN", mapping.CpmName); // CP/M name kept in the returned mapping
        Assert.Equal(0, mapping.User);
        Assert.Equal(Path.GetFullPath(destination), mapping.HostPath);
        Assert.Equal("fixture.bin", mapping.HostName);
        Assert.Equal(80, mapping.Records);
        Assert.Equal(fixture.Length, mapping.Bytes);
        Assert.Equal(before, File.ReadAllBytes(image)); // source image byte-for-byte unchanged
    }

    [Fact]
    public void ExportPreservesStoredTerminalZeroAndCtrlZWithoutTrimming()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        var data = Pattern(130); data[^2] = 0x00; data[^1] = 0x1a; // terminal bytes as stored, never trimmed
        new CpmFs(disk, profile).Put("TAIL.BIN", data);
        var image = SaveImage(disk);
        var destination = Path.Combine(Dir("out"), "tail.bin");
        var mapping = ExplorerCopyOutHost.CopyOut(image, "TAIL.BIN", 0, destination);
        Assert.Equal(Padded(data), File.ReadAllBytes(destination));
        Assert.Equal(2, mapping.Records); Assert.Equal(256, mapping.Bytes);
    }

    [Fact]
    public void MultiExtentFileRoundTripsByteIdentical()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("BIG.BIN", Pattern(40000));
        var image = SaveImage(disk);
        var destination = Path.Combine(Dir("out"), "big.bin");
        var mapping = ExplorerCopyOutHost.CopyOut(image, "BIG.BIN", 0, destination);
        Assert.Equal(Padded(Pattern(40000)), File.ReadAllBytes(destination));
        Assert.Equal(313, mapping.Records);
    }

    [Fact]
    public void DirectoryDestinationUsesSanitisedHostNameAndKeepsCpmNameInMapping()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "hcfs80"); // literal 11-byte 7-bit names, CP/M chars not legal on the host
        var disk = Dsk.Create(profile.Geometry);
        const string name = "WE?IR.D";
        new CpmFs(disk, profile).Put(name, Pattern(300));
        var image = SaveImage(disk);
        var destination = Dir("dir") + Path.DirectorySeparatorChar; // directory form: host name derived
        var mapping = ExplorerCopyOutHost.CopyOut(image, name, 0, destination);
        Assert.Equal(Path.Combine(Dir("dir"), "WE_IR.D"), mapping.HostPath);
        Assert.Equal("WE_IR.D", mapping.HostName);
        Assert.Equal(name.PadRight(11), mapping.CpmName); // disk identity kept, presentation sanitised
        Assert.Equal(3, mapping.Records);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(mapping.HostPath)!)); // no leftover temporary files
    }

    [Fact]
    public void HostCollisionIsRefusedUnlessReplaceIsExplicit()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("SAME.BIN", Pattern(500));
        var image = SaveImage(disk);
        var destination = Path.Combine(Dir("out"), "same.bin");
        File.WriteAllBytes(destination, new byte[] { 1, 2, 3 });
        var refusal = Assert.Throws<IOException>(() => ExplorerCopyOutHost.CopyOut(image, "SAME.BIN", 0, destination));
        Assert.Contains(destination, refusal.Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination)); // refused export changes nothing
        var mapping = ExplorerCopyOutHost.CopyOut(image, "SAME.BIN", 0, destination, replace: true);
        Assert.Equal(Padded(Pattern(500)), File.ReadAllBytes(destination));
        Assert.Equal(Path.GetFullPath(destination), mapping.HostPath);
    }

    [Fact]
    public void SameNameInDifferentUserAreasExportsIndependentlyAndCollidesWithinOneDirectory()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        var fs = new CpmFs(disk, profile);
        fs.Put("TWO.BIN", Pattern(200), 0);
        fs.Put("TWO.BIN", Pattern(400), 1);
        var image = SaveImage(disk);
        var mappingZero = ExplorerCopyOutHost.CopyOut(image, "TWO.BIN", 0, Path.Combine(Dir("u0"), "two.bin"));
        var mappingOne = ExplorerCopyOutHost.CopyOut(image, "TWO.BIN", 1, Path.Combine(Dir("u1"), "two.bin"));
        Assert.Equal(0, mappingZero.User); Assert.Equal(1, mappingOne.User);
        Assert.Equal(256, mappingZero.Bytes); Assert.Equal(512, mappingOne.Bytes);
        var shared = Dir("both");
        ExplorerCopyOutHost.CopyOut(image, "TWO.BIN", 0, shared);
        Assert.Throws<IOException>(() => ExplorerCopyOutHost.CopyOut(image, "TWO.BIN", 1, shared)); // same host name, refused
    }

    [Fact]
    public void SourceImageIsOpenedReadOnly()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("RO.BIN", Pattern(256));
        var image = SaveImage(disk);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(image, UnixFileMode.UserRead); // the lane must open the source read-only
        var mapping = ExplorerCopyOutHost.CopyOut(image, "RO.BIN", 0, Path.Combine(Dir("out"), "ro.bin"));
        Assert.Equal(256, mapping.Bytes);
        Assert.Equal(256, File.ReadAllBytes(Path.Combine(Dir("out"), "ro.bin")).Length);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("normalized")]
    [InlineData("directory")]
    [InlineData("source-link")]
    [InlineData("source-directory-link")]
    [InlineData("target-directory-link")]
    public void ExplicitReplacementCannotOverwriteTheSourceImage(string route)
    {
        if (route.Contains("link") && OperatingSystem.IsWindows()) return; // Linux is the test bar; Windows needs link privileges.
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("RO.BIN", Pattern(256));
        var images = Dir("images");
        var original = Path.Combine(images, "RO.BIN");
        var before = disk.Save(); File.WriteAllBytes(original, before);
        var image = original;
        var target = route == "directory" ? images : route == "normalized" ? Path.Combine(images, ".", "RO.BIN") : original;
        if (route == "source-link")
        {
            image = Path.Combine(root, "source-link.dsk");
            File.CreateSymbolicLink(image, original);
        }
        if (route.EndsWith("directory-link", StringComparison.Ordinal))
        {
            var alias = Path.Combine(root, "linked-images");
            Directory.CreateSymbolicLink(alias, images);
            if (route == "source-directory-link") image = Path.Combine(alias, "RO.BIN");
            else target = Path.Combine(alias, "RO.BIN");
        }
        Assert.Throws<IOException>(() => ExplorerCopyOutHost.CopyOut(image, "RO.BIN", 0, target, replace: true));
        Assert.Equal(before, File.ReadAllBytes(original));
        Assert.Equal(before, File.ReadAllBytes(image));
        Assert.Empty(Directory.GetFiles(images, "*.hc-*"));
    }

    [Fact]
    public void ExportOfAnAbsentEntryIsFileNotFoundAndWritesNothing()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("HERE.BIN", Pattern(128));
        var image = SaveImage(disk);
        var destination = Path.Combine(Dir("out"), "absent.bin");
        Assert.Throws<FileNotFoundException>(() => ExplorerCopyOutHost.CopyOut(image, "ABSENT.BIN", 0, destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void RawGeometryImageExportsThroughThePathOverload()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("RAW.BIN", Pattern(999));
        var image = Path.Combine(root, "raw.img"); File.WriteAllBytes(image, disk.ToRaw());
        var mapping = ExplorerCopyOutHost.CopyOut(image, "RAW.BIN", 0, Path.Combine(Dir("out"), "raw.bin"), rawGeometry: profile.Geometry);
        Assert.Equal(Padded(Pattern(999)), File.ReadAllBytes(Path.Combine(Dir("out"), "raw.bin")));
        Assert.Equal(8, mapping.Records);
    }

    [Fact]
    public void FilesystemOverloadExportsWithoutReopeningTheImage()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc40");
        var disk = Dsk.Create(profile.Geometry);
        new CpmFs(disk, profile).Put("MEM.BIN", Pattern(700));
        var fs = CpmFs.Open(Dsk.Load(disk.Save()));
        var mapping = ExplorerCopyOutHost.CopyOut(fs, "MEM.BIN", 0, Path.Combine(Dir("out"), "mem.bin"));
        Assert.Equal(Padded(Pattern(700)), File.ReadAllBytes(Path.Combine(Dir("out"), "mem.bin")));
        Assert.Equal("MEM.BIN", mapping.CpmName);
    }
}
