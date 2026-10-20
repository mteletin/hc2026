using System.Buffers.Binary;

namespace Hc2000.Machine.Tape.Codecs;

/// <summary>The native WAV header parser, driven with bounded metadata windows (never PCM payload).
/// Both the detached codec and host pre-read use this same parser. No I/O or allocation proportional to file size.</summary>
public sealed class WavPcmPreflight
{
    private enum Stage { Riff, Chunk, Format, Done }
    private Stage stage;
    private readonly long length;
    private readonly TapeImportOptions options;
    private long nextOffset;
    private uint formatSize;
    private bool hasFormat, hasData;
    private int blocks;
    public long ReadOffset { get; private set; }
    public int ReadLength => Failure is not null || stage == Stage.Done ? 0 : stage switch
        { Stage.Riff => length < 12 ? 4 : 12, Stage.Chunk => 8, _ => formatSize < 18 ? 16 : 18 };
    public bool Complete => Failure is null && stage == Stage.Done;
    public TapeFailure? Failure { get; private set; }
    public int DataOffset { get; private set; }
    public int DataLength { get; private set; }
    public int DataBlock { get; private set; }
    public int Frames { get; private set; }
    public ushort Channels { get; private set; }
    public ushort Bits { get; private set; }
    public ushort Align { get; private set; }
    public uint Rate { get; private set; }
    public TapeTime SourceDuration { get; private set; }

    public WavPcmPreflight(long length, TapeImportOptions options)
    {
        this.length = length; this.options = options;
        var limits = options.Limits;
        if (limits.InputBytes <= 0 || limits.DecodedBytes <= 0 || limits.SourceBlocks <= 0 || limits.RouteSteps <= 0
            || limits.VirtualTransitions <= 0 || limits.MaxDuration.Numerator <= 0)
            Fail(TapeError.BoundsExceeded, "Invalid WAV resource limits.");
        else if (options.Format != TapeFormat.Wav) Fail(TapeError.UnsupportedFormat, "WAV codec requires Wav format.");
        else if (!Enum.IsDefined(options.Side) || !Enum.IsDefined(options.Capacity) || !Enum.IsDefined(options.Channel))
            Fail(TapeError.InvalidFormat, "Invalid WAV side/capacity/channel.");
        else if (!options.Selections.IsDefaultOrEmpty) Fail(TapeError.InvalidRoute, "WAV has no selectable routes.");
        else if (length > limits.InputBytes || length > limits.DecodedBytes || length > int.MaxValue)
            Fail(TapeError.BoundsExceeded, "WAV source byte budget exceeded.");
        else if (length < 4) Fail(TapeError.InvalidFormat, "Missing RIFF/WAVE header.");
    }

    /// <summary>Supply exactly ReadLength bytes at ReadOffset. A failure requests no more input.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (ReadLength == 0 || bytes.Length != ReadLength) throw new ArgumentException("Wrong WAV preflight window.");
        if (stage == Stage.Riff)
        {
            if (bytes[..4].SequenceEqual("RIFX"u8) || bytes[..4].SequenceEqual("RF64"u8))
                Fail(TapeError.UnsupportedFormat, "Only little-endian RIFF PCM WAV is supported, not RIFX/RF64.");
            else if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
                Fail(TapeError.InvalidFormat, "Missing RIFF/WAVE header.");
            else if ((ulong)U32(bytes, 4) + 8 != (ulong)length)
                Fail(TapeError.InvalidFormat, "RIFF size must cover the complete input.");
            else Advance(12);
        }
        else if (stage == Stage.Chunk)
        {
            var size = U32(bytes, 4);
            var body = ReadOffset + 8;
            nextOffset = body + size + (size & 1);
            if (nextOffset > length) { Fail(TapeError.InvalidFormat, "Truncated WAV chunk payload/padding."); return; }
            if (bytes[..4].SequenceEqual("fmt "u8))
            {
                if (hasFormat || size < 16) { Fail(TapeError.InvalidFormat, "Duplicate or short WAV fmt chunk."); return; }
                hasFormat = true; formatSize = size; ReadOffset = body; stage = Stage.Format;
            }
            else
            {
                if (bytes[..4].SequenceEqual("data"u8))
                {
                    if (hasData) { Fail(TapeError.InvalidFormat, "Multiple WAV data chunks are not accepted."); return; }
                    hasData = true; DataOffset = (int)body; DataLength = (int)size; DataBlock = blocks - 1;
                }
                Advance(nextOffset);
            }
        }
        else
        {
            var encoding = U16(bytes, 0);
            Channels = U16(bytes, 2); Rate = U32(bytes, 4); Align = U16(bytes, 12); Bits = U16(bytes, 14);
            if (encoding != 1) Fail(encoding == 0xfffe ? TapeError.UnsupportedFormat : TapeError.UnsupportedCompression,
                "Only PCM format 1 is supported (no float/compression/extensible).");
            else if (Channels is not (1 or 2) || Bits is not (8 or 16))
                Fail(TapeError.UnsupportedFormat, "WAV requires mono/stereo PCM8/16.");
            else if (Rate == 0 || Align != Channels * Bits / 8 || (ulong)U32(bytes, 8) != (ulong)Rate * Align)
                Fail(TapeError.InvalidFormat, "Invalid WAV rate/blockAlign/byteRate.");
            else if (formatSize > 16 && (formatSize < 18 || U16(bytes, 16) != formatSize - 18))
                Fail(TapeError.InvalidFormat, "Invalid PCM fmt extension span.");
            else Advance(nextOffset);
        }
    }

    private void Advance(long offset)
    {
        ReadOffset = offset;
        if (offset != length)
        {
            if (++blocks > options.Limits.SourceBlocks) Fail(TapeError.BoundsExceeded, "WAV chunk budget exceeded.");
            else if (length - offset < 8) Fail(TapeError.InvalidFormat, "Truncated WAV chunk header.");
            else stage = Stage.Chunk;
            return;
        }
        stage = Stage.Done;
        if (!hasFormat || !hasData || DataLength % Align != 0)
        { Fail(TapeError.InvalidFormat, "Missing fmt/data or incomplete PCM frames."); return; }
        if (Channels == 1 && options.Channel == TapeChannel.Right)
        { Fail(TapeError.InvalidFormat, "Mono WAV has no right channel."); return; }
        Frames = DataLength / Align;
        SourceDuration = new(Frames, Rate);
        var capacity = new TapeTime(options.Capacity == TapeCapacity.C60 ? 1800 : 2700, 1);
        if (SourceDuration > capacity) Fail(TapeError.CapacityExceeded, "WAV exceeds selected side capacity; no clipping.");
        else if (capacity > options.Limits.MaxDuration) Fail(TapeError.BoundsExceeded, "WAV padded side exceeds duration budget.");
        else if (length + 576L > options.Limits.DecodedBytes)
            Fail(TapeError.BoundsExceeded, "WAV decoded/provenance storage budget exceeded.");
    }
    private void Fail(TapeError code, string detail) => Failure = new(code, detail);
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
