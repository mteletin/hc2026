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
    public Func<DiskSlot, string, ImmutableArray<byte>, MediaReply, Task>? DiskMounted { get; set; }
    public Func<Task<string?>>? WritebackBeforeClose { get; set; }
    private bool busy;
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
    public TapeDeckPanel Deck => deckPanel;
    public HddPanel Hdd => hddPanel;
    public string? Notice => notice.Text;
    public string? TapeStatus => tapeStatus.Text;

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
                if (await PickAsync("Insert DSK", "*.dsk") is { } path) await InsertCoreAsync(path, slot);
            });
            eject[index].Click += async (_, _) => await RunAsync(async () =>
            {
                if (TryWritebackEject is not null && await TryWritebackEject(slot)) return;
                await RequestAsync(new(MediaAction.EjectDisk, slot, DiscardChanges: discard.IsChecked == true));
            });
            save[index].Click += async (_, _) => await RunAsync(async () =>
            {
                var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
                var target = await provider.SaveFilePickerAsync(new() { Title = $"Save {slot}: changes as EDSK", SuggestedFileName = $"drive-{slot}.dsk", DefaultExtension = "dsk",
                    FileTypeChoices = [new("DSK disk") { Patterns = ["*.dsk"] }] });
                if (target is null) return;
                using (target)
                {
                    var path = target.TryGetLocalPath() ?? throw new InvalidOperationException("Only local files are supported.");
                    var reply = await RequestAsync(new(MediaAction.ExportDisk, slot));
                    await MediaFiles.SaveAsync(path, reply.Export);
                    notice.Text = "Saved boundary-captured EDSK copy. Live dirty markers remain; later edits require another save.";
                }
            });
            protect[index].IsCheckedChanged += async (_, _) =>
            {
                if (!updating) await RunAsync(() => RequestAsync(new(MediaAction.WriteProtect, slot, Value: protect[index].IsChecked == true)));
            };
            row.Children.Add(insert[index]); row.Children.Add(eject[index]); row.Children.Add(protect[index]); row.Children.Add(save[index]);
            Children.Add(row);
        }
        Children.Add(discard);
        refresh.Click += async (_, _) => await InspectAsync();
        Children.Add(refresh);
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
        bridge = value; state = null; deckPanel.Attach(RequestAsync); hddPanel.Attach(RequestAsync); UpdateControls();
    }
    public void Disconnect()
    {
        bridge?.Disconnect(); bridge = null; state = null; deckPanel.Disconnect(); hddPanel.Disconnect(); UpdateControls();
        notice.Text = "Media runtime disconnected.";
    }
    public Task InspectAsync() => RunAsync(() => RequestAsync(new(MediaAction.Inspect)));
    public Task PollAsync() => bridge is null || busy || bridge.HasPending || deckPanel.IsBusy || hddPanel.IsBusy
        ? Task.CompletedTask : InspectAsync();
    public async Task<bool> CanCloseAsync()
    {
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
        if (!string.Equals(Path.GetExtension(path), ".dsk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Drop a single .DSK file to insert into A:.");
        if (bridge is null) throw new InvalidOperationException("Media runtime disconnected.");
        var bytes = await MediaFiles.ReadAsync(path, MediaFiles.MaxDiskBytes);
        var mounted = await RequestAsync(new(MediaAction.InsertDisk, slot, bytes, Path.GetFileName(path), DiscardChanges: discard.IsChecked == true));
        if (DiskMounted is not null) await DiskMounted(slot, path, bytes, mounted);
    }

    private async Task<MediaReply> RequestAsync(MediaCommand command)
    {
        var source = bridge ?? throw new InvalidOperationException("Media runtime disconnected.");
        var reply = await source.RequestAsync(command);
        if (!ReferenceEquals(source, bridge)) throw new InvalidOperationException("Stale media runtime reply ignored.");
        state = reply;
        deckPanel.Observe(reply.Tape);
        hddPanel.Observe(reply.Hdd);
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
        if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
        notice.Text = "Media command applied at an emulation boundary. Save changes exports a copy; no automatic writes.";
        return reply;
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
