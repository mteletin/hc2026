using System.Collections.Immutable;

namespace Hc2000.Machine.Tape;

/// <summary>A recognized standard block: pilot start, the point its gap begins, and its flag byte.</summary>
public readonly record struct TapeBlockSpan(TapeTime Start, TapeTime End, byte Flag);

public sealed partial class TapCodec
{
    /// <summary>The blocks the TAP export recognizer would decode, found tolerantly: anything else is skipped, never
    /// refused. Spec 14 skip stops at a block's End (where its gap begins); its data-block index counts flag FF blocks.</summary>
    public static ImmutableArray<TapeBlockSpan> Blocks(ITapeSignalIndex signal)
    {
        var cursor = new PulseCursor(signal);
        var blocks = ImmutableArray.CreateBuilder<TapeBlockSpan>();
        while (!cursor.End)
        {
            if (!Within(cursor.Width, 2168, 10)) { cursor.Move(); continue; }
            var decoded = DecodeBlock(cursor);
            if (decoded.Success && AdvanceGap(cursor) is null)
            {
                var block = decoded.Value;
                blocks.Add(new(block.Start, block.End, block.Frame[0]));
            }
        }
        return blocks.ToImmutable();
    }
}
