using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Hc2000.App.Media;

/// <summary>Host-side staging of registered ROM bytes, not a live ROM swap or boot claim.</summary>
public sealed class ProfileRomPanel : StackPanel
{
    private readonly ComboBox profiles = new() { Name = "RomProfileChoice", ItemsSource = ProfileRomSelection.ProfileIds, SelectedIndex = 0 };
    private readonly TextBox root = new() { Name = "RomAssetRoot", PlaceholderText = "Asset root containing roms/hc2000/" };
    private readonly Button browse = new() { Content = "Choose ROM asset root…" };
    private readonly Button select = new() { Content = "Validate and select ROM set" };
    private readonly Button startBasic = new() { Content = "Start BASIC", IsEnabled = false };
    private readonly Button resetBasic = new() { Content = "Reset BASIC", IsEnabled = false };
    private readonly TextBlock description = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock selected = new() { Text = "Selected ROM set: none. No running machine is changed.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock notice = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private bool busy;
    private bool basicRuntimeActive;

    public SelectedProfileRomSet? SelectedRomSet { get; private set; }
    public string? Notice => notice.Text;
    public event Action<SelectedProfileRomSet>? StartRequested;
    public event Action? ResetRequested;

    public ProfileRomPanel()
    {
        Spacing = 6;
        AutomationProperties.SetName(profiles, "ROM profile");
        AutomationProperties.SetName(root, "ROM asset root");
        AutomationProperties.SetName(startBasic, "Start BASIC");
        AutomationProperties.SetName(resetBasic, "Reset BASIC");
        Children.Add(new TextBlock { Text = "Faithful profile ROM set", FontSize = 18 });
        Children.Add(profiles); Children.Add(description); Children.Add(root);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(browse); row.Children.Add(select); row.Children.Add(startBasic); row.Children.Add(resetBasic); Children.Add(row);
        Children.Add(selected); Children.Add(notice);
        profiles.SelectionChanged += (_, _) => DescribeCandidate();
        browse.Click += async (_, _) =>
        {
            if (busy) return;
            SetBusy(true);
            try
            {
                var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Folder picker unavailable.");
                var folders = await provider.OpenFolderPickerAsync(new() { Title = "Asset root containing roms/hc2000/", AllowMultiple = false });
                try
                {
                    if (folders.Count != 0) root.Text = folders[0].TryGetLocalPath() ?? throw new InvalidOperationException("Only local folders are supported.");
                }
                finally { foreach (var folder in folders) folder.Dispose(); }
            }
            catch (Exception ex) { notice.Text = "ROM folder action failed: " + ex.Message; }
            finally { SetBusy(false); }
        };
        select.Click += async (_, _) => await SelectAsync(profiles.SelectedItem as string ?? "", root.Text ?? "");
        startBasic.Click += (_, _) => { if (SelectedRomSet is { } set) StartRequested?.Invoke(set); };
        resetBasic.Click += (_, _) => ResetRequested?.Invoke();
        DescribeCandidate();
    }

    /// <summary>Atomic staging; a failed candidate retains the last verified set, clearly labelled.</summary>
    public async Task SelectAsync(string profileId, string assetRoot)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            var result = await ProfileRomSelection.SelectAsync(profileId, assetRoot);
            SelectedRomSet = result;
            startBasic.IsEnabled = true;
            selected.Text = $"Selected ROM set: {result.ProfileId} from {result.AssetRoot} ({result.Regions.Length} verified regions).";
            notice.Text = "Verified detached bytes are staged. Start BASIC to create a fresh worker-owned machine; profile selection alone never starts a runtime.";
        }
        catch (Exception ex)
        {
            notice.Text = "ROM set not selected: " + ex.Message + " Previous selection unchanged.";
        }
        finally { SetBusy(false); }
    }

    private void DescribeCandidate()
    {
        if (profiles.SelectedItem is not string id) return;
        var profile = ProfileRomSelection.Describe(id);
        description.Text = string.Join("\n", profile.RomRegions.Select(r =>
            $"{r.Role}: {r.SourcePath ?? "unavailable (missing lower half)"}, 0x{r.Length:X} bytes → {r.Store} +0x{r.StoreOffset:X4}"));
        if (!profile.HasCompleteRomDescription)
            description.Text += "\nIncomplete set: v2 lower CP/M half is unavailable. Validation will reject; no padding or shifted upper image.";
    }
    private void SetBusy(bool value)
    {
        busy = value;
        profiles.IsEnabled = root.IsEnabled = browse.IsEnabled = select.IsEnabled = !value;
        startBasic.IsEnabled = !value && !basicRuntimeActive && SelectedRomSet is not null;
        resetBasic.IsEnabled = !value && basicRuntimeActive;
    }

    public void SetBasicRuntimeActive(bool active)
    {
        basicRuntimeActive = active;
        startBasic.IsEnabled = !busy && !active && SelectedRomSet is not null;
        resetBasic.IsEnabled = !busy && active;
    }
}
