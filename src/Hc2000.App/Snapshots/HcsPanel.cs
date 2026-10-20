using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Hc2000.App.Media;
using Hc2000.Machine.Snapshots;

namespace Hc2000.App.Snapshots;

/// <summary>UI owns host pickers and persistence; only copied snapshot commands/replies cross
/// the boundary. Save captures the machine at the next ordered boundary into a .hcs file the
/// user chooses; Load reads a .hcs file and adopts it at the next ordered boundary. No codec
/// or state logic of its own: the owner side is the public Machine.Save/Load API.</summary>
public sealed class HcsPanel : StackPanel
{
    private HcsBridge? bridge;
    private bool busy;
    private readonly Func<string, int, Task<ImmutableArray<byte>>> readSnapshot;
    private readonly TextBlock notice = new() { Text = "Snapshot disconnected. Save/Load .hcs unavailable: no machine runtime attached.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button save = new() { Content = "Save snapshot…" };
    private readonly Button load = new() { Content = "Load snapshot…" };
    private readonly CheckBox discard = new() { Content = "Explicitly discard unsaved disk/cassette edits on snapshot Load" };
    private readonly Button refreshList = new() { Content = "Refresh snapshot list" };
    private readonly StackPanel snapshotRows = new() { Spacing = 3 };
    private ImmutableArray<InlineMediaRow> snapshotListed = [];
    public string? Notice => notice.Text;
    public ImmutableArray<InlineMediaRow> SnapshotListed => snapshotListed;

    /// <summary>Host-scoped snapshot directory for the bound machine; null = not configured (rows say so).</summary>
    public string? SnapshotDirectory { get; set; }
    /// <summary>This machine's persisted last snapshot selection, if any.</summary>
    public string? LastSnapshot { get; set; }

    public HcsPanel() : this(MediaFiles.ReadAsync) { }

    internal HcsPanel(Func<string, int, Task<ImmutableArray<byte>>> readSnapshot)
    {
        this.readSnapshot = readSnapshot;
        Spacing = 6;
        Children.Add(new TextBlock { Text = "Machine snapshot (.hcs)", FontSize = 18 });
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            var targetRuntime = CaptureTarget();
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File picker unavailable.");
            var target = await provider.SaveFilePickerAsync(new() { Title = "Save machine snapshot", SuggestedFileName = "snapshot.hcs", DefaultExtension = "hcs",
                FileTypeChoices = [new("HC2000 snapshot") { Patterns = ["*.hcs"] }] });
            if (target is null) return;
            using (target)
            {
                var path = target.TryGetLocalPath() ?? throw new InvalidOperationException("Only local files are supported.");
                var reply = await RequestAsync(new(HcsAction.Save), targetRuntime);
                await MediaFiles.SaveAsync(path, reply.Snapshot);
                notice.Text = $"Snapshot saved to {Path.GetFileName(path)}.";
            }
        });
        load.Click += async (_, _) => await RunAsync(async () =>
        {
            var target = CaptureTarget();
            if (await PickAsync("Load snapshot", "*.hcs") is not { } path) return;
            await LoadCoreAsync(path, target);
        });
        var listSection = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        listSection.Children.Add(new TextBlock { Text = "Snapshots in this machine's directory (host metadata only; selection uses the existing guarded load)", FontSize = 14 });
        refreshList.Click += async (_, _) => await RunAsync(() => { RefreshSnapshotList(); return Task.CompletedTask; });
        listSection.Children.Add(refreshList);
        listSection.Children.Add(snapshotRows);
        Children.Add(save); Children.Add(load); Children.Add(discard); Children.Add(listSection); Children.Add(notice);
        UpdateControls();
        RefreshSnapshotList();
    }

    /// <summary>Rebuild the inline snapshot listing from host metadata of the machine's scoped directory.</summary>
    public void RefreshSnapshotList()
    {
        var listedBridge = bridge;
        var limits = listedBridge?.Limits ?? new HcsLimits();
        snapshotListed = InlineMediaListing.Snapshots(SnapshotDirectory, LastSnapshot, limits);
        snapshotRows.Children.Clear();
        foreach (var row in snapshotListed)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            item.Children.Add(new TextBlock { Text = MediaPanel.RowText(row), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (row.Selectable)
            {
                var button = new Button { Content = "Load", Focusable = false, IsEnabled = bridge is not null && !busy };
                string path = row.Path!;
                long size = row.Size;
                button.Click += async (_, _) => await RunAsync(async () =>
                {
                    var target = (Bridge: listedBridge ?? throw new InvalidOperationException("Snapshot disconnected."), Limits: limits);
                    ValidateTarget(target);
                    if (InlineMediaListing.RevalidateSnapshot(path, size, limits) is { } refusal)
                        throw new InvalidOperationException(refusal);
                    await LoadCoreAsync(path, target);
                });
                item.Children.Add(button);
            }
            snapshotRows.Children.Add(item);
        }
    }

    public void Attach(HcsBridge value)
    {
        if (bridge is not null) throw new InvalidOperationException("Snapshot already attached.");
        bridge = value;
        notice.Text = "Snapshot connected.";
        UpdateControls();
        RefreshSnapshotList();
    }

    public void Disconnect()
    {
        bridge?.Disconnect();
        Unbind();
    }

    // A tab switch releases only the shared view, not the session-owned live bridge.
    public void Unbind()
    {
        bridge = null; UpdateControls();
        RefreshSnapshotList();
        notice.Text = "Snapshot runtime disconnected.";
    }

    public Task SaveSnapshotAsync(string path) => RunAsync(() => SaveCoreAsync(path));
    public Task LoadSnapshotAsync(string path) => RunAsync(() => LoadCoreAsync(path));

    private async Task SaveCoreAsync(string path)
    {
        var reply = await RequestAsync(new(HcsAction.Save), CaptureTarget());
        await MediaFiles.SaveAsync(path, reply.Snapshot);
        notice.Text = $"Snapshot saved to {Path.GetFileName(path)}.";
    }

    private Task LoadCoreAsync(string path) => LoadCoreAsync(path, CaptureTarget());

    private async Task LoadCoreAsync(string path, (HcsBridge Bridge, HcsLimits Limits) target)
    {
        ValidateTarget(target);
        var discardChanges = discard.IsChecked == true;
        var bytes = await readSnapshot(path, target.Limits.TotalBytes);
        await RequestAsync(new(HcsAction.Load, bytes, DiscardChanges: discardChanges), target);
        notice.Text = "Snapshot loaded at an emulation boundary.";
    }

    private (HcsBridge Bridge, HcsLimits Limits) CaptureTarget()
    {
        var source = bridge ?? throw new InvalidOperationException("Snapshot runtime disconnected.");
        source.ValidateAdmission(source.Limits);
        return (source, source.Limits);
    }

    private void ValidateTarget((HcsBridge Bridge, HcsLimits Limits) target)
    {
        if (!ReferenceEquals(target.Bridge, bridge) || !ReferenceEquals(target.Limits, target.Bridge.Limits))
            throw new InvalidOperationException("Stale snapshot runtime reply ignored.");
        target.Bridge.ValidateAdmission(target.Limits);
    }

    private async Task<HcsReply> RequestAsync(HcsCommand command, (HcsBridge Bridge, HcsLimits Limits) target)
    {
        ValidateTarget(target);
        var reply = await target.Bridge.RequestAsync(command);
        ValidateTarget(target);
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
        save.IsEnabled = load.IsEnabled = discard.IsEnabled = refreshList.IsEnabled = bridge is not null && !busy;
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
