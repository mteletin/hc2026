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
    private readonly TextBox speed, scale;
    private readonly ComboBox keyboard;
    private readonly TextBlock status = new() { Text = "Apply validates the draft, keeps the mounted media and writeback choices, and resets only this machine.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "notice" } };
    private readonly Button apply = new() { Name = "ReconfigureApply", Content = "Apply and reset this machine", Focusable = true, Classes = { "primaryAction" } };
    private readonly SoundSpeedNotice soundNotice;
    private MachineDraftTarget? target;
    private MachineConfiguration? original;
    private SettingsEditor? editor;
    private bool applying;
    public Func<MachineDraftTarget, MachineConfiguration, Task<string?>>? ApplyRequested { get; set; }
    public string? Status => status.Text;

    // The field controls are built in code (not XAML), so they never join the window NameScope and
    // FindControl can't reach them; these accessors are the only lookup path (tests, future code).
    public TextBox SpeedField => speed;
    public TextBox ScaleField => scale;
    public ComboBox KeyboardField => keyboard;
    public Button ApplyButton => apply;
    public SoundSpeedNotice SoundNotice => soundNotice;

    public ReconfigurePanel()
    {
        var form = new StackPanel { Spacing = 6 };
        form.Children.Add(new TextBlock { Text = "Reconfigure the running machine and reset only it. Unchanged media and writeback choices stay untouched; nothing is saved.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        speed = Field(form, "Speed: 0 (unlimited), 100, 200 or 400 percent (not CPU turbo)", "ReconfigureSpeed");
        // The running machine's speed is what its beeper hears, so the silence is stated here, beside
        // the field, and its one control puts the field back to 100 and applies it to this machine.
        soundNotice = new SoundSpeedNotice(speed);
        form.Children.Add(soundNotice);
        soundNotice.Restored += async (_, _) => await ApplyAsync();
        scale = Field(form, "Requested integer zoom: 1–8 (viewport fits available space)", "ReconfigureScale");
        form.Children.Add(new TextBlock { Text = "Keyboard mapping" });
        keyboard = new ComboBox { Name = "ReconfigureKeyboard", ItemsSource = new[] { "Positional", "Symbolic" }, SelectedIndex = 0 };
        form.Children.Add(keyboard);
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
        status.Text = "Apply validates the draft, keeps the mounted media and writeback choices, and resets only this machine.";
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
            KeyboardMode = keyboard.SelectedIndex == 0 ? "Positional" : "Symbolic"
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
            status.Text = "Applying…";
            var error = await ApplyRequested(captured, draft);
            status.Text = error is null
                ? "Applied; only this machine was reset. No settings saved."
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
    private static TextBox Field(StackPanel form, string label, string name)
    {
        form.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var box = new TextBox { Text = "", Name = name };
        form.Children.Add(box);
        return box;
    }
}
