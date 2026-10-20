using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class PrestartWindowTests : IDisposable
{
    private readonly HddColdAdmissionTests.PrivateScopedDomain nativeDomain = new();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-prestart-" + Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);
    private MainWindow Window(string? root = null, LaunchOptions? launch = null) => new(launch, root ?? RomSelectionFixture.RepositoryRoot(), Store);
    private static PrestartPanel Panel(MainWindow window) => (PrestartPanel)window.FindControl<ContentControl>("PrestartConfiguration")!.Content!;
    private static T Field<T>(PrestartPanel panel, string name) where T : Control => panel.GetLogicalDescendants().OfType<T>().Distinct().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task OrdinaryOpenDoesNotExecuteGuest()
    {
        var window = Window();
        try
        {
            window.Show();
            await Task.Delay(300);
            Assert.Null(window.CurrentRuntime);
            Assert.Null(window.LatestPresentedFrame);
            Assert.Equal(0, window.BootedIf1Length);
            Assert.True(Panel(window).IsVisible);
            Assert.False(File.Exists(Store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task ExplicitValidatedStartRunsTheWholeChipAndDoesNotImplicitlySave()
    {
        var window = Window();
        try
        {
            window.Show();
            Field<TextBox>(Panel(window), "PrestartSpeed").Text = "200";
            Assert.True(await Panel(window).StartAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (window.LatestPresentedFrame is null) await Task.Delay(20, deadline.Token);
            Assert.NotNull(window.CurrentRuntime);
            Assert.Equal(16384, window.BootedIf1Length);
            Assert.False(File.Exists(Store.FilePath));
            Assert.False(await Panel(window).StartAsync());
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaTheory]
    [InlineData("wat", "2")]
    [InlineData("50", "2")]
    [InlineData("100", "9")]
    public async Task InvalidStartNeverConstructsWorkerOrSaves(string speed, string scale)
    {
        var window = Window();
        try
        {
            window.Show();
            Field<TextBox>(Panel(window), "PrestartSpeed").Text = speed;
            Field<TextBox>(Panel(window), "PrestartScale").Text = scale;
            Assert.False(await Panel(window).StartAsync());
            Assert.Null(window.CurrentRuntime);
            Assert.Contains("Not started:", Panel(window).Status);
            Assert.False(File.Exists(Store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task MissingMediaFailsBeforeAnyWorkerOrInstruction()
    {
        var window = Window();
        try
        {
            window.Show();
            Field<TextBox>(Panel(window), "PrestartDiskA").Text = Path.Combine(directory, "missing.dsk");
            Assert.False(await Panel(window).StartAsync());
            Assert.Null(window.CurrentRuntime);
            Assert.Null(window.LatestPresentedFrame);
            Assert.False(File.Exists(Store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task InvalidContainerFailsBeforeAnyWorkerOrInstruction()
    {
        Directory.CreateDirectory(directory);
        var disk = Path.Combine(directory, "wrong.dsk");
        await File.WriteAllBytesAsync(disk, [1, 2, 3]);
        var window = Window();
        try
        {
            window.Show();
            Field<TextBox>(Panel(window), "PrestartDiskA").Text = disk;
            Assert.False(await Panel(window).StartAsync());
            Assert.Null(window.CurrentRuntime);
            Assert.Null(window.LatestPresentedFrame);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task ValidDskIsMountedBeforeFirstGuestInstruction()
    {
        var window = Window();
        try
        {
            window.Show();
            Field<TextBox>(Panel(window), "PrestartDiskA").Text = Path.Combine(RomSelectionFixture.RepositoryRoot(), "disks/cpm/CPM22-HC.DSK");
            Assert.True(await Panel(window).StartAsync(), Panel(window).Status + "; " + window.FindControl<TextBlock>("RuntimeStatus")!.Text);
            Assert.NotNull(window.CurrentRuntime);
            var media = window.FindControl<Hc2000.App.Media.MediaPanel>("MediaControls")!;
            await media.InspectAsync();
            Assert.Contains(media.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("CPM22-HC.DSK") == true);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public void CliOverrideClearsOnlyItsInheritedRawDescriptor()
    {
        var first = new MachineConfiguration
        {
            DiskA = new(Path.GetFullPath("first.raw"), Image: new("RawFloppy", 80, 2, 9, 512)),
            DiskB = new(Path.GetFullPath("second.raw"), Image: new("RawFloppy", 80, 2, 9, 512))
        };
        var other = new MachineConfiguration { Name = "Other draft" };
        Store.Save(new() { Workspace = new(1, first.Id, [first, other]) });
        var cli = Path.GetFullPath("override.dsk");
        var panel = new PrestartPanel(Store, new("A:" + cli, null, false, false));
        var draft = panel.Build();
        Assert.Equal(cli, draft.DiskA!.Path);
        Assert.Equal("DskContainer", draft.DiskA.Descriptor.Kind);
        Assert.Equal("RawFloppy", draft.DiskB!.Descriptor.Kind);
        Assert.Equal(2, Store.Load().Settings.Workspace!.Machines.Length);
    }

    [AvaloniaFact]
    public async Task MalformedSettingsAreNotOverwrittenByDefaultStart()
    {
        Directory.CreateDirectory(directory);
        const string invalid = "{broken original";
        File.WriteAllText(Store.FilePath, invalid);
        var window = Window();
        try
        {
            window.Show();
            Assert.True(await Panel(window).StartAsync());
            Assert.Equal(invalid, File.ReadAllText(Store.FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task ConfigurationSelectorAndCheckboxKeysNeverEnterGuestQueue()
    {
        var window = Window();
        try
        {
            window.Show();
            var panel = Panel(window);
            panel.GetLogicalDescendants().OfType<TabControl>().Distinct().Single().SelectedIndex = 2;
            window.UpdateLayout();
            foreach (var control in new Control[] { Field<ComboBox>(panel, "PrestartKeyboard"), Field<CheckBox>(panel, "PrestartSaveIntent") })
            {
                var before = window.InputQueue.BoundaryCount;
                control.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.A });
                Assert.Equal(before, window.InputQueue.BoundaryCount);
            }
            Assert.Null(window.CurrentRuntime);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task ExplicitCliStartKeepsInitialScreenFocusInsteadOfDraftFocus()
    {
        var window = Window(launch: new LaunchOptions(null, null, true, false));
        try
        {
            window.Show();
            Assert.True(window.FindControl<Hc2000.App.Runtime.VideoSurface>("Screen")!.IsFocused);
            Assert.False(Panel(window).IsVisible);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    public void Dispose() { nativeDomain.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
