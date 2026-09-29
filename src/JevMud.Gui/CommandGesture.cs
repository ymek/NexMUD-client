using Avalonia.Input;
using JevMud.Client.Settings;

namespace JevMud.Gui;

internal static class CommandGesture
{
    private const KeyModifiers RelevantModifiers =
        KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt | KeyModifiers.Shift;

    public static bool IsValid(string text) => IsValid(text, KeybindingContext.Global);

    public static bool IsValid(string text, KeybindingContext context) =>
        TryParse(text, out Key key, out KeyModifiers modifiers, out bool primary) &&
        (primary || modifiers != KeyModifiers.None || IsFunctionKey(key) ||
         (context != KeybindingContext.Global && IsNonTextNavigationKey(key)));

    public static bool Matches(CommandKeyBinding binding, KeyEventArgs e)
    {
        if (!binding.Enabled ||
            !TryParse(binding.Gesture, out Key key, out KeyModifiers required, out bool primary) ||
            key != e.Key)
        {
            return false;
        }

        KeyModifiers actual = e.KeyModifiers & RelevantModifiers;
        if (primary)
        {
            if ((actual & (KeyModifiers.Control | KeyModifiers.Meta)) == KeyModifiers.None)
            {
                return false;
            }
            actual &= ~(KeyModifiers.Control | KeyModifiers.Meta);
        }

        return actual == required;
    }

    private static bool TryParse(
        string text,
        out Key key,
        out KeyModifiers modifiers,
        out bool primary)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;
        primary = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        bool sawKey = false;
        foreach (string rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = rawPart.Trim();
            switch (part.ToLowerInvariant())
            {
                case "primary":
                    if (primary || (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
                    {
                        return false;
                    }
                    primary = true;
                    continue;
                case "ctrl":
                case "control":
                    if (primary)
                    {
                        return false;
                    }
                    modifiers |= KeyModifiers.Control;
                    continue;
                case "cmd":
                case "command":
                case "meta":
                    if (primary)
                    {
                        return false;
                    }
                    modifiers |= KeyModifiers.Meta;
                    continue;
                case "alt":
                case "option":
                    modifiers |= KeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    continue;
            }

            if (sawKey || !TryParseKey(part, out key))
            {
                return false;
            }
            sawKey = true;
        }

        return sawKey;
    }

    private static bool TryParseKey(string value, out Key key)
    {
        string normalized = value.Trim();
        if (normalized.Length == 1 && char.IsDigit(normalized[0]))
        {
            normalized = $"D{normalized}";
        }
        else if (normalized.Length == 1 && char.IsLetter(normalized[0]))
        {
            normalized = normalized.ToUpperInvariant();
        }
        else
        {
            normalized = normalized.ToLowerInvariant() switch
            {
                "esc" => "Escape",
                "return" => "Enter",
                "pgup" => "PageUp",
                "pgdn" => "PageDown",
                "space" => "Space",
                "backspace" => "Back",
                "delete" => "Delete",
                _ => normalized
            };
        }

        return Enum.TryParse(normalized, ignoreCase: true, out key) && key != Key.None;
    }

    private static bool IsFunctionKey(Key key)
    {
        string name = key.ToString();
        return name.Length >= 2 && name[0] == 'F' &&
               int.TryParse(name[1..], out int number) && number is >= 1 and <= 24;
    }

    private static bool IsNonTextNavigationKey(Key key) => key is
        Key.Tab or Key.Enter or Key.Escape or
        Key.Up or Key.Down or Key.Left or Key.Right or
        Key.PageUp or Key.PageDown or Key.Home or Key.End or
        Key.Insert or Key.Delete or Key.Back;
}
