using System.Collections.Immutable;
using Hc2000.Machine.Floppy;

namespace Hc2000.App.Media;

/// <summary>Delegate of the existing MediaSession, not another owner/session or controller.
/// Unguarded attach/detach are refused; named remapping requires the native guest-issued hold.</summary>
public sealed partial class HddMediaAdapter
{
    public const int ImageLength = 8_388_608;
    public const string SwitchUnavailable = "Guest-safe HDD switching is unavailable; upgrade BIOS close/flush/reset integration is not present.";
    private readonly int owner = Environment.CurrentManagedThreadId;
    private BlockDiskPort controller;
    private readonly string?[] names = new string?[4];
    private readonly HddSwitchTarget?[] bindings = new HddSwitchTarget?[4];
    private readonly Dictionary<long, HddSaveCapture> saves = [];
    private long sequence;
    private bool sessionBound;
    private sealed record PendingSwitch(BlockDiskPort.GuestSwitchRequest Native, HddSwitchTarget Target, bool DiscardChanges);
    private PendingSwitch? pendingSwitch;
    private HddSwitchView? switchView;
    private HddColdVolume? unresolvedSource;

    public HddMediaAdapter(BlockDiskPort controller, IEnumerable<HddInitialMedia>? initial = null)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        if (!controller.HddEnabled) throw new ArgumentException("HDD profile is off.", nameof(controller));
        if (initial is not null)
            foreach (var item in initial)
            {
                names[Index(item.Drive)] = item.Name;
                bindings[Index(item.Drive)] = item.Binding;
            }
    }

    // Only called when Reset retained the SAME logical slots, never after snapshot adoption.
    internal void Rebind(BlockDiskPort retainedController)
    {
        VerifyOwner();
        if (!retainedController.HddEnabled) throw new ArgumentException("HDD profile is off.", nameof(retainedController));
        controller = retainedController;
        sessionBound = false;
    }

    /// <summary>Existing owner, detached cold candidate only. Re-resolve the operator's current
    /// selections, not saved identities or prepared selections. The candidate is discarded on failure.</summary>
    internal static HddMediaAdapter RearmRestoredMap(BlockDiskPort controller, string path,
        ImmutableArray<(byte Drive, string Name)> selections)
    {
        controller.ValidateRestoredGuestAckRearm();
        if (controller.Capture().Slots.Any(slot => slot.Present))
            throw new InvalidOperationException("Restored map adoption requires four empty cold slots.");
        using var source = HddColdVolume.OpenMapped(path,
            selections.Select(selection => new HddColdVolumeRequest(selection.Drive, selection.Name)).ToImmutableArray());
        var media = source.MappedMedia;
        var adapter = new HddMediaAdapter(controller, media);
        foreach (var image in media)
            controller.AttachHdd(image.Drive, image.Bytes.AsSpan(), image.WriteProtected);
        controller.RearmRestoredGuestAckSession(Guid.NewGuid());
        adapter.sessionBound = true;
        return adapter;
    }

    internal void ValidateSessionSnapshotSave()
    {
        VerifyOwner();
        if (controller.GuestAckEnabled) controller.ValidateSessionSnapshotSave();
    }

    public ImmutableArray<HddDriveView> Inspect()
    {
        VerifyOwner();
        var result = ImmutableArray.CreateBuilder<HddDriveView>(4);
        for (byte drive = 2; drive <= 5; drive++)
        {
            var state = controller.CaptureHdd(drive);
            result.Add(new(drive, names[drive - 2], state.Present, state.Generation, state.WriteProtected, state.Dirty));
        }
        return result.MoveToImmutable();
    }

    public HddReply Apply(HddRequest request)
    {
        VerifyOwner();
        HddSaveCapture? export = null;
        string? error = null;
        try
        {
            if (!Enum.IsDefined(request.Action)) throw new ArgumentException("Invalid HDD action.");
            Index(request.Drive);
            switch (request.Action)
            {
                case HddAction.Attach:
                case HddAction.Detach:
                    throw new InvalidOperationException(SwitchUnavailable);
                case HddAction.BeginSwitch:
                    BeginSwitch(request);
                    break;
                case HddAction.CancelSwitch:
                    CancelSwitch(request.SwitchId);
                    break;
                case HddAction.CaptureSave:
                    var captured = controller.CaptureHdd(request.Drive); // Never ExportHdd here: it clears dirty.
                    if (!captured.Present) throw new InvalidOperationException("HDD slot is empty.");
                    // Bound retained memory to at most one outstanding save per drive.
                    foreach (var id in saves.Where(pair => pair.Value.Drive == request.Drive).Select(pair => pair.Key).ToArray())
                        saves.Remove(id);
                    export = new(checked(++sequence), request.Drive, captured.Generation, ImmutableArray.CreateRange(captured.Bytes));
                    saves.Add(export.Id, export);
                    break;
                case HddAction.AcknowledgeSave:
                    if (boundSave is not null) throw new InvalidOperationException("HDD bound publication owns saved acknowledgement.");
                    if (!saves.TryGetValue(request.SaveId, out var saved) || saved.Drive != request.Drive)
                        throw new InvalidOperationException("Unknown or stale HDD save acknowledgement.");
                    saves.Remove(saved.Id); // One use; a failed/stale acknowledgement can never be retried to clean new edits.
                    var current = controller.CaptureHdd(request.Drive);
                    if (!current.Present || current.Generation != saved.Generation || !current.Bytes.AsSpan().SequenceEqual(saved.Bytes.AsSpan()))
                        throw new InvalidOperationException("Saved copy is durable, but live HDD media changed; current edits remain unsaved.");
                    controller.ExportHdd(request.Drive); // Same owner, no guest step between comparison and cleaning.
                    break;
                case HddAction.CancelSave:
                    if (saves.TryGetValue(request.SaveId, out var cancelled) && cancelled.Drive == request.Drive)
                        saves.Remove(request.SaveId);
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            error = ex.Message;
        }
        return new(Inspect(), export, error, switchView);
    }

    private static int Index(byte drive) => drive is >= 2 and <= 5 ? drive - 2 : throw new ArgumentOutOfRangeException(nameof(drive));
    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("HDD adapter belongs to the emulation owner.");
    }

    private void BeginSwitch(HddRequest request)
    {
        if (boundSave is not null) throw new InvalidOperationException("HDD save fence is held.");
        if (!controller.GuestAckEnabled) throw new InvalidOperationException(SwitchUnavailable);
        if (pendingSwitch is not null) throw new InvalidOperationException("An HDD switch is already pending.");
        var target = request.Target ?? throw new ArgumentException("A named HDD selection is required.");
        if (target.Identity is null || string.IsNullOrWhiteSpace(target.Identity.Name) || string.IsNullOrWhiteSpace(target.Path))
            throw new ArgumentException("A named HDD selection and source path are required.");
        if (request.ExpectedGeneration is null || controller.CaptureHdd(request.Drive).Generation != request.ExpectedGeneration)
            throw new InvalidOperationException("Observed HDD generation changed; inspect again.");
        // Ordinary Bind still refuses restored controllers; only explicit cold map adoption
        // has already bound a re-armed nonce and bypasses this lazy initial-session bind.
        if (!sessionBound)
        {
            controller.BindGuestAckSession(Guid.NewGuid());
            sessionBound = true;
        }
        var native = controller.BeginGuestSwitch(request.Drive, (ushort)(1 << request.Drive));
        pendingSwitch = new(native, target, request.DiscardChanges);
        switchView = new(native.RequestId, native.PrimaryUnit, HddSwitchState.AwaitingGuest);
    }

    private void CancelSwitch(ulong id)
    {
        if (pendingSwitch is not { } pending || pending.Native.RequestId != id || switchView?.State != HddSwitchState.AwaitingGuest)
            throw new InvalidOperationException("Unknown, completed or unresolved HDD switch.");
        var cancelled = new HddSwitchView(id, pending.Native.PrimaryUnit, HddSwitchState.Cancelled);
        if (controller.TryGetHeldGuestSwitch(out var ticket))
        {
            controller.CancelGuestSwitch(ticket);
            switchView = cancelled;
            controller.FinishGuestSwitch(ticket); // No source work was started by this cancellation.
        }
        else
        {
            controller.CancelPendingGuestSwitch(pending.Native.SessionNonce, id);
            switchView = cancelled;
        }
        pendingSwitch = null;
    }

    /// <summary>Existing instruction-boundary owner only. No new queue or asynchronous source work.
    /// A bounded source read is admitted only after the real guest ACK; release follows accounting
    /// and actual source cleanup, never a timeout or an observation of a prospective outcome.</summary>
    internal void PumpGuestSwitch()
    {
        VerifyOwner();
        if (pendingSwitch is not { } pending || switchView?.State != HddSwitchState.AwaitingGuest ||
            !controller.TryGetHeldGuestSwitch(out var ticket)) return;
        HddColdVolume? source = null;
        bool outcomeEstablished = false;
        try
        {
            source = HddColdVolume.Open(pending.Target.Path, pending.Target.Identity.Name, pending.Target.Identity, pending.Target.Writable);
            var media = source.Media(pending.Native.PrimaryUnit);
            var binding = media.Binding!;
            for (int i = 0; i < bindings.Length; i++)
                if (i != Index(media.Drive) && bindings[i] is { } mounted &&
                    mounted.Identity.Store.ContainerId.AsSpan().SequenceEqual(binding.Identity.Store.ContainerId.AsSpan()) &&
                    StringComparer.OrdinalIgnoreCase.Equals(mounted.Identity.Name, binding.Identity.Name))
                    throw new InvalidOperationException("A named HDD volume is already mounted, even read-only.");
            // Preallocate all sideband before the sole native copy-before-adopt mutation.
            var committed = new HddSwitchView(pending.Native.RequestId, media.Drive, HddSwitchState.Committed);
            var result = controller.CommitGuestSwitch(ticket, media.Bytes.AsSpan(), media.WriteProtected, pending.DiscardChanges);
            if (!result.Committed) throw new InvalidOperationException(result.Error);
            outcomeEstablished = true;
            names[Index(media.Drive)] = media.Name;
            bindings[Index(media.Drive)] = binding;
            switchView = committed;
            source.Dispose(); source = null;
            controller.FinishGuestSwitch(ticket);
            pendingSwitch = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException or InvalidDataException)
        {
            // A committed outcome or failed cleanup is never relabelled as rollback. Retain the
            // native hold for reconciliation/actual old-owner retirement instead of resuming CPU.
            if (outcomeEstablished)
            {
                unresolvedSource = source;
                switchView = new(pending.Native.RequestId, pending.Native.PrimaryUnit, HddSwitchState.Unknown, ex.Message);
                return;
            }
            var cancelled = new HddSwitchView(pending.Native.RequestId, pending.Native.PrimaryUnit, HddSwitchState.Cancelled, ex.Message);
            try
            {
                controller.CancelGuestSwitch(ticket);
                switchView = cancelled;
                source?.Dispose(); source = null;
                controller.FinishGuestSwitch(ticket);
                pendingSwitch = null;
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                unresolvedSource = source;
                switchView = new(pending.Native.RequestId, pending.Native.PrimaryUnit, HddSwitchState.Unknown, cleanup.Message);
            }
        }
    }

    internal void RetireGuestSwitch()
    {
        VerifyOwner();
        // Synchronous source work has ended, but a failed cleanup must still be retried before
        // old-owner retirement can release either the source admission or the CPU hold.
        unresolvedSource?.Dispose(); unresolvedSource = null;
        controller.InvalidateGuestAckSession();
        pendingSwitch = null; switchView = null; sessionBound = false;
    }
}
