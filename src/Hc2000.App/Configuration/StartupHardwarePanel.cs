using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Hc2000.Machine.Capabilities;

namespace Hc2000.App.Configuration;

/// <summary>
/// Hardware draft only; no file access, media policy, persistence or runtime authority.
/// The user picks one of the machines the catalog admits, not a set of switches: the catalog maps exactly
/// <see cref="Cards"/> profiles, so a free combination of its rows was refused at Start more often than not,
/// with the reason shown far below. Options appear only on the machines that admit them (CPU turbo), and
/// the catalog rows each machine stands for are shown as the CLI flags that build the same machine.
/// On the X5 machine the options are the visible X0–X5 expansion card picker: X0 is included with the
/// machine, X1 points to the display choice, X2/X3/X4 are off by default, and the X5 driver roster is 0–8.
/// </summary>
public sealed partial class StartupHardwarePanel : UserControl
{
    /// <summary>One admitted machine, as the picker shows it.</summary>
    public sealed record MachineCard(string Profile, string Group, string Badge, string Title, string Caption, bool LargeMemory);

    /// <summary>Every profile ordinary Start admits, grouped as the picker shows them.</summary>
    public static IReadOnlyList<MachineCard> Cards { get; } =
    [
        new("hc2000-v2", "ORIGINAL", "64K", "Classic HC2000", "The machine as built: BASIC at power-on, 64 KiB RAM, floppy A: and B:, CP/M 2.2", false),
        new("hc2000-v2-ram-banks-v1", "ORIGINAL", "64K + 1M", "Banked RAM", "64 KiB + 1 MiB in banks the program selects", false),
        new("hc2000-v2-hdd-v1", "STORAGE", "HDD", "Hard disk", "64 KiB RAM with hard disks C: to F:", false),
        new("hc2000-v2-hdd-cpm3-v1", "STORAGE", "HDD · CP/M 3", "Hard disk, CP/M 3 from C:", "Nonbanked CP/M 3; supply a prepared 8 MiB C: image", false),
        new("hc2000-v2-hdd-cboot-v1", "STORAGE", "HDD · CP/M", "Hard disk, CP/M from C:", "64 KiB RAM with hard disks C: to F:; boots CP/M from C: at every reset, no floppy needed", false),
        new("hc2000-v2-blockport-v1", "STORAGE", "BLOCK", "Block disk", "64 KiB RAM with the block disk controller", false),
        new("hc2000-v2-tape-control-v1", "STORAGE", "TAPE", "Tape control", "64 KiB RAM with the tape control port", false),
        new("hc2000-v2-paged-v1", "EXTENDED MEMORY · NEEDS ABOUT 1 GiB FREE", "1G", "Paged RAM", "64 KiB + 1 GiB of paged memory", true),
        new("hc2000-v2-paged-m-v1", "EXTENDED MEMORY · NEEDS ABOUT 1 GiB FREE", "1G · M:", "RAM drive M:", "1 GiB with the M: transfer view", true),
        new("hc2000-v2-paged-m-v2", "EXTENDED MEMORY · NEEDS ABOUT 1 GiB FREE", "1G · 8M", "8 MiB RAM drive", "1 GiB with an 8 MiB M: drive", true),
        new("hc2000-v2-paged-tape-v2", "EXTENDED MEMORY · NEEDS ABOUT 1 GiB FREE", "1G · TAPE", "Tape as a disk", "1 GiB with tape mounted as a CP/M volume", true),
        new("hc2000-v2-paged-expansion-v1", "EXPERIMENTAL", "1G · ×16", "16 expansion CPUs", "1 GiB with sixteen side Z80 processors", true),
        new("hc2000-v2-full-workstation-v1", "WORKSTATION", "1G · C-F · M · TAPE · VGA · NET · ×16", "Full workstation",
            "CP/M from C: with 1 GiB, C: to F:, the 8 MiB M: drive, the tape, the VGA screen, sound, the network and sixteen side Z80 processors", true),
        new("hc2000-v2-combined-workstation-v2", "WORKSTATION", "1G · C-F · M · TAPE · ×16", "CP/M workstation",
            "1 GiB with C: to F: hard disks, the 8 MiB M: drive, a CP/M tape volume and sixteen side Z80 processors", true),
        new("hc2000-v2-paged-expansion-x5-v1", "EXPERIMENTAL", "1G · X5", "X5 managed CPUs", "16 compute CPUs and 0–8 driver CPUs", true),
        new("hc2000-v2-paged-expansion-x5-hdd-guest-v1", "EXPERIMENTAL", "1G · X5 · HDD", "X5 and hard disk", "X5 managed CPUs and cold hard disks C: to F:", true),
        new("hc2000-v2-cpnet-v1", "EXPERIMENTAL", "1G · ×16 · NET", "CP/NET card", "1 GiB with sixteen side Z80 processors and the CP/NET network card (gateway off by default)", true)
    ];

