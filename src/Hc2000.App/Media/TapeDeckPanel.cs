using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Hc2000.Machine.Tape;

namespace Hc2000.App.Media;

/// <summary>Physical deck controls over copied boundary observations, not a UI transport.</summary>
public sealed class TapeDeckPanel : StackPanel
{
    private readonly ITapeCodecRegistry codecs;
    private Func<MediaCommand, Task<MediaReply>>? request;
    private TapeView? state;
    private bool busy, updating;
    private readonly TextBlock status = new() { Text = "Cassette deck unavailable.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock notice = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly ComboBox capacity = new() { ItemsSource = Enum.GetValues<TapeCapacity>(), SelectedItem = TapeCapacity.C60 };
    private readonly ComboBox format = new() { ItemsSource = new[] { TapeFormat.Tap, TapeFormat.Tzx, TapeFormat.Pzx, TapeFormat.Csw, TapeFormat.Wav }, SelectedItem = TapeFormat.Tzx };
    private readonly ComboBox channel = new() { ItemsSource = Enum.GetValues<TapeChannel>(), SelectedItem = TapeChannel.Left };
    private readonly CheckBox quantize = new() { Content = "Allow quantized copy (keeps exact signal dirty)" };
    private readonly TextBox rate = new() { PlaceholderText = "Output rate Hz (optional)", Width = 160 };
    private readonly TextBox route = new() { PlaceholderText = "TZX selections occurrence:option, …", Width = 240 };
    private readonly CheckBox tab = new() { Content = "Active-side tab present" };
    private readonly CheckBox fast = new() { Content = "Fast wind (120×; normal 10×)" };
    private readonly CheckBox monitor = new() { Content = "EAR monitor (uncalibrated BEL + inverted EAR, DC-blocked)", IsChecked = true };
    private readonly CheckBox discard = new() { Content = "Explicitly discard dirty cassette on replacement/eject" };
    private readonly Dictionary<MediaAction, Button> buttons = [];
    private readonly Button open = new() { Content = "Insert tape…" };
    private readonly Button save = new() { Content = "Export active side…" };
    private readonly Button saveBoth = new() { Content = "Save cassette A + B as two TZX files…" };
    private readonly Button inspect = new() { Content = "Inspect blocks / route" };
    internal bool IsBusy => busy;
    internal bool CanCloseSafely => state?.Deck is not { } d || !(d.A.Dirty || d.B.Dirty) || discard.IsChecked == true;
    public string? Status => status.Text;
    public string? Notice => notice.Text;

    public TapeDeckPanel() : this(TapeCodecRegistry.Installed()) { }
    internal TapeDeckPanel(ITapeCodecRegistry codecs)
    {
        this.codecs = codecs;
        // Deck toggles are pointer controls, not a second keyboard target for BASIC Space.
        foreach (var toggle in new[] { tab, fast, discard, monitor, quantize }) toggle.Focusable = false;
        foreach (var choice in new[] { capacity, format, channel })
            choice.DropDownClosed += (_, _) => (TopLevel.GetTopLevel(this) as Window)?.FindControl<Hc2000.App.Runtime.VideoSurface>("Screen")?.Focus();
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Manual cassette deck", FontSize = 18 });
        Children.Add(status);
        var motion = Row();
        Add(motion, MediaAction.PlayTape, "PLAY"); Add(motion, MediaAction.RecordTape, "REC + PLAY");
        Add(motion, MediaAction.RewindTape, "REW"); Add(motion, MediaAction.ForwardTape, "FF");
        Add(motion, MediaAction.StopEjectTape, "STOP / EJECT"); Add(motion, MediaAction.PauseTape, "PAUSE");
        Children.Add(motion);
        var mechanics = Row();
        Add(mechanics, MediaAction.FlipTape, "Flip side"); Add(mechanics, MediaAction.CounterTape, "Counter 000");
        mechanics.Children.Add(tab); mechanics.Children.Add(fast); Children.Add(mechanics);
        var imports = Row(); imports.Children.Add(capacity); imports.Children.Add(open);
        Add(imports, MediaAction.BlankTape, "Insert blank"); imports.Children.Add(channel); imports.Children.Add(route);
        Children.Add(imports); Children.Add(discard);
        var exports = Row(); exports.Children.Add(format); exports.Children.Add(save); exports.Children.Add(saveBoth); exports.Children.Add(inspect); exports.Children.Add(quantize); exports.Children.Add(rate);
        Children.Add(exports); Children.Add(monitor);
        monitor.IsCheckedChanged += async (_, _) => { if (!updating) await CommandAsync(MediaAction.MonitorTape, monitor.IsChecked == true); };
        Children.Add(new TextBlock { Text = "Codec capabilities: " + string.Join("; ", Enum.GetValues<TapeFormat>().Where(f => f != TapeFormat.Blank).Select(Availability)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Children.Add(notice);
        open.Click += async (_, _) => await RunAsync(async () =>
        {
            if (await PickAsync(false, "Insert cassette", null) is { } path) await InsertCoreAsync(path);
        });
        tab.IsCheckedChanged += async (_, _) => { if (!updating) await CommandAsync(MediaAction.TabTape, tab.IsChecked == true); };
        fast.IsCheckedChanged += async (_, _) => { if (!updating) await CommandAsync(MediaAction.FastWindTape, fast.IsChecked == true); };
        format.SelectionChanged += (_, _) => UpdateControls();
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            var target = (TapeFormat)format.SelectedItem!;
            if (await PickAsync(true, "Export active side", target) is { } path)
                await SaveCoreAsync(path, state!.Deck!.ActiveSide, target);
        });
        saveBoth.Click += async (_, _) => await RunAsync(async () =>
        {
            var a = await PickAsync(true, "Save cassette side A as exact TZX", TapeFormat.Tzx);
            if (a is null) return;
            var b = await PickAsync(true, "Save cassette side B as exact TZX", TapeFormat.Tzx);
            if (b is null) return; // Cancelling either picker exports/acknowledges nothing.
            if (Path.GetFullPath(a) == Path.GetFullPath(b)) throw new InvalidOperationException("Choose two different paths.");
            var sideA = (await SendAsync(new(MediaAction.ExportTape, Side: TapeSide.A))).TapeExport!;
            var sideB = (await SendAsync(new(MediaAction.ExportTape, Side: TapeSide.B))).TapeExport!;
            if (sideA.MediaIdentity != sideB.MediaIdentity) throw new InvalidOperationException("Cassette changed between side snapshots; nothing written.");
            await TapeFiles.SaveAsync(a, sideA, codecs, new(TapeFormat.Tzx, TapeSide.A), SendAsync, exactOnly: true);
            await TapeFiles.SaveAsync(b, sideB, codecs, new(TapeFormat.Tzx, TapeSide.B), SendAsync, exactOnly: true);
            notice.Text = "Two exact TZX side copies written. HCS alone persists tabs/counters and complete state.";
        });
        inspect.Click += async (_, _) => await RunAsync(async () =>
        {
            var reply = await SendAsync(new(MediaAction.ExportTape, Side: state!.Deck!.ActiveSide));
            var image = reply.TapeExport!.Image;
            notice.Text = $"Source {image.ImportOptions.Format}; signal chunks {image.Chunks.Length}; stops {image.Markers.Length}; route "
                + string.Join(", ", image.SourceRoute.Select(r => $"{r.BlockIndex} visit{r.Visit}"))
                + "; annotations: " + string.Join("; ", image.Annotations.Select(a => a.Label));
        });
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DroppedTape(e) is not null && open.IsEnabled ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            if (DroppedTape(e) is { } path && open.IsEnabled) await InsertAsync(path);
        });
        UpdateControls();
    }

