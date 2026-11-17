using System.Collections.Immutable;
using Hc2000.Machine.Tape;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Media;

/// <summary>UI owns host pickers and persistence; only copied commands/replies cross the boundary.</summary>
public sealed class MediaPanel : StackPanel
{
    private MediaBridge? bridge;
    public Func<DiskSlot, Task<bool>>? TryWritebackEject { get; set; }
    // Host-held read admission seam: the host reserves and reads, the panel dispatches and forwards the
    // actual owner outcome; null identity means legacy read or refused outcome.
    // internal: they expose the internal admission identity (NativeImageFileId lives in HostShared);
    // only the app wiring and the test assembly assign them.
    internal Func<DiskSlot, string, CancellationToken, Task<DiskReadReservation.Reservation>>? DiskReadReserver { get; set; }
    internal Func<DiskReadReservation.Reservation, DiskSlot, MediaReply?, Task<DiskReadReservation.HostFileIdentity?>>? DiskReadCompleted { get; set; }
    internal Func<DiskSlot, bool>? DiskReadHeldFor { get; set; }
    internal Func<string, bool>? PathIsProtectedOriginal { get; set; }
    // O1: the slot's stored, validated descriptor (from the configured start or prestart). Raw-sector
    // admission is descriptor-driven and never inferred from a suffix or byte length.
    internal Func<DiskSlot, MediaImageDescriptor?>? StoredDescriptorFor { get; set; }
    internal Func<DiskSlot, string, ImmutableArray<byte>, MediaReply, DiskReadReservation.HostFileIdentity?, Task>? DiskMounted { get; set; }
    public Func<Task<string?>>? WritebackBeforeClose { get; set; }
    private bool busy;
    private Task? activePoll;
    private bool updating;
    private MediaReply? state;
    private HddReply? observedHdd;
    private bool hddFirstReadSent;
    private TapeVolumeView? observedTapeVolume;
    private readonly TextBlock notice = new() { Name = "MediaActionNotice", Text = "Media disconnected. TAP playback/instant-load unavailable: no tape device attached.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxLines = 2, TextTrimming = Avalonia.Media.TextTrimming.WordEllipsis };
    private readonly Button refresh = new() { Content = "Refresh media state" };
    private readonly CheckBox discard = new() { Content = "Allow discarding unsaved edits on insert/eject", IsChecked = false };
    private readonly Button[] insert = new Button[2], eject = new Button[2], save = new Button[2];
    private readonly MenuItem[] saveMenu = new MenuItem[2];
    private readonly CheckBox[] protect = new CheckBox[2];
    // R3 (frame amendment design-layout-pin-20261028): name, mount marker and status are
    // separate stacked TextBlocks in the row's text column; each wraps, none trims.
    private readonly TextBlock[] names = new TextBlock[2], markers = new TextBlock[2], statuses = new TextBlock[2];
    private ImmutableArray<byte> selectedTape;
    private string? selectedTapeName;
    private readonly TextBlock tapeStatus = new() { Text = "Tape runtime unavailable; instant load unavailable." };
    private readonly Button queueTape = new() { Content = "Queue selected TAP" };
    private readonly Button playTape = new() { Content = "Play" };
    private readonly Button stopTape = new() { Content = "Stop" };
    private readonly Button ejectTape = new() { Content = "Eject TAP" };
    private readonly TapeDeckPanel deckPanel = new();
    private readonly HddPanel hddPanel = new();
    private readonly WrapPanel legacyTapeControls = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel libraryRows = new() { Spacing = 3 };
    private readonly StackPanel tapeVolumeRows = new() { Spacing = 3 };
    private string? tapeVolumeActionError;
    private readonly Button refreshLibrary = new() { Content = "Refresh disk library list" };
    private ImmutableArray<InlineMediaRow> diskLibraryListed = [];
    public TapeDeckPanel Deck => deckPanel;
    public HddPanel Hdd => hddPanel;
    private readonly StackPanel additionalDriveRows = new() { Spacing = 8 };
    internal StackPanel DriveStrip { get; } = new() { Spacing = 8 };
    // The shell reparents DriveStrip into the rail; the legacy MediaPanel can be
    // detached inside its collapsed expander while these drive buttons remain live.
    internal TopLevel? PickerOwner => TopLevel.GetTopLevel(DriveStrip) ?? TopLevel.GetTopLevel(this);
    internal Explorer.DiskExplorerPanel Explorer { get; } = new();
    internal Explorer.TwoPaneCopyPanel TwoPane { get; } = new();
    internal Button OpenExplorerWorkspace { get; } = new() { Content = "Open Explorer workspace" };
    internal string? TwoPaneHomeOverride { get; set; }
    internal Func<DiskSlot, Task>? BrowseFiles { get; set; }
    internal Func<InlineMediaRow, Task>? BrowseLibraryFiles { get; set; }
    public string? Notice => notice.Text;
    internal Control NoticeElement => notice;
    public string? TapeStatus => tapeStatus.Text;

    // The shell reparents the notice line into the rail panel's header row, outside the scroll.
    internal Control TakeNotice()
    {
        Children.Remove(notice);
        return notice;
    }

    /// <summary>Canonical disk library catalog for the inline listing; null = not configured (rows say so).</summary>
    public Library.DiskLibraryCatalog? DiskLibrary { get; set; }
    public ImmutableArray<InlineMediaRow> DiskLibraryListed => diskLibraryListed;

    public MediaPanel()
    {
        Spacing = 6;
        hddPanel.Observed += reply => { observedHdd = reply; RenderAdditionalDrives(); };
        ToolTip.SetTip(notice, notice.Text);
        notice.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty) ToolTip.SetTip(notice, notice.Text);
        };
        Children.Add(new TextBlock { Text = "Disk and tape media", FontSize = 18 });
        Children.Add(DriveStrip);
        // R3: one drive row is a Grid "*,Auto" - the text column keeps at least 180 px, and the
        // button column holds at most three 26 px icon buttons (write protect and browse live in
        // the row's menu, the way the tape rows do).
        const string insertIcon = "M2,2 H11 L14,5 V14 H2 Z M8,6.5 V10 M6,8 L8,10 L10,8";
        const string ejectIcon = "M8,3.5 L11.5,8.5 H4.5 Z M2.5,11 H13.5 V13.5 H2.5 Z";
        const string saveIcon = "M8,2.5 V9 M5.8,6.8 L8,9 L10.2,6.8 M2.5,11.5 H13.5 V14 H2.5 Z";
        const string moreIcon = "M3,7 A1,1 0 1 0 3,9 A1,1 0 1 0 3,7 M8,7 A1,1 0 1 0 8,9 A1,1 0 1 0 8,7 M13,7 A1,1 0 1 0 13,9 A1,1 0 1 0 13,7";
        foreach (var slot in Enum.GetValues<DiskSlot>())
        {
            int index = (int)slot;
            names[index] = new() { Name = $"Drive{slot}Name", Text = $"{slot}: no disk", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            markers[index] = new() { Name = $"Drive{slot}Marker", Text = "disconnected", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } };
            statuses[index] = new() { Name = $"Drive{slot}Status", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } };
            var textColumn = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Top };
            textColumn.Children.Add(names[index]);
            textColumn.Children.Add(markers[index]);
            textColumn.Children.Add(statuses[index]);
            insert[index] = new() { Name = $"Drive{slot}Insert", Width = 26, Height = 26, Padding = new Thickness(5), Classes = { "ghostAction" }, Content = new PathIcon { Width = 16, Height = 16, Data = Avalonia.Media.Geometry.Parse(insertIcon) } };
            eject[index] = new() { Name = $"Drive{slot}Eject", Width = 26, Height = 26, Padding = new Thickness(5), Classes = { "ghostAction" }, Content = new PathIcon { Width = 16, Height = 16, Data = Avalonia.Media.Geometry.Parse(ejectIcon) } };
            save[index] = new() { Name = $"Drive{slot}Save", Width = 26, Height = 26, Padding = new Thickness(5), Classes = { "ghostAction" }, Content = new PathIcon { Width = 16, Height = 16, Data = Avalonia.Media.Geometry.Parse(saveIcon) } };
            var more = new Button { Name = $"Drive{slot}More", Width = 26, Height = 26, Padding = new Thickness(5), Classes = { "ghostAction" }, Content = new PathIcon { Width = 16, Height = 16, Data = Avalonia.Media.Geometry.Parse(moreIcon) } };
            Avalonia.Automation.AutomationProperties.SetName(insert[index], $"Insert disk in {slot}");
            Avalonia.Automation.AutomationProperties.SetName(eject[index], $"Eject disk from {slot}");
            Avalonia.Automation.AutomationProperties.SetName(more, $"More actions for drive {slot}");
            ToolTip.SetTip(more, $"More actions for drive {slot}.");
            protect[index] = new() { Content = "Write protect" };
            ToolTip.SetTip(protect[index], "Block guest disk writes. Changes are written back to the mounted disk image only when write-back is on and its safety checks pass.");
            ToolTip.SetTip(insert[index], "Choose an image for this drive. If a disk cannot be mounted, the message explains why.");
            ToolTip.SetTip(eject[index], "Eject the disk. Unsaved changes stay until you save or discard them.");
            ToolTip.SetTip(save[index], "Save the current changes as a copy of this disk.");
            insert[index].Click += async (_, _) => await RunAsync(async () =>
            {
                var raw = StoredDescriptorFor?.Invoke(slot)?.Kind is MediaImageKind.RawFloppy;
                if (await PickAsync(raw ? "Insert raw sector image" : "Insert DSK", raw ? "*.raw" : "*.dsk") is { } path) await InsertCoreAsync(path, slot);
            });
            eject[index].Click += async (_, _) => await RunAsync(async () =>
            {
                if (DiskReadHeldFor is not null && DiskReadHeldFor(slot)) throw new InvalidOperationException("Disk read in flight for this slot; wait for it to complete.");
                if (TryWritebackEject is not null && await TryWritebackEject(slot)) return;
                await RequestAsync(new(MediaAction.EjectDisk, slot, DiscardChanges: discard.IsChecked == true));
            });
            save[index].Click += async (_, _) => await RunAsync(async () =>
            {
                if (DiskReadHeldFor is not null && DiskReadHeldFor(slot)) throw new InvalidOperationException("Disk read in flight for this slot; wait for it to complete.");
                var provider = PickerOwner?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
                var target = await provider.SaveFilePickerAsync(new() { Title = $"Save {slot}: changes as EDSK", SuggestedFileName = $"drive-{slot}.dsk", DefaultExtension = "dsk",
                    FileTypeChoices = [new("DSK disk") { Patterns = ["*.dsk"] }] });
                if (target is null) return;
                using (target)
                {
                    var path = target.TryGetLocalPath() ?? throw new InvalidOperationException("Only local files are supported.");
                    if (PathIsProtectedOriginal is not null && PathIsProtectedOriginal(path))
                        throw new InvalidDataException("Refusing to save over a protected original disk; choose a different path.");
                    var reply = await RequestAsync(new(MediaAction.ExportDisk, slot));
                    await MediaFiles.SaveAsync(path, reply.Export);
                    notice.Text = "Saved a disk copy. The mounted disk still has changes; later edits need another save.";
                }
            });
            protect[index].IsCheckedChanged += async (_, _) =>
            {
                if (!updating) await RunAsync(() => RequestAsync(new(MediaAction.WriteProtect, slot, Value: protect[index].IsChecked == true)));
            };
            var buttonColumn = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
            buttonColumn.Children.Add(insert[index]);
            buttonColumn.Children.Add(eject[index]);
            buttonColumn.Children.Add(more);
            var row = new Grid { Name = $"Drive{slot}Row", ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(textColumn, 0);
            Grid.SetColumn(buttonColumn, 1);
            row.Children.Add(textColumn);
            row.Children.Add(buttonColumn);
            // Write protect and browse are in the row's menu, the way the tape rows do; the
            // rule sentence stays a tooltip.
            var browseItem = new MenuItem { Header = $"Browse {slot} files" };
            browseItem.Click += async (_, _) => { if (BrowseFiles is { } action) await action(slot); };
            saveMenu[index] = new MenuItem { Header = $"Save {slot} changes as a copy" };
            saveMenu[index].Click += (_, _) => save[index].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var menu = new ContextMenu();
            menu.Items.Add(protect[index]);
            menu.Items.Add(browseItem);
            menu.Items.Add(saveMenu[index]);
            row.ContextMenu = menu;
            more.Click += (_, _) => menu.Open(row);
            DriveStrip.Children.Add(row);
        }
        DriveStrip.Children.Add(additionalDriveRows);
        Children.Add(discard);
        refresh.Click += async (_, _) => await InspectAsync();
        Children.Add(refresh);
        var librarySection = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        librarySection.Children.Add(new TextBlock { Text = "Disk library", FontSize = 14, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        refreshLibrary.Click += async (_, _) => await RunAsync(() => { RefreshDiskLibraryList(); return Task.CompletedTask; });
        librarySection.Children.Add(refreshLibrary);
        librarySection.Children.Add(libraryRows);
        Children.Add(librarySection);
        var tapeVolumeSection = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        tapeVolumeSection.Children.Add(new TextBlock { Text = "Tape volumes (right-click a volume to mount it as a CP/M drive)", FontSize = 14, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        tapeVolumeSection.Children.Add(tapeVolumeRows);
        Children.Add(tapeVolumeSection);
        // Two-pane copy view: scanned disk home on the left, browsable image on the right.
        var explorerRow = new MediaExplorerLayout();
        Grid.SetColumn(TwoPane, 0);
        Grid.SetColumn(Explorer, 1);
        explorerRow.Children.Add(TwoPane);
        explorerRow.Children.Add(Explorer);
        Children.Add(explorerRow);
        Children.Add(tapeStatus);
        var tape = legacyTapeControls;
        var openTape = new Button { Content = "Open TAP" };
        openTape.Click += async (_, _) => await RunAsync(async () =>
        {
            if (await PickAsync("Select and validate TAP", "*.tap") is not { } path) return;
            await ValidateTapeCoreAsync(path);
        });
        tape.Children.Add(openTape);
        queueTape.Click += async (_, _) => await QueueSelectedTapeAsync();
        playTape.Click += async (_, _) => await PlayTapeAsync();
        stopTape.Click += async (_, _) => await StopTapeAsync();
        ejectTape.Click += async (_, _) => await EjectTapeAsync();
        tape.Children.Add(queueTape);
        tape.Children.Add(playTape);
        tape.Children.Add(stopTape);
        tape.Children.Add(ejectTape);
        tape.Children.Add(new CheckBox { Content = "Instant load", IsEnabled = false });
        Children.Add(tape); Children.Add(deckPanel); Children.Add(hddPanel);
        UpdateControls();
    }

    public void Attach(MediaBridge value)
    {
        if (bridge is not null) throw new InvalidOperationException("Media already attached.");
        bridge = value; state = null; hddFirstReadSent = false; deckPanel.Attach(RequestAsync);
        // Pin HDD replies to this owner; the HDD panel rejects stale bindings before publishing views.
        hddPanel.Attach(value.RequestAsync); UpdateControls();
        RefreshDiskLibraryList();
    }
    public void Disconnect()
    {
        bridge?.Disconnect();
        Unbind();
    }
    // A tab switch releases only the shared view, not the session-owned live bridge.
    public void Unbind()
    {
        bridge = null; state = null; activePoll = null; deckPanel.Disconnect(); hddPanel.Disconnect(); UpdateControls();
        observedHdd = null; observedTapeVolume = null;
        additionalDriveRows.Children.Clear();
        tapeVolumeActionError = null;
        RenderTapeVolumes(null);
        for (var i = 0; i < names.Length; i++)
        {
            names[i].Text = $"{(DiskSlot)i}: no disk";
            markers[i].Text = "disconnected";
            statuses[i].Text = "";
        }
        DiskReadReserver = null; DiskReadCompleted = null; DiskReadHeldFor = null; PathIsProtectedOriginal = null; StoredDescriptorFor = null;
        RefreshDiskLibraryList();
        notice.Text = "No machine connected.";
    }
    public Task InspectAsync() => RunAsync(() => RequestAsync(new(MediaAction.Inspect)));
    public Task PollAsync()
    {
        if (bridge is null || busy || bridge.HasPending || deckPanel.IsBusy || hddPanel.IsBusy) return Task.CompletedTask;
        // HDD views come only from an HDD request. The first poll after Attach reads the drives instead of the
        // media state (still one request per poll), so a started machine lists C:-F: without a manual Refresh.
        if (!hddFirstReadSent && hddPanel.AwaitsFirstInspection)
        {
            hddFirstReadSent = true;
            return activePoll = RunAsync(hddPanel.InspectFirstAsync);
        }
        return activePoll = InspectAsync();
    }
    public async Task<bool> CanCloseAsync()
    {
        if (bridge is null) return true;
        if (activePoll is { IsCompleted: false } poll)
        {
            try { await poll.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* CanCloseAsync performs its own authoritative Inspect below. */ }
        }
        if (bridge is null) return true;
        if (busy || bridge.HasPending || deckPanel.IsBusy || hddPanel.IsBusy)
        { notice.Text = "Finish the pending media action before closing."; return false; }
        try
        {
            if (WritebackBeforeClose is not null)
            {
                var writebackError = await WritebackBeforeClose().WaitAsync(TimeSpan.FromSeconds(30));
                if (writebackError is not null) { notice.Text = writebackError; return false; }
            }
            await RequestAsync(new(MediaAction.Inspect)).WaitAsync(TimeSpan.FromSeconds(5));
            if (hddPanel.RequiresRuntimeInspection)
                await hddPanel.InspectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (!hddPanel.CanCloseSafely)
            {
                notice.Text = "The hard disk has unsaved changes: save a copy or allow them to be discarded before closing. Cancel keeps the machine running.";
                return false;
            }
            if (deckPanel.CanCloseSafely) return true;
            notice.Text = "The cassette has unsaved changes: save a snapshot or copies of its sides, or allow the changes to be discarded before closing.";
        }
        catch (Exception ex) { notice.Text = "Close refused: " + ex.Message; }
        return false;
    }
    public Task InsertDiskAsync(string path, DiskSlot slot = DiskSlot.A) => RunAsync(() => InsertCoreAsync(path, slot));
    // Launch must not silently lose a disk to the periodic inspection. Wait for that
    // inspection only; other in-flight actions are refused rather than reported as success.
    internal async Task InsertLaunchDiskAsync(string path, DiskSlot slot)
    {
        var owner = bridge;
        if (activePoll is { IsCompleted: false } poll) await poll;
        if (owner is null || !ReferenceEquals(owner, bridge))
            throw new InvalidOperationException("Media runtime disconnected.");
        if (busy) throw new InvalidOperationException("Media action in flight; wait for it to complete.");
        busy = true; UpdateControls();
        try { await InsertCoreAsync(path, slot); }
        finally { busy = false; UpdateControls(); }
    }
    /// <summary>Send one command through the bridge and refuse with the lane's own wording: the reply
    /// error comes back as the thrown message, so a caller (the tape launch apply) reports it as a
    /// status line. The buttons use <see cref="RunAsync"/>, which surfaces the SAME wording in the
    /// notice — one shared lane either way.</summary>
    public async Task<MediaReply> SendAsync(MediaCommand command)
    {
        if (busy) throw new InvalidOperationException("Media action in flight; wait for it to complete.");
        busy = true; UpdateControls();
        try { return await RequestAsync(command); }
        finally { busy = false; UpdateControls(); }
    }

    /// <summary>Launch continuations may run off-thread; dispatch each command through the same
    /// UI-owned send lane as the buttons.</summary>
    public Task<MediaReply> UIThreadSendAsync(MediaCommand command)
        => Dispatcher.UIThread.InvokeAsync(() => SendAsync(command));

    /// <summary>Rebuild the inline disk library listing for the current provider and bound machine.</summary>
    public void RefreshDiskLibraryList()
    {
        diskLibraryListed = InlineMediaListing.Floppies(DiskLibrary?.Entries);
        libraryRows.Children.Clear();
        foreach (var row in diskLibraryListed) libraryRows.Children.Add(BuildDiskRow(row));
    }

    private Control BuildDiskRow(InlineMediaRow row)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        item.Children.Add(new TextBlock { Text = RowText(row), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (row.Selectable)
        {
            // Freeze this row's selected-session callback, not the shared panel's later binding.
            var browseFiles = BrowseLibraryFiles;
            var browse = new Button { Content = "Browse files", IsEnabled = browseFiles is not null };
            browse.Click += async (_, _) => { if (browseFiles is not null) await browseFiles(row); };
            item.Children.Add(browse);
            foreach (var slot in new[] { DiskSlot.A, DiskSlot.B })
            {
                var button = new Button { Content = $"Insert {slot}", Focusable = false, IsEnabled = bridge is not null && !busy };
                string path = row.Path!;
                button.Click += async (_, _) => await RunAsync(async () =>
                {
                    if (InlineMediaListing.RevalidateDiskHandle(DiskLibrary?.Entries, row.Handle, path) is { } refusal)
                        throw new InvalidOperationException(refusal);
                    await InsertCoreAsync(path, slot);
                });
                item.Children.Add(button);
            }
        }
        return item;
    }

    internal static string RowText(InlineMediaRow row) =>
        row.Name == "—" && row.Reason == row.Detail
            ? row.Detail
            : $"{row.Name} · {row.Detail}{(row.Status == InlineMediaStatus.Ready ? "" : $" · {row.Status}{(row.Reason is { } reason ? $" ({reason})" : "")}")}";

    public Task ValidateTapeAsync(string path) => RunAsync(() => ValidateTapeCoreAsync(path));
    public Task QueueSelectedTapeAsync() => RunAsync(() =>
    {
        if (selectedTape.IsDefault) throw new InvalidOperationException("Select a valid TAP first.");
        return RequestAsync(new(MediaAction.QueueTape, Bytes: selectedTape, Name: selectedTapeName));
    });
    public Task PlayTapeAsync() => RunAsync(() => RequestAsync(new(MediaAction.PlayTape)));
    public Task StopTapeAsync() => RunAsync(() => RequestAsync(new(MediaAction.StopTape)));
    public Task EjectTapeAsync() => RunAsync(() => RequestAsync(new(MediaAction.EjectTape)));

    private async Task ValidateTapeCoreAsync(string path)
    {
        selectedTape = default; selectedTapeName = null;
        var bytes = await MediaFiles.ReadAsync(path, MediaFiles.MaxTapeBytes);
        await Task.Run(() => Tap.Load(bytes.AsSpan()));
        selectedTape = bytes; selectedTapeName = Path.GetFileName(path);
        notice.Text = $"TAP validated: {selectedTapeName}. Not loaded into a machine: use Queue selected TAP when a transport is attached. Instant load unavailable.";
    }

    private async Task InsertCoreAsync(string path, DiskSlot slot)
    {
        // O1: admission is descriptor-driven. A slot configured for raw sector images admits its stored
        // descriptor's exact geometry and no suffix; every other slot keeps the legacy .dsk gate.
        MediaImageDescriptor? descriptor = StoredDescriptorFor?.Invoke(slot);
        if (descriptor?.Kind is MediaImageKind.RawFloppy)
        {
            // Exact-length and geometry validation happens in the adapter against the stored descriptor;
            // the host reserver separately revalidates canonical identity before opening.
            if (!File.Exists(path)) throw new InvalidDataException("Pick a local regular raw sector image for this slot.");
        }
        else
        {
            descriptor = null;
            if (!string.Equals(Path.GetExtension(path), ".dsk", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Drop a single .DSK file to insert into A:.");
        }
        if (bridge is null) throw new InvalidOperationException("Media runtime disconnected.");
        if (DiskReadReserver is not null)
        {
            // Host-held read: reserve and read in the host (the cold-read gate stays held by the
            // reservation until the owner outcome or a named drain), dispatch those exact bytes, and
            // forward the actual owner outcome, which acknowledges the reservation by its identity.
            var reservation = await DiskReadReserver(slot, path, CancellationToken.None);
            try
            {
                reservation.MarkDispatched();
                var source = bridge;
                var reply = await source.RequestAsync(new(MediaAction.InsertDisk, slot, reservation.Bytes, Path.GetFileName(path), Descriptor: descriptor, DiscardChanges: discard.IsChecked == true));
                if (!ReferenceEquals(source, bridge)) throw new InvalidOperationException("Stale media runtime reply ignored.");
                ApplyReplyState(reply);
                DiskReadReservation.HostFileIdentity? identity = null;
                if (DiskReadCompleted is not null) identity = await DiskReadCompleted(reservation, slot, reply);
                if (DiskMounted is not null) await DiskMounted(slot, path, reservation.Bytes, reply, identity);
                if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
                notice.Text = "Media updated. Save changes keeps a copy; files are not changed automatically.";
                return;
            }
            catch
            {
                if (DiskReadCompleted is not null) await DiskReadCompleted(reservation, slot, null);
                throw;
            }
        }
        var bytes = await MediaFiles.ReadAsync(path, MediaFiles.MaxDiskBytes);
        var mounted = await RequestAsync(new(MediaAction.InsertDisk, slot, bytes, Path.GetFileName(path), Descriptor: descriptor, DiscardChanges: discard.IsChecked == true));
        if (DiskMounted is not null) await DiskMounted(slot, path, bytes, mounted, null);
    }

    private async Task<MediaReply> RequestAsync(MediaCommand command)
    {
        var source = bridge ?? throw new InvalidOperationException("Media runtime disconnected.");
        var reply = await source.RequestAsync(command);
        if (!ReferenceEquals(source, bridge)) throw new InvalidOperationException("Stale media runtime reply ignored.");
        ApplyReplyState(reply);
        if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
        notice.Text = "Media updated. Save changes keeps a copy; files are not changed automatically.";
        return reply;
    }

    // State, panels and labels only; no throw, so the reserved path can run its outcome hook before a refused reply throws.
    private void ApplyReplyState(MediaReply reply)
    {
        if (!SameTapeVolumes(state?.TapeVolume, reply.TapeVolume))
            RenderTapeVolumes(reply.TapeVolume);
        state = reply;
        deckPanel.Observe(reply.Tape);
        hddPanel.Observe(reply.Hdd);
        observedTapeVolume = reply.TapeVolume;
        RenderAdditionalDrives();
        updating = true;
        try
        {
            var views = new[] { reply.A, reply.B };
            for (int i = 0; i < 2; i++)
            {
                var view = views[i];
                var slot = (DiskSlot)i;
                names[i].Text = $"{slot}: {(view.Present ? Path.GetFileName(view.Name ?? "mounted disk") : "no disk")}";
                markers[i].Text = view.HeldBy is { } holder ? $"held by {holder}"
                    : view.Present ? (view.WriteProtected ? "mounted, write protected" : "mounted") : "empty";
                statuses[i].Text = $"{view.DirtySectors} unsaved sectors{(view.Busy ? " · busy" : "")}";
                protect[i].IsChecked = view.WriteProtected;
            }
        }
        finally { updating = false; }
    }

    private void RenderAdditionalDrives()
    {
        additionalDriveRows.Children.Clear();
        const string browseIcon = "M2.5,3.5 H13.5 V12.5 H2.5 Z M5.5,9.5 L7.5,7.5 L9.5,9.5 M7.5,4.5 V9";
        if (observedHdd is { } hdd)
            foreach (var drive in hdd.Drives)
            {
                var letter = (char)('A' + drive.Drive);
                // Same pinned row shape as the floppy rows: Grid "*,Auto", stacked wrapped
                // name/marker/status, one 26 px icon button.
                var name = new TextBlock { Name = $"Drive{letter}Name", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = $"{letter}: {(drive.Present ? Path.GetFileName(drive.Name ?? "hard drive") : "no disk")}" };
                var marker = new TextBlock { Name = $"Drive{letter}Marker", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" }, Text = "hard drive" };
                var status = new TextBlock { Name = $"Drive{letter}Status", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" }, Text = drive.Dirty ? "unsaved changes" : "" };
                var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Top };
                text.Children.Add(name);
                text.Children.Add(marker);
                text.Children.Add(status);
                var row = new Grid { Name = $"Drive{letter}Row", ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(text, 0);
                row.Children.Add(text);
                if (drive.Present)
                {
                    var slot = drive.Drive;
                    var browse = new Button { Name = $"Drive{letter}Browse", Width = 26, Height = 26, Padding = new Thickness(5), Classes = { "ghostAction" }, Content = new PathIcon { Width = 16, Height = 16, Data = Avalonia.Media.Geometry.Parse(browseIcon) } };
                    ToolTip.SetTip(browse, $"Browse {letter} files on the hard drive.");
                    browse.Click += async (_, _) => { if (hddPanel.BrowseFiles is { } action) await action(slot); };
                    var buttons = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
                    buttons.Children.Add(browse);
                    Grid.SetColumn(buttons, 1);
                    row.Children.Add(buttons);
                }
                additionalDriveRows.Children.Add(row);
            }
        if (observedTapeVolume is { Mounted: true } volume)
        {
            const string tapeIcon = "M8,3.5 A1.5,1.5 0 1 0 8,6.5 A1.5,1.5 0 1 0 8,3.5 Z M5.5,7 H10.5 V11 A2.5,2.5 0 0 1 5.5,11 Z M3,5.5 L5.5,7 L4.5,8 L2,6.5 Z M3,9.5 L2,11.5 L4.5,12 L5.5,10.5 Z M13,5.5 L11,7 L11.5,8 L14,6.5 Z M13,9.5 L14,11.5 L11.5,12 L10.5,10.5 Z";
            var label = new TextBlock { Name = $"TapeDrive{volume.Letter}Status", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = $"{volume.Letter}: {Path.GetFileName(volume.Name ?? "tape volume")} · {volume.ImageBytes / 1024} KiB{(volume.Dirty ? " · unsaved changes" : "") }" };
            var unmount = new Button { Name = "TapeDriveUnmount", Width = 26, Height = 26, Padding = new Thickness(5), Classes = { "ghostAction" }, Content = new PathIcon { Width = 16, Height = 16, Data = Avalonia.Media.Geometry.Parse(tapeIcon) } };
            ToolTip.SetTip(unmount, $"Unmount {volume.Letter}: and save to tape.");
            unmount.Click += async (_, _) => await RunAsync(() => RequestAsync(new(MediaAction.UnmountTapeVolume)));
            var row = new Grid { Name = $"TapeDrive{volume.Letter}Row", ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(unmount, 1);
            row.Children.Add(label);
            row.Children.Add(unmount);
            additionalDriveRows.Children.Add(row);
        }
    }

    private static bool SameTapeVolumes(TapeVolumeView? previous, TapeVolumeView? current)
    {
        if (previous is null || current is null) return previous is null && current is null;
        if (previous with { Volumes = default } != current with { Volumes = default }) return false;
        if (previous.Volumes.IsDefault || current.Volumes.IsDefault)
            return previous.Volumes.IsDefault && current.Volumes.IsDefault;
        return previous.Volumes.SequenceEqual(current.Volumes);
    }

    /// <summary>One row per tape catalog volume; the right-click menu is the only mount lane.
    /// Mount binds the first free CP/M drive letter; unmount commits (writes the tape); discard leaves it unchanged.</summary>
    private void RenderTapeVolumes(TapeVolumeView? view)
    {
        tapeVolumeRows.Children.Clear();
        if (view is null) return;
        // A refused catalog may have a default array (for example, with no cassette).
        var volumes = view.Volumes.IsDefault ? [] : view.Volumes;
        foreach (var volume in volumes)
        {
            var kibi = volume.ImageBytes / 1024;
            var text = volume.Mounted
                ? $"{view.Letter}: {volume.Name} (mounted{(view.Dirty ? ", dirty" : "")}) · {kibi} KiB"
                : $"{volume.Name} · {kibi} KiB{(volume.WriteProtected ? " · write protected" : "")}";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            // Keep the whole row line in the rendered hit-test scene, not just the volume text:
            // the right-click menu must open anywhere on the row, the way the 0.1.193 floppy-row
            // pointer repair keeps scanned media rows hittable (unpainted padding is a miss).
            row.Background = Avalonia.Media.Brushes.Transparent;
            row.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var name = volume.Name;
            var menu = new ContextMenu();
            if (volume.Mounted)
            {
                var unmount = new MenuItem { Header = $"Unmount {view.Letter}: {name} (commit — writes the tape)" };
                unmount.Click += async (_, _) => await RunAsync(() => RunTapeVolumeAsync(RequestAsync(new MediaCommand(MediaAction.UnmountTapeVolume))));
                var discardTape = new MenuItem { Header = $"Discard {view.Letter}: {name} (leaves the tape unchanged)" };
                discardTape.Click += async (_, _) => await RunAsync(() => RunTapeVolumeAsync(RequestAsync(new MediaCommand(MediaAction.DiscardTapeVolume))));
                menu.Items.Add(unmount);
                menu.Items.Add(discardTape);
            }
            else
            {
                var mount = new MenuItem { Header = $"Mount {name} as a CP/M drive" };
                mount.Click += async (_, _) => await RunAsync(() => RunTapeVolumeAsync(RequestAsync(new MediaCommand(MediaAction.MountTapeVolume, Name: name))));
                menu.Items.Add(mount);
            }
            row.ContextMenu = menu;
            tapeVolumeRows.Children.Add(row);
        }
        if (view.ListError is not null)
            tapeVolumeRows.Children.Add(new TextBlock { Text = $"Tape volume list refused: {view.ListError}" });
        if (tapeVolumeActionError is not null)
            tapeVolumeRows.Children.Add(new TextBlock { Text = $"Tape volume action refused: {tapeVolumeActionError}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
    }

    /// <summary>The mount lane runs the command and, on a refusal, keeps the reason at the volume rows
    /// (the rail's global notice sits below the fold and can clip in the fixed 340 px rail).</summary>
    private async Task RunTapeVolumeAsync(Task action)
    {
        tapeVolumeActionError = null;
        try { await action; }
        catch (Exception ex)
        {
            tapeVolumeActionError = ex.Message;
            // A refused command leaves the volume view unchanged, so the poll's
            // same-view guard would never re-render the rows: render the refusal here.
            if (state?.TapeVolume is { } view) RenderTapeVolumes(view);
            throw;
        }
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (Exception ex) { notice.Text = "Media action failed: " + ex.Message; }
        finally { busy = false; UpdateControls(); }
    }
    private void UpdateControls()
    {
        for (int i = 0; i < 2; i++)
        {
            bool connected = bridge is not null && !busy;
            var view = i == 0 ? state?.A : state?.B;
            insert[i].IsEnabled = connected;
            eject[i].IsEnabled = connected && view?.Present == true;
            save[i].IsEnabled = connected && view?.Present == true;
            saveMenu[i].IsEnabled = save[i].IsEnabled;
            protect[i].IsEnabled = connected;
        }
        refresh.IsEnabled = discard.IsEnabled = bridge is not null && !busy;
        var tape = bridge is null ? null : state?.Tape;
        legacyTapeControls.IsVisible = tape?.Deck is null;
        bool available = tape is not null && !busy;
        queueTape.IsEnabled = available && !selectedTape.IsDefault;
        playTape.IsEnabled = available && tape!.Cursor.State == TapeState.Stopped;
        stopTape.IsEnabled = available && tape!.Cursor.State == TapeState.Playing;
        ejectTape.IsEnabled = available && tape!.Cursor.State != TapeState.Unloaded;
        tapeStatus.Text = tape is null ? "Tape runtime unavailable; instant load unavailable." :
            $"Tape last boundary: {tape.Cursor.State} · {tape.Name ?? "empty"} · block {tape.Cursor.BlockIndex} · media #{tape.Cursor.MediaIdentity} · generation {tape.Cursor.Generation}. Instant load unavailable.";
    }
    internal static Avalonia.Platform.Storage.FilePickerOpenOptions MediaPickerOptions(string title, string pattern)
    {
        // Native Linux filters can be case-sensitive. Match the scanner's extension policy
        // with ordinary globs (including mixed case), without relying on platform-specific
        // bracket syntax or admitting unrelated files through an all-files wildcard.
        var patterns = new List<string> { string.Empty };
        foreach (var c in pattern)
            patterns = patterns.SelectMany(prefix => char.IsAsciiLetter(c)
                ? new[] { prefix + char.ToLowerInvariant(c), prefix + char.ToUpperInvariant(c) }
                : new[] { prefix + c }).ToList();
        return new()
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new("Media") { Patterns = patterns }]
        };
    }

    private async Task<string?> PickAsync(string title, string pattern)
    {
        var provider = PickerOwner?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
        var files = await provider.OpenFilePickerAsync(MediaPickerOptions(title, pattern));
        try { return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Only local files are supported."); }
        finally { foreach (var file in files) file.Dispose(); }
    }
}
