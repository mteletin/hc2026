using System.Text;
using System.Text.Json;

namespace Hc2000.Machine.Tape.Snapshots;

internal static class TapeComponentCodec
{
    internal static TapeComponent2Dto Read(JsonElement state)
    {
        var c = TapeNativeCodec.Parse<TapeComponent2Dto>(System.Text.Encoding.UTF8.GetBytes(state.GetRawText()));
        TapeNativeCodec.Rational(c.Position); TapeNativeCodec.Rational(c.AnchorPosition);
        if (c.NextPosition is not null) TapeNativeCodec.Rational(c.NextPosition);
        if (c.IdealDue is not null) TapeNativeCodec.Rational(c.IdealDue);
        if (c.RecordStart is not null) TapeNativeCodec.Rational(c.RecordStart);
        if (c.RecordHead is not null) TapeNativeCodec.Rational(c.RecordHead);
        TapeNativeCodec.Unsigned(c.MediaIdentity); TapeNativeCodec.Unsigned(c.AnchorTick); TapeNativeCodec.Unsigned(c.PendingToken);
        if (c.CounterZeroTurns.Length != 2 || c.Tabs.Length != 2 || c.Dirty.Length != 2 || c.EditRevision.Length != 2 || c.SavedRevision.Length != 2)
            throw new ArgumentException("Tape per-side roster.");
        foreach (var n in c.CounterZeroTurns.Concat(c.EditRevision).Concat(c.SavedRevision)) TapeNativeCodec.Unsigned(n);
        if (c.Mode is not ("stopped" or "playing" or "recording" or "rewinding" or "fast-forwarding")
            || c.HoldReason is not ("none" or "format-stop" or "physical-end" or "fault") || c.ActiveSide > 1 || c.PendingKind is not (null or 100 or 101 or 102))
            throw new ArgumentException("Tape field domain.");
        return c;
    }
}
