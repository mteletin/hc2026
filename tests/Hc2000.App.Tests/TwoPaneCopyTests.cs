#pragma warning disable CA1416 // The tests skip non-Linux hosts at their first statement.
using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Hc2000.App.Explorer;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Hc2000.Machine.Storage;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// hc2000-media-home-scan-admission-v1 section 3, floppy lane only: the scan never writes, open writes
/// the managed copy created after RegisterAsync, copy-in/copy-out are byte-identical, and a refusal
/// leaves the scanned original unchanged. CanImport stays false for an HDD raw image.
/// </summary>
public sealed class TwoPaneCopyTests
{
    [Fact]
    public void ScanStopsAtTheExamineCapBeforeSorting()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux file facts");
        var home = NewHome();
        try
        {
            for (var i = 0; i < TwoPaneCopyListing.MaxExaminedFiles + 1; i++)
                File.WriteAllBytes(Path.Combine(home, $"f{i:D5}.bin"), [1]);
            var result = TwoPaneCopyListing.Scan(home);
            Assert.True(result.Truncated);
            Assert.Equal(TwoPaneCopyListing.MaxListedRows, result.Rows.Length);
            Assert.Contains("Examine cap 1024", result.Notice);
            Assert.All(result.Rows, row => Assert.Equal("unknown", row.Kind));
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void ScanStopsAtTheListCap()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux file facts");
        var home = NewHome();
        try
        {
            for (var i = 0; i < TwoPaneCopyListing.MaxListedRows + 8; i++)
                File.WriteAllBytes(Path.Combine(home, $"g{i:D4}.bin"), [2]);
            var result = TwoPaneCopyListing.Scan(home);
            Assert.True(result.Truncated);
            Assert.Equal(TwoPaneCopyListing.MaxListedRows, result.Rows.Length);
            Assert.Contains("Listing capped at 128", result.Notice);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void ZeroFillEightMiBIsUnknownNotAnHddVolume()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux file facts");
        var home = NewHome();
        try
        {
            using (var stream = new FileStream(Path.Combine(home, "blank.img"), FileMode.CreateNew, FileAccess.Write))
                stream.SetLength(HddMediaAdapter.ImageLength);
            var row = Assert.Single(TwoPaneCopyListing.Scan(home).Rows);
            Assert.Equal("unknown", row.Kind);
            Assert.Contains("not a valid cold HDD volume", row.Detail);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void AcceptedColdVolumeIsHddRawAndCannotBeImported()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux file facts");
        var home = NewHome();
        try
        {
            var path = Path.Combine(home, "volume.img");
            File.WriteAllBytes(path, ExplorerPaneUiTests.HddBytes());
            var row = Assert.Single(TwoPaneCopyListing.Scan(home).Rows);
            Assert.Equal("hdd-raw", row.Kind);
            Assert.Equal(HddMediaAdapter.ImageLength, row.Size);
            var image = new ExplorerImage("hdd", path, MediaImageDescriptor.HddRaw8MiB,
                ImmutableArray<byte>.Empty, Array.Empty<CpmEntry>());
            Assert.False(image.CanImport);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void OversizedFileIsUnknownWithoutBeingRead()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux file facts");
        var home = NewHome();
        try
        {
            var path = Path.Combine(home, "big.bin");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                stream.SetLength(MediaFiles.MaxDiskBytes + 1);
            var before = File.GetLastWriteTimeUtc(path);
            var row = Assert.Single(TwoPaneCopyListing.Scan(home).Rows);
            Assert.Equal("unknown", row.Kind);
            Assert.Equal("larger than 4 MiB; not read", row.Detail);
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void FloppyDskIsListedAndABadHomeIsRefused()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux file facts");
        var home = NewHome();
        try
        {
            File.WriteAllBytes(Path.Combine(home, "disk.dsk"), DiskLibraryWorkingCopyFixtures.Literal(false));
            var row = Assert.Single(TwoPaneCopyListing.Scan(home).Rows);
            Assert.Equal("floppy-dsk", row.Kind);
            Assert.Equal("standard DSK", row.Detail);
            var missing = TwoPaneCopyListing.Scan(Path.Combine(home, "no-such"));
            Assert.Empty(missing.Rows);
            Assert.False(missing.Truncated);
            Assert.Contains("missing, relative, or not a directory", missing.Notice);
        }
        finally { Directory.Delete(home, true); }
    }

    [AvaloniaFact]
    public async Task OpenWritesTheManagedCopyAndCopyInOutAreByteIdentical()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux private-directory admission");
        using var domain = new HddColdAdmissionTests.PrivateDomain();
        using var scope = domain.Enter();
        var home = Path.Combine(domain.Root, "home");
        Directory.CreateDirectory(home);
        var source = Path.Combine(home, "source.dsk");
        var original = Disk("ALPHA.BIN").ToArray();
        File.WriteAllBytes(source, original);
        File.SetUnixFileMode(source, UnixFileMode.UserRead);
        var catalog = DiskLibraryCatalog.Load(Path.Combine(domain.Root, "catalog.json"), DiskLibraryHostAdmission.StoppedHost);
        var window = new MainWindow(new LaunchOptions(null, null, false, false));
        window.Show();
        try
        {
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            media.DiskLibrary = catalog;
            media.TwoPaneHomeOverride = home;
            await window.ScanTwoPaneHomeAsync();
            var row = Assert.IsType<ExplorerCopyRow>(Assert.Single(media.TwoPane.Rows.Items));
            Assert.Equal("floppy-dsk", row.Kind);
            Assert.Equal(Path.GetFullPath(source), row.Path);

            await window.OpenTwoPaneCopyAsync(row);
            var session = window.CurrentSession;
            var image = session.Explorer.Image;
            Assert.NotNull(image);
            Assert.NotEqual(Path.GetFullPath(source), image.Path);
            Assert.StartsWith(Path.Combine(domain.Root, "copies") + Path.DirectorySeparatorChar, image.Path);
            Assert.True(File.Exists(image.Path));
            Assert.Single(catalog.Entries);
            var copy = Assert.Single(catalog.Copies);
            Assert.Equal(catalog.Entries[0].Id, copy.EntryId);
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(source));

            var payload = new byte[] { 0x41, 0, 0x1a };
            Assert.True(window.CopyExplorerIn(session, image, "new.bin", 15, false, payload.ToImmutableArray()));
            image = session.Explorer.Image!;
            var entry = Assert.Single(image.Entries, e => e.Name == "NEW.BIN" && e.User == 15);
            Assert.Equal(128, entry.Size);
            var exported = Path.Combine(domain.Root, "out.bin");
            Assert.NotNull(window.CopyExplorerOut(session, image, entry, exported));
            var stored = image.Open().Get("NEW.BIN", 15);
            Assert.Equal(stored, File.ReadAllBytes(exported));
            Assert.Equal(payload, stored[..payload.Length]);
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(source));

            await window.OpenTwoPaneCopyAsync(row);
            Assert.Single(catalog.Copies);
            Assert.Equal(image.Path, session.Explorer.Image!.Path);
            Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally { window.Close(); }
        await window.ShutdownCompletion;
    }

    [AvaloniaFact]
    public async Task RefusalsLeaveTheScannedOriginalUnchanged()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Real Linux file facts");
        var home = NewHome();
        var source = Path.Combine(home, "source.dsk");
        var original = DiskLibraryWorkingCopyFixtures.Literal(false);
        File.WriteAllBytes(source, original);
        File.SetUnixFileMode(source, UnixFileMode.UserRead);
        var row = new ExplorerCopyRow("source.dsk", source, "floppy-dsk", original.Length, "standard DSK");
        var window = new MainWindow(new LaunchOptions(null, null, false, false));
        window.Show();
        try
        {
            var media = window.FindControl<MediaPanel>("MediaControls")!;
            var session = window.CurrentSession;

            await window.OpenTwoPaneCopyAsync(row);
            Assert.Equal("Disk library catalog is not configured; refusing to open.", session.Explorer.Notice);
            Assert.Null(session.Explorer.Image);

            media.DiskLibrary = DiskLibraryCatalog.Load(Path.Combine(home, "catalog.json"));
            Assert.True(media.DiskLibrary.AdmissionMissing);
            await window.OpenTwoPaneCopyAsync(row);
            Assert.Contains("no host admission was provided", session.Explorer.Notice);
            Assert.Null(session.Explorer.Image);
            Assert.Empty(media.DiskLibrary.Entries);
            Assert.Empty(media.DiskLibrary.Copies);

            await window.OpenTwoPaneCopyAsync(row with { Kind = "hdd-raw" });
            Assert.Equal("Open refused: only a floppy DSK opens a managed copy.", session.Explorer.Notice);
            Assert.Null(session.Explorer.Image);
            await window.OpenTwoPaneCopyAsync(row with { Kind = "unknown" });
            Assert.Equal("Open refused: only a floppy DSK opens a managed copy.", session.Explorer.Notice);

            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(source));
        }
        finally
        {
            window.Close();
            Directory.Delete(home, true);
        }
        await window.ShutdownCompletion;
    }

    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "hc-two-pane-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static ImmutableArray<byte> Disk(string name, int user = 0)
    {
        var preset = Dpb.Presets().Single(p => p.Name == "cpm-hc80");
        var disk = Dsk.Create(preset.Geometry);
        new CpmFs(disk, preset).Put(name, [0x41, 0, 0x1a], user);
        return ImmutableArray.CreateRange(disk.Save());
    }
}
