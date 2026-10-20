using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Hc2000.App.Media;

/// <summary>Cold NEW-machine input preparation and copied runtime observations. Never owns a device.</summary>
public sealed class HddPanel : StackPanel
{
    private Func<MediaCommand, Task<MediaReply>>? request;
    private Func<ImmutableArray<HddInitialMedia>, Task>? startCold;
    private Func<HddColdVolume, byte, Task>? startVolume;
    private sealed record NamedSelection(string Path, string Name, byte Drive, Hc2000.Machine.Storage.HddVolumeIdentity Identity);
    private NamedSelection? namedSelection;
    private readonly TextBox volumePath = new() { Name = "ColdVolumePath", PlaceholderText = "Absolute container path" };
    private readonly TextBox volumeName = new() { Name = "ColdVolumeName", PlaceholderText = "Exact volume name, e.g. WORK.DSK" };
    private readonly ComboBox volumeDrive = new() { ItemsSource = new[] { "C:", "D:", "E:", "F:" }, SelectedIndex = 0 };
    private readonly Button prepareVolume = new() { Content = "Prepare named volume (read-only)" };
    private bool hddEnabled;
    private bool coldConfigurationConsumed;
    private readonly HddInitialMedia?[] selected = new HddInitialMedia?[4];
    private readonly TextBlock[] labels = new TextBlock[4];
    private readonly Button[] choose = new Button[4], save = new Button[4];
    private readonly CheckBox[] protection = new CheckBox[4];
    private readonly Button start = new() { Content = "Start NEW HDD machine (original BIOS)" };
    private readonly Button refresh = new() { Content = "Refresh HDD status" };
    private readonly CheckBox discardClose = new() { Content = "Explicitly discard unsaved HDD edits when closing" };
    private readonly TextBlock notice = new() { Text = "HDD profile off. Opt in with --hdd before starting a new window.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private ImmutableArray<HddDriveView> views = [];
    public bool IsBusy { get; private set; }
    public bool ColdConfigurationAvailable => startCold is not null;
    internal bool RequiresRuntimeInspection => hddEnabled;
    public bool CanCloseSafely => views.All(view => !view.Dirty) || discardClose.IsChecked == true;
    public string? Notice => notice.Text;

    public HddPanel()
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = "HDD C:–F: (opt-in, host session only)", FontSize = 18 });
        Children.Add(new TextBlock { Text = HddMediaAdapter.SwitchUnavailable, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        for (int index = 0; index < 4; index++)
        {
            byte drive = (byte)(index + 2);
            labels[index] = new() { Text = $"{(char)('C' + index)}: profile off" };
            choose[index] = new() { Content = "Prepare raw image…" };
            save[index] = new() { Content = "Save copy…" };
            protection[index] = new() { Content = "Write-protected at cold attach" };
            choose[index].Click += async (_, _) => await RunAsync(async () =>
            {
                var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
                var files = await provider.OpenFilePickerAsync(new() { Title = "Prepare exact 8 MiB raw HDD", AllowMultiple = false });
                try
                {
                    if (files.Count != 0) await PrepareImageAsync(files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Local images only."), drive);
                }
                finally { foreach (var file in files) file.Dispose(); }
            });
            save[index].Click += async (_, _) => await RunAsync(async () =>
            {
                var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
                using var file = await provider.SaveFilePickerAsync(new() { Title = "Save raw HDD copy", SuggestedFileName = $"drive-{(char)('A' + drive)}.raw", DefaultExtension = "raw" });
                if (file is not null) await SaveCopyAsync(file.TryGetLocalPath() ?? throw new InvalidOperationException("Local output only."), drive);
            });
            Children.Add(labels[index]);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(choose[index]); row.Children.Add(protection[index]); row.Children.Add(save[index]);
            Children.Add(row);
        }
        Children.Add(new TextBlock { Text = "One named container volume, read-only, on a NEW machine only (cannot mix with raw slots).", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Children.Add(volumePath); Children.Add(volumeName); Children.Add(volumeDrive); Children.Add(prepareVolume);
        prepareVolume.Click += async (_, _) => await RunAsync(() => PrepareVolumeAsync(volumePath.Text ?? "", volumeName.Text ?? "", (byte)(volumeDrive.SelectedIndex + 2)));
        start.Click += async (_, _) => await RunAsync(StartColdAsync);
        refresh.Click += async (_, _) => await RunAsync(InspectAsync);
        Children.Add(start); Children.Add(refresh); Children.Add(discardClose); Children.Add(notice);
        UpdateControls();
    }

    // Only the window's initial --hdd launch path may arm this, before it has attached ANY worker.
    internal void ConfigureCold(Func<ImmutableArray<HddInitialMedia>, Task> boot, Func<HddColdVolume, byte, Task>? bootVolume = null)
    {
        if (request is not null || startCold is not null || coldConfigurationConsumed)
            throw new InvalidOperationException("HDD cold configuration already active or consumed.");
        startCold = boot;
        startVolume = bootVolume;
        hddEnabled = true;
        notice.Text = "Prepare C:–F: raw images, or leave slots empty. Start creates a NEW machine; original BIOS has no guest HDD support.";
        UpdateControls();
    }
    internal void Attach(Func<MediaCommand, Task<MediaReply>> send)
    {
        request = send; coldConfigurationConsumed = true; startCold = null; startVolume = null; namedSelection = null; UpdateControls();
    }
    internal void Disconnect() { request = null; views = []; UpdateControls(); }
    internal void Observe(HddReply? reply)
    {
        if (reply is null) return;
        views = reply.Drives;
        if (views.Length == 4) hddEnabled = true;
        foreach (var view in views)
            labels[view.Drive - 2].Text = $"{(char)('A' + view.Drive)}: last observation · {(view.Present ? view.Name ?? "raw image" : "empty")} · generation {view.Generation} · {(view.WriteProtected ? "protected" : "writable")} · {(view.Dirty ? "dirty" : "clean")}";
        notice.Text = reply.Error ?? "HDD state observed. Guest writes after this observation may require another save. Live switching unavailable.";
        UpdateControls();
    }
    public async Task PrepareImageAsync(string path, byte drive)
    {
        if (startCold is null) throw new InvalidOperationException(HddMediaAdapter.SwitchUnavailable);
        if (namedSelection is not null) throw new InvalidOperationException("A named read-only volume is already prepared; raw slots cannot be mixed with it.");
        var index = Index(drive);
        var bytes = await HddFiles.ReadAsync(path);
        if (startCold is null) throw new InvalidOperationException("Cold configuration ended while reading.");
        selected[index] = new(drive, bytes, Path.GetFileName(path));
        labels[index].Text = $"{(char)('A' + drive)}: prepared {Path.GetFileName(path)}, exact {bytes.Length} bytes; not attached";
    }
    internal async Task PrepareVolumeAsync(string path, string name, byte drive)
    {
        var boot = startVolume ?? throw new InvalidOperationException("Named cold volume caller unavailable in this window.");
        _ = Index(drive);
        if (selected.Any(image => image is not null)) throw new InvalidOperationException("Raw slots are already prepared; named volumes cannot be mixed with them.");
        using var volume = await Task.Run(() => HddColdVolume.Open(path, name));
        if (!ReferenceEquals(startVolume, boot) || startCold is null)
            throw new InvalidOperationException("Cold configuration ended while reading.");
        namedSelection = new(Path.GetFullPath(path), volume.Identity.Name, drive, volume.Identity);
        labels[drive - 2].Text = $"{(char)('A' + drive)}: prepared {volume.Identity.Name}, exact 8388608 bytes, READ-ONLY; not attached";
        notice.Text = "Named selection prepared, not mounted. Start reopens/revalidates under native admission through owner acknowledgement; protection cannot be disabled.";
        UpdateControls();
    }
    internal async Task StartColdAsync()
    {
        var boot = startCold ?? throw new InvalidOperationException("Only a fresh --hdd window can start this NEW candidate.");
        if (namedSelection is { } selection)
        {
            var consume = startVolume ?? throw new InvalidOperationException("Named caller disconnected.");
            using var volume = await Task.Run(() => HddColdVolume.Open(selection.Path, selection.Name, selection.Identity));
            if (!ReferenceEquals(consume, startVolume) || !ReferenceEquals(selection, namedSelection) || startCold is null)
                throw new InvalidOperationException("Stale cold configuration ignored.");
            startCold = null; startVolume = null; coldConfigurationConsumed = true; UpdateControls();
            await consume(volume, selection.Drive);
            return;
        }
        var images = selected.Where(image => image is not null).Select(image => image! with { WriteProtected = protection[image!.Drive - 2].IsChecked == true }).ToImmutableArray();
        // Consume once BEFORE awaiting/constructing worker. It cannot become a Reset or reboot shortcut.
        startCold = null; coldConfigurationConsumed = true; UpdateControls();
        await boot(images);
    }
    public async Task InspectAsync() => await SendAsync(new(HddAction.Inspect));
    public async Task SaveCopyAsync(string path, byte drive)
    {
        var captured = (await SendAsync(new(HddAction.CaptureSave, drive))).Save!;
        try
        {
            await HddFiles.SaveAsync(path, captured.Bytes);
        }
        catch
        {
            try { await SendAsync(new(HddAction.CancelSave, drive, captured.Id)); } catch { /* Dirty remains; disconnected owner cannot be cleaned. */ }
            throw;
        }
        await SendAsync(new(HddAction.AcknowledgeSave, drive, captured.Id));
        notice.Text = "Raw copy persisted and unchanged live bytes acknowledged. This does not prove a guest deblock flush.";
    }
    private async Task<HddReply> SendAsync(HddRequest value)
    {
        var send = request ?? throw new InvalidOperationException("HDD runtime disconnected.");
        var reply = await send(new(MediaAction.Hdd, Hdd: value));
        var hdd = reply.Hdd ?? throw new InvalidOperationException("HDD profile unavailable.");
        Observe(hdd);
        if (hdd.Error is not null) throw new InvalidOperationException(hdd.Error);
        return hdd;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; UpdateControls();
        try { await action(); }
        catch (Exception ex) { notice.Text = "HDD action failed: " + ex.Message; }
        finally { IsBusy = false; UpdateControls(); }
    }
    private void UpdateControls()
    {
        for (int index = 0; index < 4; index++)
        {
            choose[index].IsEnabled = protection[index].IsEnabled = startCold is not null && namedSelection is null && !IsBusy;
            save[index].IsEnabled = request is not null && !IsBusy && views.Any(view => view.Drive == index + 2 && view.Present);
        }
        prepareVolume.IsEnabled = volumePath.IsEnabled = volumeName.IsEnabled = volumeDrive.IsEnabled = startVolume is not null && !IsBusy;
        start.IsEnabled = startCold is not null && !IsBusy;
        refresh.IsEnabled = hddEnabled && request is not null && !IsBusy;
        discardClose.IsEnabled = !IsBusy && views.Any(view => view.Dirty);
    }
    private static int Index(byte drive) => drive is >= 2 and <= 5 ? drive - 2 : throw new ArgumentOutOfRangeException(nameof(drive));
}
