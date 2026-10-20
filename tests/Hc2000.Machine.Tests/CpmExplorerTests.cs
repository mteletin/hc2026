using System.Buffers.Binary;
using System.Text;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.Machine.Tests;

/// <summary>hc2000-explorer-list-fdd: host-side read-only listing over the four CP/M presets.
/// CP/M 2.2 policy the library does not enforce: user areas 0-15 (higher entries refused,
/// never hidden or rewritten), canonical 8.3 names (non-canonical names refused rather than
/// high-bit masked), R/O and system attributes surfaced, record-rounded sizes keeping the
/// 0x1A text end. No write path, no mount.</summary>
public sealed class CpmExplorerTests
{
    public static IEnumerable<object[]> Presets() => Dpb.Presets().Select(p => new object[] {p.Name});
    public static IEnumerable<object[]> CpmPresets() => Dpb.Presets().Where(p => !p.HcHeader).Select(p => new object[] {p.Name});
    private static Dpb Preset(string name) => Dpb.Presets().Single(p => p.Name == name);

    // Raw images are crafted byte-for-byte: the HC skew is odd sector IDs then even IDs,
    // and the directory sits at logical byte Off*Spt*128.
    private static Dsk Image(Dpb p, Action<byte[], int> write)
    {
        var geometry = p.Geometry;
        var logical = new byte[geometry.Length];
        Array.Fill(logical, (byte)0xe5); // like Dsk.Create: dead directory entries, not live zero entries
        write(logical, p.Off * p.Spt * 128);
        var raw = new byte[geometry.Length];
        var sector = new byte[geometry.SectorSize];
        for (var track = 0; track < geometry.Cylinders * geometry.Sides; track++)
            for (var i = 0; i < geometry.Sectors; i++)
            {
                Array.Copy(logical, (track * geometry.Sectors + i) * geometry.SectorSize, sector, 0, geometry.SectorSize);
                Array.Copy(sector, 0, raw, (track * geometry.Sectors + p.Skew[i] - 1) * geometry.SectorSize, geometry.SectorSize);
            }
        return Dsk.Load(raw, geometry);
    }
    private static void Entry(byte[] logical, int start, int index, int user, byte[] name,
        bool readOnly = false, bool system = false, int exmLow = 0, int exmHigh = 0, int records = 0,
        int[]? blocks = null, bool word = false)
    {
        var offset = start + index * 32;
        Array.Clear(logical, offset, 32); // like CpmFs.Put: a live entry is zeroed, 0xE5 padding would be EX/S2 data
        logical[offset] = (byte)user;
        name.CopyTo(logical, offset + 1);
        if (readOnly) logical[offset + 9] |= 0x80;
        if (system) logical[offset + 10] |= 0x80;
        logical[offset + 12] = (byte)exmLow; logical[offset + 14] = (byte)exmHigh; logical[offset + 15] = (byte)records;
        if (blocks is not null)
            for (var j = 0; j < blocks.Length; j++)
            {
                if (word) BinaryPrimitives.WriteUInt16LittleEndian(logical.AsSpan(offset + 16 + j * 2), (ushort)blocks[j]);
                else logical[offset + 16 + j] = (byte)blocks[j];
            }
    }
    // Display 8.3 names become the dot-less 8+3 field on CP/M presets; HC presets keep the literal 11 bytes.
    private static byte[] Field(Dpb p, string name)
    {
        var field = p.HcHeader ? name :
            name.Contains('.')
                ? name[..name.IndexOf('.')].PadRight(8, ' ') + (name[(name.IndexOf('.') + 1)..].PadRight(3, ' '))[..3]
                : name.PadRight(8, ' ');
        return Encoding.ASCII.GetBytes(field.PadRight(11, ' ')[..11]);
    }
    // A literal 11-byte field, dot and all (for crafting non-canonical or HC entries).
    private static byte[] FieldRaw(string name) => Encoding.ASCII.GetBytes(name.PadRight(11, ' ')[..11]);

