using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.App.Workspace;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>Apply-running-configuration + binding-preserving reset on the shared window view: an invalid
/// draft never resets, a media-mutated draft is refused without a reset, and one machine's apply/reset
/// leaves every other machine's queue, configuration and bindings unchanged.</summary>
public sealed class SelectedReconfigureResetTests
{
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

    private static (MainWindow window, MachineTabContext tab, MachineSession session) RunningTabA(string name = "A")
    {
        var window = new MainWindow();
        window.Show();
        Assert.True(window.TryAddTabView(new MachineConfiguration { Name = name }, out var tab, out var refusal));
        Assert.Null(refusal);
        var session = tab!.Session;
        session.AttachWorker(Z80VisualDemo.CreateWorker());
        session.BasicRuntimeActive = true;
        return (window, tab, session);
    }

    [AvaloniaFact]
    public void InvalidDraftDoesNotResetTheMachine()
    {
        var (window, tabA, a) = RunningTabA();
        try
        {
            TabButton(window, "A");
            var panel = Reconfigure(window);
            Assert.True(panel.IsVisible, userMessage: "the reconfigure view must bind to the selected running machine");
            panel.SpeedField.Text = "999";
            panel.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.StartsWith("Not applied:", panel.Status);
            Assert.Equal(0, a.InputQueue.BoundaryCount); // an invalid draft must not touch the machine queue;
            Assert.Equal(100, tabA.Configuration.SpeedPercent);
            Assert.Equal(0, tabA.DraftRevision); // an invalid draft must not bump the draft revision;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MediaMutatedDraftIsRefusedWithoutReset()
    {
        var (window, tabA, a) = RunningTabA();
        try
        {
            TabButton(window, "A");
            var current = tabA.Configuration;
            var target = new MachineDraftTarget(tabA.TabId, tabA.DraftRevision);
            var error = window.ApplyRunningDraft(target, current with
            {
                DiskA = new FloppyConfiguration(Path.GetFullPath("other.dsk")),
                SpeedPercent = 200
            });
            Assert.Equal("Apply keeps the mounted A/B media and writeback choices unchanged; media changes need a new machine draft.", error);
            Assert.Equal(0, a.InputQueue.BoundaryCount); // a media change must not reset the machine;
            Assert.Equal(current, tabA.Configuration);
            Assert.Equal(0, tabA.DraftRevision);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ValidApplyResetsOnlyTheSelectedMachine()
    {
        var (window, tabA, a) = RunningTabA("A");
        try
        {
            Assert.True(window.TryAddTabView(new MachineConfiguration { Name = "B" }, out var tabB, out _));
            var b = tabB!.Session;
            a.WritebackHost = new DiskWritebackHost(new DiskWritebackOwnerLedger(), new MediaBridge(a.InputQueue), false);
            b.WritebackHost = new DiskWritebackHost(new DiskWritebackOwnerLedger(), new MediaBridge(b.InputQueue), false);
            TabButton(window, "A");
            while (a.InputQueue.BoundaryCount > 0) a.InputQueue.TakeNext();
            while (b.InputQueue.BoundaryCount > 0) b.InputQueue.TakeNext();
            var bConfig = tabB.Configuration;
            var bMode = b.KeyboardInput.Mode;

            var panel = Reconfigure(window);
            panel.SpeedField.Text = "200";
            panel.ScaleField.Text = "4";
            panel.KeyboardField.SelectedIndex = 1; // Symbolic
            panel.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("Applied; only this machine was reset. No settings saved.", panel.Status);
            // A received the mapping-mode release, the new pacing mode and the reset on its own FIFO.
            // The keyboard-mode change (Positional->Symbolic) releases focus first, via UpdateDraft,
            // so a ReleaseAll precedes the SetSpeed; the reset then releases focus a second time.
            Assert.Equal(4, a.InputQueue.BoundaryCount);
            Assert.IsType<ReleaseAll>(a.InputQueue.TakeNext().Command); // mapping-mode change release
            Assert.Equal(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: 200), a.InputQueue.TakeNext().Command);
            Assert.Equal(new RuntimeControl(InputControlKind.Reset), a.InputQueue.TakeNext().Command);
            Assert.IsType<ReleaseAll>(a.InputQueue.TakeNext().Command); // reset focus release
            Assert.Equal(200, tabA.Configuration.SpeedPercent);
            Assert.Equal(4, tabA.Configuration.Scale);
            Assert.Equal("Symbolic", tabA.Configuration.KeyboardMode);
            Assert.Equal(KeyMappingMode.Symbolic, a.KeyboardInput.Mode);
            // B was not touched: queue, configuration and keyboard mode all unchanged.
            Assert.Equal(0, b.InputQueue.BoundaryCount);
            Assert.Equal(bConfig, tabB.Configuration);
            Assert.Equal(bMode, b.KeyboardInput.Mode);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void StaleDraftTargetIsRefusedWithoutSecondReset()
    {
        var (window, tabA, a) = RunningTabA();
        try
        {
            var target = new MachineDraftTarget(tabA.TabId, tabA.DraftRevision);
            var error = window.ApplyRunningDraft(target, tabA.Configuration with { SpeedPercent = 200 });
            Assert.Null(error);
            Assert.Equal(200, tabA.Configuration.SpeedPercent);
            Assert.Equal(1, tabA.DraftRevision);
            // The same captured target is now stale: refused, no second reset, no change.
            var stale = window.ApplyRunningDraft(target, tabA.Configuration with { SpeedPercent = 400 });
            Assert.Equal("the machine draft changed while this apply was in flight.", stale);
            Assert.Equal(200, tabA.Configuration.SpeedPercent);
            Assert.Equal(1, tabA.DraftRevision);
            // A's FIFO holds only the FIRST apply's SetSpeed, Reset and the focus release tail.
            Assert.Equal(3, a.InputQueue.BoundaryCount);
            Assert.Equal(new RuntimeControl(InputControlKind.SetSpeed, SpeedPercent: 200), a.InputQueue.TakeNext().Command);
            Assert.Equal(new RuntimeControl(InputControlKind.Reset), a.InputQueue.TakeNext().Command);
            Assert.IsType<ReleaseAll>(a.InputQueue.TakeNext().Command);
        }
        finally { window.Close(); }
    }
}
