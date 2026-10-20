using System.Collections.Immutable;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// Presentation-only inline media listing (frozen app-tabs contract): catalog/inspection
/// projection, typed Ready/Unavailable/Refused rows, 128-row page cap, and handle
/// revalidation at action time.
/// </summary>
public sealed class InlineMediaListingTests : IDisposable
{
    private readonly string root;
    private readonly string diskRoot;
    private readonly string snapRoot;

    public InlineMediaListingTests()
    {
        root = Path.Combine(Path.GetTempPath(), "inline-media-lists-" + Guid.NewGuid().ToString("N"));
        diskRoot = Path.Combine(root, "disks");
        snapRoot = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(diskRoot);
        Directory.CreateDirectory(snapRoot);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    private static DiskLibraryEntry Entry(string id, string name, string path, long length,
        DiskLibraryFormat format = DiskLibraryFormat.StandardDsk) =>
        new(id, name, path, length, new string('a', 64), null, format,
            new("test fixture", "unit test"), new(DiskLibraryContentState.Unknown, null, [], "not-scanned"));

    [Fact]
    public void FloppiesWithoutProviderAreExplicitlyUnavailableNotAnEmptyList()
    {
        var rows = InlineMediaListing.Floppies(null);
        var row = Assert.Single(rows);
        Assert.Equal(InlineMediaStatus.Unavailable, row.Status);
        Assert.False(row.Selectable);
        Assert.Contains("not configured", row.Reason!);

        var custom = InlineMediaListing.Floppies(null, "catalog failed to load: budget");
        Assert.Contains("budget", Assert.Single(custom).Reason!);
    }

    [Fact]
    public void FloppiesProjectCatalogEntriesWithTypedAvailability()
    {
        var present = Path.Combine(diskRoot, "CPM.DSK");
        File.WriteAllBytes(present, new byte[128]);
        var entries = new[]
        {
            Entry("id-1", "CP/M", present, 128),
            Entry("id-2", "Missing", Path.Combine(diskRoot, "gone.DSK"), 256),
        };
        var rows = InlineMediaListing.Floppies(entries);
        Assert.Equal(2, rows.Length);
        var ready = rows[0];
        Assert.Equal(InlineMediaStatus.Ready, ready.Status);
        Assert.True(ready.Selectable);
        Assert.Equal(present, ready.Path);
        Assert.Equal("id-1", ready.Handle[5..]);
        Assert.Equal(128, ready.Size);
        Assert.Contains("StandardDsk", ready.Detail);
        var missing = rows[1];
        Assert.Equal(InlineMediaStatus.Unavailable, missing.Status);
        Assert.False(missing.Selectable);
        Assert.Contains("missing", missing.Reason!);
    }

    [Fact]
    public void FloppiesCapAt128RowsPerPage()
    {
        var entries = new List<DiskLibraryEntry>();
        for (int i = 0; i < 130; i++)
        {
            var path = Path.Combine(diskRoot, $"d{i:000}.DSK");
            File.WriteAllBytes(path, new byte[16]);
            entries.Add(Entry($"id-{i:000}", "Disk " + i, path, 16));
        }
        var rows = InlineMediaListing.Floppies(entries);
        Assert.Equal(129, rows.Length);
        Assert.All(rows.Take(128), row => Assert.Equal(InlineMediaStatus.Ready, row.Status));
        var overflow = rows[^1];
        Assert.Equal(InlineMediaStatus.Unavailable, overflow.Status);
        Assert.Contains("2 more", overflow.Reason!);
    }

    [Fact]
    public void HddProfileOffIsAnExplicitUnavailableRow()
    {
        var rows = InlineMediaListing.Hdd(false, [], null);
        var row = Assert.Single(rows);
        Assert.Equal(InlineMediaStatus.Unavailable, row.Status);
        Assert.Contains("New machine", row.Reason!);
        Assert.Contains("hardware", row.Reason!);
        Assert.DoesNotContain("--hdd", row.Reason!);
        Assert.False(row.Selectable);
    }

    [Fact]
    public void HddProjectsPreparedNamedSelectionAndRuntimeViews()
    {
        var prepared = ImmutableArray.Create(new HddInitialMedia(2, new byte[8388608].ToImmutableArray(), "CPM.RAW", WriteProtected: true));
        var views = ImmutableArray.Create(
            new HddDriveView(2, "CPM.RAW", Present: true, Generation: 3, WriteProtected: true, Dirty: false),
            new HddDriveView(3, null, Present: false, Generation: 1, WriteProtected: false, Dirty: false));
        var rows = InlineMediaListing.Hdd(true, views, prepared,
            (Path: "/vols/WORK.DSK", Name: "WORK.DSK", Drive: 4));
        Assert.Equal(4, rows.Length);
        var volume = rows[0];
        Assert.Equal(InlineMediaStatus.Ready, volume.Status);
        Assert.Contains("read-only", volume.Detail);
        Assert.Equal(8388608, volume.Size);
        var cold = rows[1];
        Assert.Equal(InlineMediaStatus.Ready, cold.Status);
        Assert.Contains("write-protected", cold.Detail);
        Assert.Contains("not attached", cold.Detail);
        var mounted = rows[2];
        Assert.Equal(InlineMediaStatus.Ready, mounted.Status);
        Assert.Contains("generation 3", mounted.Detail);
        Assert.DoesNotContain("dirty", mounted.Detail);
        var empty = rows[3];
        Assert.Equal(InlineMediaStatus.Unavailable, empty.Status);
        Assert.Contains("empty", empty.Name);
    }

    [Fact]
    public void HddWithNoPreparedAndNoViewsIsExplicitlyUnavailable()
    {
        var rows = InlineMediaListing.Hdd(true, [], null);
        var row = Assert.Single(rows);
        Assert.Equal(InlineMediaStatus.Unavailable, row.Status);
        Assert.Contains("No HDD media", row.Reason!);
    }

    [Fact]
    public void SnapshotsWithoutDirectoryAreExplicitlyUnavailable()
    {
        Assert.Contains("No snapshot directory configured", Assert.Single(InlineMediaListing.Snapshots(null)).Reason!);
        Assert.Contains("missing", Assert.Single(InlineMediaListing.Snapshots(Path.Combine(root, "absent"))).Reason!);
    }

    [Fact]
    public void SnapshotsListHostMetadataOnlyAndRefuseOversizedFiles()
    {
        var good = Path.Combine(snapRoot, "b.hcs");
        File.WriteAllBytes(good, new byte[64]);
        var last = Path.Combine(snapRoot, "a.hcs");
        File.WriteAllBytes(last, new byte[10]);
        var big = Path.Combine(snapRoot, "z.hcs");
        // Sparse file: host metadata only; the listing must never decode .hcs content.
        File.WriteAllBytes(big, new byte[1]);
        using (var sparse = new FileStream(big, FileMode.Open, FileAccess.ReadWrite))
            sparse.SetLength(MediaFiles.MaxSnapshotBytes + 1);
        var rows = InlineMediaListing.Snapshots(snapRoot, last);
        Assert.Equal(3, rows.Length);
        var first = rows[0];
        Assert.StartsWith(Path.GetFileName(last), first.Name);
        Assert.Contains("last selection", first.Name);
        Assert.Equal(InlineMediaStatus.Ready, first.Status);
        Assert.Equal(10, first.Size);
        Assert.Contains("B", first.Detail);
        // rows are ordered by path (a.hcs, b.hcs, z.hcs), not creation order.
        var extra = rows[1];
        Assert.Equal(InlineMediaStatus.Ready, extra.Status);
        var oversized = rows[2];
        Assert.Equal(InlineMediaStatus.Refused, oversized.Status);
        Assert.False(oversized.Selectable);
        Assert.Contains("exceeds", oversized.Reason!);
    }

    [Fact]
    public void SnapshotsCapAt128RowsPerPage()
    {
        for (int i = 0; i < 130; i++)
            File.WriteAllBytes(Path.Combine(snapRoot, $"s{i:000}.hcs"), new byte[8]);
        var rows = InlineMediaListing.Snapshots(snapRoot);
        Assert.Equal(129, rows.Length);
        Assert.Equal(InlineMediaStatus.Unavailable, rows[^1].Status);
        Assert.Contains("2 more", rows[^1].Reason!);
    }
    [Fact]
    public void SnapshotRevalidationRefusesChangedOrMissingFiles()
    {
        var path = Path.Combine(snapRoot, "v.hcs");
        File.WriteAllBytes(path, new byte[32]);
        Assert.Null(InlineMediaListing.RevalidateSnapshot(path, 32));
        File.WriteAllBytes(path, new byte[40]);
        Assert.Contains("changed since listing", InlineMediaListing.RevalidateSnapshot(path, 32));
        File.Delete(path);
        Assert.Contains("no longer exists", InlineMediaListing.RevalidateSnapshot(path, 40));
    }

    [Fact]
    public void DiskHandleRevalidationRefusesChangedOrMissingCatalogs()
    {
        var path = Path.Combine(diskRoot, "r.DSK");
        File.WriteAllBytes(path, new byte[16]);
        var entry = Entry("abc", "R", path, 16);
        Assert.Null(InlineMediaListing.RevalidateDiskHandle([entry], "disk:abc", path));
        var other = Entry("abc", "R", Path.Combine(diskRoot, "other.DSK"), 16);
        Assert.Contains("changed", InlineMediaListing.RevalidateDiskHandle([other], "disk:abc", path));
        File.Delete(path);
        Assert.Contains("missing", InlineMediaListing.RevalidateDiskHandle([entry], "disk:abc", path));
        Assert.Contains("no longer configured", InlineMediaListing.RevalidateDiskHandle(null, "disk:abc", path));
    }
}
