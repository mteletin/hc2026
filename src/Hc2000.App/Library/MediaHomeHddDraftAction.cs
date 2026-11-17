using Hc2000.App.Configuration;
using Hc2000.App.Media;
using Hc2000.App.Workspace;

namespace Hc2000.App.Library;

/// <summary>
/// The cold commit of the media-home HDD menu (hc2000-hdd-context-mount-menu): the row's
/// "Select as C:–F:" records the row through the accepted <see cref="MediaHomeHddProfileLane"/>
/// into the selected machine's draft, and "Clear C:–F:" removes the slot. The commit is a
/// revision-guarded draft update only (cold-effective): the running machine keeps its own
/// revision until the next cold start and is never reset here. A refusal, a stale draft and a
/// missing selection all leave the draft untouched and say so in the returned notice text.
/// </summary>
internal static class MediaHomeHddDraftAction
{
    /// <summary>Explicit flat512 library action. Reuse exactly one saved copy, never pick among
    /// several copies and never regenerate its container from the scanned original.</summary>
    public static async Task<string> RunLibraryRowAsync(MachineWorkspace workspace, DiskLibraryCatalog catalog,
        MediaHomeRow row, string libraryRoot, string slot, Func<DiskLibraryHostAdmission?> hostAdmission,
        CancellationToken cancellation = default)
    {
        if (workspace.CaptureSelectedDraft() is not { } target || !workspace.TryResolveDraft(target, out var tab) || tab is null)
            return "No machine draft is selected; nothing was recorded.";
        var draft = tab.Configuration;
        if (!row.Selectable || row.Kind != "hdd-raw" || slot is not ("C" or "D" or "E" or "F"))
            return "Library selection refused: explicit flat512 raw row and slot C:–F: required; nothing was recorded.";
        bool Admit()
        {
            var admission = hostAdmission();
            catalog.SetAdmission(admission);
            return admission is { KnownWriters: false } && admission.Accounted.IsEmpty;
        }
        if (!Admit()) return "Library selection refused: stop and drain host media owners first; nothing was recorded.";
        var path = Path.GetFullPath(row.Path);
        var entry = catalog.Entries.SingleOrDefault(e => e.SourcePath == path);
        if (entry is null)
        {
            var registered = await catalog.RegisterHddAsync(path, "media-home explicit flat512 library action", cancellation: cancellation);
            if (registered.Reason != DiskLibraryRefusal.Accepted || registered.Entry is null)
                return $"Library registration refused: {registered.Message}; nothing was recorded.";
            entry = registered.Entry;
        }
        if (entry.Kind != DiskLibraryKind.Hdd || entry.Format != DiskLibraryFormat.Flat512)
            return "Library selection refused: registered entry is not flat512 HDD; nothing was recorded.";
        if (!Admit()) return "Library selection refused: host ownership changed; nothing was recorded.";
        var copies = catalog.Copies.Where(c => c.EntryId == entry.Id).ToArray();
        if (copies.Length > 1)
            return "Library selection refused: multiple working copies require an explicit copy choice; nothing was recorded.";
        var copy = copies.SingleOrDefault();
        if (copy is null)
        {
            var created = await catalog.CreateHddWorkingCopyAsync(entry.Id, libraryRoot, cancellation: cancellation);
            if (created.Status != DiskLibraryCopyStatus.Created || created.Copy is null)
                return $"Library working copy refused: {created.Message}; nothing was recorded.";
            copy = created.Copy;
        }
        if (!Admit()) return "Library selection refused: host ownership changed; saved copy retained, nothing was recorded.";
        var result = await MediaHomeHddProfileLane.SelectLibraryCopyAsync(catalog, copy.Id, libraryRoot, slot, draft, cancellation);
        if (result.Outcome != MediaHomeHddProfileOutcome.Selected)
            return $"Library selection refused: {result.Message}; nothing was recorded.";
        if (cancellation.IsCancellationRequested || !Admit())
            return "Library selection cancelled or host ownership changed; private container retained, nothing was recorded.";
        if (!workspace.TryUpdateDraft(target, result.Draft))
            return "The machine draft changed; private container retained, nothing was recorded.";
        return $"Drive {slot}: library copy {copy.Id} recorded as volume 'LIBRARY.DSK'; {result.Message}";
    }

    /// <summary>Runs one menu click against the selected machine draft and returns the full
    /// notice text; <paramref name="select"/> false clears the slot instead of recording.</summary>
    public static Task<string> RunAsync(MachineWorkspace workspace, MediaHomeRow row, string slot, bool select)
        => Task.FromResult(Run(workspace, row, slot, select));

    /// <summary>Explicit managed-copy action, separate from raw-row selection. Capture the draft
    /// revision before publication so an asynchronous result cannot overwrite a newer draft.</summary>
    public static async Task<string> RunLibraryCopyAsync(MachineWorkspace workspace, DiskLibraryCatalog catalog,
        string copyId, string libraryRoot, string slot, CancellationToken cancellation = default)
    {
        if (workspace.CaptureSelectedDraft() is not { } target)
            return "No machine draft is selected; the HDD selection is closed. Nothing was recorded.";
        if (!workspace.TryResolveDraft(target, out var tab) || tab is null)
            return "The machine draft changed while this selection was in flight; nothing was recorded.";
        var result = await MediaHomeHddProfileLane.SelectLibraryCopyAsync(catalog, copyId, libraryRoot,
            slot, tab.Configuration, cancellation);
        if (result.Outcome != MediaHomeHddProfileOutcome.Selected)
            return $"Select refused, nothing recorded: {result.Message}";
        if (cancellation.IsCancellationRequested)
            return "Select cancelled, nothing recorded; the private container is retained.";
        if (!workspace.TryUpdateDraft(target, result.Draft))
            return "The machine draft changed while this selection was in flight; nothing was recorded; the private container is retained.";
        return $"Drive {slot}: library copy recorded as volume '{result.Identity?.Name}'; {result.Message}";
    }

    private static string Run(MachineWorkspace workspace, MediaHomeRow row, string slot, bool select)
    {
        if (workspace.CaptureSelectedDraft() is not { } target)
            return "No machine draft is selected; the HDD selection is closed. Nothing was recorded.";
        if (!workspace.TryResolveDraft(target, out var tab) || tab is null)
            return "The machine draft changed while this selection was in flight; nothing was recorded.";
        var draft = tab.Configuration;
        if (!select)
        {
            if (draft.Hdd.All(h => h.Slot != slot))
                return $"Clear refused, nothing changed: drive {slot}: has no recorded HDD.";
            if (!workspace.TryUpdateDraft(target, MediaHomeHddProfileLane.Clear(draft, slot)))
                return "The machine draft changed while this clear was in flight; nothing was changed.";
            return $"Drive {slot}: cleared; effective at the next cold start. The running machine is unchanged.";
        }
        var result = MediaHomeHddProfileLane.Select(row.Kind, row.Path, null, slot, draft);
        if (result.Outcome != MediaHomeHddProfileOutcome.Selected)
            return $"Select refused, nothing recorded: {result.Message}";
        if (!workspace.TryUpdateDraft(target, result.Draft))
            return "The machine draft changed while this selection was in flight; nothing was recorded.";
        return $"Drive {slot}: {row.Name} recorded as volume '{result.Identity?.Name}'; " +
            "effective at the next cold start. The running machine is unchanged.";
    }
}