    /// <summary>The turbo multipliers the catalog admits for <paramref name="profile"/>: "1" alone when it admits none.</summary>
    public static IReadOnlyList<string> TurboValues(string profile)
    {
        if (profile == "hc2000-v2-hdd-cpm3-v1") return ["1"];
        var set = CapabilityCatalog.FromProfile(profile) ?? throw new ArgumentException("Unknown profile " + profile, nameof(profile));
        return CapabilityCatalog.Find("cpu-turbo")!.AdmittedValues
            .Where(value => CapabilityCatalog.Validate(set.With("cpu-turbo", value)) is null).ToArray();
    }

    /// <summary>The command-line flags that build the same machine (text overlay is a display choice, not hardware).</summary>
    public static string CommandLine(string profile, int turbo = 1, int driverCount = 0, bool x2Vga = false, bool x3Sound = false, bool x4Math = false, bool x6Net = false)
    {
        if (profile == "hc2000-v2-hdd-cpm3-v1") return ""; // Startup profile only; --cpm-cold-boot selects CP/M 2.2.
        var set = CapabilityCatalog.FromProfile(profile, cpuTurbo: turbo, driverCount: driverCount, x2Vga: x2Vga, x3Sound: x3Sound, x4Math: x4Math, x6Net: x6Net) ?? throw new ArgumentException("Unknown profile " + profile, nameof(profile));
        var flags = new List<string>();
        foreach (var row in CapabilityCatalog.SurfaceRows)
        {
            if (row.Name == "text-overlay" || set.Get(row.Name) is not { } value) continue;
            if (!row.TakesValue) { if (value == "true") flags.Add("--" + row.Name); }
            else if (value != row.Default || row.Name == "driver-z80") flags.Add("--" + row.Name + " " + value);
        }
        return string.Join(' ', flags);
    }

    private readonly Dictionary<string, RadioButton> picks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioButton> turboPicks = new(StringComparer.Ordinal);
    private readonly StackPanel turboRow, turboSegments, extraOptions, extraDetails, driverRow;
    private readonly ComboBox driverCount;
    private readonly CheckBox x2Vga, x3Sound, x4Math, x6Net, x6Gateway;
    private readonly TextBlock turboUnavailable, turboReset, title, description, commandLine;
    private readonly Button copyCommandLine;
    private readonly string? unsupportedProfile;
    private Grid? hardwareLayout;
    private StackPanel? machineDetails;
    private bool hardwareWide;
    private string? selectedProfile;
    private int selectedTurbo = 1;
    private int selectedDriverCount;
    private bool selectedX2Vga;
    private bool selectedX3Sound;
    private bool selectedX4Math;
    private bool selectedX6Net;
    private bool selectedX6Gateway;
    private bool userChanged;
    private bool seeding = true;

    private void UpdateHardwareLayout(bool wide)
    {
        if (hardwareWide == wide && hardwareLayout is not null && hardwareLayout.ColumnDefinitions.Count > 0) return;
        hardwareWide = wide;
        if (hardwareLayout is null || machineDetails is null) return;
        hardwareLayout.ColumnDefinitions = new ColumnDefinitions(wide ? "*,320" : "*");
        hardwareLayout.RowDefinitions = new RowDefinitions(wide ? "Auto" : "Auto,Auto");
        Grid.SetColumn(machineDetails, wide ? 1 : 0);
        Grid.SetRow(machineDetails, wide ? 0 : 1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0)
            UpdateHardwareLayout(availableSize.Width >= 700);
        return base.MeasureOverride(availableSize);
    }

    /// <summary>True when the selected machine takes the raw HDD volumes (the raw HDD, X5 HDD and C:-boot profiles, the
    /// combined workstation or the full workstation).</summary>
    public bool HddEnabled => selectedProfile is "hc2000-v2-hdd-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-hdd-cboot-v1" or "hc2000-v2-hdd-cpm3-v1"
        or "hc2000-v2-combined-workstation-v1" or "hc2000-v2-combined-workstation-v2" or "hc2000-v2-full-workstation-v1";

