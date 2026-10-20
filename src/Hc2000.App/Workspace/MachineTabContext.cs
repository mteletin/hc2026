using Hc2000.App.Configuration;
using Hc2000.App.Input;

namespace Hc2000.App.Workspace;

/// <summary>
/// Tab-local configuration and the per-machine runtime session. The session owns the worker,
/// its input queue/adapter, media writeback host and read-hold admission; the window renders
/// the selected session and boots it, so a tab never shares a queue, worker, bridge or
/// evidence identity with another machine.
/// </summary>
internal sealed class MachineTabContext
{
    internal MachineTabContext(MachineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        Configuration = configuration with { };
        Session = new MachineSession(configuration.KeyboardMode == "Symbolic" ? KeyMappingMode.Symbolic : KeyMappingMode.Positional);
    }

    // Separate from the persisted configuration ID: reopening configuration is a new lifetime.
    public Guid TabId { get; } = Guid.NewGuid();
    public MachineConfiguration Configuration { get; private set; }
    public long DraftRevision { get; private set; }
    public MachineSession Session { get; }
    public InputQueue InputQueue => Session.InputQueue;
    public KeyboardInputAdapter KeyboardInput => Session.KeyboardInput;

    internal void UpdateDraft(MachineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        if (configuration.Id != Configuration.Id)
            throw new ArgumentException("Editing a draft cannot change its configuration identity.", nameof(configuration));
        var revision = checked(DraftRevision + 1);
        var copy = configuration with { };
        Session.KeyboardInput.Mode = copy.KeyboardMode == "Symbolic" ? KeyMappingMode.Symbolic : KeyMappingMode.Positional;
        Configuration = copy;
        DraftRevision = revision;
    }
}

/// <summary>Draft UI freshness only; not runtime epoch, owner receipt or media authority.</summary>
internal readonly record struct MachineDraftTarget(Guid TabId, long DraftRevision);
