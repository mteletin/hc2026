using Avalonia;
using Avalonia.Controls;
using Hc2000.App.Configuration;
using Hc2000.App.Settings;
using Hc2000.App.Workspace;

namespace Hc2000.App;

public partial class MainWindow
{
    private readonly SettingsStore configurationStore;
    // Presentation evidence from the successful prepared/owner-acknowledged cold Start, never
    // authority inferred from an edited profile string or from the currently selected tab label.
    private readonly Dictionary<MachineSession, string> adoptedProfiles = new();
    private readonly Dictionary<MachineSession, Runtime.EmulationWorker> displayedStops = new();
    private bool startingConfiguration;

    private void OpenNewMachineDraft()
    {
        if (closing || startingConfiguration) return;
        if (workspace.Tabs.Count >= MachineWorkspace.Capacity)
        {
            this.FindControl<TextBlock>("RuntimeStatus")!.Text = "Four machines are open. Close one before starting another; existing machines were not changed.";
            return;
        }
        var previous = prestart.InitialConfiguration;
        var number = 1;
        while (workspace.Tabs.Any(t => t.Configuration.Name == $"Machine {number}")) number++;
        // Only harmless preferences cross this boundary. A new record supplies a fresh Id,
        // faithful hardware, no media/writeback authority and no last-snapshot selection.
        var draft = new MachineConfiguration
        {
            Name = $"Machine {number}", SpeedPercent = previous.SpeedPercent, Scale = previous.Scale,
            KeyboardMode = previous.KeyboardMode, AudioPreference = previous.AudioPreference, KeyMap = previous.KeyMap
        };
        prestart = new PrestartPanel(configurationStore, initialConfiguration: draft, assetRoot: assetRootOverride);
        BindPrestartEvents(prestart);
        this.FindControl<ContentControl>("PrestartConfiguration")!.Content = prestart;
        this.FindControl<TextBlock>("RuntimeStatus")!.Text = "New draft only. Existing machines keep running; Start machine creates a separate machine.";
        RefreshShellView();
    }

    private void BindPrestartEvents(PrestartPanel panel)
    {
        panel.StartRequested = StartConfigurationAsync;
        panel.Started += (_, _) =>
        {
            // Saving runs after the actual Start receipt. Only hide that completed editor and
            // carry its save warning forward; never hide or overwrite a newer detached draft.
            panel.IsVisible = false;
            if (ReferenceEquals(prestart, panel))
            {
                if (CurrentSession.StartingConfiguration?.Id == panel.InitialConfiguration.Id)
                    this.FindControl<TextBlock>("RuntimeStatus")!.Text = panel.Status;
                RefreshShellView();
            }
        };
    }

    private void RefreshShellView()
    {
        var session = CurrentSession;
        var live = !session.Closed && session.Worker is { Completion.IsCompleted: false };
        this.FindControl<TextBlock>("SelectedMachineName")!.Text = workspace.Selected?.Configuration.Name ?? "HC2000";
        this.FindControl<StackPanel>("SelectedMachineHeading")!.IsVisible = workspace.Tabs.Count > 0 || live;
        this.FindControl<TextBlock>("EffectiveHardware")!.Text = adoptedProfiles.TryGetValue(session, out var profile)
            ? (live ? "Effective hardware: " : "Stopped hardware: ") + profile
            : session.Worker is not null ? "Attached runtime — no cold-start hardware claim." : "Draft only — no hardware adopted or guest executing.";
        this.FindControl<TextBlock>("EmptyWorkspace")!.IsVisible = workspace.Tabs.Count == 0 && !live;
        // The screen keeps the finite client slot left after the chrome (DockPanel); show it
        // exactly when the running workspace is, so a draft never takes the slot.
        var running = session.Worker is not null || Stage1SpectrumDemo.Enabled
            || launchOptions?.Start == true || launchOptions?.HddEnabled == true;
        this.FindControl<Control>("RunningWorkspace")!.IsVisible = running;
        this.FindControl<Control>("ScreenSlot")!.IsVisible = running;
        this.FindControl<Border>("ConfigurationCard")!.IsVisible = prestart.IsVisible;
        var add = this.FindControl<Button>("NewMachine")!;
        add.IsEnabled = !closing && !startingConfiguration && workspace.Tabs.Count < MachineWorkspace.Capacity;
        ToolTip.SetTip(add, workspace.Tabs.Count >= MachineWorkspace.Capacity
            ? "Four-machine limit. Close a machine before adding another."
            : "Open a new detached configuration; existing machines keep running.");
        this.FindControl<Button>("MachineReset")!.IsEnabled = live && session.BasicRuntimeActive;
    }
}
