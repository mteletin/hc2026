using System.Collections.Immutable;
using Hc2000.Machine.Io;

namespace Hc2000.Machine.Tape;

/// <summary>Port object of the spec 14 snapshot: selector, last result, and the generation with the deck state it counts.</summary>
public sealed record TapeControlPortState(byte Selector, byte LastResult, ushort Generation, string DeckKey);

/// <summary>
/// Spec 14 tape control: one polled byte at exact 0091 on the same Machine-owned deck, decoded only on the
/// tape-control profile. Writes 00-08 press the deck's own buttons (07/08 wind to the previous/next gap),
/// 20-2F select a status byte, anything else is unsupported. No IRQ, no DMA, no time of its own.
/// </summary>
public sealed class TapeControlPort : IPortDevice
{
    public const ushort Address = 0x0091;
    public const int StatusBytes = 14;
    private const byte Accepted = 0, Interlock = 1, NoMedia = 2, WriteProtect = 3, Fault = 4, Unsupported = 5;
    private TapeTransport? transport;
    private byte selector, lastResult;
    private ushort generation;
    private string? deckKey;
    private ITapeSignalIndex? scanned;
    private ImmutableArray<TapeBlockSpan> blocks = [];

    public void Attach(TapeTransport tape)
    {
        ArgumentNullException.ThrowIfNull(tape);
        if (transport is not null) transport.DeckStateChanged -= OnDeckStateChanged;
        transport = tape; deckKey = Key(tape.Deck.Observe());
        transport.DeckStateChanged += OnDeckStateChanged;
    }

    public IReadOnlyList<PortMapping> Mappings() => [new(Address, 0xffff, this, PortAccess.ReadWrite)];

    /// <summary>Machine reset clears the selector; the cassette, side, head and counter belong to the deck.</summary>
    public void Reset() => selector = 0;

    public byte Read(ushort port, long masterTick)
    {
        if (transport is null) return 0xff;
        return selector < StatusBytes ? Status()[selector] : (byte)0;
    }

    public void Write(ushort port, byte value, long masterTick)
    {
        if (transport is null) return;
        if (value is >= 0x20 and <= 0x2f) { selector = (byte)(value - 0x20); return; }
        lastResult = value <= 0x08 ? Command(value) : Unsupported;
    }

    public byte[] Status()
    {
        var deck = transport!.Deck.Observe();
        var side = deck.ActiveSide == TapeSide.A ? deck.A : deck.B;
        byte flags = (byte)((deck.Present ? 1 : 0) | (deck.ActiveSide == TapeSide.B ? 2 : 0) | (side.TabPresent ? 4 : 0)
            | (deck.Paused ? 8 : 0) | (deck.Mode != TapeDeckMode.Stopped ? 16 : 0) | (deck.Mode == TapeDeckMode.Recording ? 32 : 0)
            | (deck.HoldReason == TapeHoldReason.Fault ? 64 : 0));
        uint block = deck.Present ? DataBlockAt(deck.Position) : uint.MaxValue;
        ulong minutes = Math.Min(deck.ElapsedSeconds / 60, 255);
        return [flags, (byte)deck.Mode, (byte)deck.HoldReason, lastResult, (byte)generation, (byte)(generation >> 8),
            (byte)deck.Counter, (byte)(deck.Counter >> 8), (byte)minutes, (byte)(deck.ElapsedSeconds % 60),
            (byte)block, (byte)(block >> 8), (byte)(block >> 16), (byte)(block >> 24)];
    }

    public TapeControlPortState Capture()
    {
        if (transport is null) throw new InvalidOperationException("Tape control port has no deck attached.");
        return new(selector, lastResult, generation, deckKey!);
    }

    /// <summary>Restore after the deck: refused unless the deck is in the state this generation counted.</summary>
    public bool Restore(TapeControlPortState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (transport is null || state.Selector > 0x0f || state.LastResult > Unsupported
            || state.Generation != generation || state.DeckKey != Key(transport.Deck.Observe())) return false;
        selector = state.Selector; lastResult = state.LastResult; generation = state.Generation; deckKey = state.DeckKey;
        return true;
    }

    private byte Command(byte value)
    {
        var tape = transport!; var deck = tape.Deck.Observe();
        if (!deck.Present) return NoMedia;
        var result = value switch
        {
            // STOP never ejects: on a stopped deck it is already done.
            0x00 => deck.Mode == TapeDeckMode.Stopped && !deck.Paused ? null : tape.Deck.StopEject(false),
            0x01 => tape.Deck.Play(),
            0x02 => tape.Deck.RecordPlay(),
            0x03 => tape.Deck.Rewind(),
            0x04 => tape.Deck.FastForward(),
            0x05 => deck.Paused ? null : tape.Deck.Pause(),
            0x06 => deck.Paused ? tape.Deck.Pause() : null,
            0x07 => Skip(tape, deck, reverse: true),
            _ => Skip(tape, deck, reverse: false),
        };
        if (result is null || result.Success) return Accepted;
        return result.Failure!.Code switch
        {
            TapeError.Interlock => Interlock,
            TapeError.WriteProtected => WriteProtect,
            _ => Fault,
        };
    }

    /// <summary>REW/FF that stops where the previous/next recorded block's gap begins; with none that way, the plain
    /// wind runs to the physical end. Already at that end: nothing to do.</summary>
    private TapeResult<TapeDeckView>? Skip(TapeTransport tape, TapeDeckView deck, bool reverse)
    {
        if (reverse ? deck.Position == default : deck.Position == deck.Duration) return null;
        var gaps = Scan(); TapeTime? target = null;
        foreach (var block in gaps)
        {
            if (reverse ? block.End < deck.Position && block.End != default : block.End > deck.Position && block.End < deck.Duration)
            {
                if (reverse) target = block.End;
                else { target = block.End; break; }
            }
        }
        return target is { } t ? tape.WindTo(reverse, t) : reverse ? tape.Deck.Rewind() : tape.Deck.FastForward();
    }

    private uint DataBlockAt(TapeTime position)
    {
        uint index = 0;
        foreach (var block in Scan())
        {
            if (block.Flag != 0xff) continue;
            if (position >= block.Start && position < block.End) return index;
            index++;
        }
        return uint.MaxValue;
    }

    private ImmutableArray<TapeBlockSpan> Scan()
    {
        var index = transport!.ActiveIndex;
        if (index is null) return [];
        if (!ReferenceEquals(index, scanned)) { blocks = TapCodec.Blocks(index); scanned = index; }
        return blocks;
    }

    /// <summary>Each live transition is observed immediately, including manual round trips between reads.
    /// Idempotent notifications and head motion do not change the key or count.</summary>
    private void OnDeckStateChanged(TapeDeckView view)
    {
        var key = Key(view);
        if (key != deckKey) { deckKey = key; generation = unchecked((ushort)(generation + 1)); }
    }

    private static string Key(TapeDeckView d) => string.Join('|', d.Present, d.ActiveSide, d.Mode, d.Paused, d.HoldReason,
        d.A.TabPresent, d.B.TabPresent, d.FastWind, d.MediaIdentity);
}
