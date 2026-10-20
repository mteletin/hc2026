using Avalonia.Controls;

namespace Hc2000.App.Media;

public sealed partial class HddPanel
{
    private readonly Button[] boundSaveButtons = new Button[4];
    private readonly Button reconcileBoundSave = new() { Content = "Recover / reconcile bound save" };
    private readonly Button abortBoundSave = new() { Content = "Cancel captured save (before publication only)" };
    // Presentation state only. The existing FIFO owner's ledger retains the real lease,
    // including when a reply is lost or this panel disconnects.
    private bool boundSavePending;

    internal async Task SaveBoundAsync(byte drive)
    {
        _ = Index(drive);
        if (boundSavePending) throw new InvalidOperationException("Recover the retained HDD save first.");
        var binding = runtimeBinding;
        boundSavePending = true; // A lost Begin reply is unknown, never a fabricated abort.
        var begun = await SendWritebackAsync(new(WritebackOwnerOp.BeginHdd, HddDrive: drive), binding);
        RequireAccepted(begun);
        var capture = begun.HddSave ?? throw new InvalidOperationException("HDD owner capture unavailable; recover the same save.");
        await FinishBoundSaveAsync(capture.Id, reconcile: false, binding);
    }

    internal async Task ReconcileBoundSaveAsync()
    {
        var binding = runtimeBinding;
        boundSavePending = true;
        var inspected = await SendWritebackAsync(new(WritebackOwnerOp.InspectHdd), binding);
        RequireAccepted(inspected);
        if (inspected.HddSave is not { } capture)
        {
            boundSavePending = false;
            await InspectAsync();
            notice.Text = "No retained HDD save. Inspect current dirty state before starting another save.";
            return;
        }
        await FinishBoundSaveAsync(capture.Id, reconcile: true, binding);
    }

    internal async Task AbortBoundSaveAsync()
    {
        var binding = runtimeBinding;
        boundSavePending = true;
        var inspected = await SendWritebackAsync(new(WritebackOwnerOp.InspectHdd), binding);
        RequireAccepted(inspected);
        if (inspected.HddSave is { } capture)
            RequireAccepted(await SendWritebackAsync(new(WritebackOwnerOp.AbortHdd, HddSaveId: capture.Id), binding));
        boundSavePending = false;
        await InspectAsync();
        notice.Text = "No retained HDD save; unsaved native edits were not discarded.";
    }

    private async Task FinishBoundSaveAsync(long id, bool reconcile, long binding)
    {
        var saved = await SendWritebackAsync(new(reconcile ? WritebackOwnerOp.ReconcileHdd : WritebackOwnerOp.PublishHdd,
            HddSaveId: id), binding);
        RequireAccepted(saved);
        if (saved.HddPublication?.Acknowledged != true)
            throw new InvalidOperationException("HDD publication has no saved acknowledgement; recover the same save.");
        await InspectAsync();
        notice.Text = "Captured HDD bytes published and unchanged native bytes acknowledged. This does not prove a guest deblock flush.";
    }

    private async Task<WritebackOwnerReply> SendWritebackAsync(WritebackOwnerRequest value, long binding)
    {
        VerifyBinding(binding);
        var send = request ?? throw new InvalidOperationException("HDD runtime disconnected.");
        var reply = await send(new(MediaAction.Writeback, Writeback: value));
        VerifyBinding(binding);
        var owner = reply.Writeback ?? throw new InvalidOperationException(reply.Error ?? "Writeback owner unavailable; save outcome is unknown.");
        boundSavePending = owner.SessionFrozen;
        UpdateControls();
        return owner;
    }

    private static void RequireAccepted(WritebackOwnerReply reply)
    {
        if (!reply.Accepted) throw new InvalidOperationException(reply.Error ?? "HDD save refused; current edits remain unsaved.");
    }
}
