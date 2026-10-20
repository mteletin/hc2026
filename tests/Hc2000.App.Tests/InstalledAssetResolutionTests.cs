using Hc2000.App;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Spec v4.2: installed config wins over the binary walk; a bare --disk name
/// resolves to &lt;config&gt;/disks, an explicit path does not search.</summary>
public sealed class InstalledAssetResolutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hc2000-resolve-" + Guid.NewGuid().ToString("N"));

    public InstalledAssetResolutionTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void InstalledConfigWinsOverTheBinaryWalk()
    {
        var config = Path.Combine(root, "config");
        var binary = Path.Combine(root, "opt", "hc2000");
        TouchMarker(config);
        TouchMarker(Path.Combine(root, "repo"));
        Directory.CreateDirectory(binary);

        var asset = ProfileRomSelection.ResolveAssetRoot([config], binary);

        Assert.Equal(Path.GetFullPath(config), asset);
    }

    [Fact]
    public void MissingConfigFallsThroughToTheUpwardWalk()
    {
        var repo = Path.Combine(root, "repo");
        var binary = Path.Combine(repo, "src", "bin");
        TouchMarker(repo);
        Directory.CreateDirectory(binary);
        var emptyConfig = Path.Combine(root, "empty-config");
        Directory.CreateDirectory(emptyConfig);

        var asset = ProfileRomSelection.ResolveAssetRoot([emptyConfig], binary);

        Assert.Equal(Path.GetFullPath(repo), asset);
    }

    [Fact]
    public void MissingRomNamesEveryCheckedLocation()
    {
        var config = Path.Combine(root, "config");
        var binary = Path.Combine(root, "opt");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(binary);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ProfileRomSelection.ResolveAssetRoot([config], binary));

        Assert.Contains("roms/hc2000/eprom-dump-hc2k-2/M27C512.bin", error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(config, "roms"), error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(binary, "roms"), error.Message, StringComparison.Ordinal);
        Assert.Contains("Checked:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareDiskNameResolvesUnderConfigDisks()
    {
        var config = Path.Combine(root, "config");
        var disk = Path.Combine(config, "disks", "CPM22-HC.DSK");
        Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
        File.WriteAllBytes(disk, [1, 2, 3]);
        string? inserted = null;

        var status = await LaunchStartup.ApplyDisksAsync(
            new LaunchOptions("A:CPM22-HC.DSK", null, true, false),
            (_, path) => { inserted = path; return Task.CompletedTask; },
            [config],
            Path.Combine(root, "no-such-prefix"));

        Assert.Null(status);
        Assert.Equal(disk, inserted);
    }

    [Fact]
    public async Task ConfigDiskBeatsPrefixDisk()
    {
        var config = Path.Combine(root, "config");
        var prefix = Path.Combine(root, "prefix");
        var shipped = Path.Combine(config, "disks", "CPM22-HC.DSK");
        var beside = Path.Combine(prefix, "disks", "CPM22-HC.DSK");
        Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
        Directory.CreateDirectory(Path.GetDirectoryName(beside)!);
        File.WriteAllBytes(shipped, [1]);
        File.WriteAllBytes(beside, [2]);
        string? inserted = null;

        var status = await LaunchStartup.ApplyDisksAsync(
            new LaunchOptions("A:CPM22-HC.DSK", null, false, false),
            (_, path) => { inserted = path; return Task.CompletedTask; },
            [config],
            prefix);

        Assert.Null(status);
        Assert.Equal(shipped, inserted);
    }

    [Fact]
    public async Task BareNameFallsThroughToPrefixDisks()
    {
        var prefix = Path.Combine(root, "prefix");
        var beside = Path.Combine(prefix, "disks", "CPM22-HC.DSK");
        Directory.CreateDirectory(Path.GetDirectoryName(beside)!);
        File.WriteAllBytes(beside, [2]);
        string? inserted = null;

        var status = await LaunchStartup.ApplyDisksAsync(
            new LaunchOptions("B:CPM22-HC.DSK", null, false, false),
            (slot, path) =>
            {
                Assert.Equal(DiskSlot.B, slot);
                inserted = path;
                return Task.CompletedTask;
            },
            [Path.Combine(root, "empty-config")],
            prefix);

        Assert.Null(status);
        Assert.Equal(beside, inserted);
    }

    [Fact]
    public async Task ExplicitPathIsNotSearched()
    {
        var config = Path.Combine(root, "config");
        var shipped = Path.Combine(config, "disks", "CPM22-HC.DSK");
        Directory.CreateDirectory(Path.GetDirectoryName(shipped)!);
        File.WriteAllBytes(shipped, [1]);
        var missing = Path.Combine(root, "missing", "CPM22-HC.DSK");
        var calls = 0;

        var status = await LaunchStartup.ApplyDisksAsync(
            new LaunchOptions("A:" + missing, null, false, false),
            (_, _) => { calls++; return Task.CompletedTask; },
            [config],
            root);

        Assert.Equal(0, calls);
        Assert.NotNull(status);
        Assert.StartsWith("Disk A: not found:", status, StringComparison.Ordinal);
        Assert.Contains(missing, status, StringComparison.Ordinal);
        Assert.DoesNotContain("Checked:", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingBareNameNamesEveryCheckedDiskLocation()
    {
        var config = Path.Combine(root, "config");
        var prefix = Path.Combine(root, "prefix");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(prefix);

        var status = await LaunchStartup.ApplyDisksAsync(
            new LaunchOptions("A:CPM22-HC.DSK", null, false, false),
            (_, _) => Task.CompletedTask,
            [config],
            prefix);

        Assert.NotNull(status);
        Assert.Contains("Checked:", status, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(config, "disks", "CPM22-HC.DSK"), status, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(prefix, "disks", "CPM22-HC.DSK"), status, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxConfigDirectoriesIncludeHomeConfig()
    {
        if (OperatingSystem.IsWindows()) return;
        var dirs = InstalledLayout.ConfigDirectories();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(Path.GetFullPath(Path.Combine(home, ".config", "hc2000")), dirs);
    }

    private static void TouchMarker(string assetRoot)
    {
        var marker = Path.Combine(assetRoot, "roms", "hc2000", "eprom-dump-hc2k-2", "M27C512.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllBytes(marker, [0]);
    }
}
