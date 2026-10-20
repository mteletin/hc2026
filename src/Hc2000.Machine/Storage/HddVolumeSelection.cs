using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Hc2000.Machine.Storage;

/// <summary>Trusted upstream assertion over the same stable owned index||footer snapshot used by DecodeHdd.
/// Not authentication, file-origin evidence, OS CAS, or a digest recomputed by the selector.</summary>
public sealed record HddStoreToken(ImmutableArray<byte> ContainerId, ulong Revision, ImmutableArray<byte> MetadataSha256);
public sealed record HddVolumeIdentity(HddStoreToken Store, string Name, ImmutableArray<byte> PayloadSha256);
public sealed record HddMountedVolumeIdentity(ImmutableArray<byte> ContainerId, string Name);
public sealed record HddSelectionLimits(ulong MaxFileBytes, ulong MaxEntries, ulong MaxIndexBytes,
    ulong MaxMetadataBytes, ulong MaxSelectedBytes, ulong MaxResidentBytes, ulong MaxSnapshotBytes,
    ulong MaxCopyWorkBytes, ulong MaxTotalLiveBytes, ulong MaxTransactionBytes, ulong MaxIoBytes);
/// <summary>K includes this candidate and all concurrent selected/captured/prepared work; R/E/Q are accurate caller facts.</summary>
public sealed record HddSelectionUsage(uint ConcurrentSelectedImages, ulong ResidentBytes, ulong SnapshotBytes,
    ulong TransferBufferBytes);
public enum HddSelectionError
{
    None, InvalidDescriptor, InvalidIdentity, VolumeNotFound, StaleIdentity, InvalidImageSize,
    PayloadCrcMismatch, PayloadHashMismatch, AlreadyMounted, ResourceLimit
}
public sealed class HddSelectionResult
{
    public bool Success => Error == HddSelectionError.None;
    public HddSelectionError Error { get; }
    public string Detail { get; }
    public HddPreparedSelection? Selection { get; }
    internal HddSelectionResult(HddSelectionError error, string detail, HddPreparedSelection? selection = null)
    { Error = error; Detail = detail; Selection = selection; }
}
/// <summary>Owned offline candidate only. Future adoption remains initial-only on a new never-executed,
/// non-restored machine until G5; this object cannot attach, switch, save or acknowledge anything.</summary>
public sealed class HddPreparedSelection
{
    public ImmutableArray<byte> Image { get; }
    public HddVolumeIdentity Identity { get; }
    public HddSlotOrdinal SlotOrdinal { get; }
    public bool WriteProtected { get; }
    public ImmutableArray<byte> BaselineSha256 => Identity.PayloadSha256;
    internal HddPreparedSelection(ImmutableArray<byte> image, HddVolumeIdentity identity,
        HddSlotOrdinal ordinal, bool writeProtected)
    { Image = image; Identity = identity; SlotOrdinal = ordinal; WriteProtected = writeProtected; }
}

