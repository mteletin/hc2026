using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Hc2000.Machine.Tape;

/// <summary>Raw TAP importer: length framing is strict, XOR is diagnostic, never repaired.
/// Legacy Storage.Tap/LoadTap remain strict. TAP recognition/export belongs to recorder slice.</summary>
public sealed partial class TapCodec : ITapeCodec
{
    public TapeFormat Format => TapeFormat.Tap;
    public TapeCodecCapabilities Capabilities => new(true, true, null);
    public TapeResult<TapeReadResult> Read(ReadOnlySpan<byte> bytes, TapeImportOptions options)
    {
        try
        {
            TapePolicy.Validate(options.Limits);
            if (options.Format != Format || !Enum.IsDefined(options.Capacity) || !Enum.IsDefined(options.Side))
                return Fail(TapeError.InvalidFormat, "Invalid TAP options.");
            if (bytes.Length > options.Limits.InputBytes) return Fail(TapeError.BoundsExceeded, "TAP input limit.");
            var chunks = new List<TapeSignalChunk>(); var annotations = new List<TapeAnnotation>();
            var warnings = ImmutableArray.CreateBuilder<TapeWarning>();
            var route = new List<TapeRouteVisit>(); int offset = 0, block = 0; var duration = default(TapeTime);
            long transitions = 0, storage = bytes.Length;
            while (offset < bytes.Length)
            {
                if (block >= options.Limits.SourceBlocks || block >= options.Limits.RouteSteps) return Fail(TapeError.BoundsExceeded, "TAP block/route limit.");
                if (bytes.Length - offset < 2) return Fail(TapeError.InvalidFormat, "Truncated TAP length.");
                int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]); offset += 2;
                if (length < 1 || length > bytes.Length - offset) return Fail(TapeError.InvalidFormat, "Truncated/empty TAP frame.");
                var frame = bytes.Slice(offset, length); offset += length;
                byte xor = 0; foreach (var b in frame) xor ^= b;
                if (length == 1) warnings.Add(new("missing-xor", $"Raw TAP block {block} has no checksum; bytes retained."));
                else if (xor != 0) warnings.Add(new("bad-xor", $"Raw TAP block {block} has a bad XOR; bytes retained."));
                int pilot = frame[0] < 0x80 ? 8063 : 3223;
                transitions += pilot + 2 + length * 16L + 2;
                storage += 64L * (length * 16L + 8);
                if (transitions > options.Limits.VirtualTransitions || storage > options.Limits.DecodedBytes)
                    return Fail(TapeError.BoundsExceeded, "TAP signal limit.");
                chunks.Add(new((ulong)(pilot / 2), [Run(false, 2168), Run(true, 2168)]));
                var data = new List<TapeRun> { Run(false, 2168), Run(true, 667), Run(false, 735) };
                long ticks = pilot * 2168L + 667 + 735 + 3_500_000;
                foreach (byte b in frame)
                    for (int bit = 7; bit >= 0; bit--)
                    {
                        int width = (b & (1 << bit)) == 0 ? 855 : 1710;
                        data.Add(Run(true, width)); data.Add(Run(false, width)); ticks += 2 * width;
                    }
                data.Add(Run(true, 3500)); data.Add(Run(false, 3_496_500)); chunks.Add(new(1, data));
                annotations.Add(new(duration + new TapeTime(ticks - 3_500_000, TapeTransport.ReferenceHz), (uint)block,
                    TapeAnnotationKind.Metadata, "Synthesized TAP gap: 1 ms terminal hold + 999 ms low", "tape-source-0", (uint)block));
                duration += new TapeTime(ticks, TapeTransport.ReferenceHz);
                route.Add(new("tape-source-0", (uint)block++, 0));
            }
            var capacity = new TapeTime(options.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1);
            if (duration > capacity || duration > options.Limits.MaxDuration) return Fail(TapeError.CapacityExceeded, "TAP exceeds selected cassette capacity.");
            if (duration < capacity) chunks.Add(new(1, [new(false, capacity - duration)]));
            annotations.Add(new(duration, (uint)block, TapeAnnotationKind.SourceEof, "TAP source EOF", "tape-source-0", null));
            var image = new TapeSideImage(capacity, duration, chunks, [], annotations, route,
                new(Format, null, options.Target48K, [], null));
            var validated = new TapeSignalIndexFactory().Build(image, options.Limits);
            if (!validated.Success) return Fail(validated.Failure!.Code, validated.Failure.Detail);
            return TapeResult<TapeReadResult>.Ok(new(image, [new("tape-source-0", Format, bytes)], warnings.ToImmutable()));
        }
        catch (OverflowException) { return Fail(TapeError.BoundsExceeded, "TAP rational range."); }
        catch (ArgumentException e) { return Fail(TapeError.InvalidFormat, e.Message); }
    }
    private static TapeRun Run(bool level, int ticks) => new(level, new(ticks, TapeTransport.ReferenceHz));
    private static TapeResult<TapeReadResult> Fail(TapeError error, string detail) => TapeResult<TapeReadResult>.Fail(error, detail);
}

/// <summary>Explicit immutable codec set. Missing consumers remain unavailable, never dummy success.</summary>
public sealed class TapeCodecRegistry : ITapeCodecRegistry
{
    private readonly ImmutableDictionary<TapeFormat, ITapeCodec> codecs;
    public TapeCodecRegistry(IEnumerable<ITapeCodec>? codecs = null) =>
        this.codecs = (codecs ?? [new TapCodec()]).ToImmutableDictionary(c => c.Format);
    /// <summary>Every codec this build ships, sharing one run encoding: what the app and the hc tool use.</summary>
    public static TapeCodecRegistry Installed()
    {
        var encoding = new Encoding.TapeRunEncoding();
        return new([new TapCodec(), new Codecs.TzxTapeCodec(encoding), new Codecs.PzxTapeCodec(),
            new Codecs.CswTapeCodec(encoding), new Codecs.WavTapeCodec()]);
    }
    public ImmutableArray<TapeFormat> Formats => codecs.Keys.OrderBy(f => f).ToImmutableArray();
    public TapeResult<ITapeCodec> Find(TapeFormat format) => codecs.TryGetValue(format, out var codec)
        ? TapeResult<ITapeCodec>.Ok(codec) : TapeResult<ITapeCodec>.Fail(TapeError.RuntimeUnavailable, $"{format} codec is not installed.");
}
