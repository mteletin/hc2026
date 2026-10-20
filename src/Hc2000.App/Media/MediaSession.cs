using System.Collections.Immutable;
using Hc2000.App.Input;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Tape;

namespace Hc2000.App.Media;

public enum MediaAction { Inspect, InsertDisk, EjectDisk, WriteProtect, ExportDisk, QueueTape, PlayTape, StopTape, EjectTape,
    InsertCassette, BlankTape, FlipTape, TabTape, RecordTape, RewindTape, ForwardTape, PauseTape,
    CounterTape, FastWindTape, StopEjectTape, ExportTape, AcknowledgeTape, MonitorTape, Hdd, Writeback }
public enum DiskSlot { A, B }
public sealed record MediaCommand(MediaAction Action, DiskSlot Slot = DiskSlot.A,
    ImmutableArray<byte> Bytes = default, string? Name = null, bool Value = false,
    bool DiscardChanges = false, long RequestId = 0, TapeCassetteImage? Cassette = null,
    TapeCapacity Capacity = TapeCapacity.C60, TapeSide Side = TapeSide.A,
    TapeSaveAcknowledgement? Acknowledgement = null, HddRequest? Hdd = null,
    WritebackOwnerRequest? Writeback = null) : InputCommand;
public sealed record DriveView(string? Name, bool Present, bool WriteProtected, int DirtySectors, bool Busy, long Generation);
public sealed record MediaReply(long RequestId, DriveView A, DriveView B,
    ImmutableArray<byte> Export = default, string? Error = null, TapeView? Tape = null,
    TapeExportSnapshot? TapeExport = null, TapeFailure? TapeFailure = null, HddReply? Hdd = null,
    WritebackOwnerReply? Writeback = null);
public sealed record TapeView(string? Name, TapeCursor Cursor, TapeDeckView? Deck = null,
    bool AudioAvailable = false, string? AudioReason = null, TapeFormat? SourceFormat = null);

