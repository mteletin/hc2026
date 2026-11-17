using Avalonia.Input;
using Hc2000.Machine.Input;

namespace Hc2000.App.Input;

public sealed record KeyboardReferenceRow(string Action, string HostKeys, bool Supported = true);

/// <summary>Read-only explanations of the same matrix closures used by the input adapter.</summary>
public static class KeyboardReference
{
    public static string HostChord(KeyMappingMode mode, params HcKey[] keys)
    {
        if (keys.Length == 0 || keys.Any(key => !Enum.IsDefined(key)))
            return "Unsupported: no host key is mapped";
        var target = keys.ToHashSet();
        if (mode == KeyMappingMode.Symbolic)
        {
            for (var character = ' '; character <= '~'; character++)
                if (KeyMap.Character(character) is { } chord && target.SetEquals(chord.Keys))
                    return character == ' ' ? "Space" : $"Type {character} (host layout)";
            foreach (var candidate in new[] { Key.LeftCtrl, Key.Enter, Key.Back, Key.Escape, Key.Tab, Key.Left, Key.Right, Key.Up, Key.Down })
                if (KeyMap.Physical(candidate) is { } chord && SameClosures(keys, chord.Keys))
                    return candidate == Key.LeftCtrl ? "Ctrl" : candidate == Key.Enter ? "Enter" : candidate.ToString();
            if (target.Contains(HcKey.CapsShift) || target.Contains(HcKey.SymbolShift))
                return "Unsupported in Symbolic; select Positional for this BASIC chord";
        }
        var names = new List<string>();
        foreach (var key in keys)
        {
            var host = Enum.GetValues<Key>().FirstOrDefault(candidate =>
                KeyMap.Physical(candidate) is { Keys.Length: 1 } chord && chord.Keys[0] == key);
            if (host == Key.None) return "Unsupported: no host key is mapped";
            names.Add(host switch
            {
                Key.LeftShift => "Shift", Key.LeftAlt => "Alt", Key.LeftCtrl => "Ctrl",
                >= Key.D0 and <= Key.D9 => ((char)('0' + host - Key.D0)).ToString(),
                Key.Enter => "Enter", Key.Back => "Backspace", _ => host.ToString()
            });
        }
        return string.Join(" + ", names);
    }

    private static bool SameClosures(IEnumerable<HcKey> left, IEnumerable<HcKey> right)
    {
        var leftMatrix = new KeyboardMatrix();
        var rightMatrix = new KeyboardMatrix();
        foreach (var key in left) leftMatrix.SetKey(key, true);
        foreach (var key in right) rightMatrix.SetKey(key, true);
        for (var row = 0; row < 8; row++)
        {
            var port = (ushort)(0xffff ^ (1 << (row + 8)));
            if (leftMatrix.Read(port) != rightMatrix.Read(port)) return false;
        }
        return true;
    }

    public static IReadOnlyList<KeyboardReferenceRow> Basic(KeyMappingMode mode)
    {
        var extended = HostChord(mode, HcKey.CapsShift, HcKey.SymbolShift);
        var supported = !extended.StartsWith("Unsupported", StringComparison.Ordinal);
        return new[]
        {
            new KeyboardReferenceRow("PRINT (at K cursor)", HostChord(mode, HcKey.P)),
            new KeyboardReferenceRow("Quotes (\")", HostChord(mode, HcKey.SymbolShift, HcKey.P)),
            new KeyboardReferenceRow("Plus (+)", HostChord(mode, HcKey.SymbolShift, HcKey.K)),
            new KeyboardReferenceRow("Extended mode", supported ? extended + "; release chord" : extended, supported),
            new KeyboardReferenceRow("USR", supported ? extended + "; release chord, then " + HostChord(mode, HcKey.L) : extended, supported),
            new KeyboardReferenceRow("Return", HostChord(mode, HcKey.Enter))
        };
    }

    public static string PrintExample(KeyMappingMode mode) =>
        "Read these steps, then close this reference to return keyboard focus to the selected machine. " +
        "Allow startup to finish; if only the sign-on is visible, press Enter once to reach the BASIC edit line. " +
        "At the BASIC K cursor, press and release each step (letters are unshifted). " +
        (mode == KeyMappingMode.Positional ? "For Alt + K, hold Alt while pressing K, then release both. " : "Use host-layout text for symbols. ") +
        string.Join(" → ", new[] { HostChord(mode, HcKey.P), HostChord(mode, HcKey.D2),
            HostChord(mode, HcKey.SymbolShift, HcKey.K), HostChord(mode, HcKey.D3), HostChord(mode, HcKey.Enter) }) +
        ". The edit line is PRINT 2+3; the result is 5. PRINT is one keyword key, not its letters.";

    public static string BootExample(KeyMappingMode mode) =>
        "Mount a supported CP/M boot disk in A:. With input focused on the selected machine, at BASIC's K cursor: " +
            HostChord(mode, HcKey.P) + " → " + HostChord(mode, HcKey.CapsShift, HcKey.SymbolShift) +
            " (release chord) → " + HostChord(mode, HcKey.L) + " → 14446 → " + HostChord(mode, HcKey.Enter) +
            ". Check PRINT USR 14446 before Return. This leaves BASIC and boots the disk; opening this reference does not.";

    public static IReadOnlyList<KeyboardReferenceRow> Cpm()
    {
        var rows = new List<KeyboardReferenceRow>();
        foreach (var key in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Back, Key.Delete, Key.Enter, Key.Tab, Key.Escape, Key.F2 })
            if (KeyMap.CpmCode(key) is { } code)
                rows.Add(new(key.ToString(), $"{key} — ROM code {code:X2} hex"));
        foreach (var key in Enum.GetValues<Key>().Where(key => key is >= Key.A and <= Key.Z))
            if (KeyMap.CpmCode(key, KeyModifiers.Control) is { } code)
                rows.Add(new($"Control-{key}", $"Ctrl + {key} — ROM code {code:X2} hex"));
        return rows;
    }
}
