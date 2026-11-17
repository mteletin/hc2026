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
/// to that host (the explorer pane owns the browse lane, not this list). Every row gets the
/// gesture, including unknown rows: the pane decides, and a row it cannot browse gets its
/// refusal in the pane, replacing any previous pane content.
/// </summary>
public sealed class MediaHomeRowView : Border
{
    public MediaHomeRowView(MediaHomeRow row)
    {
        Row = row;
        Tag = row;
        AutomationProperties.SetName(this, $"{row.Kind}: {row.Name}");
        Padding = new Thickness(4);
        // Keep the whole row in the rendered hit-test scene, including unpainted text/padding.
        Background = Brushes.Transparent;
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
/// or a row the scan could not recognize, carries no Mount action. Unknown rows carry no Mount
/// action at all. A recognizable hdd-raw or hdd-container row with a wired <see cref="HddAction"/>
/// carries the cold HDD profile menu (Select as C:–F: and Clear C:–F:); the seam owns the machine
/// draft and the commit, the panel only reports the clicked row and slot. No third mount mechanism:
/// the selection is recorded in the machine draft and takes effect at the next cold start, so the
/// running machine is never touched here.
/// The panel carries a user-reachable Scan button (keyboard and pointer alike) wired to
/// <see cref="RefreshAsync"/>, and runs the first scan automatically when the panel is shown, so it
/// never sits at "No scan yet." with homes configured - no second scan engine and no new list.
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
    private bool firstScanDone;
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

    /// <summary>The cold HDD profile commit (hc2000-hdd-context-mount-menu): the consumer owns the
    /// machine draft and the commit; the panel only reports the clicked row and slot.
    /// (row, slot "C".."F", select: true for Select-as / false for Clear) => the full notice text.
    /// Null: no HDD menu on hdd rows; a wired seam shows the menu on every selectable hdd-raw and
    /// hdd-container row.</summary>
    public Func<MediaHomeRow, string, bool, Task<string>>? HddAction { get; set; }

    /// <summary>Explicit flat512 registration/copy action, separate from direct raw Select-as.
    /// The consumer captures the draft before I/O and owns admission and saved-copy reuse.</summary>
    public Func<MediaHomeRow, string, Task<string>>? HddLibraryAction { get; set; }

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
        var scanButton = new Button { Content = "Scan", Focusable = true };
        scanButton.Click += (_, _) => { _ = RefreshAsync(); };
        previous.Click += (_, _) => { page = Math.Max(0, page - 1); ShowPage(); };
        next.Click += (_, _) => { page = page + 1; ShowPage(); };
        var pager = new WrapPanel { Orientation = Orientation.Horizontal };
        pager.Children.Add(scanButton);
        pager.Children.Add(previous);
        pager.Children.Add(pageLabel);
        pager.Children.Add(next);
        Children.Add(new TextBlock { Text = "Media homes", FontSize = 14, TextWrapping = TextWrapping.Wrap });
        Children.Add(pager);
        Children.Add(rows);
        Children.Add(notice);
        // The automatic first scan: the panel sits under the "Media homes · scanned images"
        // expander in MainWindow, so it is attached to the visual tree when that content is
        // realized (first open) and the list fills itself then - never sitting at "No scan
        // yet." with the scan source wired. Unwired: RefreshAsync refuses fail-closed with the
        // notice, as before.
        AttachedToVisualTree += (_, _) =>
        {
            lock (gate)
            {
                if (firstScanDone) return;
                firstScanDone = true;
            }
            _ = RefreshAsync();
        };
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
            // Actions originate on the UI thread; failures also update UI notice controls.
            await work;
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
            // A real home scan yields on file I/O. Keep the UI context for ShowPage;
            // a synchronously completed test scan otherwise hides this thread-affinity bug.
            var result = await Scan(ct);
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
        // The cold HDD profile menu (hc2000-hdd-context-mount-menu): a wired seam on a selectable
        // hdd-raw or hdd-container row. Select as C:–F: records the row into the machine draft for
        // the slot; the lane's refusals (hdd-raw rows, multi-volume containers) come back as the
        // notice text and the draft is untouched. Clear C:–F: removes the slot's entry. Effective
        // only at the next cold start; the running machine is never touched.
        if (row.Selectable && HddAction is not null && row.Kind is "hdd-container" or "hdd-raw")
        {
            var menu = new ContextMenu();
            if (row.Kind == "hdd-raw" && HddLibraryAction is not null)
                foreach (var slot in new[] { "C", "D", "E", "F" })
                {
                    var slotFor = slot;
                    var library = new MenuItem { Header = $"Library flat512 copy as {slotFor}: (cold)" };
                    library.Click += (_, _) =>
                    {
                        if (HddLibraryAction is { } action)
                            _ = RunAsync(async () => ShowNotice(await action(row, slotFor)), "library HDD action failed: ");
                    };
                    menu.Items.Add(library);
                }
            foreach (var slot in new[] { "C", "D", "E", "F" })
            {
                var slotFor = slot;
                var select = new MenuItem { Header = $"Select as {slotFor}:" };
                select.Click += (_, _) => QueueHdd(row, slotFor, select: true);
                menu.Items.Add(select);
            }
            foreach (var slot in new[] { "C", "D", "E", "F" })
            {
                var slotFor = slot;
                var clear = new MenuItem { Header = $"Clear {slotFor}:" };
                clear.Click += (_, _) => QueueHdd(row, slotFor, select: false);
                menu.Items.Add(clear);
            }
            host.ContextMenu = menu;
        }
        // The consumer's browse lane attaches here (double-click); the panel only reports the row.
        // Every row gets the gesture - recognized kinds open the existing reader, and a row the
        // pane cannot browse (snapshot, unknown, unreadable) gets its refusal in the pane, which
        // replaces any previous pane content instead of leaving a stale listing behind. This is
        // a display refusal, not an action on the file: the pane reads nothing from an unknown
        // row, and the Mount/HDD menus above stay absent on it (no mount, select or copy).
        host.DoubleTapped += (_, _) =>
        {
            if (BrowseRequested is { } browse)
                _ = RunAsync(() => browse(row)); // keep the UI context; the browse lane resumes its own continuations
        };
        return host;
    }

    private void QueueMount(MediaHomeRow row, DiskSlot slot)
    {
        if (MountLane is not { } lane) return; // no mount mechanism beyond the lane; refusal is closed
        _ = RunAsync(async () =>
        {
            // Keep the UI context for ShowNotice: the lane yields on real file I/O, and a
            // thread-pool continuation would throw on the notice control instead of showing
            // the lane's own refusal text (the same affinity the scan refresh documents).
            var result = await lane.MountFromScannedRowAsync(row.Path, slot);
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
            // Keep the UI context for ShowNotice; see QueueMount.
            var result = await lane.EjectAsync(slot);
            ShowNotice(result.Ejected
                ? $"Drive {slot}: ejected through the existing media path. {result.Message}"
                : $"Eject refused, nothing changed: {result.Message}");
        }, "eject action failed: ");
    }

    private void QueueHdd(MediaHomeRow row, string slot, bool select)
    {
        if (HddAction is not { } action) return; // no commit seam; the menu is closed
        _ = RunAsync(async () =>
            ShowNotice(await action(row, slot, select)), "hdd action failed: "); // keep the UI context for ShowNotice
    }

    public void ShowNotice(string message)
    {
        notice.Text = message;
        notice.IsVisible = true;
    }
}
