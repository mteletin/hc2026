using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

public sealed class HddColdCallerTests
{
    [AvaloniaFact]
    public async Task NamedVolumeCallerCopiesLiteralSelectionThroughPausedRealOwner()
    {
        if (!OperatingSystem.IsLinux()) return; // Native qualification is Linux only; no simulated Windows pass.
        using var fixture = new HddColdVolumeTests();
        var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture.PathName));
        var panel = new HddPanel();
        var calls = 0;
        panel.ConfigureCold(_ => throw new InvalidOperationException("Named selection must not use raw startup."), async (volume, drive) =>
        {
            calls++;
            Assert.Equal(5, drive);
            Assert.Equal("VOL2.DSK", volume.Identity.Name);
            var media = volume.Media(drive);
            Assert.True(media.WriteProtected);
            Assert.Equal(8_388_608, media.Bytes.Length);
            Assert.Equal(0x42, media.Bytes[32768]);
            Assert.Equal("F484C7D32E06CBA31D0DD508A1EBC7C26CC4ECDC18A738E040CC3D6BEF7F1EC3",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media.Bytes.AsSpan())));
            var roms = (Hc2000.Machine.MachineProfile.Hc2000V2, new byte[16384], new byte[16384], new byte[16384]);
            var prepared = Hc2000.App.Runtime.BasicMachineComposition.PrepareAdmittedHddWholeChipV2(roms, media, volume.Admission);
            var queue = new Hc2000.App.Input.InputQueue();
            queue.Enqueue(new Hc2000.App.Input.RuntimeControl(Hc2000.App.Input.InputControlKind.Pause));
            var bridge = new MediaBridge(queue);
            using var worker = Hc2000.App.Runtime.BasicMachineComposition.CreateWorker(prepared, queue, bridge);
            try
            {
                var reply = await bridge.RequestAsync(new(MediaAction.Hdd, Hdd: new(HddAction.Inspect))).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Null(reply.Error);
                var view = reply.Hdd!.Drives.Single(v => v.Drive == drive);
                Assert.True(view.Present); Assert.True(view.WriteProtected); Assert.False(view.Dirty);
                Assert.Equal("VOL2.DSK", view.Name);
                Assert.Null(worker.TakeLatestFrame()); // Paused before first instruction; no HDD guest/BIOS run.
            }
            finally { worker.Dispose(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(15)); }
            var admission = Hc2000.HostFiles.NativeImageAdmission.TryAcquireColdRead(default);
            Assert.Null(admission.Refusal);
            admission.Admission!.Dispose(); // Actual owner acknowledgement released the shared fence.
        });
        await panel.PrepareVolumeAsync(fixture.PathName, "vol2.dsk", 5);
        Assert.Equal(0, calls);
        await panel.StartColdAsync();
        Assert.Equal(1, calls);
        Assert.False(panel.ColdConfigurationAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(panel.StartColdAsync);
        Assert.Equal(before, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixture.PathName)));
    }

    [AvaloniaFact]
    public async Task ChangedSelectionRefusesBeforeConsumerAndRetainsColdDraft()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var calls = 0;
        var panel = new HddPanel();
        panel.ConfigureCold(_ => Task.CompletedTask, (_, _) => { calls++; return Task.CompletedTask; });
        await panel.PrepareVolumeAsync(fixture.PathName, "VOL2.DSK", 2);
        using (var file = File.OpenWrite(fixture.PathName)) { file.Position = 8_388_608 + 32768; file.WriteByte(0x43); }
        var changed = File.ReadAllBytes(fixture.PathName);
        await Assert.ThrowsAsync<InvalidDataException>(panel.StartColdAsync);
        Assert.Equal(0, calls);
        Assert.True(panel.ColdConfigurationAvailable);
        Assert.Equal(changed, File.ReadAllBytes(fixture.PathName));
        var read = Hc2000.HostFiles.NativeImageAdmission.TryAcquireColdRead(default);
        Assert.Null(read.Refusal); read.Admission!.Dispose();
    }

    [AvaloniaFact]
    public async Task RawAndNamedInputsCannotBeSilentlyCombinedOrPreparedAfterAttach()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new HddColdVolumeTests();
        var panel = new HddPanel();
        panel.ConfigureCold(_ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        await panel.PrepareVolumeAsync(fixture.PathName, "VOL1.DSK", 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PrepareImageAsync(fixture.PathName, 3));
        panel.Attach(_ => throw new InvalidOperationException("Not a live switch."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => panel.PrepareVolumeAsync(fixture.PathName, "VOL1.DSK", 2));
        await Assert.ThrowsAsync<InvalidOperationException>(panel.StartColdAsync);
    }

    [AvaloniaFact]
    public void ColdHddPanelOffersAnExplicitNamedReadOnlyVolumeCaller()
    {
        var panel = new HddPanel();
        panel.ConfigureCold(_ => Task.CompletedTask);
        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBox>(), field => field.Name == "ColdVolumeName");
        Assert.Contains(panel.GetLogicalDescendants().OfType<Button>(), button => Equals(button.Content, "Prepare named volume"));
        Assert.False(panel.GetLogicalDescendants().OfType<CheckBox>().Single(check => check.Name == "WritableNamedVolume").IsChecked == true);
    }
}
