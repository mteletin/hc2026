using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Media;

internal sealed record HddColdVolumeRequest(byte Drive, string Name, HddVolumeIdentity? Expected = null, bool Writable = false);

/// <summary>Bounded named-volume copies from one admitted container snapshot.
/// The existing composition owns that admission through cold installation acknowledgement;
/// disposing a UI/caller reference never releases an enqueued or unknown owner outcome.</summary>
internal sealed class HddColdVolume : IDisposable
{
    internal HddColdAdmission Admission { get; }
    private readonly ImmutableArray<HddPreparedSelection> selections;
    private readonly ImmutableArray<byte> drives;
    private readonly string path;
    private bool disposed;
    public HddVolumeIdentity Identity => selections.Single().Identity;
    public ImmutableArray<HddVolumeIdentity> Identities => selections.Select(selection => selection.Identity).ToImmutableArray();
    private HddColdVolume(HddColdAdmission admission, ImmutableArray<HddPreparedSelection> selections, ImmutableArray<byte> drives, string path)
    { Admission = admission; this.selections = selections; this.drives = drives; this.path = path; }
    public HddInitialMedia Media(byte drive)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (drive is < 2 or > 5) throw new ArgumentOutOfRangeException(nameof(drive));
        var selection = selections.Single(); // Preserve the original single-volume caller; never silently discard a map.
        return new(drive, selection.Image, selection.Identity.Name, selection.WriteProtected)
            { Binding = new(path, selection.Identity, !selection.WriteProtected) };
    }
    public ImmutableArray<HddInitialMedia> MappedMedia
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return selections.Select((selection, index) =>
                new HddInitialMedia(drives[index], selection.Image, selection.Identity.Name, selection.WriteProtected)
                    { Binding = new(path, selection.Identity, !selection.WriteProtected) }).ToImmutableArray();
        }
    }

    // Finite enclosing-file bounds; up to four requested images, never the entire arena in RAM.
    private static readonly HddLimits CodecLimits = new(uint.MaxValue, 511, 12_276, 114_640);
    internal const ulong DefaultMaxTotalLiveBytes = 192UL * 1024 * 1024;
    private static readonly HddSelectionLimits Limits = new(uint.MaxValue, 511, 12_276, 114_640,
        33_554_432, 33_554_432, 33_554_432, 100_728_832, DefaultMaxTotalLiveBytes, uint.MaxValue, uint.MaxValue);

    public static HddColdVolume Open(string path, string name, HddVolumeIdentity? expected = null, bool writable = false)
        => OpenMapped(path, [new(2, name, expected, writable)]);

    public static HddColdVolume OpenMapped(string path, ImmutableArray<HddColdVolumeRequest> requests,
        ulong maxTotalLiveBytes = DefaultMaxTotalLiveBytes)
    {
        if (requests.IsDefaultOrEmpty || requests.Length > 4 || requests.Any(request => request is null || request.Drive is < 2 or > 5)
            || requests.Select(request => request.Drive).Distinct().Count() != requests.Length)
            throw new ArgumentException("Map one to four distinct HDD drives C–F.", nameof(requests));
        if (requests.Any(request => string.IsNullOrWhiteSpace(request.Name))
            || requests.Select(request => request.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Length)
            throw new InvalidDataException("A named HDD volume may be mapped only once, even read-only.");
        if (maxTotalLiveBytes == 0 || maxTotalLiveBytes > DefaultMaxTotalLiveBytes)
            throw new ArgumentOutOfRangeException(nameof(maxTotalLiveBytes));
        var limits = Limits with { MaxTotalLiveBytes = maxTotalLiveBytes };
        // E reserves all detached composition copies; R reserves all four existing native slots.
        // Q is the admitted FileStream's 64 KiB buffer: payload rechecks reuse the charged image scratch.
        var usage = new HddSelectionUsage((uint)requests.Length, 33_554_432,
            checked((ulong)requests.Length * HddMediaAdapter.ImageLength), 65536);
        var full = Path.GetFullPath(path);
        AdmitPath(full);
        var stamp = File.GetLastWriteTimeUtc(full);
        var admission = HddColdAdmission.Open(full);
        var source = admission.Source;
        try
        {
            var length = source.Length;
            if (length < 76 || (ulong)length > limits.MaxFileBytes)
                throw new InvalidDataException("HDD container length exceeds cold admission.");
            var footer = new byte[64]; source.Position = length - 64; source.ReadExactly(footer);
            var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, CodecLimits);
            if (!inspected.Success) throw new InvalidDataException(inspected.Failure!.Detail);
            var view = inspected.Value;
            // Aggregate G4 reservation precedes index/image allocation, not just each leaf selector call.
            var metadata = checked(4 * view.IndexBytes + 128UL * view.Entries + 128);
            var selectedBytes = checked((ulong)usage.ConcurrentSelectedImages * HddMediaAdapter.ImageLength);
            var copyBytes = checked(3 * selectedBytes + usage.TransferBufferBytes);
            var total = checked(usage.ResidentBytes + usage.SnapshotBytes + metadata + copyBytes);
            if (metadata > limits.MaxMetadataBytes || selectedBytes > limits.MaxSelectedBytes
                || usage.ResidentBytes > limits.MaxResidentBytes || usage.SnapshotBytes > limits.MaxSnapshotBytes
                || copyBytes > limits.MaxCopyWorkBytes || total > limits.MaxTotalLiveBytes)
                throw new InvalidDataException("HDD mapped-volume budget exceeded.");
            var index = new byte[checked((int)view.IndexBytes)];
            source.Position = checked((long)view.IndexStart); source.ReadExactly(index);
            var decoded = HddCatalogCodec.DecodeHdd(footer, index, (ulong)length, CodecLimits);
            if (!decoded.Success) throw new InvalidDataException(decoded.Failure!.Detail);
            var descriptor = decoded.Value;
            var rows = requests.Select(request => descriptor.Entries.SingleOrDefault(entry =>
                StringComparer.OrdinalIgnoreCase.Equals(entry.Name, request.Name))
                ?? throw new InvalidDataException("Named HDD volume not found.")).ToImmutableArray();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData(index); digest.AppendData(footer);
            var store = new HddStoreToken([.. view.ContainerId], view.Revision, [.. digest.GetHashAndReset()]);
            var prepared = ImmutableArray.CreateBuilder<HddPreparedSelection>(requests.Length);
            var mounted = ImmutableArray<HddMountedVolumeIdentity>.Empty;
            var image = new byte[HddMediaAdapter.ImageLength];
            for (int i = 0; i < requests.Length; i++)
            {
                var row = rows[i];
                source.Position = checked((long)row.SlotOrdinal.Value * HddMediaAdapter.ImageLength);
                source.ReadExactly(image);
                var intended = requests[i].Expected ?? new HddVolumeIdentity(store, row.Name, [.. SHA256.HashData(image)]);
                if (!StringComparer.OrdinalIgnoreCase.Equals(intended.Name, requests[i].Name))
                    throw new InvalidDataException("Selection name changed.");
                var result = HddVolumeSelection.Prepare(descriptor, store, intended, image, !requests[i].Writable, limits, usage, mounted);
                if (!result.Success) throw new InvalidDataException(result.Detail);
                prepared.Add(result.Selection!);
                mounted = mounted.Add(new(store.ContainerId, row.Name));
            }
            // Re-read only requested payloads and metadata under the same admission, not the enclosing arena.
            for (int i = 0; i < rows.Length; i++)
            {
                source.Position = checked((long)rows[i].SlotOrdinal.Value * HddMediaAdapter.ImageLength);
                for (int remaining = image.Length; remaining > 0;)
                {
                    int count = Math.Min(remaining, 65536); source.ReadExactly(image.AsSpan(0, count));
                    digest.AppendData(image, 0, count); remaining -= count;
                }
                if (!digest.GetHashAndReset().AsSpan().SequenceEqual(prepared[i].Identity.PayloadSha256.AsSpan()))
                    throw new InvalidDataException("HDD payload changed during preparation.");
            }
            var checkIndex = new byte[index.Length]; source.Position = checked((long)view.IndexStart); source.ReadExactly(checkIndex);
            var checkFooter = new byte[64]; source.ReadExactly(checkFooter);
            AdmitPath(full);
            if (source.Length != length || new FileInfo(full).Length != length || File.GetLastWriteTimeUtc(full) != stamp
                || !index.AsSpan().SequenceEqual(checkIndex) || !footer.AsSpan().SequenceEqual(checkFooter))
                throw new InvalidDataException("HDD container changed during preparation.");
            return new(admission, prepared.MoveToImmutable(), requests.Select(request => request.Drive).ToImmutableArray(), full);
        }
        catch { admission.Dispose(); throw; }
    }
    private static void AdmitPath(string full)
    {
        for (FileSystemInfo? item = new FileInfo(full); item is not null;
             item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("HDD cold admission requires an ordinary local file path, not a symlink/reparse path.");
        if ((File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new InvalidDataException("HDD cold admission requires an ordinary file.");
    }
    public void Dispose() { disposed = true; Admission.Dispose(); }
}
