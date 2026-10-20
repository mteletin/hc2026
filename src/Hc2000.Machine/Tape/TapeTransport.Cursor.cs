using Hc2000.Machine.Storage;
using Hc2000.Machine.Timing;

namespace Hc2000.Machine.Tape;

public sealed partial class TapeTransport
{
    private IReadOnlyList<byte[]>? compatibilityFrames;
    private static IReadOnlyList<byte[]>? LegacyFrames(TapeDeckCore candidate)
    {
        var cassette = candidate.Cassette;
        if (cassette is null || !cassette.A.Annotations.Any(a => a.Kind == TapeAnnotationKind.Metadata && a.Label == "Migrated tape1 EOF hold")) return null;
        var source = cassette.SourceAssets.FirstOrDefault(a => a.Format == TapeFormat.Tap);
        if (source is null) return null;
        try { return Tap.Load(source.Bytes.AsSpan()).CopyRawBlocks(); }
        catch (InvalidDataException) { return null; }
    }
    private TapeCursor DeckCursor()
    {
        var view = deck!.Observe();
        var s = !view.Present ? TapeState.Unloaded : view.Mode == TapeDeckMode.Playing && !view.Paused && view.HoldReason == TapeHoldReason.None ? TapeState.Playing
            : view.Position == view.Duration ? TapeState.AtEnd : TapeState.Stopped;
        if (compatibilityFrames is not { } frames || view.ActiveSide != TapeSide.A || view.A.Dirty)
            return new(s, 0, TapeStage.AtEnd, 0, 0, 0, view.EarLevel, 0, 0, checked((long)view.MediaIdentity), scheduler.Generation(deviceId));
        var local = view.Position; int bi = 0;
        while (bi < frames.Count)
        {
            var frame = frames[bi]; var length = new TapeTime(BlockTicks(frame), ReferenceHz);
            if (local < length) return Phase(frame, bi, local, view, s);
            local -= length; bi++;
        }
        return new(TapeState.AtEnd, bi, TapeStage.AtEnd, frames.Count == 0 ? 0 : frames[^1][0] < 0x80 ? 8063 : 3223,
            frames.Count == 0 ? 0 : frames[^1].Length * 8, 0, false, 0, 0, checked((long)view.MediaIdentity), scheduler.Generation(deviceId));
    }
    private TapeCursor Phase(byte[] frame, int bi, TapeTime p, TapeDeckView view, TapeState s)
    {
        int pilots = frame[0] < 0x80 ? 8063 : 3223, consumed = 0, bit = 0, h = 0, width;
        TapeStage st; bool waveform;
        var pilotDuration = new TapeTime(pilots * 2168L, ReferenceHz);
        if (p < pilotDuration)
        {
            consumed = checked((int)p.Quotient(new(2168, ReferenceHz)));
            p -= new TapeTime(consumed * 2168L, ReferenceHz); st = TapeStage.Pilot; width = 2168; waveform = (consumed & 1) != 0;
        }
        else
        {
            consumed = pilots; p -= pilotDuration;
            if (p < new TapeTime(667, ReferenceHz)) { st = TapeStage.Sync1; width = 667; waveform = true; }
            else
            {
                p -= new TapeTime(667, ReferenceHz);
                if (p < new TapeTime(735, ReferenceHz)) { st = TapeStage.Sync2; width = 735; waveform = false; }
                else
                {
                    p -= new TapeTime(735, ReferenceHz);
                    for (; bit < frame.Length * 8; bit++)
                    {
                        int w = BitTicks(frame, bit); var pair = new TapeTime(2 * w, ReferenceHz);
                        if (p < pair) break; p -= pair;
                    }
                    if (bit < frame.Length * 8)
                    {
                        width = BitTicks(frame, bit); var halfDuration = new TapeTime(width, ReferenceHz);
                        h = p >= halfDuration ? 1 : 0; if (h != 0) p -= halfDuration;
                        st = TapeStage.Data; waveform = h == 0;
                    }
                    else if (p < new TapeTime(3500, ReferenceHz)) { st = TapeStage.TerminalHold; width = 3500; waveform = true; }
                    else { p -= new TapeTime(3500, ReferenceHz); st = TapeStage.Pause; width = 3_496_500; waveform = false; }
                }
            }
        }
        var residual = (new TapeTime(width, ReferenceHz) - p) * (ulong)FrameTiming.MasterHz * (ulong)ReferenceHz;
        if (residual.Denominator != 1) throw new InvalidOperationException("Edited media is not a legacy TAP cursor.");
        var rem = residual.Numerator;
        var ideal = s == TapeState.Playing ? checked((Int128)scheduler.Now * ReferenceHz + rem) : target;
        return new(s, bi, st, consumed, bit, h, waveform, ideal, rem, checked((long)view.MediaIdentity), scheduler.Generation(deviceId));
    }
    private static int BitTicks(byte[] frame, int bit) => (frame[bit / 8] & (0x80 >> (bit % 8))) == 0 ? 855 : 1710;
    private static long BlockTicks(byte[] frame)
    { long ticks = (frame[0] < 0x80 ? 8063L : 3223L) * 2168 + 1402 + 3_500_000; for (int bit = 0; bit < frame.Length * 8; bit++) ticks += 2 * BitTicks(frame, bit); return ticks; }
}