    private string? DroppedTape(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files?.Length != 1 || files[0].TryGetLocalPath() is not { } path) return null;
        try { return CanRead(TapeFiles.FormatOf(path)) ? path : null; }
        catch (InvalidDataException) { return null; }
    }

    internal void Attach(Func<MediaCommand, Task<MediaReply>> source) { request = source; UpdateControls(); }
    internal void Disconnect() { request = null; state = null; UpdateControls(); }
    internal void Observe(TapeView? value)
    {
        state = value; updating = true;
        try
        {
            if (value?.Deck is { } deck)
            {
                tab.IsChecked = (deck.ActiveSide == TapeSide.A ? deck.A : deck.B).TabPresent;
                fast.IsChecked = deck.FastWind;
            }
        }
        finally { updating = false; }
        UpdateControls();
    }
    public Task InsertAsync(string path) => RunAsync(() => InsertCoreAsync(path));
    public Task CommandAsync(MediaAction action, bool value = false) => RunAsync(() => SendAsync(new(action,
        Value: value, DiscardChanges: discard.IsChecked == true, Capacity: (TapeCapacity)capacity.SelectedItem!, Side: state?.Deck?.ActiveSide ?? TapeSide.A)));

    private async Task InsertCoreAsync(string path)
    {
        var selections = ParseSelections(route.Text);
        var image = await TapeFiles.ReadAsync(path, codecs, new(TapeFiles.FormatOf(path), TapeSide.A, (TapeCapacity)capacity.SelectedItem!)
            { Channel = (TapeChannel)channel.SelectedItem!, Selections = selections });
        await SendAsync(new(MediaAction.InsertCassette, Name: Path.GetFileName(path), Cassette: image, DiscardChanges: discard.IsChecked == true));
    }
    internal static ImmutableArray<TapeSelection> ParseSelections(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var fields = part.Split(':');
            if (fields.Length != 2 || !uint.TryParse(fields[0], out var occurrence) || !uint.TryParse(fields[1], out var option))
                throw new InvalidDataException("Route choices must be occurrence:option pairs.");
            return new TapeSelection(occurrence, option);
        }).ToImmutableArray();
    }
    private async Task SaveCoreAsync(string path, TapeSide side, TapeFormat target)
    {
        var snapshot = (await SendAsync(new(MediaAction.ExportTape, Side: side))).TapeExport!;
        uint? outputRate = null;
        if (!string.IsNullOrWhiteSpace(rate.Text))
        {
            if (!uint.TryParse(rate.Text, out var value) || value == 0) throw new InvalidDataException("Output rate must be positive Hz.");
            outputRate = value;
        }
        var report = await TapeFiles.SaveAsync(path, snapshot, codecs,
            new(target, side) { Rate = outputRate, AllowQuantize = quantize.IsChecked == true }, SendAsync);
        notice.Text = $"Side {side} copy: {report.Signal}, {report.Bytes}, {report.Amplitude}; rate {report.Rate}. HCS alone saves tabs/counters and complete machine state.";
    }
    private async Task<MediaReply> SendAsync(MediaCommand command)
    {
        var reply = await (request ?? throw new InvalidOperationException("Deck disconnected."))(command);
        Observe(reply.Tape);
        if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
        return reply;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (Exception ex) { notice.Text = "Tape action refused: " + ex.Message; }
        finally { busy = false; UpdateControls(); }
    }
    private void UpdateControls()
    {
        var d = state?.Deck; bool connected = request is not null && d is not null && !busy;
        bool stopped = connected && d!.Mode == TapeDeckMode.Stopped && !d.Paused;
        foreach (var (action, button) in buttons)
            button.IsEnabled = action switch
            {
                MediaAction.BlankTape => stopped,
                MediaAction.PlayTape => connected && d!.Present && d.Capabilities.CanPlay &&
                    (stopped && d.Position < d.Duration || d.Mode == TapeDeckMode.Playing && d.HoldReason == TapeHoldReason.FormatStop),
                MediaAction.RecordTape => stopped && d!.Present && d.Position < d.Duration && d.Capabilities.CanRecord && (d.ActiveSide == TapeSide.A ? d.A : d.B).TabPresent,
                MediaAction.RewindTape => stopped && d!.Present && d.Capabilities.CanWind && d.Position > default(TapeTime),
                MediaAction.ForwardTape => stopped && d!.Present && d.Capabilities.CanWind && d.Position < d.Duration,
                MediaAction.StopEjectTape => connected && d!.Present,
                MediaAction.PauseTape => connected && d!.Mode is TapeDeckMode.Playing or TapeDeckMode.Recording && d.HoldReason == TapeHoldReason.None,
                _ => stopped && d!.Present
            };
        tab.IsEnabled = fast.IsEnabled = stopped && d!.Present;
        monitor.IsEnabled = connected && state!.AudioAvailable;
        open.IsEnabled = stopped && codecs.Formats.Any(f => CanRead(f));
        inspect.IsEnabled = stopped && d!.Present;
        save.IsEnabled = stopped && d!.Present && CanWrite((TapeFormat)format.SelectedItem!);
        saveBoth.IsEnabled = stopped && d!.Present && CanWrite(TapeFormat.Tzx);
        quantize.IsEnabled = rate.IsEnabled = save.IsEnabled;
        capacity.IsEnabled = channel.IsEnabled = route.IsEnabled = discard.IsEnabled = stopped;
        status.Text = d is null ? "Cassette deck unavailable (no live device)." :
            $"{state!.Name ?? "empty"} · source {state.SourceFormat?.ToString() ?? "unknown"} · side {d.ActiveSide} · {d.Capacity} · {d.Mode}{(d.Paused ? " + PAUSE" : "")} · hold {d.HoldReason} · {d.Counter:000} · {d.ElapsedSeconds / 60:00}:{d.ElapsedSeconds % 60:00} · A dirty={d.A.Dirty}, tab={d.A.TabPresent}; B dirty={d.B.Dirty}, tab={d.B.TabPresent} · media #{d.MediaIdentity}. {d.Capabilities.UnavailableReason} {state.AudioReason}";
    }
    private bool CanRead(TapeFormat f) => codecs.Find(f) is { Success: true } r && r.Value.Capabilities.CanRead;
    private bool CanWrite(TapeFormat f) => codecs.Find(f) is { Success: true } r && r.Value.Capabilities.CanWrite;
    private string Availability(TapeFormat f)
    {
        var result = codecs.Find(f);
        return result.Success ? $"{f} read={result.Value.Capabilities.CanRead}, write={result.Value.Capabilities.CanWrite}: {result.Value.Capabilities.UnavailableReason}"
            : $"{f} disabled: {result.Failure!.Detail}";
    }
    private void Add(StackPanel row, MediaAction action, string label)
    {
        var button = new Button { Content = label, Focusable = false }; buttons.Add(action, button); row.Children.Add(button);
        button.Click += async (_, _) => await CommandAsync(action);
    }
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private async Task<string?> PickAsync(bool saving, string title, TapeFormat? target)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
        if (saving)
        {
            using var file = await provider.SaveFilePickerAsync(new() { Title = title, DefaultExtension = target!.Value.ToString().ToLowerInvariant(),
                FileTypeChoices = [new("Tape") { Patterns = [$"*.{target.Value.ToString().ToLowerInvariant()}"] }] });
            return file?.TryGetLocalPath();
        }
        var files = await provider.OpenFilePickerAsync(new() { Title = title, AllowMultiple = false,
            FileTypeFilter = [new("Available tape readers") { Patterns = codecs.Formats.Where(CanRead).Select(f => $"*.{f.ToString().ToLowerInvariant()}").ToArray() }] });
        try { return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Only local files supported."); }
        finally { foreach (var file in files) file.Dispose(); }
    }
}