/// <summary>Owner-side adapter around the SAME drives supplied to the FDC. No host I/O,
/// CPU, replacement drive, scheduler, or UI reference. Invoke at ordered boundaries.</summary>
public sealed class MediaSession(FloppyDrive driveA, FloppyDrive driveB, TapeTransport? tape = null, bool manualDeck = false,
    HddMediaAdapter? hdd = null)
{
    public MediaSession(FloppyDrive driveA, FloppyDrive driveB, TapeTransport? tape)
        : this(driveA, driveB, tape, manualDeck: false) { }

    private readonly int owner = Environment.CurrentManagedThreadId;
    private bool receiptCoveredEject;
    private TapeTransport? transport = tape;
    private bool useDeck = manualDeck;
    private string? tapeName;
    private TapeFormat? formatA, formatB;
    internal void RestoredMetadata(TapeCassetteImage? image)
    {
        VerifyOwner(); tapeName = image is null ? null : "HCS cassette";
        formatA = image?.A.ImportOptions.Format; formatB = image?.B.ImportOptions.Format;
    }

    internal void RebindTape(TapeTransport value)
    {
        VerifyOwner(); transport = value;
    }
    private void VerifyOwner()
    {
        if (owner != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Media belongs to the emulation owner.");
    }
    private ITapeDeck RequireDeck() { useDeck = true; return RequireTape(); }
    private string? nameA;
    private string? nameB;
    private long generationA;
    private long generationB;

    /// <summary>Owner-ledger only. A queued command cannot set this; DiscardChanges remains the explicit user discard.</summary>
    internal MediaReply EjectCovered(DiskSlot slot)
    {
        receiptCoveredEject = true;
        try { return Apply(new(MediaAction.EjectDisk, slot)); }
        finally { receiptCoveredEject = false; }
    }

    public MediaReply Apply(MediaCommand command)
    {
        VerifyOwner();
        var export = default(ImmutableArray<byte>);
        TapeExportSnapshot? tapeExport = null;
        TapeFailure? failure = null;
        HddReply? hddReply = null;
        string? error = null;
        try
        {
            if (!Enum.IsDefined(command.Action) || !Enum.IsDefined(command.Slot))
                throw new ArgumentException("Invalid media command.");
            var drive = command.Slot == DiskSlot.A ? driveA : driveB;
            switch (command.Action)
            {
                case MediaAction.Hdd:
                    hddReply = hdd is null ? HddReply.Unavailable : hdd.Apply(command.Hdd ?? throw new ArgumentException("HDD request required."));
                    error = hddReply.Error;
                    break;
                case MediaAction.InsertCassette:
                    ApplyDeck(RequireDeck().Insert(command.Cassette ?? throw new ArgumentException("Cassette required."), command.DiscardChanges));
                    if (failure is null)
                    { tapeName = command.Name; formatA = command.Cassette!.A.ImportOptions.Format; formatB = command.Cassette.B.ImportOptions.Format; }
                    break;
                case MediaAction.BlankTape:
                    ApplyDeck(RequireDeck().Blank(command.Capacity, command.DiscardChanges));
                    if (failure is null) { tapeName = $"Blank {command.Capacity}"; formatA = formatB = TapeFormat.Blank; }
                    break;
                case MediaAction.FlipTape: ApplyDeck(RequireDeck().Flip()); break;
                case MediaAction.TabTape: ApplyDeck(RequireDeck().SetTab(command.Side, command.Value)); break;
                case MediaAction.RecordTape: ApplyDeck(RequireDeck().RecordPlay()); break;
                case MediaAction.RewindTape: ApplyDeck(RequireDeck().Rewind()); break;
                case MediaAction.ForwardTape: ApplyDeck(RequireDeck().FastForward()); break;
                case MediaAction.PauseTape: ApplyDeck(RequireDeck().Pause()); break;
                case MediaAction.CounterTape: ApplyDeck(RequireDeck().ResetCounter()); break;
                case MediaAction.FastWindTape: ApplyDeck(RequireDeck().SetFastWind(command.Value)); break;
                case MediaAction.StopEjectTape:
                    ApplyDeck(RequireDeck().StopEject(command.DiscardChanges));
                    if (failure is null && !RequireDeck().Observe().Present) tapeName = null;
                    break;
                case MediaAction.ExportTape:
                    var result = RequireDeck().Export(command.Side);
                    if (result.Success) tapeExport = result.Value; else SetFailure(result.Failure!);
                    break;
                case MediaAction.AcknowledgeTape:
                    ApplyDeck(RequireDeck().AcknowledgeSave(command.Acknowledgement ?? throw new ArgumentException("Save correlation required.")));
                    break;
                case MediaAction.QueueTape:
                    RequireTape().LoadTap(command.Bytes.AsSpan());
                    tapeName = command.Name; formatA = TapeFormat.Tap; formatB = TapeFormat.Blank;
                    break;
                case MediaAction.PlayTape:
                    if (useDeck) ApplyDeck(RequireDeck().Play()); else RequireTape().Play();
                    break;
                case MediaAction.StopTape:
                    RequireTape().Stop();
                    break;
                case MediaAction.EjectTape:
                    if (useDeck)
                    {
                        ApplyDeck(RequireDeck().StopEject(command.DiscardChanges));
                        if (failure is null && !RequireDeck().Observe().Present) tapeName = null;
                    }
                    else { RequireTape().Eject(); tapeName = null; }
                    break;
                case MediaAction.InsertDisk:
                case MediaAction.EjectDisk:
                    if (drive.DirtySectors.Count != 0 && !command.DiscardChanges && !receiptCoveredEject)
                        throw new InvalidOperationException("Unsaved changes: save a copy or explicitly allow discard before replacing media.");
                    // Parse into a detached container first; invalid input never ejects the old disk.
                    var disk = command.Action == MediaAction.InsertDisk ? Dsk.Load(command.Bytes.AsSpan()) : null;
                    drive.Mount(disk); // Existing mechanics reject motor-on/head-loaded replacement.
                    if (command.Slot == DiskSlot.A) { nameA = disk is null ? null : command.Name; generationA++; }
                    else { nameB = disk is null ? null : command.Name; generationB++; }
                    break;
                case MediaAction.WriteProtect:
                    drive.WriteProtected = command.Value;
                    break;
                case MediaAction.ExportDisk:
                    export = ImmutableArray.CreateRange(drive.ExportMedia());
                    break;
                case MediaAction.Writeback:
                    throw new InvalidOperationException("Writeback is applied by the owner ledger, not as a drive mutation.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            error = ex.Message;
        }
        var deckView = transport is not null && useDeck ? ((ITapeDeck)transport).Observe() : null;
        return new(command.RequestId, View(driveA, nameA, generationA), View(driveB, nameB, generationB), export, error,
            transport is null ? null : new(tapeName, transport.Cursor, deckView, SourceFormat: deckView?.ActiveSide == TapeSide.B ? formatB : formatA), tapeExport, failure, hddReply);

        void ApplyDeck(TapeResult<TapeDeckView> result)
        {
            if (!result.Success) SetFailure(result.Failure!);
        }
        void SetFailure(TapeFailure value) { failure = value; error = $"{value.Code}: {value.Detail}"; }
    }

    private TapeTransport RequireTape() => transport ?? throw new InvalidOperationException("Tape runtime unavailable.");

    private static DriveView View(FloppyDrive drive, string? name, long generation) => new(name, drive.Image is not null,
        drive.WriteProtected, drive.DirtySectors.Count, drive.MotorOn || drive.HeadLoaded, generation);
}