    /// <summary>The selected admitted profile; null while an unsupported stored profile awaits a choice.</summary>
    public string? SelectedProfile => selectedProfile;

    public event EventHandler? HddEnabledChanged;

    /// <summary>Raised on every user change of the machine or its options.</summary>
    public event EventHandler? Changed;

    public StartupHardwarePanel(MachineConfiguration initial)
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        T Part<T>(string name) where T : Control => this.FindControl<T>(name) ?? throw new InvalidOperationException("Missing " + name);
        hardwareLayout = Part<Grid>("HardwareLayout");
        machineDetails = Part<StackPanel>("MachineDetails");
        UpdateHardwareLayout(false);
        SizeChanged += (_, change) =>
        {
            if (change.NewSize.Width > 0)
                UpdateHardwareLayout(change.NewSize.Width >= 700);
        };
        var groups = Part<StackPanel>("MachineGroups");
        extraOptions = Part<StackPanel>("ExtraOptions");
        extraDetails = Part<StackPanel>("ExtraDetails");
        turboRow = Part<StackPanel>("TurboRow");
        turboSegments = Part<StackPanel>("TurboSegments");
        driverRow = Part<StackPanel>("DriverRow");
        driverCount = Part<ComboBox>("DriverCount");
        x2Vga = Part<CheckBox>("X2Vga");
        x2Vga.IsCheckedChanged += (_, _) => OnX2Vga();
        x3Sound = Part<CheckBox>("X3Sound");
        x3Sound.IsCheckedChanged += (_, _) => OnX3Sound();
        x4Math = Part<CheckBox>("X4Math");
        x4Math.IsCheckedChanged += (_, _) => OnX4Math();
        x6Net = Part<CheckBox>("X6Net");
        x6Net.IsCheckedChanged += (_, _) => OnX6Net();
        x6Gateway = Part<CheckBox>("X6Gateway");
        x6Gateway.IsCheckedChanged += (_, _) => OnX6Gateway();
        driverCount.ItemsSource = CapabilityCatalog.Find("driver-z80")!.AdmittedValues;
        driverCount.SelectionChanged += (_, _) => OnDriverCount();
        turboUnavailable = Part<TextBlock>("TurboUnavailable");
        turboReset = Part<TextBlock>("TurboResetNote");
        title = Part<TextBlock>("SelectedMachineTitle");
        description = Part<TextBlock>("PrestartHardwareDescription");
        commandLine = Part<TextBlock>("HardwareCommandLine");

        foreach (var group in Cards.GroupBy(card => card.Group))
        {
            var block = new StackPanel { Margin = new Avalonia.Thickness(0, 0, 20, 4) };
            block.Children.Add(new TextBlock { Text = group.Key, Classes = { "groupHeader" } });
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var card in group)
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                head.Children.Add(new Border { Classes = { "tileBadge" }, Child = new TextBlock { Text = card.Badge } });
                var body = new StackPanel { Spacing = 6 };
                body.Children.Add(head);
                body.Children.Add(new TextBlock { Text = card.Title, Classes = { "tileTitle" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                body.Children.Add(new TextBlock { Text = card.Caption, Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                var pick = new RadioButton { Name = "machine-" + card.Profile, GroupName = "StartupMachine", Content = body, Classes = { "machineTile" } };
                Avalonia.Automation.AutomationProperties.SetName(pick, card.Title);
                ToolTip.SetTip(pick, Describe(card.Profile, 1));
                pick.IsCheckedChanged += (_, _) => { if (pick.IsChecked == true) OnPicked(card.Profile); };
                picks[card.Profile] = pick;
                row.Children.Add(pick);
            }
            block.Children.Add(row);
            groups.Children.Add(block);
        }
        foreach (var value in CapabilityCatalog.Find("cpu-turbo")!.AdmittedValues)
        {
            var segment = new RadioButton { Name = "turbo-" + value, GroupName = "StartupTurbo", Content = value + "x", Classes = { "segment" } };
            segment.IsCheckedChanged += (_, _) => { if (segment.IsChecked == true) OnTurbo(int.Parse(value, CultureInfo.InvariantCulture)); };
            turboPicks[value] = segment;
            turboSegments.Children.Add(segment);
        }
        copyCommandLine = Part<Button>("CopyCommandLine");
        copyCommandLine.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && commandLine.Text is { Length: > 0 } flags)
                await clipboard.SetTextAsync(flags);
        };

        if (picks.ContainsKey(initial.Profile))
        {
            selectedProfile = initial.Profile;
            selectedTurbo = TurboValues(initial.Profile).Contains(initial.CpuTurboMultiplier.ToString(CultureInfo.InvariantCulture))
                ? initial.CpuTurboMultiplier : 1;
            selectedDriverCount = initial.ExpansionDriverCount is >= 0 and <= 8 ? initial.ExpansionDriverCount : 0;
            selectedX2Vga = initial.X2Vga;
            selectedX3Sound = initial.X3Sound;
            selectedX4Math = initial.X4Math;
            selectedX6Net = initial.X6Net && selectedDriverCount >= 2;
            selectedX6Gateway = selectedX6Net && initial.NetworkGateway;
            picks[initial.Profile].IsChecked = true;
        }
        else unsupportedProfile = initial.Profile;
        Refresh();
        seeding = false;
    }

