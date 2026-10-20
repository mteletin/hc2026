using System.Buffers.Binary;
using System.Collections.Immutable;
using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Tape.Snapshots;

/// <summary>Explicit tape1 cursor to continuous cassette conversion. It neither runs a CPU nor
/// mutates a scheduler. Old rounded deadlines are checked, never used as media positions.</summary>
internal static class TapeLegacyMigration
{
    internal static byte[] RawTap(IReadOnlyList<byte[]> frames)
    {
        using var output = new MemoryStream();
        Span<byte> length = stackalloc byte[2];
        foreach (var frame in frames)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)frame.Length)); output.Write(length); output.Write(frame);
        }
        return output.ToArray();
    }
    internal static TapeResult<TapeDeckState> Migrate(Tape1MigrationInput input, TapeLimits limits)
    {
        try
        {
            TapePolicy.Validate(limits); var c = input.Cursor;
            if (input.Now < 0 || input.Generation < 0 || c.Generation != input.Generation || c.MediaIdentity < 0 || !Enum.IsDefined(c.State) || !Enum.IsDefined(c.Stage))
                throw new ArgumentException("Legacy identity/state/generation.");
            if (c.State == TapeState.Unloaded)
            {
                if (!input.RawTapAssets.IsEmpty || input.PendingEvent is not null || c.Stage != TapeStage.AtEnd || c.BlockIndex != 0 || c.PilotConsumed != 0 || c.NextBit != 0
                    || c.Half != 0 || c.WaveformLevel || c.RemainingNumerator != 0 || c.IdealTargetNumerator != 0) throw new ArgumentException("Unloaded legacy cursor.");
                return TapeResult<TapeDeckState>.Ok(new(Component(input, default, null, null, null, null), null));
            }
            if (c.MediaIdentity == 0) throw new ArgumentException("Loaded legacy media needs a nonzero identity.");
            if (input.RawTapAssets.Length != 1 || input.RawTapAssets[0].Format != TapeFormat.Tap) throw new ArgumentException("Legacy TAP asset roster.");
            var bytes = input.RawTapAssets[0].Bytes.AsSpan();
            var frames = Tap.Load(bytes, Math.Min(limits.InputBytes, 64 * 1024 * 1024), Math.Min(limits.SourceBlocks, 4096)).CopyRawBlocks();
            var read = new TapCodec().Read(bytes, new(TapeFormat.Tap, TapeSide.A, TapeCapacity.C60) { Limits = limits });
            var capacity = TapeCapacity.C60;
            if (!read.Success && read.Failure!.Code == TapeError.CapacityExceeded)
            { capacity = TapeCapacity.C90; read = new TapCodec().Read(bytes, new(TapeFormat.Tap, TapeSide.A, capacity) { Limits = limits }); }
            if (!read.Success) return TapeResult<TapeDeckState>.Fail(read.Failure!.Code, "legacy-capacity/limit: " + read.Failure.Detail);
            var image = read.Value.Image; var pos = default(TapeTime);
            if (c.State == TapeState.AtEnd)
            {
                if (c.Stage != TapeStage.AtEnd || c.BlockIndex != frames.Count || c.RemainingNumerator != 0 || c.WaveformLevel || input.PendingEvent is not null)
                    throw new ArgumentException("Legacy end cursor.");
                pos = image.SourceDuration;
            }
            else
            {
                if (c.State is not (TapeState.Stopped or TapeState.Playing) || c.BlockIndex < 0 || c.BlockIndex >= frames.Count || c.Stage == TapeStage.AtEnd)
                    throw new ArgumentException("Legacy block/stage.");
                for (int i = 0; i < c.BlockIndex; i++) pos += BlockDuration(frames[i]);
                var frame = frames[c.BlockIndex]; int pilots = frame[0] < 0x80 ? 8063 : 3223;
                if (c.Half is < 0 or > 1 || c.NextBit < 0 || c.NextBit > frame.Length * 8 || c.PilotConsumed < 0 || c.PilotConsumed > pilots)
                    throw new ArgumentException("Legacy cursor bounds.");
                long startTicks = 0; int duration; bool level;
                switch (c.Stage)
                {
                    case TapeStage.Pilot:
                        if (c.PilotConsumed >= pilots || c.NextBit != 0 || c.Half != 0) throw new ArgumentException("Legacy pilot cursor.");
                        startTicks = c.PilotConsumed * 2168L; duration = 2168; level = (c.PilotConsumed & 1) != 0; break;
                    case TapeStage.Sync1:
                        Require(pilots, 0, 0); startTicks = pilots * 2168L; duration = 667; level = true; break;
                    case TapeStage.Sync2:
                        Require(pilots, 0, 0); startTicks = pilots * 2168L + 667; duration = 735; level = false; break;
                    case TapeStage.Data:
                        if (c.PilotConsumed != pilots || c.NextBit >= frame.Length * 8) throw new ArgumentException("Legacy data cursor.");
                        startTicks = pilots * 2168L + 1402 + DataTicks(frame, c.NextBit);
                        duration = BitWidth(frame, c.NextBit); startTicks += c.Half * duration; level = c.Half == 0; break;
                    case TapeStage.TerminalHold:
                        Require(pilots, frame.Length * 8, 0); startTicks = pilots * 2168L + 1402 + DataTicks(frame, frame.Length * 8);
                        duration = 3500; level = true; break;
                    case TapeStage.Pause:
                        Require(pilots, frame.Length * 8, 0); startTicks = pilots * 2168L + 1402 + DataTicks(frame, frame.Length * 8) + 3500;
                        duration = 3_496_500; level = false; break;
                    default: throw new ArgumentException("Legacy stage.");
                }
                void Require(int pc, int bit, int half)
                { if (c.PilotConsumed != pc || c.NextBit != bit || c.Half != half) throw new ArgumentException("Legacy cursor/stage pair."); }
                if (c.WaveformLevel != level) throw new ArgumentException("Legacy waveform phase.");
                var rem = c.State == TapeState.Playing ? checked(c.IdealTargetNumerator - (Int128)input.Now * TapeTransport.ReferenceHz) : c.RemainingNumerator;
                if (rem <= 0 || rem > (Int128)duration * FrameTiming.MasterHz || rem != c.RemainingNumerator) throw new ArgumentException("Legacy exact remainder.");
                if (c.State == TapeState.Playing)
                {
                    var e = input.PendingEvent ?? throw new ArgumentException("Legacy missing event.");
                    var due = checked((long)((c.IdealTargetNumerator + TapeTransport.ReferenceHz - 1) / TapeTransport.ReferenceHz));
                    if (e.DeviceId != input.DeviceId || e.Kind != (int)c.Stage || e.Phase != EventPhase.SignalEdge || e.Generation != input.Generation
                        || e.Payload != 0 || e.InsertionSequence <= 0 || e.DueTick != due || due <= input.Now) throw new ArgumentException("Legacy event pairing.");
                }
                else if (input.PendingEvent is not null) throw new ArgumentException("Stopped legacy owns no event.");
                pos += new TapeTime(startTicks + duration, TapeTransport.ReferenceHz) - new TapeTime(rem, (Int128)TapeTransport.ReferenceHz * FrameTiming.MasterHz);
            }
            image = new(image.Duration, image.SourceDuration, image.Chunks,
                [new(image.SourceDuration, 0, "legacy tape1 EOF", null, null)],
                image.Annotations.Add(new(image.SourceDuration, checked(image.Annotations.Max(a => a.Ordinal) + 1), TapeAnnotationKind.Metadata, "Migrated tape1 EOF hold", null, null)), image.SourceRoute, image.ImportOptions);
            var blank = new TapeSideImage(image.Duration, default, [new(1, [new(false, image.Duration)])], [], [], [], new(TapeFormat.Blank, null, true, [], null));
            var cassette = new TapeCassetteImage(capacity, image, blank, read.Value.SourceAssets);
            TapeTime? next = null, ideal = null; uint? kind = null;
            if (c.State == TapeState.Playing)
            {
                var index = new TapeSignalIndexFactory().Build(image, limits).Value;
                next = index.NextEdge(pos) ?? image.Duration; kind = next == image.Duration ? 102U : 100U;
                if (image.SourceDuration < next) { next = image.SourceDuration; kind = 101; }
                ideal = new TapeTime(input.Now, 1) + (next.Value - pos) * (ulong)FrameTiming.MasterHz;
            }
            return TapeResult<TapeDeckState>.Ok(new(Component(input, pos, next, ideal, kind, c.State == TapeState.AtEnd ? 0U : null), cassette));
        }
        catch (InvalidDataException e) { return TapeResult<TapeDeckState>.Fail(TapeError.InvalidFormat, e.Message); }
        catch (Exception e) when (e is ArgumentException or OverflowException or InvalidOperationException)
        { return TapeResult<TapeDeckState>.Fail(TapeError.SnapshotUnsupported, e.Message); }
    }
    private static TapeComponent2Dto Component(Tape1MigrationInput input, TapeTime pos, TapeTime? next, TapeTime? ideal, uint? kind, uint? consumed) => new(
        input.Cursor.State == TapeState.Unloaded ? null : TapeSnapshotContract.CassetteAssetId, 0, TapeNativeCodec.Rational(pos),
        input.Cursor.State == TapeState.Playing ? "playing" : "stopped", false, "none", consumed,
        ["0", "0"], [true, true], [false, false], ["0", "0"], ["0", "0"], false,
        TapeNativeCodec.Decimal((ulong)input.Cursor.MediaIdentity), TapeNativeCodec.Decimal((ulong)input.Now), TapeNativeCodec.Rational(pos),
        input.Cursor.State == TapeState.Playing && input.Cursor.WaveformLevel, false, null, null, false,
        next is { } n ? TapeNativeCodec.Rational(n) : null, ideal is { } i ? TapeNativeCodec.Rational(i) : null, kind,
        input.Cursor.State == TapeState.Playing ? TapeNativeCodec.Decimal((ulong)input.PendingEvent!.InsertionSequence) : "0");
    private static int BitWidth(byte[] frame, int bit) => (frame[bit / 8] & (0x80 >> (bit % 8))) == 0 ? 855 : 1710;
    private static long DataTicks(byte[] frame, int bits)
    { long sum = 0; for (int bit = 0; bit < bits; bit++) sum += 2 * BitWidth(frame, bit); return sum; }
    private static TapeTime BlockDuration(byte[] frame) => new((frame[0] < 0x80 ? 8063L : 3223L) * 2168 + 1402 + DataTicks(frame, frame.Length * 8) + 3_500_000, TapeTransport.ReferenceHz);
}