    [Theory]
    [MemberData(nameof(Presets))]
    public void ListsFilesAcrossAllFourPresets(string profileName)
    {
        var p = Preset(profileName); var disk = Dsk.Create(p.Geometry);
        var size = p.HcHeader ? 40000 : 200000; var stored = p.HcHeader ? size + 9 : size; // HcFs: header + 16-bit length cap
        var input = Enumerable.Range(0, size).Select(n => (byte)(n % 251)).ToArray();
        if (p.HcHeader) new HcFs(new CpmFs(disk, p)).Put("hello.txt", input, 3, 0, 0, (ushort)Math.Min(size, 65535), 10);
        else new CpmFs(disk, p).Put("HELLO.BIN", input, 3);
        new CpmFs(disk, p).Put("B.C", [1, 2, 3], 0);
        var explorer = CpmExplorer.Open(Dsk.Load(disk.Save()));
        var hello = explorer.List().Single(f => f.Name == (p.HcHeader ? "hello.txt" : "HELLO.BIN"));
        Assert.Equal(3, hello.User); Assert.False(hello.ReadOnly); Assert.False(hello.System); Assert.False(hello.Refused);
        Assert.Equal((stored + 127) / 128, hello.Records); // record-rounded, never the host length
        Assert.Equal((stored + 127) / 128 * 128, hello.Size);
        var small = explorer.List().Single(f => f.Name == "B.C");
        Assert.Equal(0, small.User); Assert.Equal(1, small.Records); Assert.Equal(128, small.Size); Assert.False(small.Refused);
        Assert.Equal(2, explorer.List().Count);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void SizeIsRecordRoundedAndKeepsTheTextEnd(string profileName)
    {
        var p = Preset(profileName); var disk = Dsk.Create(p.Geometry);
        if (p.HcHeader) new HcFs(new CpmFs(disk, p)).Put("short.tx", [97, 98], 1, 0, 0, 2, 10);
        else new CpmFs(disk, p).Put("AB.CO", [97, 98], 1);
        var file = CpmExplorer.Open(Dsk.Load(disk.Save())).List().Single();
        Assert.Equal(1, file.Records); Assert.Equal(128, file.Size); // no trimming of the 0x1A text end
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void UserAreasAbove15AreRefusedNeverHidden(string profileName)
    {
        var p = Preset(profileName);
        var disk = Image(p, (logical, start) =>
        {
            Entry(logical, start, 0, 5, Field(p, "HERE.BIN"));
            Entry(logical, start, 1, 17, Field(p, "FAR.BIN"));
            Entry(logical, start, 2, 31, Field(p, "TOP.BIN"));
        });
        var explorer = CpmExplorer.Open(disk);
        var here = explorer.List().Single(f => f.Name == "HERE.BIN");
        Assert.False(here.Refused);
        foreach (var (name, user) in new[] {("FAR.BIN", 17), ("TOP.BIN", 31)})
        {
            var refused = explorer.List().Single(f => f.Name == name);
            Assert.True(refused.Refused); Assert.Equal(user, refused.User);
            Assert.Contains("0-15", refused.Refusal); Assert.Contains(user.ToString(), refused.Refusal);
        }
        Assert.Equal(3, explorer.List().Count); // refused entries are reported, not hidden
        Assert.Throws<ArgumentException>(() => explorer.List(17));
        Assert.Throws<ArgumentException>(() => explorer.List(31));
        Assert.Empty(explorer.List(15));
        Assert.Single(explorer.List(5));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void HighBitNamesAreRefusedNotMasked(string profileName)
    {
        var p = Preset(profileName);
        var name = Field(p, "HILO.BIN"); name[2] = 0xCC; // 'L' with the high bit set; masked it would read 'L' again
        var disk = Image(p, (logical, start) => Entry(logical, start, 0, 2, name));
        var file = CpmExplorer.Open(disk).List().Single();
        Assert.True(file.Refused);
        Assert.Contains("high-bit", file.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("HILO.BIN", file.Name); // the masked form is shown only because it is refused
    }

    [Theory]
    [MemberData(nameof(CpmPresets))]
    public void DriForbiddenPunctuationIsRefused(string profileName)
    {
        var p = Preset(profileName);
        var dot = FieldRaw("A.B  BIN"); // a dot lives in the 11-byte field, not only as display separator
        var star = FieldRaw("A*B  BIN");
        var pipe = FieldRaw("A|B  BIN");
        var disk = Image(p, (logical, start) =>
        {
            Entry(logical, start, 0, 0, dot);
            Entry(logical, start, 1, 0, star);
            Entry(logical, start, 2, 0, pipe);
        });
        var files = CpmExplorer.Open(disk).List().ToArray();
        Assert.All(files, f => Assert.True(f.Refused));
        Assert.All(files, f => Assert.Contains("DRI-forbidden", f.Refusal));
        Assert.Contains(files, f => f.Name == "A.B  BIN"); // shown as stored, refused - not rewritten as ABBIN or similar
    }

    [Theory]
    [MemberData(nameof(CpmPresets))]
    public void NonPrintableAndEmptyNamesAreRefused(string profileName)
    {
        var p = Preset(profileName);
        var control = Field(p, "BAD.BIN"); control[4] = 0x01;
        var disk = Image(p, (logical, start) =>
        {
            Entry(logical, start, 0, 0, control);
            Entry(logical, start, 1, 0, Field(p, "          "));
        });
        var files = CpmExplorer.Open(disk).List().ToArray();
        Assert.All(files, f => Assert.True(f.Refused));
        Assert.Contains(files, f => f.Refusal!.Contains("control"));
        Assert.Contains(files, f => f.Refusal!.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<object[]> NonCanonicalNames() =>
        from p in Dpb.Presets().Where(p => !p.HcHeader)
        from field in new[] { "        BIN", "BAD NAMEBIN", "FILE    B N", "lower   bin", "NUL\0    BIN", "DEL\u007f    BIN" }
        select new object[] { p.Name, field };

    [Theory]
    [MemberData(nameof(NonCanonicalNames))]
    public void NonCanonicalNameComponentsAreRefused(string profileName, string field)
    {
        var p = Preset(profileName);
        var disk = Image(p, (logical, start) => Entry(logical, start, 0, 0, FieldRaw(field)));
        var bytes = disk.Save();
        var file = CpmExplorer.Open(disk).List().Single();
        Assert.True(file.Refused);
        Assert.NotNull(file.Refusal);
        Assert.Equal(bytes, disk.Save());
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void InvalidNameInLaterExtentRefusesTheWholeFile(string profileName)
    {
        var p = Preset(profileName);
        var highBitName = Field(p, "CHAIN.BIN"); highBitName[2] |= 0x80;
        var disk = Image(p, (logical, start) =>
        {
            Entry(logical, start, 0, 0, Field(p, "CHAIN.BIN"));
            Entry(logical, start, 1, 0, highBitName, exmLow: p.Exm + 1);
        });
        var file = CpmExplorer.Open(disk).List().Single();
        Assert.True(file.Refused);
        Assert.Contains("high-bit", file.Refusal);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void AttributesAreSurfaced(string profileName)
    {
        var p = Preset(profileName);
        var disk = Image(p, (logical, start) =>
        {
            Entry(logical, start, 0, 4, Field(p, "PROG.BIN"), readOnly: true);
            Entry(logical, start, 1, 4, Field(p, "SYS.BIN"), system: true);
            Entry(logical, start, 2, 4, Field(p, "BOTH.BIN"), readOnly: true, system: true);
        });
        var files = CpmExplorer.Open(disk).List().OrderBy(f => f.Name).ToArray(); // BOTH, PROG, SYS
        Assert.False(files[0].Refused); Assert.True(files[0].ReadOnly); Assert.True(files[0].System);
        Assert.False(files[1].Refused); Assert.True(files[1].ReadOnly); Assert.False(files[1].System);
        Assert.False(files[2].Refused); Assert.False(files[2].ReadOnly); Assert.True(files[2].System);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void EmptyFileIsListedWithZeroSize(string profileName)
    {
        var p = Preset(profileName);
        var disk = Image(p, (logical, start) => Entry(logical, start, 0, 0, Field(p, "EMPTY.CO")));
        var file = CpmExplorer.Open(disk).List().Single();
        Assert.Equal("EMPTY.CO", file.Name); Assert.Equal(0, file.Records); Assert.Equal(0, file.Size); Assert.False(file.Refused);
    }

    [Fact]
    public void SameNameInDifferentUserAreasIsTwoFiles()
    {
        var p = Preset("cpm-hc80");
        var disk = Image(p, (logical, start) =>
        {
            Entry(logical, start, 0, 0, Field(p, "SAME.BIN"));
            Entry(logical, start, 1, 1, Field(p, "SAME.BIN"));
        });
        var files = CpmExplorer.Open(disk).List().ToArray();
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.False(f.Refused));
        Assert.Equal([0, 1], files.Select(f => f.User).OrderBy(u => u));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void ExtentChainsSumIntoOneFile(string profileName)
    {
        var p = Preset(profileName); var disk = Dsk.Create(p.Geometry);
        var size = 60000; var stored = p.HcHeader ? size + 9 : size; // more than one extent on every preset
        var input = Enumerable.Range(0, size).Select(n => (byte)(n % 251)).ToArray();
        if (p.HcHeader) new HcFs(new CpmFs(disk, p)).Put("chain.txt", input, 6, 0, 0, (ushort)Math.Min(size, 65535), 10);
        else new CpmFs(disk, p).Put("CHAIN.BIN", input, 6);
        var saved = Dsk.Load(disk.Save());
        var ground = new CpmFs(saved, p).List().Single();
        var entry = CpmExplorer.Open(saved).List().Single();
        Assert.Equal(ground.Records, entry.Records); // explorer sums the extent chain exactly like the library
        Assert.Equal(ground.Records * 128, entry.Size);
        Assert.Equal((stored + 127) / 128, entry.Records);
        Assert.False(entry.Refused);
    }

    [Fact]
    public void UnsupportedGeometryIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => CpmExplorer.Open(Dsk.Create(new DiskGeometry(40, 2, 9, 1024))));
    }

    [Fact]
    public void CorruptDirectoriesFailClosed()
    {
        var p = Preset("cpm-hc80");
        var duplicate = Image(p, (logical, start) =>
        {
            Entry(logical, start, 0, 0, Field(p, "TWO.BIN"));
            Entry(logical, start, 1, 0, Field(p, "TWO.BIN"));
        });
        Assert.Throws<InvalidDataException>(() => CpmExplorer.Open(duplicate));
        var badRecords = Image(p, (logical, start) => Entry(logical, start, 0, 0, Field(p, "BAD.BIN"), records: 129));
        Assert.Throws<InvalidDataException>(() => CpmExplorer.Open(badRecords));
    }

    [Fact]
    public void HcLiteralNamesAreListedVerbatim()
    {
        var p = Preset("hcfs40");
        var disk = Image(p, (logical, start) => Entry(logical, start, 0, 7, FieldRaw("hello.txt")));
        var file = CpmExplorer.Open(disk).List().Single();
        Assert.Equal("hello.txt", file.Name); // literal 11-byte name: lower case and dot are not 8.3 violations
        Assert.False(file.Refused);
    }

    [Fact]
    public void UserFilterIncludesOnlyThatUsersEntries()
    {
        var p = Preset("cpm-hc40");
        var disk = Image(p, (logical, start) =>
        {
            Entry(logical, start, 0, 5, Field(p, "MINE.BIN"));
            Entry(logical, start, 1, 17, Field(p, "REFUSED.BIN"));
        });
        var explorer = CpmExplorer.Open(disk);
        Assert.Equal(["MINE.BIN"], explorer.List(5).Select(f => f.Name));
        Assert.Equal(["MINE.BIN", "REFUSED.BIN"], explorer.List().Select(f => f.Name).OrderBy(n => n));
    }
}