    /// <summary>Puts a machine-wide option (emulation speed, its notices) in the Options card.</summary>
    public void AddOption(Control option) => extraOptions.Children.Add(option);

    /// <summary>Puts a fact about every machine (the ROM) in the Your machine card.</summary>
    public void AddDetail(Control detail) => extraDetails.Children.Add(detail);

    /// <summary>Select a machine as a user would (automation and tests).</summary>
    public void Choose(string profile, int turboMultiplier = 1)
    {
        if (!picks.TryGetValue(profile, out var pick)) throw new ArgumentException("Not an admitted machine: " + profile, nameof(profile));
        if (pick.IsChecked != true) pick.IsChecked = true;
        else OnPicked(profile);
        if (turboMultiplier != 1)
        {
            var key = turboMultiplier.ToString(CultureInfo.InvariantCulture);
            if (!TurboValues(profile).Contains(key)) throw new ArgumentException($"{profile} does not take turbo {key}x.", nameof(turboMultiplier));
            turboPicks[key].IsChecked = true;
        }
    }

    /// <summary>Identity until edited; after a user choice the draft takes the picked profile and the turbo it admits.
    /// An unsupported stored profile stays unchanged until a machine is picked, and then refuses at Validate as before.</summary>
    public MachineConfiguration ApplyTo(MachineConfiguration draft) =>
        userChanged && selectedProfile is not null ? draft with { Profile = selectedProfile, CpuTurboMultiplier = selectedTurbo,
            ExpansionDriverCount = selectedDriverCount, X2Vga = selectedX2Vga, X3Sound = selectedX3Sound, X4Math = selectedX4Math,
            X6Net = selectedX6Net, NetworkGateway = selectedX6Net && selectedX6Gateway } : draft;

