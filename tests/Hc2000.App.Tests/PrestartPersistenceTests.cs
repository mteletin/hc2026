using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class PrestartPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc-prestart-save-" + Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private static T Field<T>(PrestartPanel panel, string name) where T : Control
    {
        var descendants = panel.GetLogicalDescendants().ToArray();
        // These owner-callback tests need no window/template: tab content is an explicit
        // topic tree even before Avalonia materializes its selected presenter.
        var topics = descendants.OfType<TabControl>().SelectMany(t => t.Items.OfType<TabItem>())
            .Select(t => (Control)t.Content!).SelectMany(c => c.GetLogicalDescendants());
        return descendants.Concat(topics).OfType<T>().Distinct().Single(c => c.Name == name);
    }

    [AvaloniaFact]
    public async Task SuccessfulExplicitSaveIntentPreservesOtherDraftsAndOpaquePreferences()
    {
        var first = new MachineConfiguration { Id = "first", AudioPreference = "opaque:backend", KeyMap = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("X", "Y") };
        var other = new MachineConfiguration { Id = "other", Name = "Untouched" };
        Store.Save(new() { Workspace = new(1, first.Id, [first, other]) });
        var panel = new PrestartPanel(Store);
        var calls = 0;
        panel.StartRequested = draft => { calls++; Assert.Equal(400, draft.SpeedPercent); return Task.FromResult(true); };
        Field<TextBox>(panel, "PrestartSpeed").Text = "400";
        Field<CheckBox>(panel, "PrestartSaveIntent").IsChecked = true;
        Assert.True(await panel.StartAsync());
        Assert.Equal(1, calls);
        var loaded = Store.Load();
        Assert.Null(loaded.Warning);
        Assert.Equal("first", loaded.Settings.Workspace!.SelectedId);
        var saved = loaded.Settings.Workspace.Machines[0];
        Assert.Equal(400, saved.SpeedPercent);
        Assert.Equal("opaque:backend", saved.AudioPreference);
        Assert.Equal("Y", saved.KeyMap["X"]);
        Assert.Equal(other, loaded.Settings.Workspace.Machines[1]);
    }

    [AvaloniaFact]
    public async Task CancelledStartAndTypingNeverSave()
    {
        Store.Save(new());
        var before = File.ReadAllBytes(Store.FilePath);
        var panel = new PrestartPanel(Store) { StartRequested = _ => Task.FromResult(false) };
        Field<TextBox>(panel, "PrestartSpeed").Text = "200";
        Field<CheckBox>(panel, "PrestartSaveIntent").IsChecked = true;
        _ = panel.Build();
        Assert.Equal(before, File.ReadAllBytes(Store.FilePath));
        Assert.False(await panel.StartAsync());
        Assert.Equal(before, File.ReadAllBytes(Store.FilePath));
        Assert.True(panel.IsEnabled);
    }

    [AvaloniaFact]
    public async Task CorruptOriginalRequiresExplicitReplacementConsentBeforeSavedStart()
    {
        Directory.CreateDirectory(directory);
        const string original = "{unreadable original bytes";
        File.WriteAllText(Store.FilePath, original);
        var calls = 0;
        var panel = new PrestartPanel(Store) { StartRequested = _ => { calls++; return Task.FromResult(true); } };
        Field<CheckBox>(panel, "PrestartSaveIntent").IsChecked = true;
        Assert.False(await panel.StartAsync());
        Assert.Equal(0, calls);
        Assert.Equal(original, File.ReadAllText(Store.FilePath));
        Field<CheckBox>(panel, "PrestartOverwrite").IsChecked = true;
        Assert.True(await panel.StartAsync());
        Assert.Equal(1, calls);
        Assert.Null(Store.Load().Warning);
        Assert.NotNull(Store.Load().Settings.Workspace);
    }

    [AvaloniaFact]
    public async Task SaveFailureAfterAdoptionReportsRunningUnsavedWithoutRollback()
    {
        var panel = new PrestartPanel(Store)
        {
            StartRequested = _ => { Directory.CreateDirectory(Store.FilePath); return Task.FromResult(true); }
        };
        Field<CheckBox>(panel, "PrestartSaveIntent").IsChecked = true;
        Assert.True(await panel.StartAsync());
        Assert.Contains("Running configuration not saved:", panel.Status);
        Assert.False(panel.IsEnabled);
        Assert.False(File.Exists(Store.FilePath));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [AvaloniaFact]
    public async Task SuccessfulStartSavePreservesLatestLegacyEditorValues()
    {
        Store.Save(new());
        var panel = new PrestartPanel(Store) { StartRequested = _ => Task.FromResult(true) };
        Store.Save(new() { AudioPreference = "updated-in-settings-dialog" });
        Field<CheckBox>(panel, "PrestartSaveIntent").IsChecked = true;
        Assert.True(await panel.StartAsync());
        Assert.Equal("updated-in-settings-dialog", Store.Load().Settings.AudioPreference);
    }

    [AvaloniaFact]
    public async Task DuplicateStartWhilePendingDoesNotCallOwnerTwiceOrSave()
    {
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var panel = new PrestartPanel(Store) { StartRequested = _ => { calls++; return reply.Task; } };
        Field<CheckBox>(panel, "PrestartSaveIntent").IsChecked = true;
        var pending = panel.StartAsync();
        Assert.False(panel.IsEnabled);
        Assert.False(await panel.StartAsync());
        Assert.Equal(1, calls);
        Assert.False(File.Exists(Store.FilePath));
        reply.SetResult(false);
        Assert.False(await pending);
        Assert.True(panel.IsEnabled);
        Assert.False(File.Exists(Store.FilePath));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
