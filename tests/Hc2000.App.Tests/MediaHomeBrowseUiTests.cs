using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Media;
using Hc2000.App.Settings;
using Hc2000.Machine.Floppy;
using Hc2000.App.Workspace;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

// Contract section 3 (browse) UI wiring, on the scanned media home list (hc2000-media-home-scan-
// admission-v1): the rows render in the MediaHomeListPanel; a recognized row's host is wired to
// the session's existing reader through the double-click gesture, an unknown row's host carries
// no action. Double-clicking a recognized row opens the existing reader for that kind: floppy
// and hdd-raw through the current explorer (a real image with the copy-in/out controls), tape
// and hdd-container as a read-only listing (the block list / volume catalog), a snapshot row
// refuses without a read. Rendering and a single press open nothing.
public sealed class MediaHomeBrowseUiTests : IDisposable
{
    private readonly string _root;
    private readonly string _disk;
    private readonly string _tape;
    private readonly string _snap;

    public MediaHomeBrowseUiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "media-home-browse-ui-" + Guid.NewGuid().ToString("N"));
        _disk = MakeDir("disk");
        _tape = MakeDir("tape");
        _snap = MakeDir("snap");

        // 720K cpm-hc80 floppy with one known file, through the existing writer.
        var p = Dpb.Presets().Single(x => x.Name == "cpm-hc80");
        var disk = Dsk.Create(p.Geometry);
        new CpmFs(disk, p).Put("HELLO.BIN", [0x41, 0, 0x1a], 3);
        File.WriteAllBytes(Path.Combine(_disk, "HELLO.DSK"), disk.Save());

        // A formatted 8 MiB volume: 0xe5 dead directory with one live user-15 entry, the shape
        // CpmExplorerHddTests and the lane tests craft; the scan classifies it hdd-raw.
        var hp = Dpb.HddVolume();
        var raw = Enumerable.Repeat((byte)0xe5, hp.Geometry.Length).ToArray();
        Array.Clear(raw, 0, 32); // entry 0 live; entries 1+ stay 0xe5 dead
        raw[0] = 15;
        Encoding.ASCII.GetBytes("FILE    BIN").CopyTo(raw, 1);
        raw[9] |= 0x80; raw[10] |= 0x80;
        raw[15] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(16), (ushort)hp.DirectoryBlocks);
        File.WriteAllBytes(Path.Combine(_disk, "VOL.RAW"), raw);

        // An existing hdd container (encoded index + 64-byte footer, empty arena) with one
        // volume row; the browse must list it through the existing container read.
        var id = Enumerable.Range(1, 16).Select(n => (byte)n).ToArray();
        var rows = new[] { new HddCatalogEntry("VOL1.DSK", false, 8_388_608, new HddSlotOrdinal(0), 0x0362A35B) }.ToImmutableArray();
        var encoded = HddCatalogCodec.EncodeHdd(id, 1, rows, new HddLimits(uint.MaxValue, 511, 12_276, 114_640));
        Assert.True(encoded.Success, encoded.Failure?.Detail);
        var meta = encoded.Value;
        var arena = new byte[checked((int)(meta.FileLength - (ulong)meta.Index.Length - (ulong)meta.Footer.Length))];
        File.WriteAllBytes(Path.Combine(_disk, "CONT.RAW"), arena.Concat(meta.Index).Concat(meta.Footer).ToArray());

        // Four stray bytes: no inspector accepts them, the scan classifies them unknown.
        File.WriteAllBytes(Path.Combine(_disk, "JUNK.BIN"), new byte[] { 1, 2, 3, 4 });

        // One native tape block through the existing tape writer.
        var tap = Tap.Load(ReadOnlySpan<byte>.Empty);
        tap.Put(new TapeFile("HELLO", 0, 0, 0, new byte[] { 1, 2, 3 }));
        File.WriteAllBytes(Path.Combine(_tape, "TAPE.TAP"), tap.Save());

        // A snapshot home row: opaque to the scan, refused by the browse.
        File.WriteAllBytes(Path.Combine(_snap, "SHOT.hcs"), Enumerable.Repeat((byte)0xa5, 100).ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best-effort test scratch cleanup */ }
    }

    private string MakeDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [AvaloniaFact]
    public async Task MediaHomeRowsRenderAndDoubleTappedOpensTheExistingReaderPerKind()
    {
        var settings = new AppSettings { DiskHome = _disk, TapeHome = _tape, SnapshotHome = _snap };
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.json"), JsonSerializer.Serialize(settings, AppSettings.JsonOptions));
        var window = new MainWindow(new LaunchOptions(null, null, false, false), null, new Hc2000.App.Settings.SettingsStore(config));
        window.Show();
        try
        {
            var session = Add(window, new MachineConfiguration { Name = "X" });
            var list = window.FindControl<MediaHomeListPanel>("MediaHomeList")!;

            // Awaiting the panel's own scan seam (WireMediaHomeList) keeps the headless dispatcher
            // free to pump the scan's internal awaits: MediaHomeScan does not ConfigureAwait(false),
            // so a blocking GetResult would starve that same dispatcher and deadlock. The browse
            // reads are synchronous, so every gesture below settles before it returns.
            var listing = await list.Scan!(CancellationToken.None);
            list.Show(listing);

            // The rows the scan decided on; recognized rows are wired row hosts, unknown rows
            // carry a host with no browse action.
            var rows = list.Shown;
            Assert.Equal(["CONT.RAW", "HELLO.DSK", "JUNK.BIN", "VOL.RAW"], rows.Where(r => r.Home == "disk").Select(r => r.Name).OrderBy(n => n).ToArray());
            Assert.Equal(["TAPE.TAP"], rows.Where(r => r.Home == "tape").Select(r => r.Name).ToArray());
            Assert.Equal(["SHOT.hcs"], rows.Where(r => r.Home == "snapshot").Select(r => r.Name).ToArray());
            Assert.DoesNotContain(rows, r => !r.Selectable && r.Name != "JUNK.BIN");
            foreach (var row in rows.Where(r => r.Selectable))
                Assert.Contains(list.GetLogicalDescendants().OfType<MediaHomeRowView>(), v => v.Row.Name == row.Name);

            // Rendering opens nothing.
            Assert.Null(session.Explorer.Image);
            Assert.Null(session.Explorer.Listing);

            // An unknown row's host carries no browse action: a double-click opens nothing.
            var junk = Host(list, "JUNK.BIN");
            DoubleTap(junk);
            Assert.Null(session.Explorer.Image);
            Assert.Null(session.Explorer.Listing);

            // Floppy row: the current explorer over the scanned bytes, a real image.
            var floppy = rows.Single(r => r.Name == "HELLO.DSK");
            Assert.Equal("floppy-dsk", floppy.Kind);
            await Open(list, session, "HELLO.DSK");
            Assert.NotNull(session.Explorer.Image);
            var entry = session.Explorer.Image!.Entries.Single(e => e.Name == "HELLO.BIN");
            Assert.Equal(3, entry.User);
            Assert.False(entry.Refused);

            // hdd-raw row: the existing 8 MiB volume reader.
            var hdd = rows.Single(r => r.Name == "VOL.RAW");
            Assert.Equal("hdd-raw", hdd.Kind);
            await Open(list, session, "VOL.RAW");
            Assert.NotNull(session.Explorer.Image);
            Assert.Equal(15, session.Explorer.Image!.Entries.Single(e => e.Name == "FILE.BIN").User);

            // Tape row: the block list as a read-only listing, no image.
            var tape = rows.Single(r => r.Name == "TAPE.TAP");
            Assert.Equal("tape", tape.Kind);
            await Open(list, session, "TAPE.TAP");
            Assert.Null(session.Explorer.Image);
            Assert.NotNull(session.Explorer.ListingTitle);
            var block = session.Explorer.Listing!.Single(l => l.StartsWith("HELLO"));
            Assert.Contains("3 bytes", block);

            // hdd-container row: the volume catalog as a read-only listing.
            var container = rows.Single(r => r.Name == "CONT.RAW");
            Assert.Equal("hdd-container", container.Kind);
            await Open(list, session, "CONT.RAW");
            Assert.Null(session.Explorer.Image);
            var volume = session.Explorer.Listing!.Single(l => l.Contains("VOL1.DSK"));
            Assert.Contains("volume 1", volume);
            Assert.Contains("8,388,608", volume);

            // Snapshot row: refused, the scan does not decode it and neither does the browse.
            var shot = rows.Single(r => r.Name == "SHOT.hcs");
            Assert.Equal("snapshot", shot.Kind);
            await Open(list, session, "SHOT.hcs");
            Assert.Contains("not a CP/M browse", session.Explorer.Notice);
            Assert.DoesNotContain("VOL1.DSK", session.Explorer.Notice);
            Assert.Null(session.Explorer.Image);
            Assert.Null(session.Explorer.Listing);
            Assert.Null(session.Explorer.ListingTitle);

            // Moving from a tape listing to a disk must also discard the previous listing,
            // including when the scanned disk disappeared before the double-click.
            await Open(list, session, "TAPE.TAP");
            await Open(list, session, "HELLO.DSK");
            Assert.NotNull(session.Explorer.Image);
            Assert.Null(session.Explorer.Listing);
            Assert.Null(session.Explorer.ListingTitle);
            await Open(list, session, "TAPE.TAP");
            File.Delete(Path.Combine(_disk, "HELLO.DSK"));
            await Open(list, session, "HELLO.DSK");
            Assert.Contains("refused", session.Explorer.Notice);
            Assert.Null(session.Explorer.Image);
            Assert.Null(session.Explorer.Listing);
            Assert.Null(session.Explorer.ListingTitle);
        }
        finally { window.Close(); }
    }

    private static MachineSession Add(MainWindow window, MachineConfiguration configuration)
    {
        Assert.True(window.TryAddTabView(configuration, out var tab, out var refusal), refusal);
        return tab!.Session;
    }

    // Double-click the recognized row's host: the gesture the browse lane attaches to. Await the
    // panel's action task so the assertion reads settled state; a single press opens nothing
    // (the host wires no such gesture).
    private static async Task Open(MediaHomeListPanel list, MachineSession session, string name)
    {
        DoubleTap(Host(list, name));
        if (list.PendingAction is { } pending) await pending;
        if (list.LastAction is { } last) await last;
    }

    private static MediaHomeRowView Host(MediaHomeListPanel list, string name)
        => Assert.Single(list.GetLogicalDescendants().OfType<MediaHomeRowView>(), view => view.Row.Name == name);

    // Raise a properly-typed DoubleTapped: the row host's DoubleTapped is a
    // RoutedEventHandler<TappedEventArgs>, so a plain RoutedEventArgs fails the args cast
    // (InvalidCastException) before the handler runs.
    private static void DoubleTap(MediaHomeRowView host)
    {
        var pointer = new PointerEventArgs(InputElement.DoubleTappedEvent, host, null!, null!, default, 0, default, default);
        host.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, pointer));
    }
}
