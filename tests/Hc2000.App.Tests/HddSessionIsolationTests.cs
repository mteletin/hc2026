using System.Collections.Immutable;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine.Input;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddSessionIsolationTests
{
    private static HddReply View(string name, bool dirty = false) => new(
        Enumerable.Range(2, 4).Select(drive => new HddDriveView((byte)drive,
            drive == 2 ? name : null, drive == 2, 1, false, drive == 2 && dirty)).ToImmutableArray());

    private static MediaReply Reply(HddReply hdd) => new(0,
        new(null, false, false, 0, false, 0), new(null, false, false, 0, false, 0), Hdd: hdd);

    [AvaloniaFact]
    public void NewBindingClearsPreviousLabelsRowsAndDiscardConsent()
    {
        var panel = new HddPanel();
        panel.Attach(_ => Task.FromResult(Reply(View("old.raw", dirty: true))));
        panel.Observe(View("old.raw", dirty: true));
        panel.GetLogicalDescendants().OfType<CheckBox>()
            .Single(box => Equals(box.Content, "Explicitly discard unsaved HDD edits when closing")).IsChecked = true;
        Assert.True(panel.CanCloseSafely);

        panel.Disconnect();
        panel.Attach(_ => Task.FromResult(Reply(View("new.raw", dirty: true))));
        Assert.DoesNotContain(panel.HddListed, row => row.Name == "old.raw");
        Assert.DoesNotContain(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text?.Contains("old.raw") == true);
        panel.Observe(View("new.raw", dirty: true));
        Assert.False(panel.CanCloseSafely); // Consent belongs to the previous binding, not this machine.
    }

    [AvaloniaFact]
    public async Task DisconnectInvalidatesUnconsumedColdConfiguration()
    {
        var panel = new HddPanel();
        var starts = 0;
        panel.ConfigureCold(_ => { starts++; return Task.CompletedTask; });
        panel.Disconnect();
        Assert.False(panel.ColdConfigurationAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(panel.StartColdAsync);
        Assert.Equal(0, starts);
        Assert.Throws<InvalidOperationException>(() => panel.ConfigureCold(_ => Task.CompletedTask));
    }

    [AvaloniaFact]
    public async Task RebindingDoesNotWaitForPreviousOwnersUnansweredUiAction()
    {
        var pending = new TaskCompletionSource<MediaReply>();
        var panel = new HddPanel();
        panel.Attach(_ => pending.Task);
        var refresh = panel.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Refresh HDD status"));
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(panel.IsBusy);
        try
        {
            panel.Disconnect();
            panel.Attach(_ => Task.FromResult(Reply(View("new.raw"))));
            Assert.False(panel.IsBusy);
            Assert.True(refresh.IsEnabled);
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(panel.HddListed, row => row.Name == "new.raw");
        }
        finally { pending.SetResult(Reply(View("old.raw"))); }
        await Task.Yield();
        Assert.DoesNotContain(panel.HddListed, row => row.Name == "old.raw");
    }

    [AvaloniaFact]
    public async Task StaleCaptureCannotPersistOrAcknowledgeAnotherBindingEvenWithSameDelegate()
    {
        using var nativeDomain = new HddColdAdmissionTests.PrivateScopedDomain();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".raw");
        var pending = new TaskCompletionSource<MediaReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new List<HddAction>();
        Func<MediaCommand, Task<MediaReply>> send = command =>
        {
            actions.Add(command.Hdd!.Action);
            return command.Hdd.Action == HddAction.CaptureSave ? pending.Task : Task.FromResult(Reply(View("new.raw", dirty: true)));
        };
        var panel = new HddPanel();
        panel.Attach(send);
        var save = panel.SaveCopyAsync(path, 2);
        panel.Disconnect();
        panel.Attach(send); // Same delegate and colliding owner-local save/generation numbers are not identity.
        panel.Observe(View("new.raw", dirty: true));
        var capture = new HddSaveCapture(1, 2, 1, ImmutableArray.CreateRange(new byte[HddMediaAdapter.ImageLength]));
        pending.SetResult(Reply(View("old.raw", dirty: true) with { Save = capture }));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => save);
            Assert.False(File.Exists(path));
            Assert.Equal([HddAction.CaptureSave], actions);
            Assert.Contains(panel.HddListed, row => row.Name == "new.raw");
            Assert.DoesNotContain(panel.HddListed, row => row.Name == "old.raw");
            Assert.False(panel.CanCloseSafely);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task FaithfulRuntimeRemainsCloseableAfterHddTabWasObserved()
    {
        var panel = new MediaPanel();
        panel.Attach(new MediaBridge(new InputQueue()));
        panel.Hdd.Observe(View("old.raw", dirty: true));
        panel.Disconnect();
        var queue = new InputQueue();
        var bridge = new MediaBridge(queue);
        var session = new MediaSession(new(new()), new(new()));
        var actions = new List<MediaCommand>();
        var controller = new InputController(queue, media: (command, _) =>
        {
            actions.Add(command);
            bridge.Publish(session.Apply(command));
        });
        panel.Attach(bridge);
        var close = panel.CanCloseAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!close.IsCompleted)
        {
            deadline.Token.ThrowIfCancellationRequested();
            controller.ApplyPending(0, new KeyboardMatrix());
            await Task.Delay(1, deadline.Token);
        }
        Assert.True(await close);
        Assert.Contains(actions, command => command.Hdd?.Action == HddAction.Inspect);
        Assert.False(panel.Hdd.RequiresRuntimeInspection);
        Assert.DoesNotContain(panel.Hdd.HddListed, row => row.Name == "old.raw");
        Assert.Contains(panel.Hdd.HddListed, row => row.Detail.Contains("HDD profile off"));
        panel.Disconnect();
    }

    [AvaloniaFact]
    public async Task FailedRawPreparationRetainsOriginalSelectionAndColdProtection()
    {
        using var nativeDomain = new HddColdAdmissionTests.PrivateScopedDomain();
        var directory = Path.Combine(Path.GetTempPath(), "hdd-prepare-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "original.raw");
        var shortPath = Path.Combine(directory, "short.raw");
        var bytes = RawImage();
        var calls = 0;
        var panel = new HddPanel();
        panel.ConfigureCold(images =>
        {
            calls++;
            var image = Assert.Single(images);
            Assert.Equal(2, image.Drive);
            Assert.Equal("original.raw", image.Name);
            Assert.True(image.WriteProtected);
            Assert.Equal(RawHash, Convert.ToHexString(SHA256.HashData(image.Bytes.AsSpan())));
            return Task.CompletedTask;
        });
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            await File.WriteAllBytesAsync(shortPath, [1, 2, 3]);
            await panel.PrepareImageAsync(path, 2);
            panel.GetLogicalDescendants().OfType<CheckBox>()
                .First(box => Equals(box.Content, "Write-protected at cold attach")).IsChecked = true;
            await Assert.ThrowsAsync<InvalidDataException>(() => panel.PrepareImageAsync(shortPath, 2));
            await Assert.ThrowsAsync<FileNotFoundException>(() => panel.PrepareImageAsync(Path.Combine(directory, "missing.raw"), 2));
            Assert.Contains(panel.HddListed, row => row.Name == "original.raw");
            Assert.DoesNotContain(panel.HddListed, row => row.Name == "short.raw");
            await panel.StartColdAsync();
            Assert.Equal(1, calls);
            Assert.False(panel.ColdConfigurationAvailable);
            Assert.Equal(RawHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task PanelPersistsExactCopyBeforeAcknowledgingRealOwnerDirtyState()
    {
        using var nativeDomain = new HddColdAdmissionTests.PrivateScopedDomain();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".raw");
        var memory = new Hc2000.Machine.Memory.HcMemory(new(), new byte[0x4000], new byte[0x4000], new byte[0x4000]);
        var fdc = new Hc2000.Machine.Floppy.FloppyController(new Hc2000.Machine.Timing.DeviceScheduler(), 100, new(new()), new(new()));
        var controller = new Hc2000.Machine.Floppy.BlockDiskPort(memory, fdc, hddEnabled: true);
        controller.Restore(new(0, 0, [new(RawImage(), 7, false, true),
            new([], 0, false, false), new([], 0, false, false), new([], 0, false, false)]));
        var adapter = new HddMediaAdapter(controller);
        var actions = new List<HddAction>();
        var panel = new HddPanel();
        panel.Attach(command =>
        {
            var request = command.Hdd!;
            actions.Add(request.Action);
            Assert.True(controller.CaptureHdd(2).Dirty);
            if (request.Action == HddAction.AcknowledgeSave)
                Assert.Equal(RawHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            return Task.FromResult(Reply(adapter.Apply(request)));
        });
        panel.Observe(new(adapter.Inspect()));
        Assert.False(panel.CanCloseSafely);
        try
        {
            await panel.SaveCopyAsync(path, 2);
            Assert.Equal([HddAction.CaptureSave, HddAction.AcknowledgeSave], actions);
            Assert.False(controller.CaptureHdd(2).Dirty);
            Assert.Equal(7u, controller.CaptureHdd(2).Generation);
            Assert.True(panel.CanCloseSafely);
            Assert.Equal(RawHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        finally { File.Delete(path); }
    }

    private const string RawHash = "817CB2EB0D1E787F98AD92A95A690BBC9648B19C012DEF65419EE9C860069266";
    private static byte[] RawImage()
    {
        var bytes = new byte[HddMediaAdapter.ImageLength];
        bytes[0] = 0x7f; bytes[^1] = 0x31;
        return bytes;
    }
}
