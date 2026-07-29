using System.Text;
using System.Windows.Input;

namespace Illusion.Settings;

/// <summary>
/// One key combination, as the settings file stores it and as the user reads it: <c>"Ctrl+Shift+Z"</c>,
/// <c>"G"</c>, <c>"Num /"</c>, <c>"Shift"</c> (a modifier on its own — the camera's speed keys are that),
/// or <c>""</c> for unbound.
/// <para>
/// The text form is written by hand as well as parsed, so it is deliberately not WPF's own
/// <see cref="KeyGesture"/> syntax: <see cref="KeyGestureConverter"/> refuses a bare letter
/// (an unmodified non-function key is not a legal gesture), and half of what this editor binds — G, R, S,
/// Tab, Space — is exactly that.
/// </para>
/// </summary>
public readonly record struct Hotkey(Key Key, ModifierKeys Modifiers)
{
    /// <summary>No key at all: the action is unbound and can never fire.</summary>
    public static readonly Hotkey None = default;

    /// <summary>False for the unbound combination — neither a key nor a modifier.</summary>
    public bool IsBound => Key != Key.None || Modifiers != ModifierKeys.None;

    /// <summary>True when this is a modifier held on its own (the camera's fast/slow keys).</summary>
    public bool IsModifierOnly => Key == Key.None && Modifiers != ModifierKeys.None;

    /// <summary>Does a key-down event match this binding? The modifiers must match exactly — a binding on
    /// <c>G</c> must not fire on Ctrl+G, which belongs to whatever is bound to Ctrl+G.</summary>
    public bool Matches(Key key, ModifierKeys modifiers) =>
        Key != Key.None && key == Key && modifiers == Modifiers;

    // Keys whose enum name is not what is printed on the keyboard. Everything absent from here formats as
    // its enum name, which is already right for letters, F1..F12 and the arrows.
    private static readonly (Key Key, string Text)[] Named =
    {
        (Key.Delete, "Del"), (Key.Escape, "Esc"), (Key.Return, "Enter"), (Key.Back, "Backspace"),
        (Key.PageUp, "PgUp"), (Key.PageDown, "PgDn"), (Key.Capital, "CapsLock"),
        (Key.OemQuestion, "/"), (Key.OemComma, ","), (Key.OemPeriod, "."), (Key.OemMinus, "-"),
        (Key.OemPlus, "="), (Key.OemTilde, "`"), (Key.OemOpenBrackets, "["), (Key.Oem6, "]"),
        (Key.Oem1, ";"), (Key.OemQuotes, "'"), (Key.Oem5, "\\"),
        // The numeric keypad is a separate set of keys and binds separately — "/" and "Num /" are two
        // different bindings, which is why the frame-selection shortcut ships as two of them.
        (Key.Divide, "Num /"), (Key.Multiply, "Num *"), (Key.Subtract, "Num -"),
        (Key.Add, "Num +"), (Key.Decimal, "Num ."),
    };

    private static readonly (ModifierKeys Modifier, string Text)[] NamedModifiers =
    {
        (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Shift, "Shift"),
        (ModifierKeys.Alt, "Alt"), (ModifierKeys.Windows, "Win"),
    };

    /// <summary>The readable form; <see cref="TryParse"/> reads back exactly what this writes.</summary>
    public override string ToString()
    {
        if (!IsBound) return "";

        var sb = new StringBuilder();
        foreach ((ModifierKeys modifier, string text) in NamedModifiers)
        {
            if ((Modifiers & modifier) != 0) sb.Append(text).Append('+');
        }

        if (Key != Key.None) sb.Append(FormatKey(Key));
        else sb.Length--;   // modifier on its own: drop the trailing '+'
        return sb.ToString();
    }

    /// <summary>
    /// Reads the text form. An empty string is the unbound combination and parses successfully — that is a
    /// value the user can choose, not a malformed one. False means the text was not a combination at all
    /// (a hand-edited typo); the caller keeps the default rather than dropping the action.
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text)) return true;

        ModifierKeys modifiers = ModifierKeys.None;
        Key key = Key.None;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            ModifierKeys modifier = ParseModifier(parts[i]);
            if (modifier != ModifierKeys.None)
            {
                modifiers |= modifier;
                continue;
            }

            // Only the last token may name a key, and only one of them may.
            if (i != parts.Length - 1) return false;
            key = ParseKey(parts[i]);
            if (key == Key.None) return false;
        }

        hotkey = new Hotkey(key, modifiers);
        return true;
    }

    private static string FormatKey(Key key)
    {
        foreach ((Key named, string text) in Named)
        {
            if (named == key) return text;
        }
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "Num " + (key - Key.NumPad0);
        return key.ToString();
    }

    private static Key ParseKey(string text)
    {
        foreach ((Key named, string namedText) in Named)
        {
            if (string.Equals(namedText, text, StringComparison.OrdinalIgnoreCase)) return named;
        }
        if (text.Length == 1 && char.IsAsciiDigit(text[0])) return Key.D0 + (text[0] - '0');
        if (text.Length == 5 && text.StartsWith("Num ", StringComparison.OrdinalIgnoreCase)
            && char.IsAsciiDigit(text[4]))
        {
            return Key.NumPad0 + (text[4] - '0');
        }

        // Enum.TryParse also accepts the underlying number, which would turn a stray "42" into whatever key
        // sits at 42 — nothing here ever writes a key that way, so refuse digits outright.
        if (char.IsAsciiDigit(text[0])) return Key.None;
        return Enum.TryParse(text, ignoreCase: true, out Key parsed) && Enum.IsDefined(parsed) ? parsed : Key.None;
    }

    private static ModifierKeys ParseModifier(string text)
    {
        foreach ((ModifierKeys modifier, string named) in NamedModifiers)
        {
            if (string.Equals(named, text, StringComparison.OrdinalIgnoreCase)) return modifier;
        }
        // The long spellings, for a hand-edited file.
        if (string.Equals(text, "Control", StringComparison.OrdinalIgnoreCase)) return ModifierKeys.Control;
        if (string.Equals(text, "Windows", StringComparison.OrdinalIgnoreCase)) return ModifierKeys.Windows;
        return ModifierKeys.None;
    }
}
