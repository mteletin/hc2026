using System.Buffers.Binary;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.Machine.Tests;

public sealed class StorageTests
{
    [Theory]
    [InlineData(DiskFormat.Raw)] [InlineData(DiskFormat.Standard)] [InlineData(DiskFormat.Extended)]
    public void ContainersRetainFormatAndEdits(DiskFormat format)
    {
        var geometry = new DiskGeometry(2, 2, 3, 256); var disk = Dsk.Create(geometry, format);
        var bytes = disk.Save(); var loaded = Dsk.Load(bytes, format == DiskFormat.Raw ? geometry : null);
        Assert.Equal(format, loaded.Format); Assert.Equal(bytes, loaded.Save());
        loaded.Tracks[1].Sectors[2].Data[17] = 42;
        var edited = Dsk.Load(loaded.Save(), format == DiskFormat.Raw ? geometry : null);
        Assert.Equal(42, edited.Tracks[1].Sectors[2].Data[17]);
        Assert.Equal(loaded.ToRaw(), Dsk.Load(loaded.ToContainer(DiskFormat.Extended)).ToRaw());
        Assert.Equal(loaded.ToRaw(), Dsk.Load(loaded.ToContainer(DiskFormat.Standard)).ToRaw());
    }
    [Fact]
    public void WeakShortStatusIdsPaddingAndUnformattedTracksArePreserved()
    {
        var bytes = Dsk.Create(new(2,1,2,256)).Save();
        bytes[256 + 24 + 2] = 0xc1; bytes[256 + 24 + 4] = 0x20; bytes[256 + 24 + 5] = 0x40;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(256 + 24 + 6), 128);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(256 + 32 + 6), 384);
        bytes[52 + 1] = 0; // original trailing bytes remain uninterpreted and round-trip
        var disk = Dsk.Load(bytes); Assert.Equal(bytes, disk.Save()); Assert.Empty(disk.Tracks[1].Sectors);
        Assert.Equal(128, disk.Tracks[0].Sectors[0].Data.Length); Assert.Equal(384, disk.Tracks[0].Sectors[1].Data.Length);
        Assert.Equal(0xc1, disk.Tracks[0].Sectors[0].R); Assert.Equal(0x20, disk.Tracks[0].Sectors[0].ST1);
        Assert.Throws<InvalidDataException>(() => disk.ToRaw());
    }
    [Fact]
    public void CorruptContainersAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Dsk.Load([]));
        Assert.Throws<InvalidDataException>(() => Dsk.Load(new byte[128], new(1,1,1,256)));
        var disk = Dsk.Create(new(1,1,1,256)).Save();
        Assert.Throws<InvalidDataException>(() => Dsk.Load(disk[..^1]));
        disk[256 + 21] = 30; Assert.Throws<InvalidDataException>(() => Dsk.Load(disk));
    }
    public static IEnumerable<object[]> Lengths() => Dpb.Presets().SelectMany(p =>
        new[] {0,1,127,128,129,16383,16384,16385,32767,32768,32769,65535}.Select(n => new object[] {p.Name,n}));
    [Theory]
    [MemberData(nameof(Lengths))]
    public void PutGetRecordAndHeaderLengthsAcrossExtents(string profileName, int length)
    {
        var profile = Dpb.Presets().Single(p => p.Name == profileName); var disk = Dsk.Create(profile.Geometry);
        var fs = new CpmFs(disk, profile); var input = Enumerable.Range(0,length).Select(n => (byte)(n * 17)).ToArray();
        if (length > 2) { input[^1] = 0x1a; input[^2] = 0; } // never trim legitimate terminal bytes
        if (profile.HcHeader) new HcFs(fs).Put("file.name", input, 3, 0, 0, (ushort)Math.Min(length,65535), 10);
        else fs.Put("FILE.BIN", input, 3);
        fs = CpmFs.Open(Dsk.Load(disk.Save()));
        var output = profile.HcHeader ? new HcFs(fs).Get("file.name", 3) : fs.Get("FILE.BIN", 3);
        Assert.Equal(input, output[..length]);
        Assert.Equal(profile.HcHeader ? length : (length + 127) / 128 * 128, output.Length);
        if (!profile.HcHeader) Assert.All(output[length..], b => Assert.Equal(0x1a,b));
        else { var header = new HcFs(fs).Header("file.name",3); Assert.Equal(0,header.Type); Assert.Equal(10,header.Autostart); Assert.Equal(length,header.Variables); }
        Assert.Throws<FileNotFoundException>(() => fs.Get(profile.HcHeader ? "file.name" : "FILE.BIN",0));
        fs.Delete(profile.HcHeader ? "file.name" : "FILE.BIN",3); Assert.Empty(fs.List());
    }
    [Theory]
    [InlineData("cpm-hc80",350)] [InlineData("cpm-hc40",174)] [InlineData("hcfs80",319)] [InlineData("hcfs40",159)]
    public void PhysicalBoundsFullDiskAndTransactionalFailure(string name, int max)
    {
        var profile = Dpb.Presets().Single(p => p.Name == name); var disk = Dsk.Create(profile.Geometry);
        var fs = new CpmFs(disk,profile); Assert.Equal(max,fs.MaximumBlock);
        Assert.Equal(name == "cpm-hc40" ? 0 : 1,fs.Warnings.Count);
        var bytes = new byte[(max + 1 - profile.DirectoryBlocks) * profile.BlockSize];
        fs.Put("FULL",bytes); var before = disk.Save();
        Assert.Throws<InvalidDataException>(() => fs.Put("MORE",new byte[1])); Assert.Equal(before,disk.Save());
        Assert.Equal(bytes,fs.Get("FULL"));
        // Overwrite reuses old allocation atomically and frees the remaining blocks.
        fs.Put("FULL",new byte[128]); fs.Put("MORE",new byte[128]); Assert.Equal(2,fs.List().Count);
    }
    [Fact]
    public void DirectoryFullReadonlyAndCorruptBlockReferences()
    {
        var profile = Dpb.Presets().Single(p => p.Name == "cpm-hc40"); var disk = Dsk.Create(profile.Geometry); var fs = new CpmFs(disk,profile);
        for (var i=0;i<=profile.Drm;i++) fs.Put("F"+i,[]);
        var before=disk.Save(); Assert.Throws<InvalidDataException>(()=>fs.Put("EXTRA",[])); Assert.Equal(before,disk.Save());
        disk = Dsk.Create(profile.Geometry); fs = new(disk,profile); fs.Put("FILE",new byte[128]);
        // First directory side is cylinder1/head0. First skew sector holds entry0.
        var data = disk.Tracks[2].Sectors.Single(s=>s.R==1).Data; data[9]|=0x80;
        fs=new(disk,profile); Assert.Throws<InvalidDataException>(()=>fs.Delete("FILE")); Assert.Throws<InvalidDataException>(()=>fs.Put("FILE",[]));
        data[16]=255; Assert.Throws<InvalidDataException>(()=>new CpmFs(disk,profile));
    }
    [Fact]
    public void HcRejectsCorruptHeaderAndSupportsDistinctCaseAndEmbeddedDot()
    {
        var profile=Dpb.Presets().Single(p=>p.Name=="hcfs40"); var disk=Dsk.Create(profile.Geometry); var fs=new CpmFs(disk,profile); var hc=new HcFs(fs);
        hc.Put("alien",[1]); hc.Put("ALIEN",[2]); hc.Put("inst.bio",[3]);
        Assert.Equal(new byte[]{1},hc.Get("alien")); Assert.Equal(new byte[]{2},hc.Get("ALIEN")); Assert.Equal(new byte[]{3},hc.Get("inst.bio"));
        fs.Put("bad",new HcFileHeader(1,65535,0,0,0).Encode()); Assert.Throws<InvalidDataException>(()=>hc.Get("bad"));
        Assert.Throws<InvalidDataException>(()=>hc.Put("oversize",new byte[65536]));
    }
    [Fact]
    public void HcLiteralNamesAndUntypedRawRecordsAreNotInventedTypedHeaders()
    {
        var fs=CpmFs.Format(Dpb.Presets().Single(p=>p.Name=="hcfs40")); var hc=new HcFs(fs);
        hc.Put("a/b",[1]); hc.Put("a_b",[2]); hc.Put("arkanoid\0",[3]); hc.Put("",[4]);
        Assert.Contains(hc.List(),f=>f.Directory.Name=="a/b".PadRight(11));
        Assert.Contains(hc.List(),f=>f.Directory.Name==new string(' ',11));
        Assert.Contains(hc.List(),f=>f.Directory.Name=="arkanoid\0".PadRight(11));
        Assert.Equal(new byte[]{1},hc.Get("a/b")); Assert.Equal(new byte[]{2},hc.Get("a_b"));
        Assert.Equal(new byte[]{3},hc.Get("arkanoid\0")); Assert.Equal(new byte[]{4},hc.Get(""));
        fs.Put("empty",[]); fs.Put("untyped",[5,7,6,1,2,3]);
        foreach(var name in new[]{"empty","untyped"})
        {
            var entry=hc.List().Single(f=>f.Directory.Name==name.PadRight(11)); Assert.True(entry.IsUntyped); Assert.Null(entry.Header);
            Assert.Throws<InvalidDataException>(()=>hc.Get(name)); Assert.Throws<InvalidDataException>(()=>hc.Header(name));
        }
        Assert.Empty(fs.Get("empty")); Assert.Equal(new byte[]{5,7,6,1,2,3},fs.Get("untyped")[..6]);
    }
    [Fact]
    public void TapeHeadersChecksumsAndHcMetadataConversion()
    {
        var tape=Tap.Load([]); var input=new TapeFile("program",0,10,3,[1,0,0x1a,255]); tape.Put(input);
        var bytes=tape.Save(); var read=Tap.Load(bytes); Assert.Equal(bytes,read.Save()); Assert.Equal(input.Data,read.Get("program").Data);
        Assert.Equal(new HcFileHeader(0,4,0,3,10),read.Get("program").HcHeader);
        Assert.Equal((byte)3,new TapeFile("code",3,40000,0,[]).HcHeader.Type);
        Assert.Throws<InvalidDataException>(()=>read.Put(input));
        bytes[^1]^=1; Assert.Throws<InvalidDataException>(()=>Tap.Load(bytes));
        Assert.Throws<InvalidDataException>(()=>Tap.Load([1])); Assert.Throws<InvalidDataException>(()=>Tap.Load([255,255,0]));
    }
    // Independent literals: these source-only fixtures are not an observed RED/PASS claim.
    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("0200FFFF", 4, 1)]
    [InlineData("020000000200FFFF", 8, 2)]
    public void TapBoundedExactCapsPreserveLegacyBytes(string hex, int maxBytes, int maxBlocks)
    {
        var wire = Convert.FromHexString(hex);
        var result = Tap.LoadBounded(wire, maxBytes, maxBlocks);
        Assert.True(result.Success); Assert.Null(result.Failure);
        Assert.Equal(wire, result.Value.Save());
        Assert.Equal(Tap.Load(wire, maxBytes, maxBlocks).Save(), result.Value.Save());
        Assert.Equal(wire, Tap.Load(wire).Save());
        Assert.Equal(hex.Length == 0 ? 0 : maxBlocks, result.Value.CopyRawBlocks().Count);
    }
    [Theory]
    [InlineData("0200FFFF", 3, 1, TapeError.BoundsExceeded, "TAP byte bound exceeded.")]
    [InlineData("0200FFFF", 4, 0, TapeError.BoundsExceeded, "TAP block bound exceeded.")]
    [InlineData("01", 1, 1, TapeError.InvalidFormat, "Truncated TAP length.")]
    [InlineData("01", 1, 0, TapeError.BoundsExceeded, "TAP block bound exceeded.")]
    [InlineData("01", 0, 0, TapeError.BoundsExceeded, "TAP byte bound exceeded.")]
    [InlineData("0000", 2, 1, TapeError.InvalidFormat, "Truncated TAP block.")]
    [InlineData("010000", 3, 1, TapeError.InvalidFormat, "Truncated TAP block.")]
    [InlineData("0200FF", 3, 1, TapeError.InvalidFormat, "Truncated TAP block.")]
    [InlineData("FFFF00", 3, 1, TapeError.InvalidFormat, "Truncated TAP block.")]
    [InlineData("0200FF00", 4, 1, TapeError.InvalidFormat, "Invalid TAP checksum.")]
    [InlineData("0200FFFF01", 5, 1, TapeError.BoundsExceeded, "TAP block bound exceeded.")]
    [InlineData("0200FFFF01", 5, 2, TapeError.InvalidFormat, "Truncated TAP length.")]
    [InlineData("0200FFFF0200FF00", 8, 1, TapeError.BoundsExceeded, "TAP block bound exceeded.")]
    [InlineData("0200FFFF0200FF00", 8, 2, TapeError.InvalidFormat, "Invalid TAP checksum.")]
    [InlineData("0200FF000200FFFF", 8, 1, TapeError.InvalidFormat, "Invalid TAP checksum.")]
    public void TapBoundedFirstRefusalMatchesLegacyWithoutPartialValue(string hex, int maxBytes,
        int maxBlocks, TapeError code, string legacyMessage)
    {
        var wire = Convert.FromHexString(hex);
        var result = Tap.LoadBounded(wire, maxBytes, maxBlocks);
        Assert.False(result.Success); Assert.NotNull(result.Failure);
        Assert.Equal(code, result.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => { _ = result.Value; });
        var exception = Assert.Throws<InvalidDataException>(() => Tap.Load(wire, maxBytes, maxBlocks));
        Assert.Equal(legacyMessage, exception.Message);
    }
    [Theory]
    [InlineData(-1, 0, "maxBytes")]
    [InlineData(0, -1, "maxBlocks")]
    [InlineData(-1, -1, "maxBytes")]
    public void TapBoundedNegativeArgumentsKeepLegacyValidationOrder(int maxBytes, int maxBlocks,
        string parameter)
    {
        var typed = Assert.Throws<ArgumentOutOfRangeException>(() => Tap.LoadBounded([1], maxBytes, maxBlocks));
        var legacy = Assert.Throws<ArgumentOutOfRangeException>(() => Tap.Load([1], maxBytes, maxBlocks));
        Assert.Equal(parameter, typed.ParamName); Assert.Equal(parameter, legacy.ParamName);
        Assert.Equal(legacy.Message, typed.Message);
    }
    [Fact]
    public void TapBoundedLegacyDefaultBlockCapRemains4096()
    {
        var wire = Convert.FromHexString(string.Concat(Enumerable.Repeat("0200FFFF", 4097)));
        var atDefault = wire[..(4096 * 4)];
        Assert.Equal(atDefault, Tap.Load(atDefault).Save());
        Assert.Equal(atDefault, Tap.LoadBounded(atDefault, 64 * 1024 * 1024, 4096).Value.Save());
        Assert.Equal("TAP block bound exceeded.", Assert.Throws<InvalidDataException>(() => Tap.Load(wire)).Message);
        var bounded = Tap.LoadBounded(wire, 64 * 1024 * 1024, 4096);
        Assert.Equal(TapeError.BoundsExceeded, bounded.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => { _ = bounded.Value; });
        Assert.Equal(wire, Tap.Load(wire, maxBlocks: 4097).Save());
        Assert.Equal(wire, Tap.LoadBounded(wire, wire.Length, 4097).Value.Save());
    }
    [Fact]
    public void TapBoundedLegacyDefaultByteCapPrecedesMalformedInput()
    {
        // Above the legacy byte default, malformed zero length must not be reached.
        var wire = new byte[64 * 1024 * 1024 + 1];
        Assert.Equal("TAP byte bound exceeded.", Assert.Throws<InvalidDataException>(() => Tap.Load(wire)).Message);
        var bounded = Tap.LoadBounded(wire, 64 * 1024 * 1024, 4096);
        Assert.Equal(TapeError.BoundsExceeded, bounded.Failure!.Code);
        Assert.Throws<InvalidOperationException>(() => { _ = bounded.Value; });
        Assert.Equal("Truncated TAP block.", Assert.Throws<InvalidDataException>(() => Tap.Load(wire, wire.Length)).Message);
        Assert.Equal(TapeError.InvalidFormat, Tap.LoadBounded(wire, wire.Length, 4096).Failure!.Code);
    }
    [Fact]
    public void TapBoundedSuccessRemainsDetachedFromInputAndRawCopies()
    {
        var wire = Convert.FromHexString("0200FFFF");
        var result = Tap.LoadBounded(wire, 4, 1);
        Assert.True(result.Success);
        wire[2] = 0; result.Value.CopyRawBlocks()[0][0] = 0;
        Assert.Equal(Convert.FromHexString("0200FFFF"), result.Value.Save());
    }
    [StorageAssetFact("roms/hc2000/eprom-dump-hc2k-2/M27C512.bin")]
    public void RomDumpSplitsUsingDocumentedMapAndMatchesExactKnownSlices()
    {
        var root=StorageAssetTests.Root(); var bytes=File.ReadAllBytes(Path.Combine(root,"roms/hc2000/eprom-dump-hc2k-2/M27C512.bin"));
        var tools=new RomTools(); var slices=tools.Split(bytes);
        Assert.Equal(bytes,slices.SelectMany(s=>s.Data).ToArray()); Assert.Equal(new[]{0,0x4000,0x8000,0xc000,0xe000},slices.Select(s=>s.Offset));
        Assert.Equal(File.ReadAllBytes(Path.Combine(root,"roms/hc2000/v2/basic.rom")),slices[2].Data);
        Assert.Equal(File.ReadAllBytes(Path.Combine(root,"roms/hc2000/v2/cpm-hc2kzip.bin")),slices[4].Data);
        var manifest=File.ReadAllText(Path.Combine(root,"MANIFEST.sha256")); Assert.Contains("roms/hc2000/v2/basic.rom",tools.Identify(slices[2].Data,manifest));
        Assert.Throws<InvalidDataException>(()=>tools.Split(bytes[..^1]));
    }
}
