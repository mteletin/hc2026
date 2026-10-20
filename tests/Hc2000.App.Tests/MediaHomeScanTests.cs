using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using Hc2000.App.Media;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// Contract hc2000-media-home-scan-admission-v1, section 2: read-only scan of the configured media
/// homes. Kind comes from the existing inspectors (Dsk, CpmFs.OpenHddVolume, HddCatalogCodec, Tap),
/// missing or invalid homes fail closed, per-home caps set an explicit truncated flag.
/// </summary>
public sealed class MediaHomeScanTests : IDisposable
{
    private readonly string _root;
    private readonly string _disk;
    private readonly string _tape;
    private readonly string _snap;

    public MediaHomeScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mhs-{Guid.NewGuid():N}");
        _disk = Path.Combine(_root, "disk");
        _tape = Path.Combine(_root, "tape");
        _snap = Path.Combine(_root, "snap");
        foreach (var dir in new[] { _disk, _tape, _snap }) Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best-effort test scratch cleanup */ }
    }

    private static void Write(string dir, string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(dir, name), bytes);

    private static byte[] Dsk720K() => Dsk.Create(new DiskGeometry(80, 2, 9, 512)).Save();

    /// <summary>Two TAP blocks; each payload's XOR must be 0 (Tap checksum), trailing bytes chosen so it is.</summary>
    private static byte[] TapValid()
        => new byte[] { 7, 0, 0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0x43, 5, 0, 0x23, 0x45, 0x67, 0x89, 0x88 };

    /// <summary>Formatted cold volume: 8 MiB of 0xE5 (all directory slots dead), the pattern the storage tests use.</summary>
    private static byte[] ColdVolumeRaw()
    {
        var raw = new byte[8_388_608];
        Array.Fill(raw, (byte)0xE5);
        return raw;
    }

    /// <summary>Container file: FileLength bytes with the encoded footer at the tail; the scan reads only those 64 bytes.</summary>
    private static byte[] ContainerFile()
    {
        var output = HddCatalogCodec.EncodeHdd(
                Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"), 1,
                ImmutableArray<HddCatalogEntry>.Empty,
                new HddLimits(uint.MaxValue, 511, 12_276, 114_640))
            .Value;
        var file = new byte[output.FileLength];
        output.Footer.CopyTo(file.AsSpan(file.Length - output.Footer.Length));
        return file;
    }

    private MediaHomeRow Row(MediaHomeListing listing, string home, string name)
    {
        var row = listing.Rows.SingleOrDefault(r => r.Home == home && r.Name == name);
        Assert.True(row is not null, $"expected a {home} row for {name}");
        return row!;
    }

    private MediaHomeState HomeState(MediaHomeListing listing, string home)
    {
        var state = listing.Homes.Single(h => h.Home == home);
        return state;
    }

    [Theory]
    [InlineData(0, "floppy-dsk")]
    [InlineData(1, "unknown")]
    public async Task DskInspectorKeepsFourMiBBoundary(int excessBytes, string expectedKind)
    {
        var bytes = Dsk720K();
        Array.Resize(ref bytes, MediaFiles.MaxDiskBytes + excessBytes);
        // The reader accepts trailing bytes, but that must not raise the scan's floppy cap.
        Dsk.Load(bytes);
        Write(_disk, "padded.dsk", bytes);

        var listing = await MediaHomeScan.ScanAsync(_disk, null, null);

        var row = Row(listing, "disk", "padded.dsk");
        Assert.Equal(expectedKind, row.Kind);
        Assert.Equal(excessBytes == 0, row.Recognized);
        Assert.Equal(excessBytes == 0, row.Selectable);
    }

    [Fact]
    public async Task DiskHomeRecognizesDskFloppyAndUnknown()
    {
        Write(_disk, "HELLO.DSK", Dsk720K());
        Write(_disk, "JUNK.BIN", new byte[] { 0xAB, 0xCD, 0xEF, 0x01 });

        var listing = await MediaHomeScan.ScanAsync(_disk, null, null);

        var dsk = Row(listing, "disk", "HELLO.DSK");
        Assert.Equal("floppy-dsk", dsk.Kind);
        Assert.True(dsk.Recognized);
        Assert.True(dsk.Selectable);
        Assert.False(dsk.Size <= 0);
        var junk = Row(listing, "disk", "JUNK.BIN");
        Assert.Equal("unknown", junk.Kind);
        Assert.False(junk.Selectable);
        Assert.True(HomeState(listing, "disk").Available);
    }

    [Fact]
    public async Task SnapshotHomeRecognizesHcsFromMetadataOnly()
    {
        Write(_snap, "snap-001.hcs", new byte[1234]); // content is never read or decoded
        Write(_snap, "notes.txt", new byte[] { 0x77 });

        var listing = await MediaHomeScan.ScanAsync(null, null, _snap);

        var snap = Row(listing, "snapshot", "snap-001.hcs");
        Assert.Equal("snapshot", snap.Kind);
        Assert.True(snap.Selectable);
        Assert.False(Row(listing, "snapshot", "notes.txt").Recognized);
    }

    [Fact]
    public async Task TapeHomeRecognizesTapBlankAndUnknown()
    {
        Write(_tape, "good.tap", TapValid());
        Write(_tape, "blank.tap", Array.Empty<byte>());
        Write(_tape, "junk.tap", new byte[] { 0x99, 0x88, 0x77 });

        var listing = await MediaHomeScan.ScanAsync(null, _tape, null);

        Assert.Equal("tape", Row(listing, "tape", "good.tap").Kind);
        var blank = Row(listing, "tape", "blank.tap");
        Assert.Equal("tape", blank.Kind);
        Assert.Equal(0, blank.Size);
        Assert.True(blank.Selectable);
        Assert.Equal("unknown", Row(listing, "tape", "junk.tap").Kind);
    }

    [Fact]
    public async Task DiskHomeRecognizesColdHddVolumeAndContainer()
    {
        Write(_disk, "vol.raw", ColdVolumeRaw());
        Write(_disk, "catalog.hdd", ContainerFile());

        var listing = await MediaHomeScan.ScanAsync(_disk, null, null);

        var raw = Row(listing, "disk", "vol.raw");
        Assert.Equal("hdd-raw", raw.Kind);
        Assert.True(raw.Selectable);
        var container = Row(listing, "disk", "catalog.hdd");
        Assert.Equal("hdd-container", container.Kind);
        Assert.True(container.Selectable);
    }

    [Fact]
    public async Task ZeroByteIsUnknownOutsideTheTapeHome()
    {
        Write(_disk, "empty.dsk", Array.Empty<byte>());
        Write(_snap, "empty.hcs", Array.Empty<byte>());

        var listing = await MediaHomeScan.ScanAsync(_disk, null, _snap);

        Assert.Equal("unknown", Row(listing, "disk", "empty.dsk").Kind);
        Assert.Equal("unknown", Row(listing, "snapshot", "empty.hcs").Kind);
    }

    [Fact]
    public async Task SymlinksAreNotFollowed()
    {
        var outside = Path.Combine(_root, "outside.dsk");
        File.WriteAllBytes(outside, Dsk720K());
        File.CreateSymbolicLink(Path.Combine(_disk, "link.dsk"), outside);

        var listing = await MediaHomeScan.ScanAsync(_disk, null, null);

        Assert.DoesNotContain(listing.Rows, r => r.Name == "link.dsk");
        Assert.Empty(listing.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SymlinkedHomeIsNotFollowed(bool linkedAncestor)
    {
        Write(_disk, "outside.dsk", Dsk720K());
        var linkedHome = Path.Combine(_root, "linked-home");
        Directory.CreateSymbolicLink(linkedHome, linkedAncestor ? _root : _disk);
        var home = linkedAncestor ? Path.Combine(linkedHome, "disk") : linkedHome;

        var listing = await MediaHomeScan.ScanAsync(home, null, null);

        Assert.Empty(listing.Rows);
        Assert.False(HomeState(listing, "disk").Available);
    }

    [Fact]
    public async Task DirectoryEntriesConsumeTheScanBudgetWithoutBeingListed()
    {
        for (var i = 0; i < MediaHomeScan.MaxEntriesPerHome + 1; i++)
        {
            var subtree = Path.Combine(_tape, $"subtree{i:D4}");
            Directory.CreateDirectory(subtree);
            Write(subtree, "nested.tap", Array.Empty<byte>());
        }

        var listing = await MediaHomeScan.ScanAsync(null, _tape, null);

        Assert.Empty(listing.Rows);
        Assert.True(listing.Truncated);
    }

    [Fact]
    public async Task NamedPipeIsNotABlankTape()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fifo = Path.Combine(_tape, "pipe.tap");
        using var process = Process.Start(new ProcessStartInfo("mkfifo")
        {
            ArgumentList = { fifo },
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("mkfifo did not start");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("mkfifo did not finish within 10 seconds");
        }
        Assert.Equal(0, process.ExitCode);

        var listing = await MediaHomeScan.ScanAsync(null, _tape, null);

        Assert.Empty(listing.Rows);
    }

    [Fact]
    public async Task MissingHomesFailClosed()
    {
        var listing = await MediaHomeScan.ScanAsync(null, null, null);

        Assert.Empty(listing.Rows);
        Assert.False(listing.Truncated);
        foreach (var home in new[] { "disk", "tape", "snapshot" })
        {
            var state = HomeState(listing, home);
            Assert.False(state.Available);
            Assert.Equal("not configured", state.Reason);
        }
    }

    [Fact]
    public async Task RelativeAndNonDirectoryHomesFailClosed()
    {
        var fileAsHome = Path.Combine(_root, "notadirectory");
        File.WriteAllBytes(fileAsHome, new byte[] { 1 });

        var listing = await MediaHomeScan.ScanAsync("relative/path", fileAsHome, Path.Combine(_root, "absent"));

        Assert.Empty(listing.Rows);
        Assert.False(HomeState(listing, "disk").Available);
        Assert.Equal("relative path; fail-closed, not scanned", HomeState(listing, "disk").Reason);
        Assert.Equal("not a directory; fail-closed, not scanned", HomeState(listing, "tape").Reason);
        Assert.Equal("directory missing on the host; fail-closed, not scanned", HomeState(listing, "snapshot").Reason);
    }

    [Fact]
    public async Task RowCapTruncatesPerHome()
    {
        for (var i = 0; i < 200; i++)
            Write(_tape, $"t{i:D3}.tap", new byte[] { 0x11, 0x22 });

        var listing = await MediaHomeScan.ScanAsync(null, _tape, null);

        Assert.Equal(MediaHomeScan.MaxRowsPerHome, listing.Rows.Count(r => r.Home == "tape"));
        Assert.True(listing.Truncated);
    }

    [Fact]
    public async Task EntryCapStopsEnumerationWithoutError()
    {
        for (var i = 0; i < 1100; i++)
            Write(_tape, $"t{i:D4}.tap", new byte[] { 0x33 });

        var listing = await MediaHomeScan.ScanAsync(null, _tape, null);

        Assert.Equal(MediaHomeScan.MaxRowsPerHome, listing.Rows.Length);
        Assert.True(listing.Truncated);
    }

    [Fact]
    public async Task DerivedSnapshotDirectoryIsTheFallback()
    {
        var derived = Path.Combine(_root, "derived");
        Directory.CreateDirectory(derived);
        Write(derived, "d.hcs", new byte[10]);
        Write(_snap, "s.hcs", new byte[10]);

        // Unconfigured snapshotHome falls back to the derived directory.
        var fromDerived = await MediaHomeScan.ScanAsync(null, null, null, derived);
        Assert.True(HomeState(fromDerived, "snapshot").Available);
        Assert.Equal("d.hcs", Row(fromDerived, "snapshot", "d.hcs").Name);

        // A configured snapshotHome wins over the derived directory.
        var fromConfigured = await MediaHomeScan.ScanAsync(null, null, _snap, derived);
        Assert.Single(fromDerived.Rows);
        Assert.Equal("s.hcs", Row(fromConfigured, "snapshot", "s.hcs").Name);
        Assert.DoesNotContain(fromConfigured.Rows, r => r.Name == "d.hcs");
    }

    [Fact]
    public async Task OversizedHcsIsRefusedByTheExistingLoadGuard()
    {
        var path = Path.Combine(_snap, "huge.hcs");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Seek(300L * 1024 * 1024 - 1, SeekOrigin.Begin);
            await stream.WriteAsync(new byte[] { 1 });
        }

        var listing = await MediaHomeScan.ScanAsync(null, null, _snap);

        Assert.Equal("unknown", Row(listing, "snapshot", "huge.hcs").Kind);
    }

    [Fact]
    public async Task HcsSuffixIsCaseSensitiveLikeTheFrozenSnapshotPattern()
    {
        Write(_snap, "UPPER.HCS", new byte[16]);

        var listing = await MediaHomeScan.ScanAsync(null, null, _snap);

        Assert.Equal("unknown", Row(listing, "snapshot", "UPPER.HCS").Kind);
    }
}
