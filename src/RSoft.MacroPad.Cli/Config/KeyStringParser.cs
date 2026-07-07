using RSoft.MacroPad.BLL.Infrasturture.Model;

namespace RSoft.MacroPad.Cli.Config;

public static class KeyStringParser
{
    public static IReadOnlyList<(KeyCode Key, Modifier Modifiers)> ParseKeySequence(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("keys must not be empty.");
        }

        var result = new List<(KeyCode Key, Modifier Modifiers)>();
        foreach (var stroke in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(ParseKeyStroke(stroke));
        }

        return result;
    }

    public static MediaKey ParseMediaKey(string value)
        => ParseEnum<MediaKey>(value, "media");

    public static MouseButton ParseMouseButton(string value)
        => ParseEnum<MouseButton>(value, "mouse");

    public static Modifier ParseModifiers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Modifier.None;
        }

        var modifiers = Modifier.None;
        foreach (var token in value.Split(['+', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            modifiers |= ParseModifier(token);
        }

        return modifiers;
    }

    private static (KeyCode Key, Modifier Modifiers) ParseKeyStroke(string stroke)
    {
        var parts = stroke.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new FormatException("keys contains an empty stroke.");
        }

        var keyName = parts[^1];
        if (!Enum.TryParse<KeyCode>(keyName, ignoreCase: true, out var key))
        {
            throw new FormatException($"Unknown key '{keyName}'. Use a KeyCode enum name such as A, D1, F5, Enter, or SpaceKey.");
        }

        var modifiers = Modifier.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= ParseModifier(parts[i]);
        }

        return (key, modifiers);
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"{fieldName} must not be empty.");
        }

        if (!Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed))
        {
            throw new FormatException($"Unknown {fieldName} value '{value}'. Use a {typeof(TEnum).Name} enum name.");
        }

        return parsed;
    }

    private static Modifier ParseModifier(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "ctrl" or "control" => Modifier.LeftCtrl,
            "shift" => Modifier.LeftShift,
            "alt" or "option" => Modifier.LeftAlt,
            "win" or "cmd" or "command" => Modifier.LeftWin,
            "lctrl" or "leftctrl" => Modifier.LeftCtrl,
            "rctrl" or "rightctrl" => Modifier.RightCtrl,
            "lshift" or "leftshift" => Modifier.LeftShift,
            "rshift" or "rightshift" => Modifier.RightShift,
            "lalt" or "leftalt" => Modifier.LeftAlt,
            "ralt" or "rightalt" => Modifier.RightAlt,
            "lwin" or "leftwin" => Modifier.LeftWin,
            "rwin" or "rightwin" => Modifier.RightWin,
            _ => throw new FormatException($"Unknown modifier '{value}'. Use Ctrl, Shift, Alt, Win, LCtrl, RCtrl, LShift, RShift, LAlt, RAlt, LWin, or RWin.")
        };
    }
}
