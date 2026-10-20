using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddRunningRemapCallerTests
{
    [AvaloniaFact]
    public async Task RuntimeSelectionBeginsWithCopiedIdentityAndObservedGenerationThenCancelsExactRequest()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux native file identity contract");
        using var source = new HddColdVolumeTests();
        var hash = Hash(source.PathName);
        var panel = new HddPanel();
        var requests = new List<HddRequest>();
        panel.Attach(command =>
        {
            var request = command.Hdd!; requests.Add(request);
            var state = request.Action == HddAction.CancelSwitch ? HddSwitchState.Cancelled : HddSwitchState.AwaitingGuest;
            return Task.FromResult(Reply(new([new(4, "OLD.DSK", true, 7, true, false)], Switch: new(91, 4, state))));
        });
        panel.Observe(new([new(4, "OLD.DSK", true, 7, true, false)]));
        panel.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == "ColdVolumePath").Text = source.PathName;
        panel.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == "ColdVolumeName").Text = "vol2.dsk";
        panel.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedIndex = 2;
        var begin = Button(panel, "Request guest-safe remap");
        Assert.True(begin.IsEnabled);
        begin.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Idle(panel);
        var pending = Assert.Single(requests);
        Assert.Equal(HddAction.BeginSwitch, pending.Action);
        Assert.Equal((byte)4, pending.Drive); Assert.Equal(7u, pending.ExpectedGeneration);
        Assert.False(pending.DiscardChanges);
        Assert.Equal(Path.GetFullPath(source.PathName), pending.Target?.Path);
        Assert.Equal("VOL2.DSK", pending.Target?.Identity.Name);
        Assert.False(begin.IsEnabled);
        Assert.Contains("HDSWAP", panel.Notice);
        var cancel = Button(panel, "Cancel pending remap");
        Assert.True(cancel.IsEnabled);
        cancel.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Idle(panel);
        Assert.Equal(HddAction.CancelSwitch, requests[1].Action);
        Assert.Equal(91UL, requests[1].SwitchId);
        Assert.True(begin.IsEnabled); Assert.False(cancel.IsEnabled);
        Assert.Contains("cancelled", panel.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hash, Hash(source.PathName));
        using var reopened = HddColdVolume.Open(source.PathName, "VOL2.DSK");
        Assert.True(reopened.Media(4).WriteProtected);
    }

    [AvaloniaFact]
    public void UnknownSwitchCannotBeReplacedOrCancelledByUi()
    {
        var panel = new HddPanel();
        panel.Attach(_ => throw new InvalidOperationException("Unknown must not enqueue more work."));
        panel.Observe(new([new(2, "OLD.DSK", true, 7, true, false)], Switch: new(91, 2, HddSwitchState.Unknown, "Owner cleanup unconfirmed.")));
        Assert.False(Button(panel, "Request guest-safe remap").IsEnabled);
        Assert.False(Button(panel, "Cancel pending remap").IsEnabled);
        Assert.Contains("Unknown", panel.Notice);
        panel.Disconnect();
        Assert.False(Button(panel, "Request guest-safe remap").IsEnabled);
    }

    [AvaloniaFact]
    public async Task DirtyRefusalNeverRetriesWithImplicitDiscard()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux native file identity contract");
        using var source = new HddColdVolumeTests();
        var requests = new List<HddRequest>();
        var panel = new HddPanel();
        panel.Attach(command =>
        {
            var request = command.Hdd!; requests.Add(request);
            return Task.FromResult(Reply(request.DiscardChanges
                ? new([new(2, "OLD.DSK", true, 7, false, true)], Switch: new(92, 2, HddSwitchState.AwaitingGuest))
                : new([new(2, "OLD.DSK", true, 7, false, true)], Error: "Dirty HDD edits require explicit discard.")));
        });
        panel.Observe(new([new(2, "OLD.DSK", true, 7, false, true)]));
        panel.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == "ColdVolumePath").Text = source.PathName;
        panel.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == "ColdVolumeName").Text = "VOL2.DSK";
        var begin = Button(panel, "Request guest-safe remap");
        begin.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Idle(panel);
        Assert.False(Assert.Single(requests).DiscardChanges);
        Assert.Contains("explicit discard", panel.Notice);
        var discard = panel.GetLogicalDescendants().OfType<CheckBox>().Single(check => Equals(check.Content, "Explicitly discard this drive's unsaved edits on remap"));
        Assert.False(discard.IsChecked == true);
        discard.IsChecked = true;
        begin.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Idle(panel);
        Assert.Equal(2, requests.Count); Assert.True(requests[1].DiscardChanges);
        Assert.Contains("HDSWAP", panel.Notice);
        Assert.False(discard.IsChecked == true);
    }

    [AvaloniaFact]
    public async Task DelayedBeginAndCancelRepliesCannotReachReplacementRuntime()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux native file identity contract");
        using var source = new HddColdVolumeTests();
        foreach (bool cancel in new[] { false, true })
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new TaskCompletionSource<MediaReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            var panel = new HddPanel();
            panel.Attach(command => { reached.SetResult(); return reply.Task; });
            panel.Observe(new([new(2, "OLD.DSK", true, 7, true, false)],
                Switch: cancel ? new(91, 2, HddSwitchState.AwaitingGuest) : null));
            var action = cancel ? panel.CancelVolumeSwitchAsync() : panel.BeginVolumeSwitchAsync(source.PathName, "VOL2.DSK", 2);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            panel.Disconnect();
            int newRequests = 0;
            panel.Attach(_ => { newRequests++; throw new InvalidOperationException("Old request reached new runtime."); });
            panel.Observe(new([new(2, "NEW.DSK", true, 9, true, false)]));
            var notice = panel.Notice;
            reply.SetResult(Reply(new([new(2, "OLD.DSK", true, 8, true, false)], Switch: new(91, 2, HddSwitchState.Committed))));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => action);
            Assert.Contains("runtime changed", error.Message);
            Assert.Equal(0, newRequests); Assert.Equal(notice, panel.Notice);
            Assert.True(Button(panel, "Request guest-safe remap").IsEnabled);
            Assert.False(Button(panel, "Cancel pending remap").IsEnabled);
        }
    }

    private static Button Button(HddPanel panel, string text) => panel.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, text));
    private static async Task Idle(HddPanel panel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (panel.IsBusy) await Task.Delay(10, deadline.Token);
    }
    private static MediaReply Reply(HddReply hdd) => new(0, new(null, false, false, 0, false, 0), new(null, false, false, 0, false, 0), Hdd: hdd);
    private static byte[] Hash(string path) { using var file = File.OpenRead(path); return SHA256.HashData(file); }
}
