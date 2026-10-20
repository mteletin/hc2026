using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Hc2000.App.Input;
using Hc2000.App.Media;
using Hc2000.Machine;
using Hc2000.Machine.Input;
using Hc2000.Machine.Tape;
using Xunit;

namespace Hc2000.App.Tests;

// The media list's tape-volume rows: right-click "Mount volume N as a CP/M drive", then the
// drive-letter row with commit/discard wording; refusal reasons reach the panel's notice.
// Failing-first UI side of hc2000-tape-mount-app-ui, same session binding the lane owns.
[Collection("tape-paged-session")]
public sealed class TapeVolumeMountUiTests
{
    private sealed class World : IDisposable
    {
        public readonly TapeVolumeMediaSessionTests.Rig Rig;
        public readonly MediaPanel Media = new();
        public readonly MediaBridge Bridge;
        public readonly InputController Controller;
        public readonly List<MediaCommand> Seen = [];
        public readonly Window Window;
        public World(int imageBytes = 16384, byte fill = 0xe5, bool phantomFiveMiB = false)
        {
            Rig = new TapeVolumeMediaSessionTests.Rig(imageBytes, fill, phantomFiveMiB: phantomFiveMiB);
            var queue = new InputQueue();
            Bridge = new MediaBridge(queue);
            Controller = new InputController(queue, media: (c, _) => { Seen.Add(c); Bridge.Publish(Rig.Media.Apply(c)); });
            Media.Attach(Bridge);
            Window = new Window { Content = Media };
            Window.Show();
        }
        public void Dispose() { Window.Close(); }

