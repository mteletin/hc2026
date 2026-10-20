using System.Collections.Immutable;
using Hc2000.Machine.Tape;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
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
    private readonly TextBlock notice = new() { Text = "Media disconnected. TAP playback/instant-load unavailable: no tape device attached.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button refresh = new() { Content = "Refresh media state" };
    private readonly CheckBox discard = new() { Content = "Allow discarding unsaved edits on insert/eject", IsChecked = false };
    private readonly Button[] insert = new Button[2], eject = new Button[2], save = new Button[2];
    private readonly CheckBox[] protect = new CheckBox[2];
    private readonly TextBlock[] labels = new TextBlock[2];
    private ImmutableArray<byte> selectedTape;
    private string? selectedTapeName;
    private readonly TextBlock tapeStatus = new() { Text = "Tape runtime unavailable; instant load unavailable." };
    private readonly Button queueTape = new() { Content = "Queue selected TAP" };
    private readonly Button playTape = new() { Content = "Play" };
    private readonly Button stopTape = new() { Content = "Stop" };
    private readonly Button ejectTape = new() { Content = "Eject TAP" };
    private readonly TapeDeckPanel deckPanel = new();
    private readonly HddPanel hddPanel = new();
    private readonly StackPanel legacyTapeControls = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel libraryRows = new() { Spacing = 3 };
    private readonly StackPanel tapeVolumeRows = new() { Spacing = 3 };
    private readonly Button refreshLibrary = new() { Content = "Refresh disk library list" };
    private ImmutableArray<InlineMediaRow> diskLibraryListed = [];
    public TapeDeckPanel Deck => deckPanel;
    public HddPanel Hdd => hddPanel;
    internal Explorer.DiskExplorerPanel Explorer { get; } = new();
    internal Explorer.TwoPaneCopyPanel TwoPane { get; } = new();
    internal string? TwoPaneHomeOverride { get; set; }
    internal Func<DiskSlot, Task>? BrowseFiles { get; set; }
    internal Func<InlineMediaRow, Task>? BrowseLibraryFiles { get; set; }
    public string? Notice => notice.Text;
    public string? TapeStatus => tapeStatus.Text;

    /// <summary>Canonical disk library catalog for the inline listing; null = not configured (rows say so).</summary>
    public Library.DiskLibraryCatalog? DiskLibrary { get; set; }
    public ImmutableArray<InlineMediaRow> DiskLibraryListed => diskLibraryListed;

    public MediaPanel()
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Disk and tape media", FontSize = 18 });
        foreach (var slot in Enum.GetValues<DiskSlot>())
        {
            int index = (int)slot;
            labels[index] = new() { Text = $"{slot}: disconnected" };
            Children.Add(labels[index]);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            insert[index] = new() { Content = "Insert DSK…" };
            eject[index] = new() { Content = "Eject" };
            save[index] = new() { Content = "Save changes…" };
            protect[index] = new() { Content = "Write protect" };
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
                var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
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
                    notice.Text = "Saved boundary-captured EDSK copy. Live dirty markers remain; later edits require another save.";
                }
            });
            protect[index].IsCheckedChanged += async (_, _) =>
            {
                if (!updating) await RunAsync(() => RequestAsync(new(MediaAction.WriteProtect, slot, Value: protect[index].IsChecked == true)));
            };
            var browse = new Button { Content = $"Browse {slot} files" };
            browse.Click += async (_, _) => { if (BrowseFiles is { } action) await action(slot); };
            row.Children.Add(insert[index]); row.Children.Add(eject[index]); row.Children.Add(protect[index]); row.Children.Add(save[index]); row.Children.Add(browse);
            Children.Add(row);
        }
        Children.Add(discard);
        refresh.Click += async (_, _) => await InspectAsync();
        Children.Add(refresh);
        var librarySection = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        librarySection.Children.Add(new TextBlock { Text = "Disk library (inline listing; selection uses the existing guarded insert)", FontSize = 14 });
        refreshLibrary.Click += async (_, _) => await RunAsync(() => { RefreshDiskLibraryList(); return Task.CompletedTask; });
        librarySection.Children.Add(refreshLibrary);
        librarySection.Children.Add(libraryRows);
        Children.Add(librarySection);
        var tapeVolumeSection = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        tapeVolumeSection.Children.Add(new TextBlock { Text = "Tape volumes (right-click a volume to mount it as a CP/M drive)", FontSize = 14 });
        tapeVolumeSection.Children.Add(tapeVolumeRows);
        Children.Add(tapeVolumeSection);
        // Two-pane copy view: scanned disk home on the left, browsable image on the right.
        var explorerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        Grid.SetColumn(TwoPane, 0);
        Grid.SetColumn(Explorer, 1);
        explorerRow.Children.Add(TwoPane);
        explorerRow.Children.Add(Explorer);
        Children.Add(explorerRow);
        Children.Add(tapeStatus);
        var tape = legacyTapeControls;
        var openTape = new Button { Content = "Open TAP…" };
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
        Children.Add(tape); Children.Add(deckPanel); Children.Add(hddPanel); Children.Add(notice);
        UpdateControls();
    }

    public void Attach(MediaBridge value)
    {
        if (bridge is not null) throw new InvalidOperationException("Media already attached.");
        bridge = value; state = null; deckPanel.Attach(RequestAsync);
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
        DiskReadReserver = null; DiskReadCompleted = null; DiskReadHeldFor = null; PathIsProtectedOriginal = null; StoredDescriptorFor = null;
        RefreshDiskLibraryList();
        notice.Text = "Media runtime disconnected.";
    }
    public Task InspectAsync() => RunAsync(() => RequestAsync(new(MediaAction.Inspect)));
    public Task PollAsync() => bridge is null || busy || bridge.HasPending || deckPanel.IsBusy || hddPanel.IsBusy
        ? Task.CompletedTask : (activePoll = InspectAsync());
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
                notice.Text = "Dirty HDD retained: save a copy or explicitly allow HDD discard before closing. Cancel keeps the running machine.";
                return false;
            }
            if (deckPanel.CanCloseSafely) return true;
            notice.Text = "Dirty cassette retained: save HCS / exact side copies, or explicitly allow discard before closing.";
        }
        catch (Exception ex) { notice.Text = "Close refused: " + ex.Message; }
        return false;
    }
    public Task InsertDiskAsync(string path, DiskSlot slot = DiskSlot.A) => RunAsync(() => InsertCoreAsync(path, slot));

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
        $"{row.Name} · {row.Detail}{(row.Status == InlineMediaStatus.Ready ? "" : $" · {row.Status}{(row.Reason is { } reason ? $" ({reason})" : "")}")}";

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
                notice.Text = "Media command applied at an emulation boundary. Save changes exports a copy; no automatic writes.";
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
        notice.Text = "Media command applied at an emulation boundary. Save changes exports a copy; no automatic writes.";
        return reply;
    }

    // State, panels and labels only; no throw, so the reserved path can run its outcome hook before a refused reply throws.
    private void ApplyReplyState(MediaReply reply)
    {
        state = reply;
        deckPanel.Observe(reply.Tape);
        hddPanel.Observe(reply.Hdd);
        RenderTapeVolumes(reply.TapeVolume);
        updating = true;
        try
        {
            var views = new[] { reply.A, reply.B };
            for (int i = 0; i < 2; i++)
            {
                var view = views[i];
                labels[i].Text = $"{(DiskSlot)i}: last boundary #{view.Generation} · {(view.Present ? view.Name ?? "mounted disk" : "empty")} · dirty sectors {view.DirtySectors}{(view.Busy ? " · busy" : "")}";
                protect[i].IsChecked = view.WriteProtected;
            }
        }
        finally { updating = false; }
    }

    /// <summary>One row per tape catalog volume; the right-click menu is the only mount lane.
    /// Mount binds the first free CP/M drive letter; unmount commits (writes the tape); discard leaves it unchanged.</summary>
    private void RenderTapeVolumes(TapeVolumeView? view)
    {
        tapeVolumeRows.Children.Clear();
        if (view is null) return;
        foreach (var volume in view.Volumes)
        {
            var kibi = volume.ImageBytes / 1024;
            var text = volume.Mounted
                ? $"{view.Letter}: {volume.Name} (mounted{(view.Dirty ? ", dirty" : "")}) · {kibi} KiB"
                : $"{volume.Name} · {kibi} KiB{(volume.WriteProtected ? " · write protected" : "")}";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var name = volume.Name;
            var menu = new ContextMenu();
            if (volume.Mounted)
            {
                var unmount = new MenuItem { Header = $"Unmount {view.Letter}: {name} (commit — writes the tape)" };
                unmount.Click += async (_, _) => await RunAsync(() => RequestAsync(new MediaCommand(MediaAction.UnmountTapeVolume)));
                var discardTape = new MenuItem { Header = $"Discard {view.Letter}: {name} (leaves the tape unchanged)" };
                discardTape.Click += async (_, _) => await RunAsync(() => RequestAsync(new MediaCommand(MediaAction.DiscardTapeVolume)));
                menu.Items.Add(unmount);
                menu.Items.Add(discardTape);
            }
            else
            {
                var mount = new MenuItem { Header = $"Mount {name} as a CP/M drive" };
                mount.Click += async (_, _) => await RunAsync(() => RequestAsync(new MediaCommand(MediaAction.MountTapeVolume, Name: name)));
                menu.Items.Add(mount);
            }
            row.ContextMenu = menu;
            tapeVolumeRows.Children.Add(row);
        }
        if (view.ListError is not null)
            tapeVolumeRows.Children.Add(new TextBlock { Text = $"Tape volume list refused: {view.ListError}" });
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
    private async Task<string?> PickAsync(string title, string pattern)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
        var files = await provider.OpenFilePickerAsync(new() { Title = title, AllowMultiple = false,
            FileTypeFilter = [new("Media") { Patterns = [pattern] }] });
        try { return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Only local files are supported."); }
        finally { foreach (var file in files) file.Dispose(); }
    }
}
