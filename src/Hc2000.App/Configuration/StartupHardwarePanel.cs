using Avalonia.Controls;

namespace Hc2000.App.Configuration;

/// <summary>Hardware draft only; no file access, media policy, persistence or runtime authority.</summary>
public sealed class StartupHardwarePanel : UserControl
{
    private readonly ComboBox profiles = new() { Name = "PrestartHardwareProfile", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly TextBlock description = new() { Name = "PrestartHardwareDescription", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly string initialProfile;
    public bool HddEnabled => SelectedProfile == "hc2000-v2-hdd-v1";
    public event EventHandler? HddEnabledChanged;
    private string SelectedProfile => (profiles.SelectedItem as ComboBoxItem)?.Tag as string ?? initialProfile;

    public StartupHardwarePanel(MachineConfiguration initial)
    {
        initialProfile = initial.Profile;
        var choices = new List<ComboBoxItem>
        {
            Choice("hc2000-v2", "Faithful HC2000 — 64 KiB"),
            Choice("hc2000-v2-ram-banks-v1", "Extended RAM — 1 MiB extra, 128 × 8 KiB banks"),
            Choice("hc2000-v2-paged-v1", "Paged RAM — 1 GiB extra, 65,536 × 16 KiB pages"),
            Choice("hc2000-v2-paged-m-v1", "Advanced M-compatible paged view — 1 GiB extra"),
            Choice("hc2000-v2-paged-m-v2", "Advanced M-compatible paged view v2 — 1 GiB extra, 8 MiB M: drive"),
            Choice("hc2000-v2-hdd-v1", "Raw HDD controller — 64 KiB, C–F slots")
        };
        var selected = choices.FirstOrDefault(c => (string?)c.Tag == initial.Profile);
        if (selected is null)
        {
            selected = Choice(initial.Profile, "Unsupported stored hardware: " + initial.Profile);
            choices.Add(selected);
        }
        profiles.ItemsSource = choices;
        profiles.SelectedItem = selected;
        Describe();
        profiles.SelectionChanged += (_, _) =>
        {
            Describe();
            HddEnabledChanged?.Invoke(this, EventArgs.Empty);
        };
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Draft hardware — adopted only by a new cold Start", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                profiles, description,
                new TextBlock
                {
                    Text = "All choices retain the original whole-chip M27C512/IF1 BIOS. RAM and HDD are alternative profiles, not combinable switches. Selected Reset cannot adopt hardware changes; use New machine.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            }
        };
    }

    public MachineConfiguration ApplyTo(MachineConfiguration draft) => draft with { Profile = SelectedProfile };

    private static ComboBoxItem Choice(string profile, string label) => new() { Tag = profile, Content = label };
    private void Describe() => description.Text = SelectedProfile switch
    {
        "hc2000-v2" => "Faithful 64 KiB RAM. No extended RAM or HDD controller.",
        "hc2000-v2-ram-banks-v1" => "64 KiB base + 1 MiB banked RAM (1,114,112 bytes total). Guest software selects the bank; this does not enlarge CP/M TPA automatically.",
        "hc2000-v2-paged-v1" => "64 KiB base + 1 GiB paged RAM. Each machine allocates a contiguous 1 GiB store at Start; available memory and snapshot limits still apply.",
        "hc2000-v2-paged-m-v1" => "Advanced: 64 KiB base + 1 GiB, exact M-transfer policy. Allocates a contiguous 1 GiB per machine; M firmware/driver installation is not supplied.",
        "hc2000-v2-paged-m-v2" => "Advanced: 64 KiB base + 1 GiB, exact M-transfer v2 policy with the 8 MiB M: (pages 0-511). Allocates a contiguous 1 GiB per machine; M firmware/driver installation is not supplied.",
        "hc2000-v2-hdd-v1" => "64 KiB RAM plus raw HDD controller. An empty draft selects bundled formatted D:/E:/F: volumes; no C: image or HDD boot is supplied. Change or clear selections in Media. Guest software is required: the original BIOS does not access HDD. Guest writes affect private copies only, never installed images; live switching and HDD snapshots remain unavailable.",
        _ => "This stored profile is not supported by ordinary Start. It remains unchanged until you explicitly choose supported hardware."
    };
}
