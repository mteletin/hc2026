using Avalonia.Input;
using Hc2000.Machine.Input;

namespace Hc2000.App.Input;

public enum InputReleaseOrigin { Unspecified, Deactivated, MappingModeChange, Reset, SessionEnd }

/// <summary>UI-side translator only: no live machine reads, waits, timers or device writes.</summary>
public sealed class KeyboardInputAdapter(InputQueue queue)
{
    public const KeyMappingMode DefaultMode = KeyMappingMode.Positional;
    public FrontendInputEvidence Evidence { get; } = new();
    private readonly HashSet<Key> pressed = new();
    private Key? textKey;
    private bool suppressText;
    private KeyMappingMode mode = DefaultMode;
    public event Action<string>? Rejected;
    public KeyMappingMode Mode
    {
        get => mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == mode) return;
            ReleaseFocus(InputReleaseOrigin.MappingModeChange); mode = value;
        }
    }

    public bool KeyDown(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Observe("receipt", "key-down", key, modifiers, "delivered");
        suppressText = !pressed.Add(key);
        if (suppressText) { Observe("mapping", "key-down", key, modifiers, "repeat-suppressed"); textKey = key; return true; } // ROM owns typematic, not PC repeat.
        textKey = key;
        if (mode == KeyMappingMode.Symbolic)
        {
            // Shift/AltGr choose host characters; their physical closures must not pollute the chord.
            // AltGr can arrive as Ctrl followed by Alt: retire that Ctrl closure.
            if (modifiers.HasFlag(KeyModifiers.Alt))
            {
                foreach (var control in new[] { Key.LeftCtrl, Key.RightCtrl })
                    if (pressed.Contains(control))
                        Handoff(new KeyTransition(Source(control), new KeyChord(HcKey.Space), false), "symbolic-alt-control-release", control, modifiers);
            }
            if (key is Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
            { Observe("mapping", "key-down", key, modifiers, "symbolic-modifier-suppressed"); return true; }
            var controlLetter = modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Alt);
            // The native backend may suppress TextInput when KeyDown is handled
            // (Win32 WM_CHAR and X11 input routing). Track the press, but leave the
            // first text key-down unhandled so the host layout can supply its character.
            if (!controlLetter && IsTextKey(key))
            { Observe("mapping", "key-down", key, modifiers, "await-host-text"); return false; }
        }
        var chord = KeyMap.Physical(key, modifiers);
        Observe("mapping", "key-down", key, modifiers, chord is null ? "unmapped" : "mapped", chord);
        if (chord is null) return false;
        suppressText = true;
        Handoff(new KeyTransition(Source(key), chord, true), "key-down", key, modifiers);
        return true;
    }

    public bool TextInput(string? text)
    {
        Observe("receipt", "text-input", textKey, KeyModifiers.None, "delivered");
        if (mode == KeyMappingMode.Positional || string.IsNullOrEmpty(text))
        { Observe("mapping", "text-input", textKey, KeyModifiers.None, "ignored"); return false; }
        if (suppressText)
        { Observe("mapping", "text-input", textKey, KeyModifiers.None, "suppressed"); return true; }
        if (text.Length == 1 && textKey is Key key && pressed.Contains(key))
        {
            var chord = KeyMap.Character(text[0]);
            Observe("mapping", "text-input", key, KeyModifiers.None, chord is null ? "unmapped" : "mapped", chord);
            if (chord is null) { Rejected?.Invoke("Character is not mapped to the HC2000 keyboard."); return false; }
            Handoff(new KeyTransition(Source(key), chord, true), "text-input", key);
            return true;
        }
        return Paste(text); // IME/text-only input is serialized as bounded master-time paste.
    }

    public void KeyUp(Key key)
    {
        Observe("receipt", "key-up", key, KeyModifiers.None, "delivered");
        pressed.Remove(key);
        Handoff(new KeyTransition(Source(key), new KeyChord(HcKey.Space), false), "key-up", key);
        if (textKey == key) { textKey = null; suppressText = false; }
    }

    public bool Paste(string text)
    {
        Observe("receipt", "paste-request", null, KeyModifiers.None, "request-no-content-retained");
        try { Handoff(new PasteText(text), "paste-request"); return true; }
        catch (ArgumentException ex) { Observe("refusal", "paste-request", null, KeyModifiers.None, "invalid-paste"); Rejected?.Invoke(ex.Message); return false; }
        catch (InvalidOperationException ex) { Observe("refusal", "paste-request", null, KeyModifiers.None, "paste-queue-full"); Rejected?.Invoke(ex.Message); return false; }
    }

    public void ReleaseFocus() => ReleaseFocus(InputReleaseOrigin.Unspecified);

    public void ReleaseFocus(InputReleaseOrigin cause)
    {
        var outcome = cause switch
        {
            InputReleaseOrigin.Deactivated => "window-deactivated",
            InputReleaseOrigin.MappingModeChange => "mapping-mode-change",
            InputReleaseOrigin.Reset => "reset-request",
            InputReleaseOrigin.SessionEnd => "session-end-request",
            _ => "cause-unavailable"
        };
        Observe("receipt", "release-request", null, KeyModifiers.None, outcome);
        pressed.Clear(); textKey = null; suppressText = false;
        Handoff(new ReleaseAll(), "release-request");
    }

    private void Observe(string stage, string origin, Key? key, KeyModifiers modifiers,
        string outcome, KeyChord? chord = null, long? sequence = null)
        => Evidence.Record(stage, origin, key, modifiers, mode, outcome, chord, sequence);

    private void Handoff(InputCommand command, string origin, Key? key = null,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var sequence = queue.Enqueue(command);
        Observe("queue-handoff", origin, key, modifiers, "enqueued-not-guest-effect",
            command is KeyTransition transition && transition.Down ? transition.Chord : null, sequence);
    }

    private static string Source(Key key) => "pc:" + key;
    private static bool IsTextKey(Key key) => key is >= Key.A and <= Key.Z
        or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9
        or Key.Space or Key.OemQuotes or Key.OemComma or Key.OemPeriod or Key.OemMinus
        or Key.OemPlus or Key.OemQuestion or Key.OemSemicolon or Key.OemOpenBrackets
        or Key.OemCloseBrackets or Key.OemPipe or Key.OemBackslash or Key.OemTilde;
}
