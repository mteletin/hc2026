using System.Collections.Immutable;
using Avalonia.Input;
using Hc2000.Machine.Input;

namespace Hc2000.App.Input;

public enum KeyMappingMode { Symbolic, Positional }

/// <summary>Immutable logical keys; extra-key closures remain owned by KeyboardMatrix.</summary>
public sealed record KeyChord
{
    public ImmutableArray<HcKey> Keys { get; }
    public KeyChord(params HcKey[] keys)
    {
        if (keys.Length == 0 || keys.Any(k => !Enum.IsDefined(k)))
            throw new ArgumentException("A chord requires valid keys.", nameof(keys));
        Keys = keys.Distinct().ToImmutableArray();
    }
}

public static class KeyMap
{
    // Symbol-shift conversion follows KB hc2000 doc source-cpm-rom §4.
    // The BASIC ROM may interpret these closures as tokens, not ASCII characters.
    private const string Symbols = "~*?\\\u001c{}^\u001d-+=.,;\"\u001e<|>]/\u001f`[:";
    private const string SymbolLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static KeyChord? Character(char value)
    {
        if (value == '\r' || value == '\n') return new(HcKey.Enter);
        if (value == '\t') return new(HcKey.Tab);
        if (value == ' ') return new(HcKey.Space);
        if (value == '\u007f') return new(HcKey.Delete);
        if (value == '\u001b') return new(HcKey.Edit);
        if (value is >= '\u0001' and <= '\u001a')
            return new(HcKey.Control, Letter((char)('A' + value - 1)));
        if (value is >= 'a' and <= 'z') return new(Letter(char.ToUpperInvariant(value)));
        if (value is >= 'A' and <= 'Z') return new(HcKey.CapsShift, Letter(value));
        if (value is >= '0' and <= '9') return new(Digit(value));
        var digit = "_!@#$%&'()".IndexOf(value);
        if (digit >= 0) return new(HcKey.SymbolShift, Digit((char)('0' + digit)));
        var symbol = Symbols.IndexOf(value);
        return symbol < 0 ? null : new(HcKey.SymbolShift, Letter(SymbolLetters[symbol]));
    }

    private static HcKey Letter(char letter) => Enum.Parse<HcKey>(letter.ToString());
    private static HcKey Digit(char digit) => Enum.Parse<HcKey>("D" + digit);

    public static KeyChord? Physical(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        HcKey? hc = key switch
        {
            >= Key.A and <= Key.Z => Letter((char)('A' + key - Key.A)),
            >= Key.D0 and <= Key.D9 => Digit((char)('0' + key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => Digit((char)('0' + key - Key.NumPad0)),
            Key.LeftShift or Key.RightShift => HcKey.CapsShift,
            Key.LeftCtrl or Key.RightCtrl => HcKey.Control,
            Key.LeftAlt or Key.RightAlt => HcKey.SymbolShift,
            Key.Left => HcKey.Left, Key.Down => HcKey.Down,
            Key.Up => HcKey.Up, Key.Right => HcKey.Right,
            Key.Back or Key.Delete => HcKey.Delete,
            Key.Enter => HcKey.Enter, Key.Space => HcKey.Space,
            Key.Escape or Key.F2 => HcKey.Edit,
            Key.CapsLock => HcKey.CapsLock, Key.F3 => HcKey.Graph,
            Key.Tab => HcKey.Tab, _ => null
        };
        if (hc is null) return null;
        if (key is >= Key.A and <= Key.Z && modifiers.HasFlag(KeyModifiers.Control))
            return new(HcKey.Control, hc.Value);
        // Modifier key holds themselves are independent sources; letters are physical positions.
        return new(hc.Value);
    }

    /// <summary>Diagnostic expected ROM code only; runtime always sends matrix chords, never RAM writes.</summary>
    public static byte? CpmCode(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (key is >= Key.A and <= Key.Z)
            return (byte)(modifiers.HasFlag(KeyModifiers.Control) ? key - Key.A + 1
                : key - Key.A + (modifiers.HasFlag(KeyModifiers.Shift) ? 'A' : 'a'));
        return key switch
        {
            Key.Left => 0x13, Key.Down => 0x18, Key.Up => 0x05, Key.Right => 0x04,
            Key.Delete or Key.Back => 0x7f, Key.Enter => 0x0d, Key.Tab => 0x09,
            Key.Escape or Key.F2 => 0x1b, _ => null
        };
    }
}
