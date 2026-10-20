using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Hc2000.App.Media;

/// <summary>Cold NEW-machine input preparation and copied runtime observations. Never owns a device.</summary>
public sealed partial class HddPanel : StackPanel
{
    private Func<MediaCommand, Task<MediaReply>>? request;
    private Func<ImmutableArray<HddInitialMedia>, Task>? startCold;
    private Func<HddColdVolume, byte, Task>? startVolume;
    private Func<HddColdVolume, Task>? startMap;
    private sealed record NamedSelection(string Path, string Name, byte Drive, Hc2000.Machine.Storage.HddVolumeIdentity Identity, bool Writable);
    private ImmutableArray<NamedSelection> namedSelections = [];
    private int preparationVersion;
    private readonly Button clearVolumes = new() { Content = "Clear named mapping" };
    private readonly TextBox volumePath = new() { Name = "ColdVolumePath", PlaceholderText = "Absolute container path" };
    private readonly TextBox volumeName = new() { Name = "ColdVolumeName", PlaceholderText = "Exact volume name, e.g. WORK.DSK" };
    private readonly ComboBox volumeDrive = new() { ItemsSource = new[] { "C:", "D:", "E:", "F:" }, SelectedIndex = 0 };
    private readonly CheckBox writableVolume = new() { Name = "WritableNamedVolume", Content = "Opt in to writes (catalogue read-only still wins)" };
    private readonly Button prepareVolume = new() { Content = "Prepare named volume" };
    private readonly Button switchVolume = new() { Content = "Request guest-safe remap" };
    private readonly Button cancelSwitch = new() { Content = "Cancel pending remap" };
    private readonly CheckBox discardSwitch = new() { Content = "Explicitly discard this drive's unsaved edits on remap" };
    private HddSwitchView? lastSwitch;
    private bool hddEnabled;
    private bool runtimeInspected;
    private long runtimeBinding;
    private bool coldConfigurationConsumed;
    private readonly HddInitialMedia?[] selected = new HddInitialMedia?[4];
    private readonly TextBlock[] labels = new TextBlock[4];
    private readonly Button[] choose = new Button[4], save = new Button[4];
    private readonly CheckBox[] protection = new CheckBox[4];
    private readonly Button start = new() { Content = "Start NEW HDD machine (original BIOS)" };
    private readonly Button refresh = new() { Content = "Refresh HDD status" };
    private readonly CheckBox discardClose = new() { Content = "Explicitly discard unsaved HDD edits when closing" };
    private readonly TextBlock notice = new() { Text = "HDD is off for this machine. Choose New machine, then enable HDD in Hardware before Start.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel hddRows = new() { Spacing = 3 };
    private ImmutableArray<InlineMediaRow> hddListed = [];
    private ImmutableArray<HddDriveView> views = [];
    public bool IsBusy { get; private set; }
    public bool ColdConfigurationAvailable => startCold is not null;
    internal bool RequiresRuntimeInspection => request is not null && (!runtimeInspected || hddEnabled);
    public bool CanCloseSafely => !boundSavePending && (views.All(view => !view.Dirty) || discardClose.IsChecked == true);
    public string? Notice => notice.Text;
    internal Func<byte, Task>? BrowseFiles { get; set; }
    public ImmutableArray<InlineMediaRow> HddListed => hddListed;

    public HddPanel()
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = "HDD C:–F: (opt-in, host session only)", FontSize = 18 });
        Children.Add(new TextBlock { Text = "Named remaps require HDINST and guest HDSWAP close/flush/reset cooperation; raw attach/detach and restored remaps remain refused.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
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
                var binding = runtimeBinding;
                var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
                using var file = await provider.SaveFilePickerAsync(new() { Title = "Save raw HDD copy", SuggestedFileName = $"drive-{(char)('A' + drive)}.raw", DefaultExtension = "raw" });
                VerifyBinding(binding);
                if (file is not null) await SaveCopyAsync(file.TryGetLocalPath() ?? throw new InvalidOperationException("Local output only."), drive);
            });
            Children.Add(labels[index]);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var browse = new Button { Content = $"Browse {(char)('A' + drive)} files" };
            browse.Click += async (_, _) => { if (BrowseFiles is { } action) await action(drive); };
            boundSaveButtons[index] = new() { Content = $"Save {(char)('A' + drive)} to bound container" };
            boundSaveButtons[index].Click += async (_, _) => await RunAsync(() => SaveBoundAsync(drive));
            row.Children.Add(choose[index]); row.Children.Add(protection[index]); row.Children.Add(save[index]); row.Children.Add(browse);
            row.Children.Add(boundSaveButtons[index]);
            Children.Add(row);
        }
        var hddListSection = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        hddListSection.Children.Add(new TextBlock { Text = "HDD entries (inline listing of prepared images and inspection results)", FontSize = 14 });
        hddListSection.Children.Add(hddRows);
        Children.Add(hddListSection);
        Children.Add(new TextBlock { Text = "Named maps default to read-only; writable selections require explicit opt-in. Preparation cannot mix with raw slots. Saving a capture does not prove a guest deblock flush.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Children.Add(volumePath); Children.Add(volumeName); Children.Add(volumeDrive); Children.Add(writableVolume); Children.Add(prepareVolume); Children.Add(clearVolumes);
        prepareVolume.Click += async (_, _) => await RunAsync(() => PrepareVolumeAsync(volumePath.Text ?? "", volumeName.Text ?? "", (byte)(volumeDrive.SelectedIndex + 2), writableVolume.IsChecked == true));
        clearVolumes.Click += (_, _) => ClearPreparedVolumes();
        Children.Add(discardSwitch); Children.Add(switchVolume); Children.Add(cancelSwitch);
        switchVolume.Click += async (_, _) => await RunAsync(() => BeginVolumeSwitchAsync(volumePath.Text ?? "", volumeName.Text ?? "", (byte)(volumeDrive.SelectedIndex + 2), discardSwitch.IsChecked == true, writableVolume.IsChecked == true));
        cancelSwitch.Click += async (_, _) => await RunAsync(CancelVolumeSwitchAsync);
        start.Click += async (_, _) => await RunAsync(StartColdAsync);
        refresh.Click += async (_, _) => await RunAsync(InspectAsync);
        Children.Add(start); Children.Add(refresh); Children.Add(discardClose);
        reconcileBoundSave.Click += async (_, _) => await RunAsync(ReconcileBoundSaveAsync);
        abortBoundSave.Click += async (_, _) => await RunAsync(AbortBoundSaveAsync);
        Children.Add(reconcileBoundSave); Children.Add(abortBoundSave); Children.Add(notice);
        UpdateControls();
        RefreshHddList();
    }

    // Only a fresh cold candidate may arm this, before it has attached ANY worker.
    internal void ConfigureCold(Func<ImmutableArray<HddInitialMedia>, Task> boot, Func<HddColdVolume, byte, Task>? bootVolume = null,
        Func<HddColdVolume, Task>? bootMap = null)
    {
        if (request is not null || startCold is not null || coldConfigurationConsumed)
            throw new InvalidOperationException("HDD cold configuration already active or consumed.");
        startCold = boot;
        startVolume = bootVolume;
        startMap = bootMap;
        hddEnabled = true;
        notice.Text = "Prepare C:–F: raw images or a named read-only map, or leave slots empty. Start creates a NEW machine; original BIOS has no guest HDD support.";
        UpdateControls();
        RefreshHddList();
    }
    internal void Attach(Func<MediaCommand, Task<MediaReply>> send)
    {
        ResetBinding();
        request = send; coldConfigurationConsumed = true;
        notice.Text = "HDD status not yet inspected for this machine.";
        UpdateControls();
        RefreshHddList();
    }
    internal void Disconnect()
    {
        ResetBinding();
        notice.Text = "HDD runtime disconnected; no current observation.";
        UpdateControls(); RefreshHddList();
    }
    private void ResetBinding()
    {
        runtimeBinding++;
        if (startCold is not null) coldConfigurationConsumed = true;
        request = null; startCold = null; startVolume = null; startMap = null;
        namedSelections = []; preparationVersion++;
        lastSwitch = null; discardSwitch.IsChecked = false; writableVolume.IsChecked = false; boundSavePending = false;
        views = []; hddEnabled = false; runtimeInspected = false; discardClose.IsChecked = false; IsBusy = false;
        Array.Clear(selected);
        for (var index = 0; index < labels.Length; index++)
            labels[index].Text = $"{(char)('C' + index)}: no current observation";
    }
    internal void Observe(HddReply? reply)
    {
        if (reply is null) return;
        views = reply.Drives;
        runtimeInspected = true;
        hddEnabled = !views.IsEmpty;
        lastSwitch = reply.Switch;
        foreach (var view in views)
            labels[view.Drive - 2].Text = $"{(char)('A' + view.Drive)}: last observation · {(view.Present ? view.Name ?? "raw image" : "empty")} · generation {view.Generation} · {(view.WriteProtected ? "protected" : "writable")} · {(view.Dirty ? "dirty" : "clean")}";
        notice.Text = reply.Error ?? (lastSwitch?.State switch
        {
            HddSwitchState.AwaitingGuest => "Remap pending: close files and run HDSWAP from A:; no media changes before the guest ACK. Refresh to observe completion, or cancel.",
            HddSwitchState.Committed => "Remap committed under the guest ACK hold. Inspect the replacement protection before another operation.",
            HddSwitchState.Cancelled => "Remap cancelled; the original binding remains installed.",
            HddSwitchState.Unknown => "Unknown remap outcome; CPU hold and source authority remain retained. " + lastSwitch.Error,
            _ => "HDD state observed. Guest writes after this observation may require another save."
        });
        UpdateControls();
        RefreshHddList();
    }

    /// <summary>Rebuild the inline HDD entry listing from existing cold preparation and inspection data.</summary>
    public void RefreshHddList()
    {
        bool cold = startCold is not null;
        var prepared = cold ? selected.Where(image => image is not null).Select(image => image!).ToImmutableArray()
            : ImmutableArray<HddInitialMedia>.Empty;
        hddListed = InlineMediaListing.Hdd(hddEnabled || request is not null && !runtimeInspected, views,
            prepared.IsDefault ? null : prepared,
            namedMap: cold ? namedSelections.Select(named => (named.Path, named.Name, named.Drive)).ToImmutableArray() : [],
            writableNamedDrives: cold ? namedSelections.Where(named => named.Writable).Select(named => named.Drive).ToHashSet() : null);
        var runtimeHandles = views.Where(view => view.Present).Select(view => $"hdd:{view.Drive}").ToHashSet(StringComparer.Ordinal);
        hddRows.Children.Clear();
        foreach (var row in hddListed)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            item.Children.Add(new TextBlock { Text = MediaPanel.RowText(row), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            // Inspect revalidates the runtime view, not a host file, so the row needs no path.
            if (runtimeHandles.Contains(row.Handle) && request is not null)
            {
                var button = new Button { Content = "Inspect", Focusable = false, IsEnabled = request is not null && !IsBusy };
                button.Click += async (_, _) => await RunAsync(InspectAsync);
                item.Children.Add(button);
            }
            hddRows.Children.Add(item);
        }
    }
    public async Task PrepareImageAsync(string path, byte drive)
    {
        if (startCold is null) throw new InvalidOperationException(HddMediaAdapter.SwitchUnavailable);
        if (!namedSelections.IsEmpty) throw new InvalidOperationException("Named read-only volumes are already prepared; raw slots cannot be mixed with them.");
        var index = Index(drive);
        var version = preparationVersion;
        var bytes = await HddFiles.ReadAsync(path);
        if (startCold is null || preparationVersion != version || !namedSelections.IsEmpty)
            throw new InvalidOperationException("Cold configuration changed while reading.");
        selected[index] = new(drive, bytes, Path.GetFileName(path));
        preparationVersion++;
        labels[index].Text = $"{(char)('A' + drive)}: prepared {Path.GetFileName(path)}, exact {bytes.Length} bytes; not attached";
        RefreshHddList();
    }
    internal async Task PrepareVolumeAsync(string path, string name, byte drive, bool writable = false)
    {
        var boot = startVolume;
        var map = startMap;
        if (startCold is null || (boot is null && map is null))
            throw new InvalidOperationException("Named cold volume caller unavailable in this window.");
        _ = Index(drive);
        if (selected.Any(image => image is not null)) throw new InvalidOperationException("Raw slots are already prepared; named volumes cannot be mixed with them.");
        var version = preparationVersion;
        var full = Path.GetFullPath(path);
        // Legacy single-volume callers still replace their selection; map callers replace only the chosen drive.
        var retained = map is null ? [] : namedSelections.Where(selection => selection.Drive != drive).ToImmutableArray();
        if (retained.Any(selection => !StringComparer.Ordinal.Equals(selection.Path, full)))
            throw new InvalidOperationException("Use one container for the named mapping; clear it before choosing another.");
        var requests = retained.Select(selection => new HddColdVolumeRequest(selection.Drive, selection.Name, selection.Identity, selection.Writable))
            .Append(new(drive, name, Writable: writable)).ToImmutableArray();
        using var volume = await Task.Run(() => HddColdVolume.OpenMapped(full, requests));
        if (!ReferenceEquals(startVolume, boot) || !ReferenceEquals(startMap, map) || startCold is null || preparationVersion != version)
            throw new InvalidOperationException("Cold configuration changed while reading.");
        var identities = volume.Identities;
        var media = volume.MappedMedia;
        namedSelections = requests.Select((selection, index) => new NamedSelection(full, identities[index].Name, selection.Drive, identities[index], !media[index].WriteProtected)).ToImmutableArray();
        preparationVersion++;
        RefreshPreparedLabels();
        notice.Text = "Named map prepared, not mounted. Start revalidates every selection under one native admission; catalogue read-only protection cannot be disabled.";
        UpdateControls();
        RefreshHddList();
    }
    internal void ClearPreparedVolumes()
    {
        if (startCold is null) throw new InvalidOperationException("Only a fresh cold configuration can clear its named mapping.");
        namedSelections = []; preparationVersion++;
        RefreshPreparedLabels(); UpdateControls(); RefreshHddList();
    }
    private void RefreshPreparedLabels()
    {
        for (int index = 0; index < labels.Length; index++)
        {
            var named = namedSelections.SingleOrDefault(selection => selection.Drive == index + 2);
            if (named is not null)
                labels[index].Text = $"{(char)('C' + index)}: prepared {named.Name}, exact 8388608 bytes, {(named.Writable ? "WRITABLE (opt-in)" : "READ-ONLY")}; not attached";
            else if (selected[index] is null)
                labels[index].Text = $"{(char)('C' + index)}: empty cold slot";
        }
    }
    internal async Task StartColdAsync()
    {
        var boot = startCold ?? throw new InvalidOperationException("Choose New machine, then enable HDD in Hardware; only a fresh, unattached candidate can use this cold-start route.");
        if (!namedSelections.IsEmpty)
        {
            var consume = startVolume;
            var consumeMap = startMap;
            if (consume is null && consumeMap is null) throw new InvalidOperationException("Named caller disconnected.");
            var selections = namedSelections;
            var version = preparationVersion;
            var requests = selections.Select(selection => new HddColdVolumeRequest(selection.Drive, selection.Name, selection.Identity, selection.Writable)).ToImmutableArray();
            using var volume = await Task.Run(() => HddColdVolume.OpenMapped(selections[0].Path, requests));
            if (!ReferenceEquals(consume, startVolume) || !ReferenceEquals(consumeMap, startMap) || preparationVersion != version || startCold is null)
                throw new InvalidOperationException("Stale cold configuration ignored.");
            startCold = null; startVolume = null; startMap = null; coldConfigurationConsumed = true; preparationVersion++; UpdateControls();
            if (consumeMap is not null) await consumeMap(volume);
            else await consume!(volume, selections.Single().Drive);
            RefreshHddList();
            return;
        }
        var images = selected.Where(image => image is not null).Select(image => image! with { WriteProtected = protection[image!.Drive - 2].IsChecked == true }).ToImmutableArray();
        // Consume once BEFORE awaiting/constructing worker. It cannot become a Reset or reboot shortcut.
        startCold = null; startVolume = null; startMap = null; coldConfigurationConsumed = true; preparationVersion++; UpdateControls();
        await boot(images);
        RefreshHddList();
    }
    public async Task InspectAsync() => await SendAsync(new(HddAction.Inspect));
    internal async Task BeginVolumeSwitchAsync(string path, string name, byte drive, bool discardChanges = false, bool writable = false)
    {
        _ = Index(drive);
        var binding = runtimeBinding;
        if (request is null || !hddEnabled || lastSwitch?.State is HddSwitchState.AwaitingGuest or HddSwitchState.Unknown)
            throw new InvalidOperationException("Inspect a connected HDD machine with no unresolved remap first.");
        var generation = views.Single(view => view.Drive == drive).Generation;
        var target = await Task.Run(() =>
        {
            var full = Path.GetFullPath(path);
            using var volume = HddColdVolume.Open(full, name, writable: writable);
            return new HddSwitchTarget(full, volume.Identity, !volume.Media(drive).WriteProtected);
        });
        // This is only an intended identity, not cold-attach or mutation authority. The existing
        // owner reopens and revalidates it only under the real guest ACK hold.
        await SendAsync(new(HddAction.BeginSwitch, drive, Target: target, ExpectedGeneration: generation, DiscardChanges: discardChanges), binding);
        discardSwitch.IsChecked = false;
    }
    internal async Task CancelVolumeSwitchAsync()
    {
        var pending = lastSwitch;
        if (pending?.State != HddSwitchState.AwaitingGuest)
            throw new InvalidOperationException("No cancellable remap in this runtime.");
        await SendAsync(new(HddAction.CancelSwitch, pending.Drive, SwitchId: pending.Id));
    }
    public async Task SaveCopyAsync(string path, byte drive)
    {
        var binding = runtimeBinding;
        var captured = (await SendAsync(new(HddAction.CaptureSave, drive), binding)).Save
            ?? throw new InvalidOperationException("HDD save capture unavailable.");
        try
        {
            await HddFiles.SaveAsync(path, captured.Bytes);
        }
        catch
        {
            try { await SendAsync(new(HddAction.CancelSave, drive, captured.Id), binding); } catch { /* Dirty remains; a new binding must not receive the old cancellation. */ }
            throw;
        }
        // Save ids and generations are owner-local: even equal values cannot authorize another session.
        await SendAsync(new(HddAction.AcknowledgeSave, drive, captured.Id), binding);
        notice.Text = "Raw copy persisted and unchanged live bytes acknowledged. This does not prove a guest deblock flush.";
    }
    private Task<HddReply> SendAsync(HddRequest value) => SendAsync(value, runtimeBinding);
    private async Task<HddReply> SendAsync(HddRequest value, long binding)
    {
        VerifyBinding(binding);
        var send = request ?? throw new InvalidOperationException("HDD runtime disconnected.");
        var reply = await send(new(MediaAction.Hdd, Hdd: value));
        VerifyBinding(binding);
        var hdd = reply.Hdd ?? throw new InvalidOperationException(reply.Error ?? "HDD profile unavailable.");
        Observe(hdd);
        // An explicit owner reply that the faithful profile has no HDD is a successful status probe,
        // not a failed close. Other refusals (including save refusals) remain failures.
        if (value.Action == HddAction.Inspect && hdd == HddReply.Unavailable) return hdd;
        if (reply.Error is not null || hdd.Error is not null) throw new InvalidOperationException(reply.Error ?? hdd.Error);
        return hdd;
    }
    private void VerifyBinding(long binding)
    {
        if (binding != runtimeBinding) throw new InvalidOperationException("HDD runtime changed; stale action ignored and live edits remain unsaved.");
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        var binding = runtimeBinding;
        IsBusy = true; UpdateControls();
        try { await action(); }
        catch (Exception ex) { if (binding == runtimeBinding) notice.Text = "HDD action failed: " + ex.Message; }
        finally { if (binding == runtimeBinding) { IsBusy = false; UpdateControls(); } }
    }
    private void UpdateControls()
    {
        for (int index = 0; index < 4; index++)
        {
            choose[index].IsEnabled = protection[index].IsEnabled = startCold is not null && namedSelections.IsEmpty && !IsBusy;
            save[index].IsEnabled = request is not null && !IsBusy && views.Any(view => view.Drive == index + 2 && view.Present);
            boundSaveButtons[index].IsEnabled = request is not null && !IsBusy && !boundSavePending
                && views.Any(view => view.Drive == index + 2 && view.Present && !view.WriteProtected);
        }
        var coldNamed = startVolume is not null || startMap is not null;
        var runtimeNamed = request is not null && hddEnabled && !boundSavePending && lastSwitch?.State is not (HddSwitchState.AwaitingGuest or HddSwitchState.Unknown);
        prepareVolume.IsEnabled = coldNamed && !IsBusy;
        volumePath.IsEnabled = volumeName.IsEnabled = volumeDrive.IsEnabled = writableVolume.IsEnabled = (coldNamed || runtimeNamed) && !IsBusy;
        switchVolume.IsEnabled = discardSwitch.IsEnabled = runtimeNamed && !IsBusy;
        cancelSwitch.IsEnabled = request is not null && lastSwitch?.State == HddSwitchState.AwaitingGuest && !IsBusy;
        clearVolumes.IsEnabled = startCold is not null && !namedSelections.IsEmpty && !IsBusy;
        start.IsEnabled = startCold is not null && !IsBusy;
        refresh.IsEnabled = request is not null && !IsBusy;
        discardClose.IsEnabled = !IsBusy && !boundSavePending && views.Any(view => view.Dirty);
        reconcileBoundSave.IsEnabled = abortBoundSave.IsEnabled = request is not null && hddEnabled && !IsBusy;
    }
    private static int Index(byte drive) => drive is >= 2 and <= 5 ? drive - 2 : throw new ArgumentOutOfRangeException(nameof(drive));
}
