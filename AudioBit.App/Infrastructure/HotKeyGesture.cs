using System.Windows.Input;

namespace AudioBit.App.Infrastructure;

[Flags]
internal enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

internal readonly record struct HotKeyGesture(HotKeyModifiers Modifiers, Key Key)
{
    public static bool TryParse(string? text, out HotKeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotKeyModifiers.None;
        Key? key = null;

        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotKeyModifiers.Control;
                    continue;

                case "alt":
                    modifiers |= HotKeyModifiers.Alt;
                    continue;

                case "shift":
                    modifiers |= HotKeyModifiers.Shift;
                    continue;

                case "win":
                case "windows":
                    modifiers |= HotKeyModifiers.Windows;
                    continue;
            }

            if (!TryParseKey(rawPart, out var parsedKey))
            {
                return false;
            }

            key = parsedKey;
        }

        if (key is null || key == Key.None)
        {
            return false;
        }

        gesture = new HotKeyGesture(modifiers, key.Value);
        return true;
    }

    public static bool TryCreate(KeyEventArgs e, out HotKeyGesture gesture)
    {
        var resolvedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (resolvedKey is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.None)
        {
            gesture = default;
            return false;
        }

        var modifiers = HotKeyModifiers.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0)
        {
            modifiers |= HotKeyModifiers.Windows;
        }

        gesture = new HotKeyGesture(modifiers, resolvedKey);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotKeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(Key));
        return string.Join(" + ", parts);
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.Length == 1 && char.IsLetter(token[0]))
        {
            key = Enum.Parse<Key>(token.ToUpperInvariant(), ignoreCase: true);
            return true;
        }

        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            key = token[0] switch
            {
                '0' => Key.D0,
                '1' => Key.D1,
                '2' => Key.D2,
                '3' => Key.D3,
                '4' => Key.D4,
                '5' => Key.D5,
                '6' => Key.D6,
                '7' => Key.D7,
                '8' => Key.D8,
                '9' => Key.D9,
                _ => Key.None,
            };
            return key != Key.None;
        }

        var normalized = token.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.ToLowerInvariant() switch
        {
            "space" => SetKey(Key.Space, out key),
            "enter" => SetKey(Key.Enter, out key),
            "esc" or "escape" => SetKey(Key.Escape, out key),
            "tab" => SetKey(Key.Tab, out key),
            "backspace" => SetKey(Key.Back, out key),
            "delete" => SetKey(Key.Delete, out key),
            "insert" => SetKey(Key.Insert, out key),
            "home" => SetKey(Key.Home, out key),
            "end" => SetKey(Key.End, out key),
            "pageup" => SetKey(Key.PageUp, out key),
            "pagedown" => SetKey(Key.PageDown, out key),
            "up" => SetKey(Key.Up, out key),
            "down" => SetKey(Key.Down, out key),
            "left" => SetKey(Key.Left, out key),
            "right" => SetKey(Key.Right, out key),
            _ => Enum.TryParse(normalized, ignoreCase: true, out key),
        };
    }

    private static string FormatKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString().ToUpperInvariant();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => key.ToString(),
        };
    }

    private static bool SetKey(Key resolvedKey, out Key key)
    {
        key = resolvedKey;
        return true;
    }
}