        // Drive the worker until the condition holds, one queued command per pass.
        public async Task PumpUntilAsync(Func<bool> done)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!done())
            {
                deadline.Token.ThrowIfCancellationRequested();
                var poll = Media.PollAsync();
                Controller.ApplyPending(0, new KeyboardMatrix());
                await poll.WaitAsync(deadline.Token);
                await Task.Delay(1, deadline.Token);
            }
        }
        public async Task InspectAsync() => await PumpUntilAsync(() =>
            Media.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("VOL1.DSK") == true));

        public Control VolumeRow(string namePart)
        {
            var label = Media.GetLogicalDescendants().OfType<TextBlock>()
                .First(t => t.Text?.Contains(namePart) == true && (t.ContextMenu is null));
            return label.GetVisualAncestors().OfType<StackPanel>().First(p => p.ContextMenu is not null);
        }
        public MenuItem VolumeMenuItem(string namePart, string headerPart)
        {
            var menu = VolumeRow(namePart).ContextMenu!;
            var item = menu.Items.OfType<MenuItem>().First(m => ((string?)m.Header)!.Contains(headerPart));
            return item;
        }
    }

    [AvaloniaFact]
    public async Task MountMenu_BindsTheFirstFreeLetter_AndTheRowShowsTheLetter()
    {
        using var w = new World();
        await w.InspectAsync();

        // Listed before mounting: the volume name and its size, no drive letter offered yet.
        var row = w.VolumeRow("VOL1.DSK");
        Assert.Equal("VOL1.DSK · 16 KiB",
            row.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text is not null).Text);
        var unmountedMenu = row.ContextMenu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal(new[] { "Mount VOL1.DSK as a CP/M drive" }, unmountedMenu.Select(m => m.Header?.ToString() ?? "").ToArray());

        // Right-click mount: the bound session is reported with the drive letter, and the
        // mounted row offers commit/discard instead of a second mount.
        w.VolumeMenuItem("VOL1.DSK", "Mount").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await w.PumpUntilAsync(() => w.Media.GetLogicalDescendants().OfType<TextBlock>()
            .Any(t => t.Text?.StartsWith("C: VOL1.DSK") == true));
        var mountedRow = w.VolumeRow("C: VOL1.DSK");
        Assert.Equal("C: VOL1.DSK (mounted) · 16 KiB",
            mountedRow.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text is not null).Text);
        var mountedMenu = mountedRow.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToArray();
        Assert.Equal(
            [
                "Unmount C: VOL1.DSK (commit — writes the tape)",
                "Discard C: VOL1.DSK (leaves the tape unchanged)",
            ],
            mountedMenu);
        // The mount command carried the selected volume name through the bridge.
        var mount = w.Seen.Single(c => c.Action == MediaAction.MountTapeVolume);
        Assert.Equal("VOL1.DSK", mount.Name);
        Assert.True(w.Rig.Session.Mounted);
    }

    [AvaloniaFact]
    public async Task PlayingDeck_MountRefusalReason_ReachesTheNotice()
    {
        using var w = new World();
        await w.InspectAsync();
        Assert.True(w.Rig.Deck.RecordPlay().Success);
        w.VolumeMenuItem("VOL1.DSK", "Mount").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await w.PumpUntilAsync(() => w.Media.Notice is not null
            && w.Media.Notice.Contains("Interlock: Mount requires a stopped cassette."));
        Assert.Contains("Media action failed:", w.Media.Notice);
        Assert.False(w.Rig.Session.Mounted);
    }

    [AvaloniaFact]
    public async Task VolumeOverFourMiB_ListedButMountRefused_WithTheExistingContractReason()
    {
        // 5 MiB: over the 4 MiB product cap. Phantom entry: a synthesized C90 TAP cannot physically
        // carry over ~450 KiB, and the mount lane refuses from the catalog before any image is read.
        using var w = new World(imageBytes: 5242880, fill: 0x3a, phantomFiveMiB: true);
        await w.InspectAsync();
        Assert.Equal("VOL1.DSK · 5120 KiB",
            w.VolumeRow("VOL1.DSK").GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text is not null).Text);
        w.VolumeMenuItem("VOL1.DSK", "Mount").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await w.PumpUntilAsync(() => w.Media.Notice is not null && w.Media.Notice.Contains("Invalid volume size."));
        Assert.Contains("Media action failed: InvalidFormat: Invalid volume size.", w.Media.Notice);
        Assert.False(w.Rig.Session.Mounted);
    }

    [AvaloniaFact]
    public async Task CommitAndDiscard_ReachTheirLanes_WithTheChosenWording()
    {
        using var w = new World();
        await w.InspectAsync();
        w.VolumeMenuItem("VOL1.DSK", "Mount").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await w.PumpUntilAsync(() => w.Rig.Session.Mounted);

        // Commit: the unmount lane writes the dirty sector back to the tape before unmounting.
        w.Rig.Session.Write(0, 0x11);
        w.VolumeMenuItem("VOL1.DSK", "commit").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await w.PumpUntilAsync(() => !w.Rig.Session.Mounted);
        var committed = w.Seen.Single(c => c.Action == MediaAction.UnmountTapeVolume);
        Assert.DoesNotContain("failed", w.Media.Notice ?? ""); // The lane applied without a refusal.
        var fresh = new TapeVolumeSession(MachineProfile.Hc2000V2PagedTapeV2, w.Rig.Tape, w.Rig.Memory);
        Assert.True(fresh.Mount("VOL1.DSK", 'C').Value);
        Assert.Equal(0x11, fresh.Read(0));
        Assert.True(fresh.Unmount().Value);

        // Discard: mount again, dirty it, and the discard lane leaves the tape unchanged.
        w.VolumeMenuItem("VOL1.DSK", "Mount").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await w.PumpUntilAsync(() => w.Rig.Session.Mounted);
        w.Rig.Session.Write(0, 0x22);
        w.VolumeMenuItem("VOL1.DSK", "Discard").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await w.PumpUntilAsync(() => !w.Rig.Session.Mounted);
        Assert.Contains(w.Seen, c => c.Action == MediaAction.DiscardTapeVolume);
        var untouched = new TapeVolumeSession(MachineProfile.Hc2000V2PagedTapeV2, w.Rig.Tape, w.Rig.Memory);
        Assert.True(untouched.Mount("VOL1.DSK", 'C').Value);
        Assert.Equal(0x11, untouched.Read(0)); // 0x22 never reached the tape.
    }
}
