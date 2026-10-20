using Hc2000.Machine.Tape;
using Hc2000.Machine.Tape.Codecs;

namespace Hc2000.App.Media;

/// <summary>Detached parsing and explicit host persistence; never invoked by Machine.</summary>
public static class TapeFiles
{
    public static TapeFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tap" => TapeFormat.Tap, ".tzx" => TapeFormat.Tzx, ".pzx" => TapeFormat.Pzx,
        ".csw" => TapeFormat.Csw, ".wav" => TapeFormat.Wav,
        _ => throw new InvalidDataException("Select TAP, TZX, PZX, CSW or WAV.")
    };

    public static async Task<TapeCassetteImage> ReadAsync(string path, ITapeCodecRegistry registry,
        TapeImportOptions options, long transientBytes = 4L * 1024 * 1024 * 1024)
    {
        var codec = Require(registry.Find(FormatOf(path)));
        if (!codec.Capabilities.CanRead)
            throw new InvalidOperationException(codec.Capabilities.UnavailableReason ?? "Reader unavailable.");
        options = TapeInputPolicy.Select(options with { Format = codec.Format });
        var bytes = await ReadInputAsync(path, options, transientBytes);
        var decoded = await Task.Run(() => Require(codec.Read(bytes.AsSpan(), options)));
        var duration = new TapeTime(options.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1);
        // Detached empty other side, no second playback/index implementation.
        var blank = new TapeSideImage(duration, default, [new(1, [new(false, duration)])], [], [], [],
            new(TapeFormat.Blank, null, true, [], null));
        return new(options.Capacity, options.Side == TapeSide.A ? decoded.Image : blank,
            options.Side == TapeSide.B ? decoded.Image : blank, decoded.SourceAssets);
    }

    private static async Task<byte[]> ReadInputAsync(string path, TapeImportOptions options, long transientBytes)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var length = input.Length;
        if (length > options.Limits.InputBytes || length > int.MaxValue || transientBytes <= 0
            || 4L * length + 3L * options.Limits.DecodedBytes > transientBytes)
            throw new InvalidDataException("BoundsExceeded: input/transient-copy budget.");
        if (options.Format == TapeFormat.Wav)
        {
            var probe = new WavPcmPreflight(length, options);
            var header = new byte[18];
            while (probe.ReadLength != 0)
            {
                input.Position = probe.ReadOffset;
                var size = probe.ReadLength;
                await input.ReadExactlyAsync(header.AsMemory(0, size));
                probe.Feed(header.AsSpan(0, size));
            }
            if (probe.Failure is { } failure) throw new InvalidDataException($"{failure.Code}: {failure.Detail}");
            if (input.Length != length) throw new InvalidDataException("BoundsExceeded: input changed during preflight.");
            input.Position = 0;
        }
        var bytes = new byte[(int)length];
        await input.ReadExactlyAsync(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("BoundsExceeded: input grew while reading.");
        return bytes;
    }

    public static async Task<TapeWriteReport> SaveAsync(string path, TapeExportSnapshot snapshot,
        ITapeCodecRegistry registry, TapeExportOptions options, Func<MediaCommand, Task<MediaReply>> request, bool exactOnly = false)
    {
        if (FormatOf(path) != options.Target) throw new InvalidDataException("Extension must match the selected output format.");
        var codec = Require(registry.Find(options.Target));
        if (!codec.Capabilities.CanWrite)
            throw new InvalidOperationException(codec.Capabilities.UnavailableReason ?? "Writer unavailable.");
        var written = await Task.Run(() => Require(codec.Write(snapshot.Image, options with { Side = snapshot.Side })));
        if (written.Report.Target != options.Target)
            throw new InvalidDataException("Writer returned the wrong output format.");
        if (options.Target == TapeFormat.Tzx && written.Report.Signal == TapeSignalFidelity.Quantized && !options.AllowQuantize
            || exactOnly && written.Report.Signal != TapeSignalFidelity.ExactSignal)
            throw new InvalidDataException("ExactEncodingUnavailable: explicitly select a quantized copy; cassette save requires exact TZX.");
        await MediaFiles.SaveAsync(path, written.Bytes);
        // A lossy/data-only copy is useful but is NOT an exact working-signal save.
        if ((written.Report.Target == TapeFormat.Tzx && written.Report.Signal == TapeSignalFidelity.ExactSignal)
            || written.Report.Bytes == TapeByteFidelity.OriginalBytes)
        {
            var ack = new TapeSaveAcknowledgement(snapshot.MediaIdentity, snapshot.Side, snapshot.EditRevision,
                written.Report.Signal, written.Report.Bytes, written.Report.Target);
            var reply = await request(new(MediaAction.AcknowledgeTape, Acknowledgement: ack));
            if (reply.Error is not null) throw new InvalidOperationException("Copy written; save acknowledgement refused: " + reply.Error);
        }
        return written.Report;
    }

    private static T Require<T>(TapeResult<T> result) where T : notnull => result.Success ? result.Value
        : throw new InvalidOperationException($"{result.Failure!.Code}: {result.Failure.Detail}");
}
