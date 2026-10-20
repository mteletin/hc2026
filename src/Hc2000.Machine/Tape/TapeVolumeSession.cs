using System.Collections.Immutable;
using Hc2000.Machine.Memory;
using Hc2000.Machine.Storage;

namespace Hc2000.Machine.Tape;

/// <summary>The bound paged range IS the image; guest stores use the existing paged latches.</summary>
public interface ITapeVolumeGuestWindow
{
    bool Mounted { get; }
    int ImageBytes { get; }
    bool Dirty { get; }
    bool WriteProtected { get; }
    bool MediaLive { get; }
    void Bind(HcMemory memory);
    void Unbind();
    byte Read(ulong offset);
    void Write(ulong offset, byte value);
}

/// <summary>Spec 14's one owner-bound session. No filesystem paths, CPU address, port or second image.</summary>
public sealed partial class TapeVolumeSession : ITapeVolumeGuestWindow
{
    private readonly TapeTransport tape;
    private readonly HcMemory memory;
    private readonly Func<char, bool> slotPresent;
    private readonly TapeCatalogCodec codec = new();
    private readonly TapeLimits limits = new();
    private TapeCatalogEntry? entry;
    private TapeCatalogIdentity? catalogIdentity;
    private ulong epoch;
    private TapeSide side;
    private bool boundWriteProtected;
    private PagedMemoryRange? range;
    private ITapeSignalIndex? cachedIndex;
    private TapeCatalogSide? cachedSide;
    private TapeCatalogRecord? cachedCatalog;
    public bool Mounted => range is not null;
    public int ImageBytes => checked((int)(entry?.ImageBytes ?? 0));
    public bool Dirty { get; private set; }
    public char Letter { get; private set; }
    public string? Name => entry?.Name;
    public int FirstPage => range?.FirstPage ?? -1;
    public bool WriteProtected
    {
        get
        {
            var view = tape.Deck.Observe();
            return entry?.WriteProtected == true || !(side == TapeSide.A ? view.A : view.B).TabPresent;
        }
    }
    public bool MediaLive
    {
        get
        {
            if (!Mounted || epoch != tape.MediaEpoch) return false;
            var view = tape.Deck.Observe();
            return view.Present && view.ActiveSide == side && RefreshCatalog().Success && cachedCatalog?.Identity == catalogIdentity;
        }
    }
    public TapeVolumeSession(MachineProfile profile, TapeTransport tape, HcMemory memory, Func<char, bool>? slotPresent = null)
    {
        profile.Validate();
        if (profile.Name != MachineProfile.Hc2000V2PagedTapeV2.Name
            || profile.Memory != memory.Profile with { RomProvenance = profile.Memory.RomProvenance })
            throw new ArgumentException("The exact paged tape profile and its memory are required.", nameof(profile));
        this.tape = tape; this.memory = memory; this.slotPresent = slotPresent ?? (_ => false);
        memory.UseSessionPaging();
        tape.DeckStateChanged += OnDeckStateChanged;
    }
    private void OnDeckStateChanged(TapeDeckView view)
    {
        // Retain stale dirty bytes for explicit cancellation; revoke only the
        // guest alias. A same-epoch side round-trip is recoverable.
        if (Mounted && (epoch != tape.MediaEpoch || boundWriteProtected != WriteProtected))
        {
            InvalidateDescriptor();
            memory.SetSessionPagingEngaged(false);
        }
    }
    private TapeResult<bool> RefreshCatalog()
    {
        var index = tape.ActiveIndex;
        if (cachedSide is not null && ReferenceEquals(index, cachedIndex)) return TapeResult<bool>.Ok(true);
        cachedSide = null; cachedCatalog = null; cachedIndex = index;
        var raw = tape.ReadCatalogSide();
        if (!raw.Success) return Fail(raw.Failure!);
        var selection = codec.FindNewest(raw.Value, limits);
        if (!selection.Success) return Fail(selection.Failure!);
        cachedSide = raw.Value; cachedCatalog = selection.Value.Newest;
        return TapeResult<bool>.Ok(true);
    }
    public TapeResult<ImmutableArray<TapeCatalogEntry>> List()
    {
        var refresh = RefreshCatalog();
        return refresh.Success ? TapeResult<ImmutableArray<TapeCatalogEntry>>.Ok(cachedCatalog?.Entries ?? [])
            : TapeResult<ImmutableArray<TapeCatalogEntry>>.Fail(refresh.Failure!.Code, refresh.Failure.Detail);
    }
    public TapeResult<TapeDeckView> GoToFile(uint dataBlock)
    {
        var blocks = tape.ActiveIndex is { } index ? TapCodec.Blocks(index).Where(b => b.Flag == 0xff).ToArray() : [];
        if (dataBlock >= blocks.Length) return TapeResult<TapeDeckView>.Fail(TapeError.BoundsExceeded, "Data-block index is outside this side.");
        var view = tape.Deck.Observe();
        return tape.WindTo(blocks[dataBlock].Start < view.Position, blocks[dataBlock].Start);
    }
    public TapeResult<bool> Mount(string name, char letter)
    {
        letter = char.ToUpperInvariant(letter);
        if (Mounted || letter is < 'C' or > 'P' or 'M' || letter <= 'F' && slotPresent(letter))
            return Refuse("Drive letter is unavailable or a volume is already mounted.");
        var view = tape.Deck.Observe();
        if (!view.Present || view.Mode != TapeDeckMode.Stopped || view.Paused) return Refuse("Mount requires a stopped cassette.");
        var refresh = RefreshCatalog(); if (!refresh.Success) return refresh;
        var selected = cachedCatalog?.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (selected is null) return Refuse("Volume is not in the newest valid catalog.");
        var image = codec.ReadImage(cachedSide!, selected, limits);
        if (!image.Success) return Fail(image.Failure!);
        entry = selected; catalogIdentity = cachedCatalog!.Identity;
        epoch = tape.MediaEpoch; side = view.ActiveSide; Letter = letter;
        try { Bind(memory); }
        catch (InvalidOperationException e) { Drop(); return Refuse(e.Message); }
        image.Value.AsSpan().CopyTo(Bytes.Span);
        return TapeResult<bool>.Ok(true);
    }
    public void Bind(HcMemory target)
    {
        if (!ReferenceEquals(target, memory) || entry is null || range is not null || ImageBytes is < 16384 or > 8388608 || ImageBytes % 4096 != 0)
            throw new InvalidOperationException("No legal unbound image for this memory.");
        range = memory.ClaimPagedPages(this, (ImageBytes + 0x3fff) / 0x4000, Write);
        boundWriteProtected = WriteProtected;
        memory.SetSessionPagingEngaged(true, range);
        PublishDescriptor();
    }
    private Memory<byte> Bytes => memory.ClaimedPagedBytes(this)[..ImageBytes];
    public void Unbind()
    {
        if (Dirty) throw new InvalidOperationException("Dirty session must be written back or cancelled.");
        Drop();
    }
    private void Drop()
    {
        InvalidateDescriptor();
        memory.SetSessionPagingEngaged(false);
        memory.ReleasePagedPages(this); range = null; entry = null; catalogIdentity = null;
        Dirty = false; Letter = default;
    }
    public void Cancel() => Drop();
    public TapeResult<bool> Unmount()
    {
        if (Dirty) return TapeResult<bool>.Fail(TapeError.DirtyConfirmationRequired, "Dirty session must be written back or cancelled.");
        Unbind(); return TapeResult<bool>.Ok(true);
    }
    public byte Read(ulong offset)
    {
        if (!Mounted || offset >= (ulong)ImageBytes) throw new ArgumentOutOfRangeException(nameof(offset));
        return Bytes.Span[(int)offset];
    }
    public void Write(ulong offset, byte value)
    {
        if (!Mounted || offset >= (ulong)ImageBytes || WriteProtected || !MediaLive) return;
        Bytes.Span[(int)offset] = value; Dirty = true;
    }
    public TapeResult<bool> Writeback()
    {
        if (!MediaLive) return Refuse("Bound media epoch, side or catalog has changed.");
        if (WriteProtected) return TapeResult<bool>.Fail(TapeError.WriteProtected, "Volume or side is write protected.");
        if (!Dirty) return TapeResult<bool>.Ok(true);
        // This is a detached codec input only, not a second session image or guest transfer path.
        var mutation = new TapeCatalogMutation(entry!.Name, ImmutableArray.CreateRange(Bytes.ToArray()), entry.WriteProtected);
        var current = cachedSide! with { WriteProtected = WriteProtected };
        var planned = codec.PlanAppend(current, catalogIdentity, mutation, new(limits.InputBytes, limits.SourceBlocks, true), limits);
        if (!planned.Success) return Fail(planned.Failure!);
        var published = tape.PublishCatalog(planned.Value, epoch, side);
        if (!published.Success) return published;
        cachedSide = null;
        var refreshed = RefreshCatalog(); if (!refreshed.Success) return refreshed;
        var checkedPair = codec.CheckPublication(cachedSide!, planned.Value, limits);
        if (!checkedPair.Success) return checkedPair;
        catalogIdentity = planned.Value.Published.Identity;
        entry = planned.Value.Published.Entries.Single(e => e.Name == entry.Name);
        Dirty = false;
        return TapeResult<bool>.Ok(true);
    }
    private static TapeResult<bool> Refuse(string detail) => TapeResult<bool>.Fail(TapeError.Interlock, detail);
    private static TapeResult<bool> Fail(TapeFailure failure) => TapeResult<bool>.Fail(failure.Code, failure.Detail);
}
