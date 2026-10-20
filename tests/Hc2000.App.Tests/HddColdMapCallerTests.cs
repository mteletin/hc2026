using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.App.Runtime;
using Hc2000.HostFiles;
using Hc2000.Machine;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddColdMapCallerTests
{
    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task PreparedMapReachesOnePausedOwnerWithoutDroppingEarlierDrives(int count)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        if (count == 4) HddColdVolumeMapTests.WriteFourVolumeLiteral(fixture.PathName);
        var before = Hash(fixture.PathName);
        var panel = new HddPanel();
        var calls = 0;
        panel.ConfigureCold(_ => throw new InvalidOperationException("Not raw startup."), bootMap: async volume =>
        {
            calls++;
            var images = volume.MappedMedia;
            Assert.Equal(count, images.Length);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(5 - i, images[i].Drive);
                Assert.Equal($"VOL{i + 1}.DSK", images[i].Name);
                Assert.True(images[i].WriteProtected);
                Assert.Equal(i % 2 == 0 ? 0x41 : 0x42, images[i].Bytes[32768]);
            }
            var roms = (MachineProfile.Hc2000V2, new byte[16384], new byte[16384], new byte[16384]);
            var prepared = BasicMachineComposition.PrepareAdmittedHddMapWholeChipV2(roms, volume);
            var queue = new InputQueue();
            queue.Enqueue(new RuntimeControl(InputControlKind.Pause));
            var bridge = new MediaBridge(queue);
            using var worker = BasicMachineComposition.CreateWorker(prepared, queue, bridge);
            try
            {
                var reply = await bridge.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect))).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Null(reply.Error);
                foreach (var view in reply.Hdd!.Drives)
                {
                    var expected = images.SingleOrDefault(image => image.Drive == view.Drive);
                    Assert.Equal(expected is not null, view.Present);
                    if (expected is null) continue;
                    Assert.Equal(expected.Name, view.Name);
                    Assert.True(view.WriteProtected); Assert.False(view.Dirty);
                }
                Assert.Null(worker.TakeLatestFrame()); // Paused before any guest instruction.
            }
            finally { worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(15)); }
        });
        for (int i = 0; i < count; i++)
            await panel.PrepareVolumeAsync(fixture.PathName, $"vol{i + 1}.dsk", (byte)(5 - i));
        Assert.Equal(count, panel.HddListed.Length);
        Assert.Equal(count, panel.HddListed.Select(row => row.Handle).Distinct().Count());
        Assert.All(panel.HddListed, row => Assert.Contains("read-only", row.Detail));
        Assert.All(panel.GetLogicalDescendants().OfType<CheckBox>().Where(box => Equals(box.Content, "Write-protected at cold attach")), box =>
        {
            Assert.False(box.IsEnabled);
            box.IsChecked = false; // A host checkbox cannot make named catalogue-writable entries writable.
        });
        Assert.Equal(0, calls);
        await panel.StartColdAsync();
        Assert.Equal(1, calls);
        Assert.False(panel.ColdConfigurationAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(panel.StartColdAsync);
        Assert.Throws<InvalidOperationException>(panel.ClearPreparedVolumes);
        Assert.Equal(before, Hash(fixture.PathName));
        AssertReadable();
    }

    [AvaloniaFact]
    public async Task RefusedNewSelectionsPreserveEveryPreviouslyPreparedDrive()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var another = fixture.PathName + ".other";
        File.Copy(fixture.PathName, another);
        var panel = new HddPanel();
        panel.ConfigureCold(_ => Task.CompletedTask, bootMap: _ => Task.CompletedTask);
        await panel.PrepareVolumeAsync(fixture.PathName, "VOL1.DSK", 2);
        await panel.PrepareVolumeAsync(fixture.PathName, "VOL2.DSK", 5);
        var listed = panel.HddListed;
        await Assert.ThrowsAsync<InvalidDataException>(() => panel.PrepareVolumeAsync(fixture.PathName, "vol1.dsk", 3));
        await Assert.ThrowsAsync<InvalidDataException>(() => panel.PrepareVolumeAsync(fixture.PathName, "MISSING.DSK", 4));
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PrepareVolumeAsync(another, "VOL2.DSK", 3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => panel.PrepareVolumeAsync(fixture.PathName, "VOL1.DSK", 6));
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PrepareImageAsync(fixture.PathName, 3));
        Assert.Equal(listed, panel.HddListed);
        Assert.True(panel.ColdConfigurationAvailable);
        AssertReadable();
    }

    [AvaloniaFact]
    public async Task ChangedEarlierPayloadRefusesAddingOrStartingWithoutConsumingTheDraft()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var panel = new HddPanel(); var calls = 0;
        panel.ConfigureCold(_ => Task.CompletedTask, bootMap: _ => { calls++; return Task.CompletedTask; });
        await panel.PrepareVolumeAsync(fixture.PathName, "VOL1.DSK", 2);
        using (var file = File.OpenWrite(fixture.PathName)) { file.Position = 32768; file.WriteByte(0x43); }
        var changed = Hash(fixture.PathName);
        await Assert.ThrowsAsync<InvalidDataException>(() => panel.PrepareVolumeAsync(fixture.PathName, "VOL2.DSK", 3));
        await Assert.ThrowsAsync<InvalidDataException>(panel.StartColdAsync);
        Assert.Single(panel.HddListed);
        Assert.Equal("VOL1.DSK", panel.HddListed[0].Name);
        Assert.True(panel.ColdConfigurationAvailable); Assert.Equal(0, calls);
        Assert.Equal(changed, Hash(fixture.PathName));
        AssertReadable();
    }

    [AvaloniaFact]
    public async Task ReplacingOneDrivePreservesOthersAndExplicitClearAllowsRawPreparation()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        HddColdVolumeMapTests.WriteFourVolumeLiteral(fixture.PathName);
        var panel = new HddPanel();
        panel.ConfigureCold(_ => Task.CompletedTask, bootMap: _ => Task.CompletedTask);
        await panel.PrepareVolumeAsync(fixture.PathName, "VOL1.DSK", 2);
        await panel.PrepareVolumeAsync(fixture.PathName, "VOL2.DSK", 3);
        await panel.PrepareVolumeAsync(fixture.PathName, "VOL3.DSK", 2);
        Assert.Equal(new[] { "VOL2.DSK", "VOL3.DSK" }, panel.HddListed.Select(row => row.Name));
        var clear = panel.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Clear named mapping"));
        Assert.True(clear.IsEnabled);
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(clear.IsEnabled);
        Assert.DoesNotContain(panel.HddListed, row => row.Handle.StartsWith("hdd:volume:", StringComparison.Ordinal));
        var raw = fixture.PathName + ".raw";
        using (var file = File.Create(raw)) file.SetLength(HddMediaAdapter.ImageLength);
        await panel.PrepareImageAsync(raw, 4);
        Assert.Contains(panel.HddListed, row => row.Handle == "hdd:prepared:4");
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PrepareVolumeAsync(fixture.PathName, "VOL1.DSK", 2));
    }

    [AvaloniaFact]
    public async Task HddOptInWindowUsesTheMappedColdCallerAndConsumesItOnce()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var before = Hash(fixture.PathName);
        var window = new MainWindow(new LaunchOptions(null, null, false, false, HddEnabled: true), RomSelectionFixture.RepositoryRoot());
        window.Show();
        try
        {
            var panel = window.FindControl<MediaPanel>("MediaControls")!.Hdd;
            Assert.Null(window.CurrentRuntime);
            var fields = panel.GetLogicalDescendants().OfType<TextBox>().ToArray();
            fields.Single(field => field.Name == "ColdVolumePath").Text = fixture.PathName;
            fields.Single(field => field.Name == "ColdVolumeName").Text = "VOL1.DSK";
            panel.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Prepare named volume"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (panel.IsBusy) await Task.Delay(10, deadline.Token);
            Assert.Single(panel.HddListed);
            await panel.PrepareVolumeAsync(fixture.PathName, "VOL2.DSK", 5);
            await panel.StartColdAsync();
            Assert.NotNull(window.CurrentRuntime);
            Assert.False(panel.ColdConfigurationAvailable);
            var mounted = panel.HddListed.Where(row => row.Handle is "hdd:2" or "hdd:5").ToArray();
            Assert.Equal(2, mounted.Length);
            Assert.All(mounted, row => Assert.Equal(InlineMediaStatus.Ready, row.Status));
            Assert.Equal(new[] { "VOL1.DSK", "VOL2.DSK" }, mounted.Select(row => row.Name));
            await Assert.ThrowsAsync<InvalidOperationException>(panel.StartColdAsync);
        }
        finally { window.Close(); await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(20)); }
        Assert.Equal(before, Hash(fixture.PathName));
        AssertReadable();
    }

    private static byte[] Hash(string path)
    {
        using var source = File.OpenRead(path);
        return SHA256.HashData(source);
    }
    private static void AssertReadable()
    {
        var result = NativeImageAdmission.TryAcquireColdRead(default);
        using var admission = result.Admission;
        Assert.NotNull(admission); Assert.Null(result.Refusal);
    }
}
