using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Hc2000.Machine.Tape.Codecs;

/// <summary>Native detached PCM WAV conversion over the shared rational tape signal.
/// Comparator v1 is an uncalibrated schematic-informed approximation, not physical fidelity.</summary>
public sealed class WavTapeCodec : ITapeCodec
{
    public const string ComparatorModel = "hc2000-ac-q32.32-backward-euler-611_2500000-v1";
    public TapeFormat Format => TapeFormat.Wav;
    public TapeCodecCapabilities Capabilities => new(true, true, null);

    public TapeResult<TapeReadResult> Read(ReadOnlySpan<byte> bytes, TapeImportOptions options)
    {
        try { return ReadPcm(bytes, options); }
        catch (OverflowException) { return TapeResult<TapeReadResult>.Fail(TapeError.BoundsExceeded, "WAV arithmetic/size exceeds supported bounds."); }
    }

    private static TapeResult<TapeReadResult> ReadPcm(ReadOnlySpan<byte> bytes, TapeImportOptions options)
    {
        TapeResult<TapeReadResult> Fail(TapeError code, string detail) => TapeResult<TapeReadResult>.Fail(code, detail);
        var limits = options.Limits;
        var header = new WavPcmPreflight(bytes.Length, options);
        while (header.ReadLength != 0)
            header.Feed(bytes.Slice((int)header.ReadOffset, header.ReadLength));
        if (header.Failure is { } failure) return Fail(failure.Code, failure.Detail);
        var dataOffset = header.DataOffset; var dataBlock = header.DataBlock;
        var frames = header.Frames; var rate = header.Rate; var bits = header.Bits;
        var channels = header.Channels; var align = header.Align;
        var sourceDuration = header.SourceDuration;
        var capacity = new TapeTime(options.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1);

        var runs = new List<TapeRun>();
        long xPrev = 0, yPrev = 0;
        var alphaNum = checked((Int128)611 * rate);
        var alphaDen = checked(alphaNum + 2_500_000);
        var level = false; var segmentStart = 0; var transitions = 0;
        var channelOffset = options.Channel == TapeChannel.Right ? bits / 8 : 0;
        // Budget retained bytes plus conservative per-run/chunk/provenance storage before allocation.
        var sourceByteCount = bytes.Length;
        bool HasSpace(int count) => (long)sourceByteCount + 512L + count * 64L <= limits.DecodedBytes;
        if (!HasSpace(1)) return Fail(TapeError.BoundsExceeded, "WAV decoded/provenance storage budget exceeded.");
        for (var n = 0; n < frames; n++)
        {
            var offset = dataOffset + n * align + channelOffset;
            var x = bits == 8 ? ((long)bytes[offset] - 128) << 25 : (long)BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]) << 17;
            // Q32.32 backward Euler; C# signed integer division truncates toward zero.
            var y = checked((long)(checked(alphaNum * checked((Int128)yPrev + x - xPrev)) / alphaDen));
            var next = y == 0 ? level : y < 0;
            if (next != level)
            {
                if (++transitions > limits.VirtualTransitions) return Fail(TapeError.BoundsExceeded, "WAV transition budget exceeded.");
                if (n > segmentStart)
                {
                    if (!HasSpace(runs.Count + 2)) return Fail(TapeError.BoundsExceeded, "WAV run storage budget exceeded.");
                    runs.Add(new(level, new(n - segmentStart, rate)));
                }
                segmentStart = n; level = next;
            }
            xPrev = x; yPrev = y;
        }
        if (frames > segmentStart) runs.Add(new(level, new(frames - segmentStart, rate)));
        if (sourceDuration < capacity)
        {
            if (frames > 0 && level && ++transitions > limits.VirtualTransitions) return Fail(TapeError.BoundsExceeded, "WAV terminal transition budget exceeded.");
            var blank = capacity - sourceDuration;
            if (runs.Count > 0 && !runs[^1].Level) runs[^1] = new(false, runs[^1].Duration + blank);
            else runs.Add(new(false, blank));
        }
        if (!HasSpace(runs.Count)) return Fail(TapeError.BoundsExceeded, "WAV run storage budget exceeded.");
        // The HCS source roster uses canonical local IDs; asset SHA binds the original bytes.
        var sourceId = "tape-source-0";
        var annotations = new TapeAnnotation[]
        {
            new(default, 0, TapeAnnotationKind.Metadata, $"PCM{bits}, {channels} channel(s), {rate} Hz; selected {options.Channel}; {ComparatorModel}; amplitude discarded", sourceId, null),
            new(sourceDuration, 1, TapeAnnotationKind.SourceEof, "WAV source EOF; remaining side is low blank material", sourceId, null)
        };
        var image = new TapeSideImage(capacity, sourceDuration, [new TapeSignalChunk(1, runs)], [], annotations,
            [new(sourceId, (uint)dataBlock, 0)], new(TapeFormat.Wav, options.Channel, options.Target48K, [], ComparatorModel));
        return TapeResult<TapeReadResult>.Ok(new(image, [new(sourceId, TapeFormat.Wav, bytes)],
            [new("ComparatorApproximation", "AC-coupled Q32.32 comparator v1 is uncalibrated; source amplitude retained only in original bytes.")]));
    }

    public TapeResult<TapeWriteResult> Write(TapeSideImage image, TapeExportOptions options)
    {
        try { return WritePcm(image, options); }
        catch (OverflowException) { return TapeResult<TapeWriteResult>.Fail(TapeError.BoundsExceeded, "WAV arithmetic/header/size overflow."); }
    }

    private static TapeResult<TapeWriteResult> WritePcm(TapeSideImage image, TapeExportOptions options)
    {
        TapeResult<TapeWriteResult> Fail(TapeError code, string detail) => TapeResult<TapeWriteResult>.Fail(code, detail);
        var limits = options.Limits; var rate = options.Rate ?? 44100; var bits = options.WavBitsPerSample;
        if (!ValidLimits(limits)) return Fail(TapeError.BoundsExceeded, "Invalid WAV resource limits.");
        if (options.Target != TapeFormat.Wav) return Fail(TapeError.UnsupportedFormat, "WAV codec requires Wav target.");
        if (!Enum.IsDefined(options.Side) || rate == 0) return Fail(TapeError.InvalidFormat, "Invalid WAV side/rate.");
        if (bits is not (8 or 16)) return Fail(TapeError.UnsupportedFormat, "WAV output supports PCM8/16 only.");
        if (options.Channel is not null || options.UseZrle) return Fail(TapeError.UnsupportedFormat, "WAV output is mono PCM, not channel selection/Z-RLE.");
        if (image.Duration > limits.MaxDuration) return Fail(TapeError.BoundsExceeded, "WAV duration budget exceeded.");
        if (image.SourceDuration > image.Duration) return Fail(TapeError.InvalidFormat, "Source duration exceeds side duration.");
        if (image.Markers.Length + (long)image.Annotations.Length > limits.SourceBlocks || image.SourceRoute.Length > limits.RouteSteps)
            return Fail(TapeError.BoundsExceeded, "WAV annotation/route budget exceeded.");
        if (image.Markers.Any(m => m.Position > image.Duration) || image.Annotations.Any(a => a.Position > image.Duration))
            return Fail(TapeError.InvalidFormat, "Out-of-side WAV annotation/marker.");
        // Validate exact coverage before allocating or returning output. No independent index/clock.
        var total = default(TapeTime); ulong runCount = 0;
        foreach (var chunk in image.Chunks)
        {
            var count = checked(chunk.Repeat * (ulong)chunk.Runs.Length);
            runCount = checked(runCount + count);
            if (runCount > (ulong)limits.VirtualTransitions + 1) return Fail(TapeError.BoundsExceeded, "WAV expanded run budget exceeded.");
            var chunkDuration = default(TapeTime);
            foreach (var run in chunk.Runs) chunkDuration += run.Duration;
            total += chunkDuration * chunk.Repeat;
        }
        if (total != image.Duration) return Fail(TapeError.InvalidFormat, "WAV signal does not cover declared duration.");
        var samples = (image.Duration * rate).Ceiling();
        var dataSize = checked(samples * (bits / 8));
        var byteRate = checked((ulong)rate * (bits / 8));
        var outputSize = checked(44 + dataSize + (dataSize & 1));
        if (byteRate > uint.MaxValue || outputSize - 8 > uint.MaxValue || outputSize > int.MaxValue || outputSize > limits.DecodedBytes)
            return Fail(TapeError.BoundsExceeded, "WAV output exceeds byteRate/RIFF/storage bounds.");
        var bytes = new byte[(int)outputSize];
        "RIFF"u8.CopyTo(bytes); Put32(bytes, 4, (uint)(outputSize - 8)); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        Put32(bytes, 16, 16); Put16(bytes, 20, 1); Put16(bytes, 22, 1); Put32(bytes, 24, rate);
        Put32(bytes, 28, (uint)byteRate); Put16(bytes, 32, (ushort)(bits / 8)); Put16(bytes, 34, (ushort)bits);
        "data"u8.CopyTo(bytes.AsSpan(36)); Put32(bytes, 40, (uint)dataSize);
        var position = default(TapeTime); long samplePosition = 0; var maxError = default(TapeTime);
        ulong inputTransitions = 0, outputTransitions = 0; bool? previousLevel = null, emittedLevel = null;
        foreach (var chunk in image.Chunks)
        for (ulong repetition = 0; repetition < chunk.Repeat; repetition++)
        foreach (var run in chunk.Runs)
        {
            if (previousLevel.HasValue && previousLevel != run.Level)
            {
                inputTransitions++;
                var edgeError = new TapeTime(samplePosition, rate) - position;
                if (edgeError > maxError) maxError = edgeError;
            }
            previousLevel = run.Level;
            position += run.Duration;
            var endSample = (position * rate).Ceiling();
            // Cumulative endpoints, never separately rounded durations; zero-length projected runs emit nothing.
            if (endSample > samplePosition)
            {
                if (emittedLevel.HasValue && emittedLevel != run.Level) outputTransitions++;
                emittedLevel = run.Level;
            }
            if (bits == 8) bytes.AsSpan(checked(44 + (int)samplePosition), checked((int)(endSample - samplePosition))).Fill(run.Level ? (byte)64 : (byte)192);
            else for (var sample = samplePosition; sample < endSample; sample++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(checked(44 + (int)sample * 2)), run.Level ? (short)-16384 : (short)16384);
            samplePosition = endSample;
        }
        var endError = new TapeTime(samplePosition, rate) - position;
        if (endError > maxError) maxError = endError;
        // WAV has no stop/annotation/control fields. Explicitly report all discarded route annotations.
        var changed = image.Annotations.ToBuilder();
        foreach (var marker in image.Markers)
            changed.Add(new(marker.Position, marker.Ordinal, TapeAnnotationKind.UnsupportedAction, "WAV omits stop: " + marker.Label, marker.SourceId, marker.SourceBlock));
        if (!image.SourceRoute.IsEmpty)
            changed.Add(new(default, 0, TapeAnnotationKind.Metadata, "WAV omits source route/control provenance", null, null));
        var report = new TapeWriteReport(TapeFormat.Wav, maxError.Numerator == 0 ? TapeSignalFidelity.ExactSignal : TapeSignalFidelity.Quantized,
            TapeByteFidelity.Transformed, TapeAmplitudeFidelity.Synthesized, rate, maxError, inputTransitions - outputTransitions, changed.ToImmutable());
        return TapeResult<TapeWriteResult>.Ok(new(bytes, report));
    }

    private static bool ValidLimits(TapeLimits limits) => limits.InputBytes > 0 && limits.DecodedBytes > 0 && limits.SourceBlocks > 0
        && limits.RouteSteps > 0 && limits.VirtualTransitions > 0 && limits.MaxDuration.Numerator > 0;
    private static void Put16(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], value);
    private static void Put32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
}
