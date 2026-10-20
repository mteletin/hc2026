using System.Collections.ObjectModel;
using Avalonia.Input;
using Hc2000.App.Configuration;
using Hc2000.App.Input;

namespace Hc2000.App.Workspace;

/// <summary>
/// Instance-scoped machine tab collection and synchronous guest-input routing. Called by the UI
/// thread only. No runtime factory, persistence or media admission authority lives here: each
/// tab's runtime state (worker, queue, evidence, read hold) lives in its MachineSession, and
/// removal is view-side — the caller has already retired the session or the tab was a draft.
/// </summary>
internal sealed class MachineWorkspace
{
    public const int Capacity = 4;
    private readonly List<MachineTabContext> tabs = [];
    public ReadOnlyCollection<MachineTabContext> Tabs { get; }
    public MachineTabContext? Selected { get; private set; }

    public MachineWorkspace() => Tabs = tabs.AsReadOnly();

    public bool TryAddDraft(MachineConfiguration configuration, out MachineTabContext? tab, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        tab = null;
        if (tabs.Count >= Capacity)
        {
            refusal = "At most four machine tabs are supported; no existing tab was replaced.";
            return false;
        }
        if (tabs.Any(t => t.Configuration.Id == configuration.Id))
        {
            refusal = "This machine configuration already has a tab.";
            return false;
        }
        // Construct and validate before publishing anything or releasing the old selection.
        tab = new MachineTabContext(configuration);
        tabs.Add(tab);
        if (Selected is null) Selected = tab;
        refusal = null;
        return true;
    }

    // View-side removal: the session's runtime (worker disposed, queue drained, named hold
    // released) is already retired by the caller, or the tab was a draft with no runtime.
    // Selection moves to the first surviving tab.
    public bool RemoveTab(Guid tabId, out MachineTabContext? removed)
    {
        removed = null;
        var index = tabs.FindIndex(t => t.TabId == tabId);
        if (index < 0) return false;
        removed = tabs[index];
        tabs.RemoveAt(index);
        if (ReferenceEquals(Selected, removed)) Selected = tabs.Count > 0 ? tabs[0] : null;
        return true;
    }

    public bool Select(Guid tabId)
    {
        var next = tabs.Find(t => t.TabId == tabId);
        if (next is null) return false;
        if (ReferenceEquals(next, Selected)) return true;
        Selected?.KeyboardInput.ReleaseFocus(); // enqueue on OLD FIFO before routing changes
        Selected = next;
        return true;
    }

    public MachineDraftTarget? CaptureSelectedDraft()
        => Selected is { } tab ? new(tab.TabId, tab.DraftRevision) : null;

    public bool TryResolveDraft(MachineDraftTarget target, out MachineTabContext? tab)
    {
        tab = tabs.Find(t => t.TabId == target.TabId && t.DraftRevision == target.DraftRevision);
        return tab is not null;
    }

    // An asynchronous draft editor keeps its original target across selection changes. A stale
    // revision is refused rather than overwriting newer edits or the newly selected tab.
    public bool TryUpdateDraft(MachineDraftTarget target, MachineConfiguration configuration)
    {
        if (!TryResolveDraft(target, out var tab)) return false;
        tab!.UpdateDraft(configuration);
        return true;
    }

    // Route only events already classified by the view as guest input. Text/configuration
    // focus filtering remains the existing view's responsibility, not a global key listener.
    public bool KeyDown(Key key, KeyModifiers modifiers = KeyModifiers.None)
        => Selected?.KeyboardInput.KeyDown(key, modifiers) ?? false;
    public void KeyUp(Key key) => Selected?.KeyboardInput.KeyUp(key);
    public bool TextInput(string text) => Selected?.KeyboardInput.TextInput(text) ?? false;
    public bool Paste(string text) => Selected?.KeyboardInput.Paste(text) ?? false;
    public void ReleaseFocus(InputReleaseOrigin origin = InputReleaseOrigin.Unspecified)
        => Selected?.KeyboardInput.ReleaseFocus(origin);
}
