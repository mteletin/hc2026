using System.Security.Cryptography;
using Hc2000.HostFiles;
using Hc2000.Machine.Storage;
using static Hc2000.Tools.StorageCommand;

namespace Hc2000.Tools;

// Caller only: decode/prepare one bounded baseline, then hand publication and its lifetime to the shipped writer.
internal static class HddOfflineReplaceCommand
{
    private const int ImageBytes = 8_388_608;
    private static readonly HddLimits CodecLimits = new(uint.MaxValue, 511, 12_276, 114_640);
    private static readonly HddSelectionLimits Limits = new(uint.MaxValue, 511, 12_276, 114_640,
        ImageBytes, 0, 4UL * ImageBytes + 65536, 3UL * ImageBytes + 65536,
        64UL * 1024 * 1024, uint.MaxValue, uint.MaxValue);
    // Reserve input/baseline/preparation copies and the disposed cold-read buffer conservatively;
    // the writer adds its own fixed 32 KiB buffer and metadata/copy charges. There are no mounted images.
    private static readonly HddSelectionUsage Usage = new(1, 0, 4UL * ImageBytes + 65536, 0);

    internal static int Run(string container, string name, string rawImage, TextWriter output, TextWriter error)
    {
        var canonical = VolumeCatalogMetadata.CanonicalName83(name);
        if (canonical is null || !StringComparer.OrdinalIgnoreCase.Equals(VolumeCatalogMetadata.ParseName83(canonical, 0), name))
            throw new ArgumentException("Volume name must be a canonical catalog 8.3 name.");
        if (container.StartsWith("\\\\", StringComparison.Ordinal) || container.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported remote/shared HDD store.");
        var path = Path.GetFullPath(container);
        var identity = ReadIdentity(path, name);
        var replacement = ReadRawImage(rawImage);
        var request = new OfflineReplaceRequest(path, identity, Guid.NewGuid(), Guid.NewGuid(),
            new(true, true, true, true), Limits, Usage);
        // Exactly one attempt; Dispose deliberately does not release an Unknown publication/quarantine.
        using var attempt = ReplaceOffline(request, replacement);
        var receipt = attempt.Receipt;
        var report = $"hdd-replace-volume outcome={receipt.Outcome} error={receipt.Error} attempt={receipt.AttemptId:D} writer-drained={receipt.WriterDrained}";
        if (receipt.Outcome == OfflineOutcome.KnownNew && receipt.WriterDrained && receipt.Error == OfflineError.None)
        {
            var saved = receipt.IntendedVolume!;
            output.WriteLine(report + $" id={Convert.ToHexStringLower(saved.Store.ContainerId.AsSpan())} revision={saved.Store.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)} name={saved.Name} sha256={Convert.ToHexStringLower(saved.PayloadSha256.AsSpan())} file-sha256={Convert.ToHexStringLower(receipt.IntendedFileSha256.AsSpan())}");
            return 0;
        }
        error.WriteLine("hc hdd: " + report);
        if (receipt.Outcome == OfflineOutcome.Unknown || !receipt.WriterDrained)
            error.WriteLine("Publication is unresolved; retain/quarantine this copy and its attempt ownership; do not retry or abandon its lease.");
        return 1;
    }

    private static byte[] ReadRawImage(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        if (source.Length != ImageBytes)
            throw new InvalidDataException($"Raw HDD image must be exactly {ImageBytes} bytes; found {source.Length}.");
        var bytes = new byte[ImageBytes];
        source.ReadExactly(bytes);
        if (source.Length != ImageBytes || source.ReadByte() != -1)
            throw new InvalidDataException("Raw HDD image length changed during reading.");
        return bytes;
    }

    private static HddVolumeIdentity ReadIdentity(string path, string name)
    {
        NativeImageAdmission? admission = null;
        try
        {
            if (NativeImageAdmission.PlatformMode == ImageAdmissionPlatformMode.GuardedLinux)
            {
                var result = NativeImageAdmission.TryAcquireColdRead(default);
                admission = result.Admission ?? throw new AdmissionException(result.Refusal!.Value);
            }
            using var source = admission?.OpenColdSource(path)
                ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            var facts = admission?.ValidateOpenedSource(source, path);
            var length = source.Length;
            if (length < 76 || (ulong)length > Limits.MaxFileBytes)
                throw new InvalidDataException("HDD container length exceeds offline admission.");
            var footer = new byte[64];
            source.Position = length - footer.Length;
            source.ReadExactly(footer);
            var inspected = HddCatalogCodec.InspectHddFooter(footer, (ulong)length, CodecLimits);
            if (!inspected.Success) throw new InvalidDataException(inspected.Failure!.Detail);
            var view = inspected.Value;
            var index = new byte[checked((int)view.IndexBytes)];
            source.Position = checked((long)view.IndexStart);
            source.ReadExactly(index);
            var decoded = HddCatalogCodec.DecodeHdd(footer, index, (ulong)length, CodecLimits);
            if (!decoded.Success) throw new InvalidDataException(decoded.Failure!.Detail);
            var row = decoded.Value.Entries.SingleOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Name, name))
                ?? throw new InvalidDataException("Named HDD volume not found.");
            if (row.ReadOnly) throw new InvalidDataException("Named HDD volume is protected.");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData(index); digest.AppendData(footer);
            var store = new HddStoreToken([.. view.ContainerId], view.Revision, [.. digest.GetHashAndReset()]);
            var image = new byte[ImageBytes];
            source.Position = checked((long)row.SlotOrdinal.Value * ImageBytes);
            source.ReadExactly(image);
            var intended = new HddVolumeIdentity(store, row.Name, [.. SHA256.HashData(image)]);
            var selected = HddVolumeSelection.Prepare(decoded.Value, store, intended, image, false, Limits, Usage, []);
            if (!selected.Success) throw new InvalidDataException(selected.Detail);
            if (source.Length != length) throw new InvalidDataException("HDD container length changed during preparation.");
            if (facts is not null) admission!.ValidateSourceStillCurrent(source, facts);
            return selected.Selection!.Identity;
        }
        finally
        {
            // End the read holder before the existing writer acquires publication admission and rechecks this identity.
            admission?.Dispose();
        }
    }
}
