using System.Buffers.Binary;
using Hc2000.Machine.Io;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;

namespace Hc2000.Machine.Floppy;

public sealed record BlockDiskPortState(ushort Pointer, byte Status, HddSlotState[] Slots);

/// <summary>Typed bounded-capture refusal receipt; success is Error == None, Detail is raw evidence, not a parse.</summary>
public sealed record HddSelectedCaptureReceipt(HddSelectedCaptureError Error, string Detail)
{
    public bool Success => Error == HddSelectedCaptureError.None;
}

public enum HddSelectedCaptureError
{
    None, HddDisabled, WrongOwner, InvalidUnit, NullSelection, InvalidImage, ReadOnlySelection,
    NullExpectation, StaleGeneration, StaleState, DirtyMedia, WriteProtectedMedia,
    GuestSwitchActive = 12, Uncooperated = 13
}

/// <summary>Opt-in virtual guest ABI v1/v2 (not the faithful 8272 or an upgraded BIOS).
/// One emulation-thread owner; caller drains device time before each bus operation.</summary>
public sealed partial class BlockDiskPort : IPortDevice
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly HcMemory memory;
    private readonly FloppyController controller;
    private readonly BlockDiskTransfer transfer;
    private HddSlot[]? slots;
    public bool HddEnabled => slots is not null;
    private ushort pointer;
    private byte status;

    public BlockDiskPort(HcMemory memory, FloppyController controller, bool hddEnabled = false, bool guestAckEnabled = false)
    {
        this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        if (guestAckEnabled && !hddEnabled) throw new ArgumentException("Guest ACK requires HDD slots.", nameof(guestAckEnabled));
        GuestAckEnabled = guestAckEnabled;
        transfer = new(memory, controller.DriveA, controller.DriveB);
        if (hddEnabled) slots = Enumerable.Range(0, 4).Select(_ => new HddSlot()).ToArray();
    }

    /// <summary>Owner-boundary reset retires idle guest binding and pointer/status; refuses undrained work.</summary>
    public void Reset()
    {
        VerifyOwner();
        RequireNoGuestSwitch();
        InvalidateGuestAckSession();
        pointer = 0; status = 0;
    }

    /// <summary>Host-session byte operations at the emulation owner boundary, never file I/O.</summary>
    public void AttachHdd(byte drive, ReadOnlySpan<byte> image, bool writeProtected = false, bool discardDirty = false)
    {
        VerifyOwner();
        RequireRestoredCooperation();
        RequireNoGuestSwitch();
        Slot(drive).Attach(image, writeProtected, discardDirty);
        completedGuestCooperation = false;
    }
    public void DetachHdd(byte drive, bool discardDirty = false)
    {
        VerifyOwner();
        RequireRestoredCooperation();
        RequireNoGuestSwitch();
        Slot(drive).Detach(discardDirty);
        coldRestoreBoundary = false;
        completedGuestCooperation = false;
    }
    public byte[] ExportHdd(byte drive) => Slot(drive).Export();
    public HddSlotState CaptureHdd(byte drive) => Slot(drive).Capture();

    /// <summary>Validate saved captures atomically at the owner boundary, then acknowledge dirty media.
    /// This unused native seam does not save host bytes or establish host persistence.</summary>
    public bool TryAcknowledgeHddSaved(ReadOnlySpan<(byte Unit, HddSlotState Expected)> expectations)
    {
        if (Environment.CurrentManagedThreadId != owner || slots is null ||
            expectations.Length is < 1 or > 4)
            return false;

        var selected = 0;
        foreach (var (unit, expected) in expectations)
        {
            if (unit is < 2 or > 5) return false;
            var index = unit - 2;
            var bit = 1 << index;
            if ((selected & bit) != 0 || !slots[index].MatchesSaved(expected)) return false;
            selected |= bit;
        }

        // No callback or guest work between validation and acknowledgement: the owner serializes access.
        for (var index = 0; index < slots.Length; index++)
            if ((selected & (1 << index)) != 0) slots[index].AcknowledgeSaved();
        return true;
    }

    /// <summary>One bounded writable capture of one prepared selection into the EXISTING slot.
    /// The actual owned image bytes and pending-Saved (dirty) state persist in the slot until a matching
    /// TryAcknowledgeHddSaved legitimately succeeds; every refusal preserves the prior slot bytes and state.
    /// No host I/O, no publication, no re-validation of selection invariants (selector already validated them).</summary>
    public HddSelectedCaptureReceipt TryCaptureSelectedHdd(byte unit, HddPreparedSelection selection,
        HddSlotState expectedCurrent, bool discardCurrentDirty)
    {
        if (Environment.CurrentManagedThreadId != owner)
            return new(HddSelectedCaptureError.WrongOwner, "Capture belongs to the emulation owner.");
        if (slots is null) return new(HddSelectedCaptureError.HddDisabled, "disk.hdd is disabled.");
        if (uncooperated)
            return new(HddSelectedCaptureError.Uncooperated, "Uncooperated restored binding requires a post-re-arm guest ACK cycle.");
        if (unit is < 2 or > 5)
            return new(HddSelectedCaptureError.InvalidUnit, $"Unit {unit} is not an HDD unit.");
        if (guestRequest is not null)
            return new(HddSelectedCaptureError.GuestSwitchActive, "Drain or retire the guest switch before selected capture.");
        if (selection is null)
            return new(HddSelectedCaptureError.NullSelection, "Prepared selection is null.");
        if (selection.Image.Length != HddSlot.ImageBytes)
            return new(HddSelectedCaptureError.InvalidImage, "Selected image must be exactly 8,388,608 bytes.");
        if (selection.WriteProtected)
            return new(HddSelectedCaptureError.ReadOnlySelection, "Writable capture accepts only writable selections.");
        if (expectedCurrent is null || expectedCurrent.Bytes is null)
            return new(HddSelectedCaptureError.NullExpectation, "Expected current state or its bytes are null.");
        var slot = slots[unit - 2];
        var current = slot.Capture();
        if (expectedCurrent.Generation != current.Generation)
            return new(HddSelectedCaptureError.StaleGeneration,
                $"Expected generation {expectedCurrent.Generation} but the slot is at {current.Generation}.");
        if (expectedCurrent.WriteProtected != current.WriteProtected || expectedCurrent.Dirty != current.Dirty)
            return new(HddSelectedCaptureError.StaleState, "Expected protection or dirty flag differs from the slot.");
        if (current.Present)
        {
            if (expectedCurrent.Bytes.Length != HddSlot.ImageBytes ||
                !current.Bytes.AsSpan().SequenceEqual(expectedCurrent.Bytes))
                return new(HddSelectedCaptureError.StaleState, "Expected image bytes differ from the current slot image.");
        }
        else if (expectedCurrent.Bytes.Length != 0)
        {
            return new(HddSelectedCaptureError.StaleState, "Empty slot expectation must carry no image bytes.");
        }
        if (slot.Dirty && !discardCurrentDirty)
            return new(HddSelectedCaptureError.DirtyMedia, "Acknowledge or explicitly discard dirty media before capture.");
        if (slot.WriteProtected)
            return new(HddSelectedCaptureError.WriteProtectedMedia, "Capture is a write to a write-protected slot.");
        // All expectations are validated before any mutation; validated inputs cannot throw in the core attach.
        slot.AttachSelected(selection.Image.AsSpan(), discardCurrentDirty);
        coldRestoreBoundary = false;
        completedGuestCooperation = false;
        return new(HddSelectedCaptureError.None, "");
    }

    private HddSlot Slot(byte drive)
    {
        VerifyOwner();
        if (slots is null) throw new InvalidOperationException("disk.hdd is disabled.");
        if (drive is < 2 or > 5) throw new ArgumentOutOfRangeException(nameof(drive));
        return slots[drive - 2];
    }

    /// <summary>Owned logical state for snapshot composition. No host path or handle.</summary>
    public BlockDiskPortState Capture()
    {
        VerifyOwner();
        if (!HddEnabled) throw new InvalidOperationException("disk.hdd is disabled.");
        RequireNoGuestSwitch();
        return new(pointer, status, slots!.Select(s => s.Capture()).ToArray());
    }

    public void Restore(BlockDiskPortState? state)
    {
        VerifyOwner();
        if (!HddEnabled) throw new InvalidOperationException("disk.hdd is disabled.");
        RequireNoGuestSwitch();
        if (state is null)
        {
            slots = Enumerable.Range(0, 4).Select(_ => new HddSlot()).ToArray();
            RetireGuestAckForRestore();
            pointer = 0; status = 0;
            uncooperated = false;
            coldRestoreBoundary = true;
            return;
        }
        ArgumentNullException.ThrowIfNull(state.Slots);
        if (state.Slots.Length != 4) throw new ArgumentException("HDD snapshot/profile mismatch.", nameof(state));
        // Validate and copy every slot before adopting any state.
        var restored = state.Slots.Select(HddSlot.Restore).ToArray();
        RetireGuestAckForRestore();
        slots = restored;
        pointer = state.Pointer; status = state.Status;
    }

    /// <summary>Unattached adapter mappings for device-level composition/verification only.
    /// Faithful profiles never install these; explicit blockport/HDD profiles do.
    /// Construct the final PortDecoder to enforce directional overlap validation.</summary>
    public IReadOnlyList<PortMapping> Mappings()
    {
        VerifyOwner();
        ushort mask = HddEnabled ? (ushort)0xffff : (ushort)0xff;
        return [
            new(0x81, mask, this, PortAccess.ReadWrite),
            new(0x83, mask, this, PortAccess.ReadWrite),
            new(0x89, mask, this, PortAccess.ReadWrite)
        ];
    }

    public byte Read(ushort port, long masterTick)
    {
        VerifyBoundary(masterTick);
        return (HddEnabled ? port : port & 0xff) switch
        {
            0x81 => (byte)pointer,
            0x83 => (byte)(pointer >> 8),
            0x89 => status,
            _ => 0xff
        };
    }

    public void Write(ushort port, byte value, long masterTick)
    {
        VerifyBoundary(masterTick);
        switch (HddEnabled ? port : port & 0xff)
        {
            case 0x81: pointer = (ushort)((pointer & 0xff00) | value); break;
            case 0x83: pointer = (ushort)((pointer & 0x00ff) | (value << 8)); break;
            case 0x89: status = Execute(value); break;
        }
    }

    private byte Execute(byte operation)
    {
        // Every error is checked before a transfer mutates RAM/media. The descriptor is
        // captured in full first, including when READ overwrites it as an ordinary DMA effect.
        if (operation is not (1 or 2)) return 0x01;
        if (HddEnabled && memory.Read(pointer) == 2) return ExecuteHdd(operation);
        if (pointer > 0xfff8) return 0x03;
        for (var i = 0; i < 8; i++)
        {
            var location = memory.Resolve((ushort)(pointer + i));
            if (!location.Writable || location.Store is not (MemoryStore.Ram or MemoryStore.ShadowRam)) return 0x03;
        }
        Span<byte> command = stackalloc byte[8];
        for (var i = 0; i < command.Length; i++) command[i] = memory.Read((ushort)(pointer + i));
        if (command[0] != 1 || command[7] != 0) return 0x02;
        if (command[1] > 1) return 0x04;
        if (GuestSwitchHoldsCpu) return 0x0e;
        // ReadStatus can advance byte readiness under fdc.instant. Inspect returns the same
        // current MSR without that programmed-poll effect or any result consumption.
        if (controller.Fdc.Inspect().MainStatus is not (0x00 or 0x80)) return 0x0b;
        var result = transfer.Execute(operation == 2, command[1], command[2], command[3], command[4],
            (ushort)(command[5] | (command[6] << 8)));
        // Host experimental enum ordinals are deliberately NOT the guest wire status.
        return result switch
        {
            BlockDiskStatus.Success => 0x00,
            BlockDiskStatus.InvalidDrive => 0x04,
            BlockDiskStatus.NoMedia => 0x05,
            BlockDiskStatus.SectorNotFound => 0x06,
            BlockDiskStatus.InvalidSectorSize => 0x07,
            BlockDiskStatus.WriteProtected => 0x08,
            BlockDiskStatus.InvalidDma => 0x09,
            BlockDiskStatus.MediaError => 0x0a,
            _ => throw new InvalidOperationException("Unknown internal block transfer result.")
        };
    }

    private byte ExecuteHdd(byte doorbell)
    {
        if (doorbell != 1) return 0x01;
        if (pointer > 0xfff0) return 0x03;
        for (var i = 0; i < 16; i++)
        {
            var location = memory.Resolve((ushort)(pointer + i));
            if (!location.Writable || location.Store is not (MemoryStore.Ram or MemoryStore.ShadowRam)) return 0x03;
        }
        Span<byte> command = stackalloc byte[16];
        for (var i = 0; i < command.Length; i++) command[i] = memory.Read((ushort)(pointer + i));
        if (command[3] != 0 || command[10] != 0 || command[11] != 0) return 0x02;
        if (command[2] is < 2 or > 5) return 0x04;
        var operation = command[1];
        if (GuestAckEnabled && operation is 4 or 5) return ExecuteGuestAck(command);
        if (operation is not (1 or 2 or 3)) return 0x01;
        if (GuestSwitchHoldsCpu) return 0x0e;
        var slot = slots![command[2] - 2];
        var lba = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
        var dma = BinaryPrimitives.ReadUInt16LittleEndian(command[8..]);
        var generation = BinaryPrimitives.ReadUInt32LittleEndian(command[12..]);
        if (operation != 3)
        {
            if (!slot.Present) return 0x05;
            if (generation != slot.Generation) return 0x0d;
            if (lba >= HddSlot.SectorCount) return 0x0c;
            if (operation == 2 && slot.WriteProtected) return 0x08;
        }
        if (!transfer.ValidDma(dma)) return 0x09;
        if (operation == 3)
        {
            Span<byte> reply = stackalloc byte[16];
            reply.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(reply, slot.Present ? (uint)HddSlot.SectorCount : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(reply[4..], slot.Generation);
            reply[8] = slot.Present ? (byte)(slot.WriteProtected ? 1 : 3) : (byte)0;
            transfer.WriteDma(dma, reply);
        }
        else if (operation == 1) transfer.WriteDma(dma, slot.Read(lba));
        else slot.Write(lba, transfer.ReadDma(dma));
        return 0;
    }

    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != owner)
            throw new InvalidOperationException("Block disk access belongs to the emulation owner.");
    }

    private void VerifyBoundary(long tick)
    {
        VerifyOwner();
        coldRestoreBoundary = false;
        if (tick != controller.MasterTick)
            throw new InvalidOperationException("Drain shared device time to the bus tick before block disk access.");
    }
}
