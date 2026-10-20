using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Audio;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Runtime;
using Hc2000.App.Settings;
using Hc2000.App.Workspace;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Away from 100% speed the App says, in the speed control itself, that sound is silent, and
/// one control puts that speed back to 100%. At 100% the notice is absent. This slice only states the
/// rule BeeperAudioOutput.ProcessFrame already enforces; it does not change that rule.</summary>
public sealed class SoundSilenceNoticeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "hc2000-sound-notice-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [AvaloniaFact]
    public void NoticeNamesTheSilenceAtFiftyAndTwentyFiveAndIsAbsentAtOneHundred()
    {
        var speed = new TextBox { Text = "100" };
        var notice = new SoundSpeedNotice(speed);
        Assert.False(notice.IsShowing);
        Assert.Equal("", notice.NoticeText);

        speed.Text = "50";
        Assert.True(notice.IsShowing);
        Assert.Contains("silent", notice.NoticeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("50%", notice.NoticeText, StringComparison.Ordinal);
        Assert.Contains("100%", notice.NoticeText, StringComparison.Ordinal);

        speed.Text = "25";
        Assert.True(notice.IsShowing);
        Assert.Contains("25%", notice.NoticeText, StringComparison.Ordinal);
        Assert.Contains("silent", notice.NoticeText, StringComparison.OrdinalIgnoreCase);

        speed.Text = "100";
        Assert.False(notice.IsShowing);
        // An unreadable draft is not a silent machine: the notice stays hidden rather than inventing a speed.
        speed.Text = "wat";
        Assert.False(notice.IsShowing);
    }

    [AvaloniaFact]
    public void RestoreControlSetsTheBoundSpeedToOneHundred()
    {
        var speed = new TextBox { Text = "25" };
        var notice = new SoundSpeedNotice(speed);
        var restored = 0;
        notice.Restored += (_, _) => restored++;
        Assert.True(notice.IsShowing);
        notice.RestoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("100", speed.Text);
        Assert.Equal(1, restored);
        Assert.False(notice.IsShowing);
    }

    [AvaloniaFact]
    public void RunningSpeedControlShowsTheNoticeAndReturnsTheMachineToOneHundred()
    {
        var (window, tab, session) = RunningTab();
        try
        {
            TabButton(window, "A");
            var panel = Reconfigure(window);
            Assert.Same(panel.SoundNotice, NoticeIn(panel));
            Assert.False(panel.SoundNotice.IsShowing);

            panel.SpeedField.Text = "50";
            Assert.True(panel.SoundNotice.IsShowing);
            Assert.Contains("50%", panel.SoundNotice.NoticeText, StringComparison.Ordinal);
            panel.SpeedField.Text = "25";
            Assert.True(panel.SoundNotice.IsShowing);
            Assert.Contains("silent", panel.SoundNotice.NoticeText, StringComparison.OrdinalIgnoreCase);
            panel.SpeedField.Text = "100";
            Assert.False(panel.SoundNotice.IsShowing);

            // Typing alone does not change the running speed. Apply a valid off-100 speed first, so the
            // restore control has a real machine speed to put back.
            panel.SpeedField.Text = "200";
            panel.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.StartsWith("Applied", panel.Status);
            Assert.Equal(200, tab.Configuration.SpeedPercent);
            Assert.True(panel.SoundNotice.IsShowing);
            while (session.InputQueue.BoundaryCount > 0) session.InputQueue.TakeNext();

            panel.SoundNotice.RestoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("100", panel.SpeedField.Text);
            Assert.False(panel.SoundNotice.IsShowing);
            Assert.Equal(100, tab.Configuration.SpeedPercent);
            Assert.StartsWith("Applied", panel.Status);
            Assert.Equal(3, session.InputQueue.BoundaryCount);
            Assert.Equal(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: 100), session.InputQueue.TakeNext().Command);
            Assert.Equal(new RuntimeControl(InputControlKind.Reset), session.InputQueue.TakeNext().Command);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PrestartAndSettingsSpeedControlsCarryTheSameNotice()
    {
        var window = new MainWindow(null, RomSelectionFixture.RepositoryRoot(), new SettingsStore(directory));
        SettingsWindow? settings = null;
        try
        {
            window.Show();
            var prestart = (PrestartPanel)window.FindControl<ContentControl>("PrestartConfiguration")!.Content!;
            Assert.False(prestart.SoundNotice.IsShowing);
            prestart.SoundNotice.SpeedField.Text = "50";
            Assert.True(prestart.SoundNotice.IsShowing);
            Assert.Contains("silent", prestart.SoundNotice.NoticeText, StringComparison.OrdinalIgnoreCase);
            prestart.SoundNotice.SpeedField.Text = "25";
            Assert.Contains("25%", prestart.SoundNotice.NoticeText, StringComparison.Ordinal);
            prestart.SoundNotice.RestoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("100", prestart.SoundNotice.SpeedField.Text);
            Assert.False(prestart.SoundNotice.IsShowing);

            settings = new SettingsWindow(new SettingsStore(directory));
            settings.Show();
            Assert.False(settings.SoundNotice.IsShowing);
            settings.SoundNotice.SpeedField.Text = "50";
            Assert.True(settings.SoundNotice.IsShowing);
            settings.SoundNotice.SpeedField.Text = "25";
            Assert.Contains("25%", settings.SoundNotice.NoticeText, StringComparison.Ordinal);
            Assert.Contains("silent", settings.SoundNotice.NoticeText, StringComparison.OrdinalIgnoreCase);
            settings.SoundNotice.SpeedField.Text = "100";
            Assert.False(settings.SoundNotice.IsShowing);
            settings.SoundNotice.SpeedField.Text = "200";
            settings.SoundNotice.RestoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("100", settings.SoundNotice.SpeedField.Text);
            Assert.False(settings.SoundNotice.IsShowing);
        }
        finally
        {
            settings?.Close();
            window.Close();
        }
    }

    private static SoundSpeedNotice NoticeIn(Control root) =>
        root.GetLogicalDescendants().OfType<SoundSpeedNotice>().Single();

    private static Button TabButton(MainWindow window, string name)
    {
        var panel = window.FindControl<StackPanel>("MachineTabs")!;
        var button = panel.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => Equals(b.Content, name) || Equals(b.Content, "\u25CF " + name));
        Assert.NotNull(button);
        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return button;
    }

    private static ReconfigurePanel Reconfigure(MainWindow window)
        => (ReconfigurePanel)window.FindControl<ContentControl>("ReconfigureConfiguration")!.Content!;

    private static (MainWindow window, MachineTabContext tab, MachineSession session) RunningTab()
    {
        var window = new MainWindow();
        window.Show();
        Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "A" }, out var tab, out var refusal));
        Assert.Null(refusal);
        var session = tab!.Session;
        session.AttachWorker(Z80VisualDemo.CreateWorker());
        session.BasicRuntimeActive = true;
        return (window, tab, session);
    }
}
