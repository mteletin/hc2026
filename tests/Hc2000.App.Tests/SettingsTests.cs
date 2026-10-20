using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"hc2000-settings-{Guid.NewGuid():N}");
    private SettingsStore Store => new(directory);

    [Fact]
    public void MissingFileLoadsFaithfulDefaultsWithoutWriting()
    {
        var loaded = Store.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal("hc2000-v2", loaded.Settings.Profile);
        Assert.Equal("roms/hc2000/eprom-dump-hc2k-2", loaded.Settings.RomSet);
        Assert.Equal(100, loaded.Settings.SpeedPercent);
        Assert.Equal(2, loaded.Settings.Scale);
        Assert.Equal("default", loaded.Settings.AudioPreference);
        Assert.Empty(loaded.Settings.KeyMap);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void DefaultStoreUsesUserConfigFolder()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "hc2000", "settings.json"), new SettingsStore().FilePath);
    }

    [Fact]
    public void EditorEditsSurviveNewStoreAndEditorInstances()
    {
        var draft = new SettingsEditor(Store.Load().Settings)
        {
            Profile = "hc2000-v1", RomSet = "roms/hc2000/v1", SpeedPercent = "0", Scale = "3",
            AudioPreference = "future-backend:device-2", KeyMapJson = "{\"Enter\":\"Return\",\"Quote\":\"SymbolShift+P\"}"
        };
        Store.Save(draft.Build());
        var loaded = new SettingsStore(directory).Load();
        Assert.Null(loaded.Warning);
        var reopened = new SettingsEditor(loaded.Settings);
        Assert.Equal(draft.Profile, reopened.Profile);
        Assert.Equal(draft.RomSet, reopened.RomSet);
        Assert.Equal(draft.SpeedPercent, reopened.SpeedPercent);
        Assert.Equal(draft.Scale, reopened.Scale);
        Assert.Equal(draft.AudioPreference, reopened.AudioPreference);
        Assert.Equal("Return", loaded.Settings.KeyMap["Enter"]);
        Assert.Equal("SymbolShift+P", loaded.Settings.KeyMap["Quote"]);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"scale\":0}")]
    [InlineData("{\"speedPercent\":50}")]
    [InlineData("{\"keyMap\":null}")]
    [InlineData("{\"keyMap\":{\"Enter\":null}}")]
    [InlineData("{\"profile\":null}")]
    public void MalformedConfigRecoversWithoutDestroyingOriginal(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Store.FilePath, json);
        var loaded = Store.Load();
        Assert.NotNull(loaded.Warning);
        Assert.Equal(100, loaded.Settings.SpeedPercent);
        Assert.Equal(json, File.ReadAllText(Store.FilePath));
        Store.Save(loaded.Settings);
        Assert.Null(Store.Load().Warning);
    }

    [Fact]
    public void PartialConfigUsesDefaultsAndIgnoresFutureFields()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Store.FilePath, "{\"scale\":4,\"futureFeature\":true}");
        var loaded = Store.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal(4, loaded.Settings.Scale);
        Assert.Equal("hc2000-v2", loaded.Settings.Profile);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(0)]
    public void SupportedSpeedsRoundTrip(int speed)
    {
        Store.Save(new AppSettings { SpeedPercent = speed });
        Assert.Equal(speed, Store.Load().Settings.SpeedPercent);
    }

    [Fact]
    public void InvalidSaveLeavesPreviousFileIntact()
    {
        Store.Save(new AppSettings());
        var original = File.ReadAllText(Store.FilePath);
        Assert.Throws<ArgumentException>(() => Store.Save(new AppSettings { Scale = -1 }));
        Assert.Equal(original, File.ReadAllText(Store.FilePath));
    }

    [Theory]
    [InlineData("wat", "2", "{}")]
    [InlineData("50", "2", "{}")]
    [InlineData("100", "0", "{}")]
    [InlineData("100", "2", "null")]
    [InlineData("100", "2", "{\"\":\"x\"}")]
    public void InvalidDraftDoesNotChangeSavedSettings(string speed, string scale, string map)
    {
        Store.Save(new AppSettings());
        var draft = new SettingsEditor(Store.Load().Settings) { SpeedPercent = speed, Scale = scale, KeyMapJson = map };
        Assert.Throws<ArgumentException>(() => draft.Build());
        Assert.Equal(100, Store.Load().Settings.SpeedPercent);
        Assert.Equal(2, Store.Load().Settings.Scale);
    }

    [Fact]
    public void MalformedKeyMapIsRejectedAndDiscardedEditsDoNotPersist()
    {
        Store.Save(new AppSettings());
        var draft = new SettingsEditor(Store.Load().Settings) { Profile = "unsaved", KeyMapJson = "{" };
        Assert.Throws<System.Text.Json.JsonException>(() => draft.Build());
        Assert.Equal("hc2000-v2", new SettingsStore(directory).Load().Settings.Profile);
    }

    [Fact]
    public void WriteFailureIsReportedAndDoesNotLeaveTemporaryFile()
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Store.FilePath);
        Assert.ThrowsAny<IOException>(() => Store.Save(new AppSettings()));
        Assert.Empty(Directory.GetFiles(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
