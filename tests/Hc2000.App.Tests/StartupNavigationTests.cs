using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Runtime;
using Hc2000.App.Settings;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class StartupNavigationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-startup-navigation-" + Guid.NewGuid().ToString("N"));

    private MainWindow Window() => new(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory));

    private static PrestartPanel Draft(MainWindow window) =>
        Assert.IsType<PrestartPanel>(window.FindControl<ContentControl>("PrestartConfiguration")!.Content);

    private static T Named<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetLogicalDescendants().OfType<T>().Distinct(), control => control.Name == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task OrdinaryLaunchSeparatesConfigurationTopicsFromMachinesWithoutStarting()
    {
        var window = Window();
        try
        {
            window.Show();
            Assert.Equal("Machine configuration", Named<TextBlock>(window, "ConfigurationHeading").Text);
            Assert.Equal("Machines", Named<TextBlock>(window, "MachineWorkspaceHeading").Text);
            Assert.Equal("New machine", window.FindControl<Button>("NewMachine")!.Content);
            Assert.Equal("Start machine", Named<Button>(Draft(window), "PrestartStart").Content);
            Assert.False(window.FindControl<Control>("RunningWorkspace")!.IsVisible);
            Assert.Null(window.CurrentRuntime);
            Assert.Null(window.LatestPresentedFrame);
            Assert.False(File.Exists(new SettingsStore(directory).FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task MachineSelectionNamesTheTargetWithoutTreatingTopicsAsAnotherMachine()
    {
        var window = Window();
        try
        {
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk A" }, out var a, out _));
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Desk B" }, out var b, out _));
            Assert.Equal("Desk B", Named<TextBlock>(window, "SelectedMachineName").Text);
            Assert.Equal("Reset selected machine", window.FindControl<Button>("MachineReset")!.Content);
            var tabs = window.FindControl<StackPanel>("MachineTabs")!;
            Click(tabs.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Desk A")));
            Assert.Same(a!.Session, window.CurrentSession);
            Assert.Equal("Desk A", Named<TextBlock>(window, "SelectedMachineName").Text);
            Assert.Equal(2, tabs.Children.Count);
            Assert.Null(a.Session.Worker);
            Assert.Null(b!.Session.Worker);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task NewMachineOpensADetachedDraftWithoutReplacingTheSelectedRuntime()
    {
        var window = Window();
        try
        {
            window.Show();
            var aConfiguration = new MachineConfiguration { Name = "Desk A" };
            var bConfiguration = new MachineConfiguration { Name = "Desk B" };
            Assert.True(window.TryAddTabView(aConfiguration, out var a, out _));
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            var aWorker = a!.Session.Worker;
            Assert.True(window.TryAddTabView(bConfiguration, out var b, out _));
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            var bWorker = b!.Session.Worker;
            Click(window.FindControl<Button>("NewMachine")!);
            var draft = Draft(window);
            Assert.True(draft.IsVisible);
            Assert.True(draft.IsEnabled);
            Assert.NotEqual(aConfiguration.Id, draft.Build().Id);
            Assert.NotEqual(bConfiguration.Id, draft.Build().Id);
            Assert.Same(aWorker, a.Session.Worker);
            Assert.Same(bWorker, window.CurrentRuntime);
            Assert.Equal(2, window.FindControl<StackPanel>("MachineTabs")!.Children.Count);
            Assert.False(File.Exists(new SettingsStore(directory).FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task SecondExplicitStartHasSeparateOwnersAndClosingItKeepsTheFirstRunning()
    {
        var window = Window();
        try
        {
            window.Show();
            var first = Draft(window);
            Named<TextBox>(first, "PrestartName").Text = "Desk A";
            Assert.True(await first.StartAsync(), first.Status);
            var a = window.CurrentSession;
            var workerA = window.CurrentRuntime;
            Assert.NotNull(workerA);
            Assert.False(first.IsVisible);
            Assert.False(window.FindControl<Border>("ConfigurationCard")!.IsVisible);
            Assert.True(window.FindControl<Control>("RunningWorkspace")!.IsVisible);
            Click(window.FindControl<Button>("NewMachine")!);
            var second = Draft(window);
            Named<TextBox>(second, "PrestartName").Text = "Desk B";
            Assert.True(await second.StartAsync(), second.Status);
            var b = window.CurrentSession;
            Assert.NotSame(a, b);
            Assert.NotSame(workerA, window.CurrentRuntime);
            Assert.NotSame(a.InputQueue, b.InputQueue);
            Assert.NotSame(a.MediaBridge, b.MediaBridge);
            Assert.NotSame(a.SnapshotBridge, b.SnapshotBridge);
            Assert.Same(workerA, a.Worker);
            Assert.False(workerA.Completion.IsCompleted);
            Assert.Equal("Desk B", Named<TextBlock>(window, "SelectedMachineName").Text);
            Assert.Equal("Effective hardware: hc2000-v2", Named<TextBlock>(window, "EffectiveHardware").Text);
            var tabs = window.FindControl<StackPanel>("MachineTabs")!;
            Assert.Equal(2, tabs.Children.Count);
            Click(tabs.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Desk A")));
            Assert.Same(workerA, window.CurrentRuntime);
            Assert.Equal("Desk A", Named<TextBlock>(window, "SelectedMachineName").Text);
            Click(tabs.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Desk B")));
            // Close the selected candidate through the existing lifetime path, not a new backend.
            Click(tabs.GetLogicalDescendants().OfType<Button>().Single(button =>
                Avalonia.Automation.AutomationProperties.GetName(button) == "Close machine Desk B"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (tabs.Children.Count != 1) await Task.Delay(5, deadline.Token);
            Assert.Same(workerA, window.CurrentRuntime);
            Assert.False(workerA.Completion.IsCompleted);
            Assert.Single(tabs.Children);
            Assert.False(File.Exists(new SettingsStore(directory).FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task FailedColdCandidateRestoresTheSelectedSurvivorAndDoesNotSave()
    {
        var missingAssets = Path.Combine(directory, "missing-assets");
        var window = new MainWindow(null, missingAssets, new SettingsStore(directory));
        try
        {
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Survivor" }, out var a, out _));
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            var workerA = window.CurrentRuntime;
            Click(window.FindControl<Button>("NewMachine")!);
            var second = Draft(window);
            Named<CheckBox>(second, "PrestartSaveIntent").IsChecked = true;
            Assert.False(await second.StartAsync());
            Assert.Same(a!.Session, window.CurrentSession);
            Assert.Same(workerA, window.CurrentRuntime);
            Assert.False(workerA!.Completion.IsCompleted);
            Assert.Single(window.FindControl<StackPanel>("MachineTabs")!.Children);
            Assert.True(second.IsVisible);
            Assert.True(second.IsEnabled);
            Assert.True(window.FindControl<Control>("RunningWorkspace")!.IsVisible);
            Assert.False(File.Exists(new SettingsStore(directory).FilePath));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task NewIdentityHasNoInheritedMediaAndSaveAppendsWithoutReplayingUnrelatedSettings()
    {
        var store = new SettingsStore(directory);
        var source = new MachineConfiguration
        {
            Name = "Stored hardware", Profile = "hc2000-v2-hdd-v1", SpeedPercent = 200, Scale = 3,
            KeyboardMode = "Symbolic", DiskA = new(Path.Combine(directory, "absent.dsk")),
            Hdd = [new("C", Path.Combine(directory, "absent.raw"), false)], LastSnapshot = Path.Combine(directory, "old.hcs")
        };
        var other = new MachineConfiguration { Name = "Keep this record" };
        store.Save(new() { Workspace = new(1, source.Id, [source, other]) });
        var window = Window();
        try
        {
            window.Show();
            Click(window.FindControl<Button>("NewMachine")!);
            var panel = Draft(window);
            var draft = panel.Build();
            Assert.NotEqual(source.Id, draft.Id);
            Assert.NotEqual(other.Id, draft.Id);
            Assert.Equal("hc2000-v2", draft.Profile);
            Assert.Null(draft.DiskA); Assert.Null(draft.DiskB);
            Assert.Empty(draft.Hdd); Assert.Null(draft.LastSnapshot);
            Assert.Equal(source.SpeedPercent, draft.SpeedPercent);
            Assert.Equal(source.Scale, draft.Scale);
            Assert.Equal(source.KeyboardMode, draft.KeyboardMode);
            // Simulate the existing independent preferences writer after this draft opened.
            store.Save(store.Load().Settings with { SpeedPercent = 400, Scale = 4 });
            Named<CheckBox>(panel, "PrestartSaveIntent").IsChecked = true;
            Assert.True(await panel.StartAsync(), panel.Status);
            var saved = store.Load();
            Assert.Null(saved.Warning);
            Assert.Equal(400, saved.Settings.SpeedPercent);
            Assert.Equal(4, saved.Settings.Scale);
            Assert.Equal(draft.Id, saved.Settings.Workspace!.SelectedId);
            Assert.Equal(3, saved.Settings.Workspace.Machines.Length);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(source), System.Text.Json.JsonSerializer.Serialize(saved.Settings.Workspace.Machines[0]));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(other), System.Text.Json.JsonSerializer.Serialize(saved.Settings.Workspace.Machines[1]));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(draft), System.Text.Json.JsonSerializer.Serialize(saved.Settings.Workspace.Machines[2]));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(draft), System.Text.Json.JsonSerializer.Serialize(new PrestartPanel(store).Build()));
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaFact]
    public async Task FaultedSelectedOwnerDoesNotStopPresentationForASurvivingMachine()
    {
        var window = Window();
        try
        {
            window.Show();
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Survivor" }, out var a, out _));
            window.AttachRuntime(Z80VisualDemo.CreateWorker());
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "Failed" }, out _, out _));
            var failed = new EmulationWorker(() => throw new IOException("test owner failure"), _ => { });
            window.AttachRuntime(failed);
            await Assert.ThrowsAsync<IOException>(async () => await failed.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (window.FindControl<TextBlock>("RuntimeStatus")!.Text?.Contains("stopped") != true)
                await Task.Delay(5, deadline.Token);
            Assert.Contains(window.FindControl<StackPanel>("MachineTabs")!.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Stopped");
            var before = window.FindControl<VideoSurface>("Screen")!.PresentedMasterTick;
            Click(window.FindControl<StackPanel>("MachineTabs")!.GetLogicalDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Survivor")));
            while (window.FindControl<VideoSurface>("Screen")!.PresentedMasterTick <= before)
                await Task.Delay(5, deadline.Token);
            Assert.Same(a!.Session.Worker, window.CurrentRuntime);
            Assert.False(window.CurrentRuntime!.Completion.IsCompleted);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaTheory]
    [InlineData(1280, 900)]
    [InlineData(960, 720)]
    public async Task NormalAndCompactShellKeepsStartAccessibleAndTheWholeRunningFrameVisible(int width, int height)
    {
        var window = Window();
        window.Width = width; window.Height = height;
        try
        {
            window.Show(); window.UpdateLayout();
            var start = Named<Button>(Draft(window), "PrestartStart");
            var startOrigin = start.TranslatePoint(default, window)!.Value;
            Assert.InRange(startOrigin.X + start.Bounds.Width, 1, width);
            Assert.InRange(startOrigin.Y + start.Bounds.Height, 1, height);
            Assert.True(await Draft(window).StartAsync(), Draft(window).Status);
            window.UpdateLayout();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, Avalonia.Threading.DispatcherPriority.Background);
            var viewport = window.FindControl<AspectRatioViewport>("ScreenViewport")!;
            var frame = viewport.Child!;
            var origin = frame.TranslatePoint(default, window)!.Value;
            Assert.InRange(frame.Bounds.Width, 1, width);
            Assert.InRange(frame.Bounds.Height, 1, height);
            Assert.True(origin.Y + frame.Bounds.Height <= window.ClientSize.Height + 1,
                $"frame bottom {origin.Y + frame.Bounds.Height}; viewport cap {viewport.MaxHeight}; viewport {viewport.Bounds}; client {window.ClientSize}");
            Assert.InRange(frame.Bounds.Width / frame.Bounds.Height, 352.0 / 296 - .001, 352.0 / 296 + .001);
            Assert.Equal(2, viewport.RequestedScale); // fit must not rewrite the user's integer zoom request
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    [AvaloniaTheory]
    [InlineData(1280, 900)]
    [InlineData(960, 720)]
    public async Task OpeningAnotherDraftKeepsTheRunningFrameReachableBelowTheEditor(int width, int height)
    {
        var window = Window();
        window.Width = width; window.Height = height;
        try
        {
            window.Show();
            Assert.True(await Draft(window).StartAsync(), Draft(window).Status);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (window.LatestPresentedFrame is null) await Task.Delay(5, deadline.Token);
            var worker = window.CurrentRuntime;
            Click(window.FindControl<Button>("NewMachine")!);
            window.UpdateLayout();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, Avalonia.Threading.DispatcherPriority.Background);
            var viewport = window.FindControl<AspectRatioViewport>("ScreenViewport")!;
            viewport.BringIntoView();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, Avalonia.Threading.DispatcherPriority.Background);
            var frame = viewport.Child!;
            var origin = frame.TranslatePoint(default, window)!.Value;
            Assert.True(frame.Bounds.Height >= 128, $"frame {frame.Bounds}; viewport cap {viewport.MaxHeight}");
            Assert.InRange(origin.Y, -1, window.ClientSize.Height);
            Assert.True(origin.Y + frame.Bounds.Height <= window.ClientSize.Height + 1,
                $"frame bottom {origin.Y + frame.Bounds.Height}; client {window.ClientSize}");
            Assert.InRange(frame.Bounds.Width / frame.Bounds.Height, 352.0 / 296 - .001, 352.0 / 296 + .001);
            Assert.Equal(2, viewport.RequestedScale);
            Assert.True(Draft(window).IsVisible);
            Assert.Single(window.FindControl<StackPanel>("MachineTabs")!.Children);
            Assert.Same(worker, window.CurrentRuntime);
            Assert.False(worker!.Completion.IsCompleted);
        }
        finally { window.Close(); await window.ShutdownCompletion; }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
