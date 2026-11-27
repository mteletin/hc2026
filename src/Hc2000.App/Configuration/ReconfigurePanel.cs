using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Hc2000.App.Audio;
using Hc2000.App.Settings;
using Hc2000.App.Workspace;

namespace Hc2000.App.Configuration;

/// <summary>Reconfigure + reset for ONE selected running machine. It edits the live controls
/// (speed, requested zoom, keyboard mapping) of the bound draft only: the panel has no media fields,
/// so the mounted A/B media and writeback choices are structurally unchanged, and the window-side
/// apply re-checks that policy and the writeback reset guard before it resets only that machine.
/// Apply never persists: saving stays the explicit prestart save and Settings… only.</summary>
internal sealed class ReconfigurePanel : UserControl
{
    private readonly TextBox speed, scale, border;
    private readonly TextBox mainCpu;
    private readonly ComboBox keyboard;
    private readonly TextBlock status = new() { Text = "Apply keeps mounted media and restarts only this machine.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "notice" } };
    private readonly Button apply = new() { Name = "ReconfigureApply", Content = "Apply and reset this machine", Focusable = true, Classes = { "primaryAction" } };
    private readonly SoundSpeedNotice soundNotice;
    private readonly StackPanel turboChoices = new() { Spacing = 6 };
    private MachineDraftTarget? target;
    private MachineConfiguration? original;
    private SettingsEditor? editor;
    private bool applying, syncingBorder;
    public Func<MachineDraftTarget, MachineConfiguration, Task<string?>>? ApplyRequested { get; set; }
    public event Action<string>? BorderChanged;
    public string? Status => status.Text;

    // The field controls are built in code (not XAML), so they never join the window NameScope and
    // FindControl can't reach them; these accessors are the only lookup path (tests, future code).
    public TextBox SpeedField => speed;
    public TextBox ScaleField => scale;
    public TextBox BorderField => border;
    public ComboBox KeyboardField => keyboard;
    public Button ApplyButton => apply;
    public SoundSpeedNotice SoundNotice => soundNotice;
    /// <summary>The bound main-CPU clock field (hc2000-accelerated-clock-domains): the segments
    /// write it, Bind seeds it, Build reads it — the same pattern as the other hidden bound fields.</summary>
    public TextBox MainCpuField => mainCpu;

    public void SyncBorder(string value)
    {
        syncingBorder = true;
        try
        {
            if (editor is not null) editor.Border = value;
            border.Text = value;
        }
        finally { syncingBorder = false; }
    }

    public ReconfigurePanel()
    {
        var form = new StackPanel { Spacing = 6 };
        var hardware = new StackPanel { Spacing = 6 };
        var display = new StackPanel { Spacing = 6 };
        var input = new StackPanel { Spacing = 6 };
        form.Children.Add(new TextBlock { Text = "Change this machine's settings. Apply restarts only this machine; mounted media stays in place.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        speed = new TextBox { Name = "ReconfigureSpeed", IsVisible = false };
        hardware.Children.Add(speed);
        ChoiceSegments.Speed(hardware, speed);
        // hc2000-accelerated-clock-domains: the live main-Z80 clock choice. Unlike CPU turbo (a
        // new-machine choice, disabled below), a main-CPU clock switch applies live at the next
        // safe boundary without a reset; an unsafe boundary is refused and the previous selection
        // and state are kept. 280 MHz is a target: the status line shows achieved speed or overload.
        mainCpu = new TextBox { Name = "ReconfigureMainCpu", Text = Hc2000.Machine.MainCpuSpeedInfo.Auto, IsVisible = false };
        hardware.Children.Add(mainCpu);
        ChoiceSegments.Build(hardware, "Main CPU clock", mainCpu, new[]
        {
            ("auto", "Auto", "The machine's own speed: 140 MHz on the CP/M workstations, the faithful 3.5 MHz on Classic and on the BASIC expansion machines, whose tape loading needs it."),
            ("faithful", "Faithful", "The original 3.5 MHz class (3,546,895 T/s); video and the frame interrupt stay at the faithful cadence."),
            ("7MHz", "7 MHz (target)", "A 7,000,000 T/s target; video and the frame interrupt stay at the faithful cadence. The status line shows the achieved speed."),
            ("14MHz", "14 MHz (target)", "A 14,000,000 T/s target; video and the frame interrupt stay at the faithful cadence. The status line shows the achieved speed."),
            ("28MHz", "28 MHz (target)", "A 28,000,000 T/s target; video and the frame interrupt stay at the faithful cadence. The status line shows the achieved speed."),
            ("140MHz", "140 MHz (target)", "A 140,000,000 T/s target, off spec: from 140 MHz up timing is relaxed for speed. The status line shows the achieved speed or overload."),
            ("280MHz", "280 MHz (target)", "A 280,000,000 T/s target, not a promise, off spec like 140 MHz: the status line shows the achieved speed or overload.")
        });
        // The running machine's speed is what its beeper hears, so the silence is stated here, beside
        // the field, and its one control puts the field back to 100 and applies it to this machine.
        soundNotice = new SoundSpeedNotice(speed);
        hardware.Children.Add(soundNotice);
        hardware.Children.Add(turboChoices);
        hardware.Children.Add(new TextBlock { Text = "Choose CPU turbo when starting a new machine.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted" } });
        soundNotice.Restored += async (_, _) => await ApplyAsync();
        scale = new TextBox { Name = "ReconfigureScale", IsVisible = false };
        border = new TextBox { Name = "ReconfigureBorder", Text = "Thin", IsVisible = false };
        display.Children.Add(scale);
        display.Children.Add(border);
        ChoiceSegments.Zoom(display, scale);
        ChoiceSegments.Border(display, border);
        border.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty || syncingBorder) return;
            var next = (border.Text ?? "").Trim();
            if (next is "Thin" or "None" or "Full")
                BorderChanged?.Invoke(next);
        };
        input.Children.Add(new TextBlock { Text = "Keyboard mapping" });
        keyboard = new ComboBox { Name = "ReconfigureKeyboard", ItemsSource = new[] { "Positional", "Symbolic" }, SelectedIndex = 0 };
        input.Children.Add(keyboard);
        form.Children.Add(ConfigurationSections.Tabs("RunningConfigurationTopics",
            ConfigurationSections.Topic("Hardware", hardware), ConfigurationSections.Topic("Display", display),
            ConfigurationSections.Topic("Input", input),
            ConfigurationSections.Topic("Media", new TextBlock { Text = "Use Media in the side bar to insert, browse or eject disks and tapes.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }),
            ConfigurationSections.Topic("Audio", new TextBlock { Text = "Sound plays only while the machine runs at normal speed.", TextWrapping = Avalonia.Media.TextWrapping.Wrap })
        ));
        form.Children.Add(status);
        form.Children.Add(apply);
        apply.Click += async (_, _) => await ApplyAsync();
        Content = form;
    }

    /// <summary>Bind the panel to the selected running tab's current draft. Rebinding replaces any
    /// previous binding; an in-flight apply keeps its captured target and refuses a second start.</summary>
    public void Bind(MachineDraftTarget target, MachineConfiguration configuration)
    {
        this.target = target;
        original = configuration;
        editor = new SettingsEditor(configuration.Preferences());
        speed.Text = editor.SpeedPercent;
        scale.Text = editor.Scale;
        keyboard.SelectedIndex = configuration.KeyboardMode == "Positional" ? 0 : 1;
        mainCpu.Text = configuration.MainCpuTarget;
        turboChoices.Children.Clear();
        var turbo = new TextBox { Name = "ReconfigureTurbo", Text = configuration.CpuTurboMultiplier.ToString(CultureInfo.InvariantCulture), IsVisible = false };
        turboChoices.Children.Add(turbo);
        ChoiceSegments.Build(turboChoices, "CPU turbo", turbo, StartupHardwarePanel.TurboValues(configuration.Profile)
            .Select(value => (value, value + "x", "Choose CPU turbo when starting a new machine.")).ToArray()).IsEnabled = false;
        status.Text = "Apply keeps mounted media and restarts only this machine.";
    }

    /// <summary>Parsed, validated draft from the current field values. Throws ArgumentException on
    /// invalid speed/scale, exactly like the prestart panel; the caller surfaces it as a refusal.</summary>
    public MachineConfiguration Build()
    {
        if (original is null) throw new InvalidOperationException("The reconfigure panel is not bound to a machine.");
        editor!.SpeedPercent = speed.Text ?? "";
        editor.Scale = scale.Text ?? "";
        var preferences = editor.Build();
        var draft = original with
        {
            SpeedPercent = preferences.SpeedPercent,
            Scale = preferences.Scale,
            KeyboardMode = keyboard.SelectedIndex == 0 ? "Positional" : "Symbolic",
            MainCpuTarget = (mainCpu.Text ?? "").Trim()
        };
        draft.Validate();
        return draft;
    }

    /// <summary>Apply the draft and reset only the bound machine. Returns the window-side refusal
    /// (null on success); invalid drafts and guard refusals leave the machine untouched.</summary>
    public async Task<string?> ApplyAsync()
    {
        if (applying || ApplyRequested is null)
        {
            status.Text = "Not applied: apply is busy or unavailable.";
            return "apply is busy or unavailable.";
        }
        if (target is null || original is null)
        {
            status.Text = "Not applied: this machine is not running.";
            return "this machine is not running.";
        }
        applying = true;
        apply.IsEnabled = false;
        var captured = target.Value;
        try
        {
            var draft = Build();
            // Classify the draft BEFORE ApplyRequested: on success the window rebinds this panel to
            // the committed draft, which would make any post-apply comparison against the old
            // original degenerate (it is why the live-switch line never showed before demo #189).
            var noChange = IsNoChange(draft);
            var liveSpeedOnly = IsLiveSpeedOnly(draft);
            status.Text = "Applying…";
            var error = await ApplyRequested(captured, draft);
            status.Text = error is null
                ? (noChange ? "No changes; this machine kept running. Settings were not saved."
                    : liveSpeedOnly ? "Applied: the main CPU clock switched live; this machine kept running. Settings were not saved."
                    : "Applied: only this machine restarted. Settings were not saved.")
                : "Not applied: " + error;
            return error;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            status.Text = "Not applied: " + ex.Message;
            return ex.Message;
        }
        finally
        {
            applying = false;
            apply.IsEnabled = true;
        }
    }

    private static bool IsExpected(Exception ex) => ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException or OverflowException;

    // A draft that changed nothing but the main-CPU clock takes the live switch lane (no reset).
    private bool IsLiveSpeedOnly(MachineConfiguration draft) => original is not null
        && draft.MainCpuTarget != original.MainCpuTarget
        && draft.SpeedPercent == original.SpeedPercent
        && draft.Scale == original.Scale
        && draft.KeyboardMode == original.KeyboardMode;

    // A draft that changed nothing is a no-op: the machine keeps running untouched (demo #189).
    private bool IsNoChange(MachineConfiguration draft) => original is not null
        && draft.MainCpuTarget == original.MainCpuTarget
        && draft.SpeedPercent == original.SpeedPercent
        && draft.Scale == original.Scale
        && draft.KeyboardMode == original.KeyboardMode;
}
