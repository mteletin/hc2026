using Avalonia.Controls;
using Hc2000.App.Configuration;
using Hc2000.App.Input;
using Hc2000.App.Workspace;
using HostSpeedCheckResult = Hc2000.App.Runtime.HostSpeedCheck.Result;
using Hc2000.Machine;

namespace Hc2000.App;

// hc2000-speed-140-turbo-280: the Turbo button. It takes the selected running extended machine to 280 MHz (its side
// Z80s on the machine's clock too) and back to the speed chosen for it, without a restart: the owner applies the switch
// at the next safe point. Turbo is never saved; a machine starts at its chosen speed. Source: the owner,
// 2026-11-25: "can we go 140 default with a TURBO button that puts it at 280?".
public partial class MainWindow
{
    private const long TurboTStatesPerSecond = 280_000_000;
    private bool syncingTurbo;

    private Avalonia.Controls.Primitives.ToggleButton TurboButton =>
        this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("TurboToggle")!;

    private void WireTurbo() => TurboButton.IsCheckedChanged += (_, _) =>
    {
        if (!syncingTurbo) TurboSwitched(TurboButton.IsChecked == true);
    };

    /// <summary>Ctrl+Shift+T: the same switch from the keyboard, for the selected running extended machine.</summary>
    internal bool ToggleTurbo()
    {
        var session = CurrentSession;
        if (TurboTarget(session) is null) return false;
        TurboSwitched(!TurboOn(session));
        return true;
    }

    // The configuration of the selected running machine.
    private MachineConfiguration? RunningConfiguration(MachineSession session)
    {
        if (session.Worker is not { Completion.IsCompleted: false }) return null;
        return workspace.Selected?.Configuration ?? session.StartingConfiguration;
    }

    // The configuration of the selected machine, when it runs an extended profile whose chosen speed is not already
    // Turbo's 280 MHz (there Turbo would change nothing, and its off state could never show).
    private MachineConfiguration? TurboTarget(MachineSession session) =>
        RunningConfiguration(session) is { } configuration && MachineProfile.IsExtended(configuration.Profile)
            && MainCpuSpeedInfo.Resolve(configuration.MainCpuTarget, configuration.Profile) != MainCpuSpeed.MHz280
            ? configuration : null;

    // The machines the boot check's advice is for: those that start CP/M. On the BASIC expansion machines the ROM's tape
    // loader needs the real clock, so a one-click faster speed would break loading a tape there.
    private MachineConfiguration? AdviceTarget(MachineSession session) =>
        RunningConfiguration(session) is { } configuration && MachineProfile.StartsCpm(configuration.Profile) ? configuration : null;

    private static bool TurboOn(MachineSession session) =>
        session.TurboRequested ?? session.Worker?.RateLatest?.RequestedTStatesPerSecond == TurboTStatesPerSecond;

    private void TurboSwitched(bool on)
    {
        var session = CurrentSession;
        if (TurboTarget(session) is not { } configuration) return;
        var chosen = MainCpuSpeedInfo.Resolve(configuration.MainCpuTarget, configuration.Profile);
        var target = on ? MainCpuSpeed.MHz280 : chosen;
        session.TurboRequested = on;
        session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.SetMainCpuSpeed, MainCpuSpeedIndex: (int)target));
        this.FindControl<TextBlock>("StatusLine")!.Text = on
            ? "Turbo: this machine runs at 280 MHz, its side Z80s too; click Turbo again for its chosen speed."
            : $"Turbo off: this machine runs at its chosen speed, {target.Setting()}.";
        RenderTurbo(session);
    }

    /// <summary>Shows the Turbo button for the selected running extended machine (each presentation tick). The pending
    /// request ends once the machine's requested rate shows it.</summary>
    private void RenderTurbo(MachineSession session)
    {
        var button = TurboButton;
        var configuration = TurboTarget(session);
        button.IsVisible = configuration is not null;
        if (configuration is null) { session.TurboRequested = null; return; }
        var running280 = session.Worker?.RateLatest?.RequestedTStatesPerSecond == TurboTStatesPerSecond;
        if (session.TurboRequested == running280) session.TurboRequested = null;
        var on = TurboOn(session);
        syncingTurbo = true;
        try { button.IsChecked = on; }
        finally { syncingTurbo = false; }
        button.Content = session.TurboRequested is not null ? (on ? "Turbo 280 …" : "Turbo off …") : on ? "Turbo 280 ON" : "Turbo 280";
    }

    // ---- The boot check's recommendation (HostSpeedCheck; shown beside Turbo) ----
    private HostSpeedCheckResult? hostSpeed;

    private Button SpeedAdviceButton => this.FindControl<Button>("SpeedAdvice")!;

    private void WireSpeedAdvice() => SpeedAdviceButton.Click += async (_, _) => await ApplySpeedAdviceAsync();

    /// <summary>The boot check's result (the app's entry starts the check; tests hand one in). The window shows the
    /// recommendation beside Turbo whenever it differs from the selected extended machine's chosen speed.</summary>
    internal void ShowHostSpeed(HostSpeedCheckResult result)
    {
        hostSpeed = result;
        RenderSpeedAdvice(CurrentSession);
    }

    private void RenderSpeedAdvice(MachineSession session)
    {
        var button = SpeedAdviceButton;
        var configuration = AdviceTarget(session);
        if (hostSpeed is not { } result || configuration is null
            || MainCpuSpeedInfo.Resolve(configuration.MainCpuTarget, configuration.Profile) == result.Recommended)
        {
            button.IsVisible = false;
            return;
        }
        button.IsVisible = true;
        button.Content = $"This host carries {result.Recommended.Setting()} · Apply";
        ToolTip.SetTip(button, $"Measured when the app started (machine time over host time): {result.Describe()}. "
            + "Apply sets this machine's speed; nothing changes until you click.");
    }

    /// <summary>One click: the selected running machine takes the recommended speed (a live switch, no reset), as if
    /// chosen in its settings. Turbo ends with it.</summary>
    private async Task ApplySpeedAdviceAsync()
    {
        var session = CurrentSession;
        if (hostSpeed is not { } result || AdviceTarget(session) is not { } configuration) return;
        session.TurboRequested = null;
        var chosen = configuration with { MainCpuTarget = result.Recommended.Setting() };
        string? error = null;
        if (workspace.Selected is { } tab && workspace.CaptureSelectedDraft() is { } target)
            error = await ApplyRunningDraftAsync(target, chosen);
        else
        {
            // The window's own machine (started with --start, no tab): a live switch, and its starting configuration
            // records the choice so Turbo comes back to it.
            session.StartingConfiguration = chosen;
            session.InputQueue.Enqueue(new RuntimeControl(InputControlKind.SetMainCpuSpeed, MainCpuSpeedIndex: (int)result.Recommended));
        }
        this.FindControl<TextBlock>("StatusLine")!.Text = error is null
            ? $"This machine now runs at {result.Recommended.Setting()}, the speed this host carries; save it with Settings to keep it."
            : "The recommended speed was not applied: " + error;
        RenderSpeedAdvice(session);
    }
}
