using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Hc2000.App.Library;
using Hc2000.App.Media;
using Xunit;

namespace Hc2000.App.Tests;

/// <summary>
/// Scanned media-home list: 12 rows per page, Mount/Eject only on a selectable floppy-dsk row
/// when the accepted lane is wired, and a later admission refusal stays a closed notice.
/// </summary>
public sealed class MediaHomeListPanelUiTests
{
    private static MediaHomeRow Row(string kind, string name, bool recognized, string path = "/tmp/scanned.dsk")
        => new("disk", kind, name, path, 184320, "recognized", recognized);

    private static MediaHomeListPanel Shown(params MediaHomeRow[] rows)
    {
        var panel = new MediaHomeListPanel();
        panel.Show(new MediaHomeListing(rows.ToImmutableArray(), ImmutableArray<MediaHomeState>.Empty, false));
        return panel;
    }

    private static MediaHomeRowView Host(MediaHomeListPanel panel, string name)
        => Assert.Single(panel.GetLogicalDescendants().OfType<MediaHomeRowView>(), view => view.Row.Name == name);

    private static async Task Click(MenuItem item, MediaHomeListPanel panel)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var action = panel.PendingAction ?? panel.LastAction;
        Assert.NotNull(action);
        await action;
    }

    [AvaloniaFact]
    public void ThirteenRowsShowTwelveThenTheRemainder()
    {
        var rows = Enumerable.Range(0, 13).Select(i => Row("floppy-dsk", $"D{i:00}.DSK", true)).ToArray();
        var panel = new MediaHomeListPanel();
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            panel.Show(new MediaHomeListing(
                rows.ToImmutableArray(),
                [MediaHomeState.Refused("tape", "directory missing")],
                false));
            Assert.Equal(MediaHomeListPanel.RowsPerPage, panel.GetLogicalDescendants().OfType<MediaHomeRowView>().Count());
            Assert.Contains("page 1 of 2", panel.GetLogicalDescendants().OfType<TextBlock>().Single(b => b.Text?.StartsWith("page ") == true).Text);
            Assert.Contains("tape: unavailable", panel.GetLogicalDescendants().OfType<TextBlock>().Single(b => b.Text?.Contains("unavailable") == true).Text);
            panel.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Next page"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("D12.DSK", Assert.Single(panel.GetLogicalDescendants().OfType<MediaHomeRowView>()).Row.Name);
            Assert.Equal("D12.DSK", Assert.Single(panel.Shown).Name);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MountMenuIsOnlyOnASelectableFloppyWithALane()
    {
        var lane = Lane();
        var panel = Shown(
            Row("floppy-dsk", "GAME.DSK", true),
            Row("hdd-raw", "DISK.IMG", true),
            Row("unknown", "NOTE.TXT", false),
            Row("tape", "SAVE.TAP", true));
        panel.MountLane = lane;
        panel.Show(new MediaHomeListing(
            [
                Row("floppy-dsk", "GAME.DSK", true),
                Row("hdd-raw", "DISK.IMG", true),
                Row("unknown", "NOTE.TXT", false),
                Row("tape", "SAVE.TAP", true)
            ],
            ImmutableArray<MediaHomeState>.Empty,
            false));
        Assert.Equal(4, Menu(Host(panel, "GAME.DSK")).Count);
        Assert.Null(Host(panel, "DISK.IMG").ContextMenu);
        Assert.Null(Host(panel, "NOTE.TXT").ContextMenu);
        Assert.Null(Host(panel, "SAVE.TAP").ContextMenu);

        panel.MountLane = null;
        panel.Show(new MediaHomeListing([Row("floppy-dsk", "GAME.DSK", true)], ImmutableArray<MediaHomeState>.Empty, false));
        Assert.Null(Host(panel, "GAME.DSK").ContextMenu);
    }

    [AvaloniaFact]
    public async Task MountClickReportsTheAdmissionRefusalAndMountsNothing()
    {
        var panel = Shown(Row("floppy-dsk", "GAME.DSK", true, "/tmp/not-registered.dsk"));
        panel.MountLane = Lane();
        panel.Show(new MediaHomeListing(
            [Row("floppy-dsk", "GAME.DSK", true, "/tmp/not-registered.dsk")],
            ImmutableArray<MediaHomeState>.Empty,
            false));
        var mountA = Menu(Host(panel, "GAME.DSK"))[0];
        Assert.Equal("Mount as drive A:", mountA.Header);
        await Click(mountA, panel);
        Assert.Contains("Mount refused, nothing mounted", panel.NoticeText);
        Assert.Contains("OwnershipUnknown", panel.NoticeText);
    }

    [AvaloniaFact]
    public async Task EjectClickUsesTheLaneSlot()
    {
        DiskSlot? seen = null;
        var lane = Lane();
        var empty = new DriveView(null, false, false, 0, false, 0);
        lane.DispatchEject = (slot, _) =>
        {
            seen = slot;
            return Task.FromResult(new MediaReply(1, empty, empty));
        };
        var panel = new MediaHomeListPanel { MountLane = lane };
        panel.Show(new MediaHomeListing(
            [Row("floppy-dsk", "GAME.DSK", true)],
            ImmutableArray<MediaHomeState>.Empty,
            false));
        var ejectB = Menu(Host(panel, "GAME.DSK"))[3];
        Assert.Equal("Eject B:", ejectB.Header);
        await Click(ejectB, panel);
        Assert.Equal(DiskSlot.B, seen);
        Assert.Contains("Drive B: ejected", panel.NoticeText);
    }

    private static List<MenuItem> Menu(MediaHomeRowView host)
        => ((ContextMenu)host.ContextMenu!).Items.OfType<MenuItem>().ToList();

    private static MediaHomeMountLane Lane()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "media-home-ui-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var catalog = DiskLibraryCatalog.Load(Path.Combine(root, "disk-library.json"));
        return new MediaHomeMountLane(catalog, Path.GetDirectoryName(catalog.CatalogPath)!, new DiskLibraryMount(catalog, Path.GetDirectoryName(catalog.CatalogPath)!));
    }
}
