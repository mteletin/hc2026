namespace Hc2000.Machine.Floppy;

/// <summary>Owned logical media, never a host path or open file. Capture/export return copies.</summary>
public sealed record HddSlotState(byte[] Bytes, uint Generation, bool WriteProtected, bool Dirty)
{
    public bool Present => Bytes.Length != 0;
}

/// <summary>One phase-2 raw volume. Access is serialized by the block-port owner boundary.</summary>
internal sealed class HddSlot
{
    public const int ImageBytes = 8_388_608;
    public const int SectorCount = ImageBytes / 512;
    private byte[] bytes = [];
    public bool Present => bytes.Length != 0;
    public uint Generation { get; private set; }
    public bool WriteProtected { get; private set; }
    public bool Dirty { get; private set; }

    public void Attach(ReadOnlySpan<byte> image, bool writeProtected, bool discardDirty) =>
        AttachCore(image, writeProtected, discardDirty, pending: false);

    /// <summary>Writable selection capture: the slot holds the actual owned image bytes immediately and
    /// becomes pending-Saved (dirty), so only a matching TryAcknowledgeHddSaved clears the state.
    /// The bounded-capture seam validates all expectations before calling this.</summary>
    internal void AttachSelected(ReadOnlySpan<byte> image, bool discardDirty) =>
        AttachCore(image, writeProtected: false, discardDirty, pending: true);

    private void AttachCore(ReadOnlySpan<byte> image, bool writeProtected, bool discardDirty, bool pending)
    {
        if (image.Length != ImageBytes) throw new ArgumentException("HDD images must be exactly 8,388,608 bytes.", nameof(image));
        RequireClean(discardDirty);
        var generation = checked(Generation + 1); // Never wrap into an old media identity.
        var owned = image.ToArray();
        bytes = owned; Generation = generation; WriteProtected = writeProtected; Dirty = pending;
    }

    public void Detach(bool discardDirty)
    {
        RequireClean(discardDirty);
        bytes = []; WriteProtected = false; Dirty = false; // Retain the generation across empty slots.
    }

    private void RequireClean(bool discardDirty)
    {
        if (Dirty && !discardDirty) throw new InvalidOperationException("Export or explicitly discard dirty HDD media first.");
    }

    public byte[] Export()
    {
        var result = (byte[])bytes.Clone();
        Dirty = false;
        return result;
    }

    public byte[] Read(uint lba) => bytes.AsSpan(checked((int)lba * 512), 512).ToArray();
    public void Write(uint lba, ReadOnlySpan<byte> data)
    {
        data.CopyTo(bytes.AsSpan(checked((int)lba * 512), 512));
        Dirty = true;
    }

    public HddSlotState Capture() => new((byte[])bytes.Clone(), Generation, WriteProtected, Dirty);

    internal bool MatchesSaved(HddSlotState? expected) =>
        expected is not null && expected.Bytes is not null && Present && !WriteProtected &&
        expected.Generation == Generation && expected.WriteProtected == WriteProtected &&
        expected.Dirty == Dirty && expected.Bytes.Length == ImageBytes &&
        bytes.AsSpan().SequenceEqual(expected.Bytes);

    internal void AcknowledgeSaved() => Dirty = false;

    public static HddSlot Restore(HddSlotState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Bytes);
        if (state.Bytes.Length != 0 && state.Bytes.Length != ImageBytes ||
            state.Bytes.Length == 0 && (state.Dirty || state.WriteProtected) ||
            state.Bytes.Length != 0 && state.Generation == 0)
            throw new ArgumentException("Invalid HDD logical media state.", nameof(state));
        return new() { bytes = (byte[])state.Bytes.Clone(), Generation = state.Generation,
            WriteProtected = state.WriteProtected, Dirty = state.Dirty };
    }
}
