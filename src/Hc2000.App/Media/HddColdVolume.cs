using System.Collections.Immutable;
using System.Security.Cryptography;
using Hc2000.Machine.Storage;

namespace Hc2000.App.Media;

/// <summary>Bounded selected-volume copy under the delivered native admission.
/// The existing composition owns that admission through cold installation acknowledgement;
/// disposing a UI/caller reference never releases an enqueued or unknown owner outcome.</summary>
internal sealed class HddColdVolume : IDisposable
{
    internal HddColdAdmission Admission { get; }
    private readonly HddPreparedSelection selection;
    private bool disposed;
    public HddVolumeIdentity Identity => selection.Identity;
    private HddColdVolume(HddColdAdmission admission, HddPreparedSelection selection)
    { Admission = admission; this.selection = selection; }
    public HddInitialMedia Media(byte drive)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (drive is < 2 or > 5) throw new ArgumentOutOfRangeException(nameof(drive));
        return new(drive, selection.Image, selection.Identity.Name, WriteProtected: true);
    }

    // Fixed, deliberately finite cold caller admission: <=511 entries / <4GiB file, only ONE selected image,
    // reserve four resident slots plus 8MiB of other host copies and the selector's 3S copy-work allowance.
    private static readonly HddLimits CodecLimits = new(uint.MaxValue, 511, 12_276, 114_640);
    private static readonly HddSelectionLimits Limits = new(uint.MaxValue, 511, 12_276, 114_640,
        8_388_608, 33_554_432, 8_388_608, 25_231_360, 83_886_080, uint.MaxValue, uint.MaxValue);
    private static readonly HddSelectionUsage Usage = new(1, 33_554_432, 8_388_608, 65536);

    public static HddColdVolume Open(string path, string name, HddVolumeIdentity? expected = null)
    {
        var full = Path.GetFullPath(path);
        AdmitPath(full);
        var stamp = File.GetLastWriteTimeUtc(full);
        var admission = HddColdAdmission.Open(full);
        var source = admission.Source;
        try
        {
            var length = source.Length;
            if (length < 76 || (ulong)length > Limits.MaxFileBytes)
                throw new InvalidDataException("HDD container length exceeds cold admission.");
            var footer = new byte[64]; source.Position = length - 64; source.ReadExactly(footer);
            var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, CodecLimits);
            if (!inspected.Success) throw new InvalidDataException(inspected.Failure!.Detail);
            var view = inspected.Value;
            var index = new byte[checked((int)view.IndexBytes)];
            source.Position = checked((long)view.IndexStart); source.ReadExactly(index);
            var decoded = HddCatalogCodec.DecodeHdd(footer, index, (ulong)length, CodecLimits);
            if (!decoded.Success) throw new InvalidDataException(decoded.Failure!.Detail);
            var descriptor = decoded.Value;
            var row = descriptor.Entries.SingleOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Name, name));
            if (row is null) throw new InvalidDataException("Named HDD volume not found.");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData(index); digest.AppendData(footer);
            var store = new HddStoreToken([.. view.ContainerId], view.Revision, [.. digest.GetHashAndReset()]);
            var image = new byte[HddMediaAdapter.ImageLength];
            var offset = checked((long)row.SlotOrdinal.Value * HddMediaAdapter.ImageLength);
            source.Position = offset; source.ReadExactly(image);
            var intended = expected ?? new HddVolumeIdentity(store, row.Name, [.. SHA256.HashData(image)]);
            if (!StringComparer.OrdinalIgnoreCase.Equals(intended.Name, name))
                throw new InvalidDataException("Selection name changed.");
            var result = HddVolumeSelection.Prepare(descriptor, store, intended, image, true, Limits, Usage, []);
            if (!result.Success) throw new InvalidDataException(result.Detail);
            // Re-read only the selected payload and metadata, not the enclosing arena. One charged 64KiB buffer.
            var buffer = new byte[65536]; source.Position = offset;
            for (int remaining = image.Length; remaining > 0;)
            {
                int count = Math.Min(remaining, buffer.Length); source.ReadExactly(buffer.AsSpan(0, count));
                digest.AppendData(buffer, 0, count); remaining -= count;
            }
            if (!digest.GetHashAndReset().AsSpan().SequenceEqual(intended.PayloadSha256.AsSpan()))
                throw new InvalidDataException("HDD payload changed during preparation.");
            var checkIndex = new byte[index.Length]; source.Position = checked((long)view.IndexStart); source.ReadExactly(checkIndex);
            var checkFooter = new byte[64]; source.ReadExactly(checkFooter);
            AdmitPath(full);
            if (source.Length != length || new FileInfo(full).Length != length || File.GetLastWriteTimeUtc(full) != stamp
                || !index.AsSpan().SequenceEqual(checkIndex) || !footer.AsSpan().SequenceEqual(checkFooter))
                throw new InvalidDataException("HDD container changed during preparation.");
            return new(admission, result.Selection!);
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