/// <summary>Pure bounded preparation, not parsing, mounting or host I/O. The trusted caller pairs descriptor and observed
/// token from the SAME stable owned DecodeHdd snapshot, provides independently intended identity/bytes/usage/aliases,
/// and keeps all inputs stable throughout this call. Shape checks cannot authenticate public descriptor constructors.</summary>
public static class HddVolumeSelection
{
    private sealed class Refusal(HddSelectionError error, string detail) : Exception(detail)
    { public HddSelectionError Error => error; }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, HddSelectionError error, string detail)
    { if (!condition) throw new Refusal(error, detail); }
    private static bool Bytes(ImmutableArray<byte> a, ImmutableArray<byte> b) => a.AsSpan().SequenceEqual(b.AsSpan());
    private static bool ValidId(ImmutableArray<byte> id)
    {
        if (id.IsDefault || id.Length != 16) return false;
        foreach (var b in id) if (b != 0) return true;
        return false;
    }
    private static bool ValidStore(HddStoreToken? token) => token is not null && ValidId(token.ContainerId)
        && token.Revision != 0 && !token.MetadataSha256.IsDefault && token.MetadataSha256.Length == 32;
    private static bool SameStore(HddStoreToken a, HddStoreToken b) => a.Revision == b.Revision
        && Bytes(a.ContainerId, b.ContainerId) && Bytes(a.MetadataSha256, b.MetadataSha256);
    private static bool ValidName(string? name) => name is not null && VolumeCatalogMetadata.CanonicalName83(name) is { } bytes
        && VolumeCatalogMetadata.ParseName83(bytes, 0) == name;

    public static HddSelectionResult Prepare(HddContainerDescriptor descriptor, HddStoreToken observedStore,
        HddVolumeIdentity expectedVolume, ReadOnlySpan<byte> image, bool hostReadOnly,
        HddSelectionLimits limits, HddSelectionUsage usage, ImmutableArray<HddMountedVolumeIdentity> mountedVolumes)
    {
        try { return PrepareCore(descriptor, observedStore, expectedVolume, image, hostReadOnly, limits, usage, mountedVolumes); }
        catch (Refusal e) { return new(e.Error, e.Message); }
        catch (OverflowException) { return new(HddSelectionError.ResourceLimit, "Selection accounting overflow."); }
        catch (OutOfMemoryException) { return new(HddSelectionError.ResourceLimit, "Selection allocation refused."); }
    }

    private static HddSelectionResult PrepareCore(HddContainerDescriptor descriptor, HddStoreToken observedStore,
        HddVolumeIdentity expectedVolume, ReadOnlySpan<byte> image, bool hostReadOnly,
        HddSelectionLimits limits, HddSelectionUsage usage, ImmutableArray<HddMountedVolumeIdentity> mountedVolumes)
    {
        Require(descriptor is not null && descriptor.Footer is not null && !descriptor.Entries.IsDefault,
            HddSelectionError.InvalidDescriptor, "Missing descriptor.");
        var footer = descriptor!.Footer;
        Require(limits is not null && usage is not null, HddSelectionError.ResourceLimit, "Explicit limits and usage required.");
        var n = (ulong)descriptor.Entries.Length;
        Require(n <= 65535, HddSelectionError.ResourceLimit, "Entry maximum exceeded.");
        ulong index, arena, file, metadata, selected, copies, total;
        checked
        {
            index = 12 + 24 * n;
            arena = n * VolumeCatalogMetadata.SlotBytes;
            file = arena + index + 64;
            metadata = 4 * index + 128 * n + 128;
            selected = (ulong)usage!.ConcurrentSelectedImages * VolumeCatalogMetadata.SlotBytes;
            copies = 3 * selected + usage.TransferBufferBytes;
            total = usage.ResidentBytes + usage.SnapshotBytes + metadata + copies;
        }
        Require(usage!.ConcurrentSelectedImages is >= 1 and <= 4 && usage.TransferBufferBytes <= 65536
            && usage.ResidentBytes <= 4UL * VolumeCatalogMetadata.SlotBytes
            && n <= limits!.MaxEntries && file <= limits.MaxFileBytes && index <= limits.MaxIndexBytes
            && metadata <= limits.MaxMetadataBytes && selected <= limits.MaxSelectedBytes
            && usage.ResidentBytes <= limits.MaxResidentBytes && usage.SnapshotBytes <= limits.MaxSnapshotBytes
            && copies <= limits.MaxCopyWorkBytes && total <= limits.MaxTotalLiveBytes,
            HddSelectionError.ResourceLimit, "Selection budget exceeded.");
        // Transaction/I/O caps are explicit but this leaf performs neither publication nor I/O.
        Require(footer.Entries == n && footer.ArenaBytes == arena && footer.IndexBytes == index
            && footer.IndexStart == arena && footer.IndexEnd == arena + index && footer.FileLength == file
            && footer.Revision != 0 && footer.ContainerId is { Length: 16 },
            HddSelectionError.InvalidDescriptor, "Inconsistent descriptor ranges or identity.");
        Require(ValidStore(observedStore) && expectedVolume is not null && ValidStore(expectedVolume.Store)
            && !expectedVolume.PayloadSha256.IsDefault && expectedVolume.PayloadSha256.Length == 32,
            HddSelectionError.InvalidIdentity, "Invalid identity shape.");
        // Exactly one bounded read/copy of the caller's mutable descriptor ID; never retain its array.
        var id = ImmutableArray.CreateRange(footer.ContainerId);
        Require(ValidId(id), HddSelectionError.InvalidDescriptor, "Empty descriptor identity.");
        Require(Bytes(id, observedStore.ContainerId) && footer.Revision == observedStore.Revision
            && SameStore(observedStore, expectedVolume!.Store), HddSelectionError.StaleIdentity, "Store token mismatch.");
        Require(ValidName(expectedVolume!.Name), HddSelectionError.InvalidIdentity, "Invalid expected canonical name.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordinals = new bool[descriptor.Entries.Length];
        HddCatalogEntry? selectedEntry = null;
        foreach (var row in descriptor.Entries)
        {
            Require(row is not null && ValidName(row.Name) && names.Add(row.Name)
                && row.ImageBytes == VolumeCatalogMetadata.SlotBytes && row.SlotOrdinal.Value < n,
                HddSelectionError.InvalidDescriptor, "Invalid descriptor entry.");
            Require(!ordinals[row!.SlotOrdinal.Value], HddSelectionError.InvalidDescriptor, "Duplicate ordinal.");
            ordinals[row.SlotOrdinal.Value] = true;
            if (StringComparer.OrdinalIgnoreCase.Equals(row.Name, expectedVolume.Name)) selectedEntry = row;
        }
        Require(selectedEntry is not null, HddSelectionError.VolumeNotFound, "Named volume not found.");
        Require(!mountedVolumes.IsDefault && mountedVolumes.Length <= 4,
            HddSelectionError.InvalidIdentity, "Invalid mounted identity list.");
        foreach (var mounted in mountedVolumes)
        {
            Require(mounted is not null && ValidId(mounted.ContainerId) && ValidName(mounted.Name),
                HddSelectionError.InvalidIdentity, "Invalid mounted identity.");
            Require(!(Bytes(id, mounted!.ContainerId) && StringComparer.OrdinalIgnoreCase.Equals(mounted.Name, selectedEntry!.Name)),
                HddSelectionError.AlreadyMounted, "Volume already mounted, including read-only aliases.");
        }
        Require(image.Length == VolumeCatalogMetadata.SlotBytes, HddSelectionError.InvalidImageSize, "Image must be exactly 8 MiB.");
        Require(VolumeCatalogMetadata.Crc32(image) == selectedEntry!.ImageCrc,
            HddSelectionError.PayloadCrcMismatch, "Stored image CRC mismatch.");
        var hash = SHA256.HashData(image);
        Require(hash.AsSpan().SequenceEqual(expectedVolume.PayloadSha256.AsSpan()),
            HddSelectionError.PayloadHashMismatch, "Stored image baseline SHA mismatch.");
        var store = new HddStoreToken(id, observedStore.Revision, ImmutableArray.CreateRange(observedStore.MetadataSha256));
        var identity = new HddVolumeIdentity(store, selectedEntry.Name, ImmutableArray.CreateRange(hash));
        var owned = ImmutableArray.CreateRange(image.ToArray());
        return new(HddSelectionError.None, "", new HddPreparedSelection(owned, identity, selectedEntry.SlotOrdinal,
            selectedEntry.ReadOnly || hostReadOnly));
    }
}
