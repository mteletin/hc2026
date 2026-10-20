using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Xunit;
using IOFile = System.IO.File;

namespace Hc2000.App.Tests;

// The media home browse lane (contract hc2000-media-home-scan-admission-v1 @0b82cbec, section 3):
// a double-click on a scanned row opens the existing per-kind reader over the scanned bytes -
// floppy through the current explorer, hdd-raw through the existing 8 MiB volume reader, tape
// through Tap.List, hdd-container through the existing container read. No new parser, no kind
// guess: the row kind comes from the row (the scan's decision). A snapshot row is not a CP/M
// browse and refuses without a read; a reader refusal surfaces as a refusal, not a crash. The
// lane is read-only: the scanned bytes are identical after the call.
public sealed class MediaHomeBrowseLaneTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _dirs = new();
    private readonly MediaHomeBrowseLane _lane = new();

    public MediaHomeBrowseLaneTests()
    {
        _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "media-home-browse-root-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_root, true); }
        catch { /* best-effort test scratch cleanup */ }
    }

    private string NewDir()
    {
        var dir = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static string ScratchFile(string dir, string name, byte[] bytes)
    {
        var path = System.IO.Path.Combine(dir, name);
        IOFile.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AssertUntouched(string path, byte[] before)
        => Assert.Equal(before, IOFile.ReadAllBytes(path));

    [Fact]
    public async Task FloppyRowListsKnownFileFrom720KImage()
    {
        // The cpm-hc80 preset: 80-track HC CP/M, 720K - the contract's floppy-720 writer.
        var p = Dpb.Presets().Single(x => x.Name == "cpm-hc80");
        var disk = Dsk.Create(p.Geometry);
        var size = 40000;
        var input = Enumerable.Range(0, size).Select(n => (byte)(n % 251)).ToArray();
        new CpmFs(disk, p).Put("HELLO.BIN", input, 3);
        var container = RawFloppyAdapter.Save(disk, MediaImageDescriptor.DskContainer, new DriveProfile(80, 2));
        Assert.True(container.Length <= MediaFiles.MaxDiskBytes);

        var dir = NewDir();
        var path = ScratchFile(dir, "720.dsk", container);
        var result = await _lane.OpenAsync(MediaHomeRowKind.FloppyDsk, path);

        Assert.Equal(MediaHomeBrowseOutcome.Browsable, result.Outcome);
        Assert.NotNull(result.Files);
        var file = Assert.Single(result.Files!);
        Assert.Equal("HELLO.BIN", file.Name);
        Assert.Equal(3, file.User);
        Assert.True(file.Records > 0);
        Assert.False(file.Refused);
        AssertUntouched(path, container);
    }

    [Fact]
    public async Task HddRawRowListsKnownFileFromFormatted8MibVolume()
    {
        // A formatted 8 MiB volume: 0xe5 dead directory with one live user-15 entry, the same
        // shape CpmExplorerHddTests crafts; the volume reader is Dpb.HddVolume(), no guessing.
        var p = Dpb.HddVolume();
        var raw = Enumerable.Repeat((byte)0xe5, p.Geometry.Length).ToArray();
        Array.Clear(raw, 0, 32); // entry 0 live; entries 1+ stay 0xe5 dead
        raw[0] = 15;
        Encoding.ASCII.GetBytes("FILE    BIN").CopyTo(raw, 1);
        raw[9] |= 0x80; raw[10] |= 0x80;
        raw[15] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16), (ushort)p.DirectoryBlocks);
        Assert.Equal(HddMediaAdapter.ImageLength, raw.Length);

        var dir = NewDir();
        var path = ScratchFile(dir, "vol.hdd", raw);
        var result = await _lane.OpenAsync(MediaHomeRowKind.HddRaw, path);

        Assert.Equal(MediaHomeBrowseOutcome.Browsable, result.Outcome);
        var file = Assert.Single(result.Files!);
        Assert.Equal("FILE.BIN", file.Name);
        Assert.Equal(15, file.User);
        Assert.Equal(1, file.Records);
        Assert.Equal(128, file.Size);
        Assert.True(file.ReadOnly);
        Assert.True(file.System);
        Assert.False(file.Refused);
        AssertUntouched(path, raw);
    }

    [Fact]
    public async Task TapeRowListsBlockList()
    {
        // Two native files written through the existing tape writer; the lane must list them
        // through Tap.List with the same names.
        var tap = Tap.Load(ReadOnlySpan<byte>.Empty);
        tap.Put(new TapeFile("HELLO", 0, 0, 0, new byte[] { 1, 2, 3 }));
        tap.Put(new TapeFile("DATA", 1, 0x8000, 0, new byte[] { 4, 5 }));
        var bytes = tap.Save();

        var dir = NewDir();
        var path = ScratchFile(dir, "blocks.tap", bytes);
        var result = await _lane.OpenAsync(MediaHomeRowKind.Tape, path);

        Assert.Equal(MediaHomeBrowseOutcome.Browsable, result.Outcome);
        Assert.NotNull(result.Tape);
        Assert.Equal(["HELLO", "DATA"], result.Tape!.Select(f => f.Name).ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Tape[0].Data);
        AssertUntouched(path, bytes);
    }

    [Fact]
    public async Task HddContainerRowListsVolumeCatalog()
    {
        // One existing container (arena + encoded index + 64-byte footer) built with the shared
        // codec; the lane reads footer and index only, never arena bytes.
        var id = Enumerable.Range(1, 16).Select(n => (byte)n).ToArray();
        var rows = new[] { new HddCatalogEntry("VOL1.DSK", false, 8_388_608, new HddSlotOrdinal(0), 0x0362A35B) }.ToImmutableArray();
        var encoded = HddCatalogCodec.EncodeHdd(id, 1, rows, new HddLimits(uint.MaxValue, 511, 12_276, 114_640));
        Assert.True(encoded.Success, encoded.Failure?.Detail);
        var meta = encoded.Value;
        var arena = new byte[checked((int)(meta.FileLength - (ulong)meta.Index.Length - (ulong)meta.Footer.Length))];
        var file = arena.Concat(meta.Index).Concat(meta.Footer).ToArray();

        var dir = NewDir();
        var path = ScratchFile(dir, "hdd.hcv", file);
        var result = await _lane.OpenAsync(MediaHomeRowKind.HddContainer, path);

        Assert.Equal(MediaHomeBrowseOutcome.Browsable, result.Outcome);
        Assert.NotNull(result.Container);
        var entry = Assert.Single(result.Container!.Entries);
        Assert.Equal("VOL1.DSK", entry.Name);
        Assert.Equal(8_388_608u, entry.ImageBytes);
        Assert.Equal(1UL, result.Container.Footer.Revision);
        AssertUntouched(path, file);
    }

    [Fact]
    public async Task SnapshotRowIsNotACpmBrowse()
    {
        var dir = NewDir();
        var path = ScratchFile(dir, "snap.bin", new byte[100]);
        var result = await _lane.OpenAsync(MediaHomeRowKind.Snapshot, path);

        Assert.Equal(MediaHomeBrowseOutcome.SnapshotRefused, result.Outcome);
        Assert.Contains("not a CP/M browse", result.Message);
        Assert.Null(result.Files);
        Assert.Null(result.Tape);
        Assert.Null(result.Container);
    }

    [Fact]
    public async Task UnknownRowRefuses()
    {
        var dir = NewDir();
        var path = ScratchFile(dir, "mystery.bin", new byte[100]);
        var result = await _lane.OpenAsync(MediaHomeRowKind.Unknown, path);

        Assert.Equal(MediaHomeBrowseOutcome.UnknownRefused, result.Outcome);
        Assert.Null(result.Files);
        Assert.Null(result.Tape);
        Assert.Null(result.Container);
    }

    [Theory]
    [InlineData("short.dsk", 100)]
    [InlineData("long.dsk", 5 * 1024 * 1024)]
    public async Task FloppyRowRefusalsAreSurfaceNotCrash(string name, int length)
    {
        var dir = NewDir();
        var path = ScratchFile(dir, name, new byte[length]);
        var result = await _lane.OpenAsync(MediaHomeRowKind.FloppyDsk, path);

        Assert.Equal(MediaHomeBrowseOutcome.ReaderRefused, result.Outcome);
        Assert.Contains("refused", result.Message);
        Assert.Null(result.Files);
    }

    [Fact]
    public async Task HddRawRowRefusesWrongLengthVolume()
    {
        // 8 MiB plus one: the existing volume reader wants exactly 8,388,608 bytes and refuses
        // to guess; the capped read is the refusal.
        var dir = NewDir();
        var raw = new byte[HddMediaAdapter.ImageLength + 1];
        var path = ScratchFile(dir, "wrong.hdd", raw);
        var result = await _lane.OpenAsync(MediaHomeRowKind.HddRaw, path);

        Assert.Equal(MediaHomeBrowseOutcome.ReaderRefused, result.Outcome);
        Assert.Null(result.Files);
    }

    [Fact]
    public async Task TapeRowRefusesCorruptBlock()
    {
        var tap = Tap.Load(ReadOnlySpan<byte>.Empty);
        tap.Put(new TapeFile("HELLO", 0, 0, 0, new byte[] { 1, 2, 3 }));
        var bytes = tap.Save();
        bytes[^1] ^= 0xFF; // break the last block's checksum

        var dir = NewDir();
        var path = ScratchFile(dir, "corrupt.tap", bytes);
        var result = await _lane.OpenAsync(MediaHomeRowKind.Tape, path);

        Assert.Equal(MediaHomeBrowseOutcome.ReaderRefused, result.Outcome);
        Assert.Null(result.Tape);
    }

    [Fact]
    public async Task HddContainerRowRefusesGarbageFooter()
    {
        var dir = NewDir();
        var path = ScratchFile(dir, "junk.hcv", new byte[200]);
        var result = await _lane.OpenAsync(MediaHomeRowKind.HddContainer, path);

        Assert.Equal(MediaHomeBrowseOutcome.ReaderRefused, result.Outcome);
        Assert.Null(result.Container);
    }

    [Fact]
    public async Task MissingFileIsARefusal()
    {
        var dir = NewDir();
        var path = System.IO.Path.Combine(dir, "absent.dsk");
        var result = await _lane.OpenAsync(MediaHomeRowKind.FloppyDsk, path);

        Assert.Equal(MediaHomeBrowseOutcome.ReaderRefused, result.Outcome);
        Assert.Null(result.Files);
    }

    [Theory]
    [InlineData("snapshot", MediaHomeRowKind.Snapshot)]
    [InlineData("floppy-dsk", MediaHomeRowKind.FloppyDsk)]
    [InlineData("hdd-raw", MediaHomeRowKind.HddRaw)]
    [InlineData("hdd-container", MediaHomeRowKind.HddContainer)]
    [InlineData("tape", MediaHomeRowKind.Tape)]
    [InlineData("unknown", MediaHomeRowKind.Unknown)]
    [InlineData("", MediaHomeRowKind.Unknown)]
    [InlineData("bogus-kind", MediaHomeRowKind.Unknown)]
    public static void ParseKindMapsTheScanRowKindStringsAndClosesAnythingElseAsUnknown(string kind, MediaHomeRowKind expected)
        => Assert.Equal(expected, MediaHomeBrowseLane.ParseKind(kind));

    [Fact]
    public async Task OpenRowDispatchesOnTheRowKindNotAFilenameGuess()
    {
        // The row kind is the scan's decision: a row whose file is named .dsk but whose kind is
        // "snapshot" is refused as a snapshot without a read; a kind the scan cannot emit closes
        // as unknown.
        var dir = NewDir();
        var path = ScratchFile(dir, "lookalike.dsk", new byte[100]);
        var snapshotRow = new MediaHomeRow("disk", "snapshot", "lookalike.dsk", path, 100, "", true);
        var snapshot = await _lane.OpenRowAsync(snapshotRow);
        Assert.Equal(MediaHomeBrowseOutcome.SnapshotRefused, snapshot.Outcome);
        var unknownRow = new MediaHomeRow("disk", "bogus", "lookalike.dsk", path, 100, "", true);
        var unknown = await _lane.OpenRowAsync(unknownRow);
        Assert.Equal(MediaHomeBrowseOutcome.UnknownRefused, unknown.Outcome);
    }
}
