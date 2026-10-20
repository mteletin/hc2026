using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hc2000.App.Library;

namespace Hc2000.App.Media;

/// <summary>
/// One rendered scanned media home row: the row data plus the actions the contract allows on it.
/// The row host is <see cref="MediaHomeRowView"/>; a consumer attaches its own double-click browse
/// to that host (the explorer pane owns the browse lane, not this list).
/// </summary>
public sealed class MediaHomeRowView : Border
{
    public MediaHomeRowView(MediaHomeRow row)
    {
        Row = row;
        Tag = row;
        AutomationProperties.SetName(this, $"{row.Kind}: {row.Name}");
        Padding = new Thickness(4);
        Child = new TextBlock
        {
            Text = $"{row.Home} · {row.Kind} · {row.Name} · {row.Size:N0} B{DetailSuffix}",
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    private string DetailSuffix => string.IsNullOrWhiteSpace(Row.Detail) ? "" : " · " + Row.Detail;

    public MediaHomeRow Row { get; }

    /// <summary>Rows are located by visual descendant of this type; no duplicate XAML names.</summary>
}

/// <summary>
/// The scanned media home list (contract hc2000-media-home-scan-admission-v1, section 2; the
/// scan itself is <see cref="MediaHomeScan"/>). At most 12 rows per page. A scanned floppy row the
/// existing admission can act on (a <c>floppy-dsk</c> row with <see cref="MediaHomeRow.Selectable"/>
/// and a wired lane) gets right-click Mount as drive A: or B: and Eject through the accepted
/// <see cref="MediaHomeMountLane"/> only - no new mount mechanism. A row the admission refuses,
/// or a row the scan could not recognize, carries no Mount action. Unknown, hdd-raw and
/// hdd-container rows carry no Mount action at all.
/// </summary>
public sealed class MediaHomeListPanel : StackPanel
{
    /// <summary>Rows per page, per the item contract; the scan's per-home cap is a different budget.</summary>
    public const int RowsPerPage = 12;

    private readonly StackPanel rows = new() { Spacing = 2 };
    private readonly TextBlock pageLabel = new() { Text = "No scan yet." };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button previous, next;
    private MediaHomeListing listing = new(ImmutableArray<MediaHomeRow>.Empty, ImmutableArray<MediaHomeState>.Empty, false);
    private int page;
    private bool busy;
    private Task? pendingAction;
    private Task? lastAction;

    /// <summary>The accepted lane (catalog + working copy + one-action mount) the Mount/Eject items dispatch to.
    /// Null: no Mount/Eject actions are shown and a refresh keeps the list read-only. This is the only mount path.</summary>
    public MediaHomeMountLane? MountLane { get; set; }

    /// <summary>Refresh source: scan the configured homes. Null until wired; Refresh then refuses fail-closed.</summary>
    public Func<CancellationToken, Task<MediaHomeListing>>? Scan { get; set; }

    /// <summary>Raised when the consumer's browse lane should open a row (double-click host); the panel
    /// itself does not browse. Consumers attach to the row host's DoubleTapped and call this.</summary>
    public Func<MediaHomeRow, Task>? BrowseRequested { get; set; }

    /// <summary>The rows of the currently shown page, in order.</summary>
    public IReadOnlyList<MediaHomeRow> Shown { get; private set; } = [];

    /// <summary>The in-flight Mount/Eject/browse action, if any (tests await this); see also <see cref="LastAction"/>.
    /// The action task is stored before the first await, so a synchronous caller of the UI sees it.</summary>
    public Task? PendingAction { get { lock (gate) return pendingAction; } }

    /// <summary>The last completed Mount/Eject/browse action (or null); awaitable, so a test that reads it
    /// after the action already finished still gets the completed task.</summary>
    public Task? LastAction { get { lock (gate) return lastAction; } }

    /// <summary>The last notice text (mount/eject/refresh refusal or success); empty until shown.</summary>
    public string NoticeText => notice.Text ?? "";

    private readonly object gate = new();

    public MediaHomeListPanel()
    {
        Spacing = 6;
        previous = new Button { Content = "Previous page", Focusable = false, IsEnabled = false };
        next = new Button { Content = "Next page", Focusable = false, IsEnabled = false };
        previous.Click += (_, _) => { page = Math.Max(0, page - 1); ShowPage(); };
        next.Click += (_, _) => { page = page + 1; ShowPage(); };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pager.Children.Add(previous);
        pager.Children.Add(pageLabel);
        pager.Children.Add(next);
        Children.Add(new TextBlock { Text = "Media homes (scanned; at most 12 rows per page)", FontSize = 14 });
        Children.Add(pager);
        Children.Add(rows);
        Children.Add(notice);
    }

    /// <summary>Runs one action fail-closed: a refused result is reported, never thrown out of the UI.</summary>
    private async Task RunAsync(Func<Task> action, string? failurePrefix = null)
    {
        if (busy) return;
        busy = true;
        Task work;
        lock (gate)
        {
            work = action();
            pendingAction = work;
        }
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowNotice(failurePrefix is null ? ex.Message : failurePrefix + ex.Message);
        }
        finally
        {
            lock (gate)
            {
                busy = false;
                pendingAction = null;
                lastAction = work;
            }
        }
    }

    /// <summary>Refresh the listing from the configured homes; a refusal or failure shows the reason and
    /// keeps the last good page; never a fake empty list.</summary>
    public Task RefreshAsync(CancellationToken ct = default)
    {
        if (Scan is null)
        {
            ShowNotice("Refresh refused fail-closed: the configured homes scan is not wired.");
            return Task.CompletedTask;
        }
        return RunAsync(async () =>
        {
            var result = await Scan(ct).ConfigureAwait(false);
            listing = result;
            page = 0;
            ShowPage();
        }, "scan failed: ");
    }

    /// <summary>Replaces the listing (tests, or the wiring after an explicit scan).</summary>
    public void Show(MediaHomeListing value)
    {
        listing = value;
        page = 0;
        ShowPage();
    }

    private void ShowPage()
    {
        int total = listing.Rows.Length;
        var pageCount = total == 0 ? 1 : (total + RowsPerPage - 1) / RowsPerPage;
        page = Math.Clamp(page, 0, pageCount - 1);
        rows.Children.Clear();
        List<MediaHomeRow> slice = listing.Rows
            .Skip(page * RowsPerPage)
            .Take(RowsPerPage)
            .ToList();
        foreach (var row in slice)
            rows.Children.Add(BuildRow(row));
        Shown = slice;
        previous.IsEnabled = page > 0;
        next.IsEnabled = page + 1 < pageCount;
        pageLabel.Text = $"page {page + 1} of {pageCount} · {total} row(s){(listing.Truncated ? " (truncated)" : "")}";
        foreach (var home in listing.Homes)
        {
            if (!home.Available)
                pageLabel.Text += $"\n{home.Home}: unavailable — {home.Reason}";
        }
    }

    private MediaHomeRowView BuildRow(MediaHomeRow row)
    {
        var host = new MediaHomeRowView(row);
        if (row.Kind == "floppy-dsk" && row.Selectable && MountLane is not null)
        {
            var menu = new ContextMenu();
            var mountA = new MenuItem { Header = "Mount as drive A:" };
            var mountB = new MenuItem { Header = "Mount as drive B:" };
            var ejectA = new MenuItem { Header = "Eject A:" };
            var ejectB = new MenuItem { Header = "Eject B:" };
            mountA.Click += (_, _) => QueueMount(row, DiskSlot.A);
            mountB.Click += (_, _) => QueueMount(row, DiskSlot.B);
            ejectA.Click += (_, _) => QueueEject(DiskSlot.A);
            ejectB.Click += (_, _) => QueueEject(DiskSlot.B);
            menu.Items.Add(mountA);
            menu.Items.Add(mountB);
            menu.Items.Add(ejectA);
            menu.Items.Add(ejectB);
            host.ContextMenu = menu;
        }
        // The consumer's browse lane attaches here (double-click); the panel only reports the row.
        // Every recognized kind gets the gesture: the pane decides what the reader opens and
        // refuses in words where the kind is not a CP/M browse (snapshot).
        if (row.Selectable && row.Kind is "floppy-dsk" or "hdd-raw" or "hdd-container" or "tape" or "snapshot")
            host.DoubleTapped += (_, _) =>
            {
                if (BrowseRequested is { } browse)
                    _ = RunAsync(async () => await browse(row).ConfigureAwait(false));
            };
        return host;
    }

    private void QueueMount(MediaHomeRow row, DiskSlot slot)
    {
        if (MountLane is not { } lane) return; // no mount mechanism beyond the lane; refusal is closed
        _ = RunAsync(async () =>
        {
            var result = await lane.MountFromScannedRowAsync(row.Path, slot).ConfigureAwait(false);
            ShowNotice(result.Outcome switch
            {
                MediaHomeMountOutcome.Mounted => $"{row.Name} is mounted as drive {slot}: through the existing library lane. {result.Message}",
                _ => $"Mount refused, nothing mounted: {result.Message}"
            });
        }, "mount action failed: ");
    }

    private void QueueEject(DiskSlot slot)
    {
        if (MountLane is not { } lane) return;
        _ = RunAsync(async () =>
        {
            var result = await lane.EjectAsync(slot).ConfigureAwait(false);
            ShowNotice(result.Ejected
                ? $"Drive {slot}: ejected through the existing media path. {result.Message}"
                : $"Eject refused, nothing changed: {result.Message}");
        }, "eject action failed: ");
    }

    public void ShowNotice(string message)
    {
        notice.Text = message;
        notice.IsVisible = true;
    }
}
