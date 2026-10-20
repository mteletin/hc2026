using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Hc2000.App.Media;

namespace Hc2000.App.Snapshots;

/// <summary>UI owns host pickers and persistence; only copied snapshot commands/replies cross
/// the boundary. Save captures the machine at the next ordered boundary into a .hcs file the
/// user chooses; Load reads a .hcs file and adopts it at the next ordered boundary. No codec
/// or state logic of its own: the owner side is the public Machine.Save/Load API.</summary>
public sealed class HcsPanel : StackPanel
{
    private HcsBridge? bridge;
    private bool busy;
    private readonly TextBlock notice = new() { Text = "Snapshot disconnected. Save/Load .hcs unavailable: no machine runtime attached.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button save = new() { Content = "Save snapshot…" };
    private readonly Button load = new() { Content = "Load snapshot…" };
    private readonly CheckBox discard = new() { Content = "Explicitly discard unsaved disk/cassette edits on snapshot Load" };
    public string? Notice => notice.Text;

    public HcsPanel()
    {
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Machine snapshot (.hcs)", FontSize = 18 });
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
            var target = await provider.SaveFilePickerAsync(new() { Title = "Save machine snapshot", SuggestedFileName = "snapshot.hcs", DefaultExtension = "hcs",
                FileTypeChoices = [new("HC2000 snapshot") { Patterns = ["*.hcs"] }] });
            if (target is null) return;
            using (target)
            {
                var path = target.TryGetLocalPath() ?? throw new InvalidOperationException("Only local files are supported.");
                var reply = await RequestAsync(new(HcsAction.Save));
                await MediaFiles.SaveAsync(path, reply.Snapshot);
                notice.Text = $"Snapshot saved to {Path.GetFileName(path)}.";
            }
        });
        load.Click += async (_, _) => await RunAsync(async () =>
        {
            if (await PickAsync("Load snapshot", "*.hcs") is not { } path) return;
            await LoadCoreAsync(path);
        });
        Children.Add(save); Children.Add(load); Children.Add(discard); Children.Add(notice);
        UpdateControls();
    }

    public void Attach(HcsBridge value)
    {
        if (bridge is not null) throw new InvalidOperationException("Snapshot already attached.");
        bridge = value;
        notice.Text = "Snapshot connected.";
        UpdateControls();
    }

    public void Disconnect()
    {
        bridge?.Disconnect(); bridge = null; UpdateControls();
        notice.Text = "Snapshot runtime disconnected.";
    }

    public Task SaveSnapshotAsync(string path) => RunAsync(() => SaveCoreAsync(path));
    public Task LoadSnapshotAsync(string path) => RunAsync(() => LoadCoreAsync(path));

    private async Task SaveCoreAsync(string path)
    {
        var reply = await RequestAsync(new(HcsAction.Save));
        await MediaFiles.SaveAsync(path, reply.Snapshot);
        notice.Text = $"Snapshot saved to {Path.GetFileName(path)}.";
    }

    private async Task LoadCoreAsync(string path)
    {
        var bytes = await MediaFiles.ReadAsync(path, MediaFiles.MaxSnapshotBytes);
        await RequestAsync(new(HcsAction.Load, bytes, DiscardChanges: discard.IsChecked == true));
        notice.Text = "Snapshot loaded at an emulation boundary.";
    }

    private async Task<HcsReply> RequestAsync(HcsCommand command)
    {
        var source = bridge ?? throw new InvalidOperationException("Snapshot runtime disconnected.");
        var reply = await source.RequestAsync(command);
        if (!ReferenceEquals(source, bridge)) throw new InvalidOperationException("Stale snapshot runtime reply ignored.");
        if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
        return reply;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (Exception ex) { notice.Text = "Snapshot action failed: " + ex.Message; }
        finally { busy = false; UpdateControls(); }
    }

    private void UpdateControls()
    {
        save.IsEnabled = load.IsEnabled = discard.IsEnabled = bridge is not null && !busy;
    }

    private async Task<string?> PickAsync(string title, string pattern)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
        var files = await provider.OpenFilePickerAsync(new() { Title = title, AllowMultiple = false,
            FileTypeFilter = [new("Snapshot") { Patterns = [pattern] }] });
        try { return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new InvalidOperationException("Only local files are supported."); }
        finally { foreach (var file in files) file.Dispose(); }
    }
}
