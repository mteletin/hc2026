using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// Media homes (contract hc2000-media-home-scan-admission-v1, section 1): optional AppSettings
/// strings, absent by default, validated without touching the filesystem. Missing, relative or
/// non-directory values fail closed at scan time, which belongs to the scan slice.
/// </summary>
public sealed class MediaHomeSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"hc2000-media-homes-{Guid.NewGuid():N}");
    private SettingsStore Store => new(directory);

    [Fact]
    public void HomesAreAbsentByDefaultAndOmittedFromTheFile()
    {
        Store.Save(new AppSettings());
        var json = File.ReadAllText(Store.FilePath);
        Assert.DoesNotContain("diskHome", json);
        Assert.DoesNotContain("tapeHome", json);
        Assert.DoesNotContain("snapshotHome", json);
        var loaded = new SettingsStore(directory).Load().Settings;
        Assert.Null(loaded.DiskHome);
        Assert.Null(loaded.TapeHome);
        Assert.Null(loaded.SnapshotHome);
    }

    [Fact]
    public void MediaHomesRoundTripThroughSaveAndReload()
    {
        Directory.CreateDirectory(directory);
        var existing = Directory.CreateDirectory(Path.Combine(directory, "disks")).FullName;
        var missing = Path.Combine(directory, "does-not-exist");
        Store.Save(new AppSettings { DiskHome = existing, TapeHome = missing, SnapshotHome = "relative/home" });
        var loaded = new SettingsStore(directory).Load().Settings;
        Assert.Equal(existing, loaded.DiskHome);
        Assert.Equal(missing, loaded.TapeHome);
        Assert.Equal("relative/home", loaded.SnapshotHome);
    }

    [Fact]
    public void SerializedKeysAreCamelCase()
    {
        Store.Save(new AppSettings { DiskHome = "/x" });
        var json = File.ReadAllText(Store.FilePath);
        Assert.Contains("\"diskHome\": \"/x\"", json);
    }

    [Theory]
    [InlineData("diskHome")]
    [InlineData("tapeHome")]
    [InlineData("snapshotHome")]
    public void EmptyStringIsRejectedAtValidate(string name)
    {
        var settings = name switch
        {
            "diskHome" => new AppSettings { DiskHome = "" },
            "tapeHome" => new AppSettings { TapeHome = "" },
            _ => new AppSettings { SnapshotHome = "" }
        };
        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Theory]
    [InlineData("relative/home")]
    [InlineData("/no/such/directory/anywhere")]
    [InlineData(" ")]
    public void ValidateAcceptsNonEmptyStringsAndNeverTouchesTheFilesystem(string home)
    {
        var settings = new AppSettings { DiskHome = home };
        settings.Validate(); // no existence check, no relative check
        Assert.False(File.Exists(home) || Directory.Exists(home), $"{home} was created by save/validate");
    }

    [Fact]
    public void SaveAcceptsMissingAndRelativeHomes()
    {
        // A missing or relative directory is valid at save time; it fails closed only at scan time.
        Store.Save(new AppSettings { DiskHome = "relative/home", TapeHome = "/missing/tapes" });
        var loaded = new SettingsStore(directory).Load().Settings;
        Assert.Equal("relative/home", loaded.DiskHome);
        Assert.Equal("/missing/tapes", loaded.TapeHome);
    }

    [Fact]
    public void ExistingSettingsFileWithoutHomeKeysStillLoads()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Store.FilePath, "{\"profile\":\"hc2000-v1\",\"scale\":4,\"keyMap\":{\"Enter\":\"Return\"}}");
        var loaded = Store.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal("hc2000-v1", loaded.Settings.Profile);
        Assert.Equal(4, loaded.Settings.Scale);
        Assert.Equal("Return", loaded.Settings.KeyMap["Enter"]);
        Assert.Null(loaded.Settings.DiskHome);
        Assert.Null(loaded.Settings.TapeHome);
        Assert.Null(loaded.Settings.SnapshotHome);
    }

    [Fact]
    public void ClearingASavedHomeOmitsTheKeyAgain()
    {
        Store.Save(new AppSettings { DiskHome = "/home/disks" });
        Assert.Contains("diskHome", File.ReadAllText(Store.FilePath));
        var loaded = new SettingsStore(directory).Load();
        var cleared = new SettingsEditor(loaded.Settings) { DiskHome = "" };
        Store.Save(cleared.Build());
        Assert.DoesNotContain("diskHome", File.ReadAllText(Store.FilePath));
        Assert.Null(new SettingsStore(directory).Load().Settings.DiskHome);
    }

    [Fact]
    public void EditorCarriesHomesAndBlankClearsThem()
    {
        Store.Save(new AppSettings { DiskHome = "/a/disks", TapeHome = "/b/tapes", SnapshotHome = "/c/snapshots" });
        var draft = new SettingsEditor(Store.Load().Settings);
        Assert.Equal("/a/disks", draft.DiskHome);
        Assert.Equal("/b/tapes", draft.TapeHome);
        Assert.Equal("/c/snapshots", draft.SnapshotHome);
        draft.SnapshotHome = "   ";
        var built = draft.Build();
        Assert.Equal("/a/disks", built.DiskHome);
        Assert.Equal("/b/tapes", built.TapeHome);
        Assert.Null(built.SnapshotHome);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