    private void OnX2Vga()
    {
        if (seeding || selectedX2Vga == (x2Vga.IsChecked == true)) return;
        selectedX2Vga = x2Vga.IsChecked == true;
        userChanged = true;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnX3Sound()
    {
        if (seeding || selectedX3Sound == (x3Sound.IsChecked == true)) return;
        selectedX3Sound = x3Sound.IsChecked == true;
        userChanged = true;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnX4Math()
    {
        if (seeding || selectedX4Math == (x4Math.IsChecked == true)) return;
        selectedX4Math = x4Math.IsChecked == true;
        userChanged = true;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The X6 card is handed between two driver Z80s, so choosing it raises the roster to two; a roster below two
    // takes it away (OnDriverCount). Choosing it switches its gateway on; the gateway
    // box can still turn it off.
    private void OnX6Net()
    {
        if (seeding || selectedX6Net == (x6Net.IsChecked == true)) return;
        selectedX6Net = x6Net.IsChecked == true;
        if (selectedX6Net) selectedX6Gateway = true;
        if (selectedX6Net && selectedDriverCount < 2) selectedDriverCount = 2;
        userChanged = true;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The owner's gateway switch for the machine's next start; it means nothing without the card.
    private void OnX6Gateway()
    {
        if (seeding || selectedX6Gateway == (x6Gateway.IsChecked == true)) return;
        selectedX6Gateway = x6Gateway.IsChecked == true;
        userChanged = true;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnDriverCount()
    {
        if (seeding || driverCount.SelectedItem is not string value) return;
        var count = int.Parse(value, CultureInfo.InvariantCulture);
        if (count == selectedDriverCount) return;
        selectedDriverCount = count;
        if (count < 2) selectedX6Net = false;
        userChanged = true;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTurbo(int multiplier)
    {
        if (seeding || multiplier == selectedTurbo) return;
        selectedTurbo = multiplier;
        userChanged = true;
        turboReset.IsVisible = false;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPicked(string profile)
    {
        if (seeding) return;
        var hddBefore = HddEnabled;
        var turboBefore = selectedTurbo;
        var before = selectedProfile;
        selectedProfile = profile;
        // The full workstation comes with every card on: two driver units, X2, X3 and X6 with the gateway
        // (hc2000-full-workstation-default). X4 is not part of it.
        if (profile == "hc2000-v2-full-workstation-v1" && before != profile)
        { selectedDriverCount = 2; selectedX2Vga = selectedX3Sound = selectedX6Net = selectedX6Gateway = true; }
        if (profile is not ("hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1" or "hc2000-v2-full-workstation-v1"))
        { selectedDriverCount = 0; selectedX2Vga = false; selectedX3Sound = false; selectedX4Math = false; selectedX6Net = false; }
        if (profile == "hc2000-v2-paged-expansion-x5-hdd-guest-v1")
            selectedX2Vga = selectedX3Sound = selectedX4Math = false;
        if (profile == "hc2000-v2-full-workstation-v1") selectedX4Math = false;
        if (!TurboValues(profile).Contains(selectedTurbo.ToString(CultureInfo.InvariantCulture))) selectedTurbo = 1;
        turboReset.IsVisible = turboBefore != 1 && selectedTurbo == 1;
        userChanged = true;
        Refresh();
        if (hddBefore != HddEnabled) HddEnabledChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        var wasSeeding = seeding;
        seeding = true;
        try
        {
            if (selectedProfile is null)
            {
                turboRow.IsVisible = turboUnavailable.IsVisible = false;
                driverRow.IsVisible = false;
                title.Text = "No machine chosen";
                commandLine.Text = "";
                description.Text = "Unsupported stored hardware: " + unsupportedProfile
                    + ". Pick a machine on the left; until then this stored choice is kept unchanged and cannot start.";
                return;
            }
            var values = TurboValues(selectedProfile);
            driverRow.IsVisible = selectedProfile is "hc2000-v2-paged-expansion-x5-v1" or "hc2000-v2-paged-expansion-x5-hdd-guest-v1"
                or "hc2000-v2-full-workstation-v1";
            x2Vga.IsVisible = x3Sound.IsVisible = x4Math.IsVisible = selectedProfile != "hc2000-v2-paged-expansion-x5-hdd-guest-v1";
            if (selectedProfile == "hc2000-v2-full-workstation-v1") x4Math.IsVisible = false;
            driverCount.SelectedItem = selectedDriverCount.ToString(CultureInfo.InvariantCulture);
            x2Vga.IsChecked = selectedX2Vga;
            x3Sound.IsChecked = selectedX3Sound;
            x4Math.IsChecked = selectedX4Math;
            x6Net.IsChecked = selectedX6Net;
            x6Gateway.IsChecked = selectedX6Net && selectedX6Gateway;
            x6Gateway.IsEnabled = selectedX6Net;
            turboRow.IsVisible = values.Count > 1;
            turboUnavailable.IsVisible = values.Count <= 1;
            foreach (var (value, segment) in turboPicks)
            {
                segment.IsVisible = values.Contains(value);
                segment.IsChecked = value == selectedTurbo.ToString(CultureInfo.InvariantCulture);
            }
            title.Text = Cards.First(card => card.Profile == selectedProfile).Title;
            description.Text = Describe(selectedProfile, selectedTurbo);
            var hasCommandLine = selectedProfile != "hc2000-v2-hdd-cpm3-v1";
            copyCommandLine.IsEnabled = hasCommandLine;
            commandLine.Text = !hasCommandLine ? "Select this profile here and supply a prepared raw C: image."
                : CommandLine(selectedProfile, selectedTurbo, selectedDriverCount, selectedX2Vga, selectedX3Sound, selectedX4Math, selectedX6Net) is { Length: > 0 } flags ? flags : "(no flags: the default machine)";
        }
        finally { seeding = wasSeeding; }
    }

    private static string Describe(string profile, int turboMultiplier) => profile switch
    {
        "hc2000-v2" => "The original machine: 64 KiB RAM, no extended RAM or HDD controller." + TurboSentence(turboMultiplier),
        "hc2000-v2-ram-banks-v1" => "64 KiB base + 1 MiB banked RAM (1,114,112 bytes total). Guest software selects the bank; CP/M's TPA does not grow by itself." + TurboSentence(turboMultiplier),
        "hc2000-v2-paged-v1" => "64 KiB base + 1 GiB paged RAM. Each machine allocates a contiguous 1 GiB store at Start; available memory and snapshot limits still apply.",
        "hc2000-v2-paged-m-v1" => "64 KiB base + 1 GiB with the exact M-transfer view. Allocates a contiguous 1 GiB per machine; M firmware/driver installation is not supplied.",
        "hc2000-v2-paged-m-v2" => "64 KiB base + 1 GiB with the M-transfer v2 view and the 8 MiB M: (pages 0-511). Allocates a contiguous 1 GiB per machine; M firmware/driver installation is not supplied.",
        "hc2000-v2-paged-expansion-v1" => "64 KiB base + 1 GiB with the M-transfer v1 view and sixteen expansion CPUs. Allocates a contiguous 1 GiB per machine; a guest job loader is not supplied.",
        "hc2000-v2-paged-expansion-x5-v1" => "64 KiB base + 1 GiB with managed main and driver maps, sixteen compute CPUs and up to eight driver CPUs. Allocates a contiguous 1 GiB per machine; guest drivers are not supplied.",
        "hc2000-v2-paged-expansion-x5-hdd-guest-v1" => "X5 managed CPUs and cold hard disks C: to F:. Allocates 1 GiB; guest drivers are needed. Live HDD switching and snapshots are unavailable.",
        "hc2000-v2-cpnet-v1" => "64 KiB base + 1 GiB with the M-transfer v1 view, sixteen expansion CPUs and the CP/NET network card. The host gateway is off by default: every frame is refused with no target until one is configured.",
        "hc2000-v2-blockport-v1" => "64 KiB RAM and the faithful block disk controller.",
        "hc2000-v2-tape-control-v1" => "64 KiB RAM and the tape control register (0091).",
        "hc2000-v2-paged-tape-v2" => "Paged RAM with tape control and the CP/M tape volume. Each machine allocates a contiguous 1 GiB store at Start.",
        "hc2000-v2-hdd-v1" => "64 KiB RAM plus the raw HDD controller. An empty draft gets the bundled C: utilities and formatted empty D:/E:/F: volumes; change them in Media. The original BIOS does not read the hard disk, so guest software is needed. Guest writes go to private copies, never to installed images; live switching and HDD snapshots are not available.",
        "hc2000-v2-combined-workstation-v1" => "The qualified combined CP/M workstation: the 1 GiB paged store with the 8 MiB M: (pages 0-511), four raw 8 MiB hard-disk volumes C: to F:, the CP/M tape volume on its free letter and the sixteen side Z80 processors. CPU turbo is not composed; guest software installs the C-F, tape and compute doors. Each machine allocates a contiguous 1 GiB store at Start.",
        "hc2000-v2-combined-workstation-v2" => "The qualified combined CP/M workstation: the 1 GiB paged store with the 8 MiB M: (pages 0-511), four raw 8 MiB hard-disk volumes C: to F:, the CP/M tape volume on its free letter and the sixteen side Z80 processors. C-F uses the guest driver, so the HDINST resident installs the drives in a guest session; host-side live switching is refused for this machine. CPU turbo is not composed. Each machine allocates a contiguous 1 GiB store at Start.",
        "hc2000-v2-hdd-cpm3-v1" => "Boots nonbanked CP/M 3 from a prepared raw 8 MiB C: image. No floppy is needed.",
        "hc2000-v2-hdd-cboot-v1" => "64 KiB RAM, the raw HDD controller and the direct-C:-boot CP/M ROM. At power-on and at every reset the machine enters the CP/M boot stub, which loads the boot image from C: and starts CP/M there - no floppy needed. C: must keep its boot image; guest writes go to private copies, never to installed images.",
        "hc2000-v2-full-workstation-v1" => "The full workstation: CP/M boots from C: at power-on and at every reset, into the folder shell, with 1 GiB of paged RAM, hard disks C: to F:, the 8 MiB M: drive, the CP/M tape volume, the VGA screen, sound, the network card with the gateway on and sixteen side Z80 processors. The drives run from paged RAM, so CP/M programs keep the stock TPA. Live HDD switching and snapshots are unavailable. Each machine allocates a contiguous 1 GiB store at Start.",
        _ => "This stored hardware is not supported by ordinary Start."
    };

    private static string TurboSentence(int turboMultiplier) => turboMultiplier == 1 ? "" : $" CPU turbo {turboMultiplier}x.";
}
