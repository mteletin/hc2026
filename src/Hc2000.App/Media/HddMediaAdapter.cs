using System.Collections.Immutable;
using Hc2000.Machine.Floppy;

namespace Hc2000.App.Media;

/// <summary>Delegate of the existing MediaSession, not another owner/session or controller.
/// Live attach/detach are always refused. Initial population belongs only to the cold factory.</summary>
public sealed class HddMediaAdapter
{
    public const int ImageLength = 8_388_608;
    public const string SwitchUnavailable = "Guest-safe HDD switching is unavailable; upgrade BIOS close/flush/reset integration is not present.";
    private readonly int owner = Environment.CurrentManagedThreadId;
    private BlockDiskPort controller;
    private readonly string?[] names = new string?[4];
    private readonly Dictionary<long, HddSaveCapture> saves = [];
    private long sequence;

    public HddMediaAdapter(BlockDiskPort controller, IEnumerable<HddInitialMedia>? initial = null)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        if (!controller.HddEnabled) throw new ArgumentException("HDD profile is off.", nameof(controller));
        if (initial is not null)
            foreach (var item in initial) names[Index(item.Drive)] = item.Name;
    }

    // Only called when Reset retained the SAME logical slots, never after snapshot adoption.
    internal void Rebind(BlockDiskPort retainedController)
    {
        VerifyOwner();
        if (!retainedController.HddEnabled) throw new ArgumentException("HDD profile is off.", nameof(retainedController));
        controller = retainedController;
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
        return new(Inspect(), export, error);
    }

    private static int Index(byte drive) => drive is >= 2 and <= 5 ? drive - 2 : throw new ArgumentOutOfRangeException(nameof(drive));
    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("HDD adapter belongs to the emulation owner.");
    }
}
