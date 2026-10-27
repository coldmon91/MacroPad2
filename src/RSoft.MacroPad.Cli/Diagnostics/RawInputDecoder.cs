using System.Text;
using static RSoft.MacroPad.Cli.Diagnostics.RawInputNative;

namespace RSoft.MacroPad.Cli.Diagnostics;

/// <summary>
/// Turns raw keyboard/mouse report fields into readable text.
/// </summary>
internal static class RawInputDecoder
{
    private static readonly Dictionary<ushort, string> VirtualKeyNames = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x10] = "Shift",
        [0x11] = "Ctrl", [0x12] = "Alt", [0x13] = "Pause", [0x14] = "CapsLock",
        [0x1B] = "Esc", [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown",
        [0x23] = "End", [0x24] = "Home", [0x25] = "Left", [0x26] = "Up",
        [0x27] = "Right", [0x28] = "Down", [0x2C] = "PrintScreen", [0x2D] = "Insert",
        [0x2E] = "Delete", [0x5B] = "LWin", [0x5C] = "RWin", [0x5D] = "Menu",
        [0xA0] = "LShift", [0xA1] = "RShift", [0xA2] = "LCtrl", [0xA3] = "RCtrl",
        [0xA4] = "LAlt", [0xA5] = "RAlt", [0x90] = "NumLock", [0x91] = "ScrollLock"
    };

    internal static string DescribeKeyboard(ushort virtualKey, ushort makeCode, ushort flags)
    {
        var state = (flags & RI_KEY_BREAK) != 0 ? "up  " : "down";
        var extended = new StringBuilder();
        if ((flags & RI_KEY_E0) != 0)
        {
            extended.Append(" E0");
        }

        if ((flags & RI_KEY_E1) != 0)
        {
            extended.Append(" E1");
        }

        return $"{state} key={DescribeVirtualKey(virtualKey),-12} vk=0x{virtualKey:X2} scan=0x{makeCode:X2}{extended}";
    }

    internal static string DescribeVirtualKey(ushort virtualKey)
    {
        if (VirtualKeyNames.TryGetValue(virtualKey, out var name))
        {
            return name;
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        var mapped = MapVirtualKeyW(virtualKey, MAPVK_VK_TO_CHAR) & 0xFFFF;
        if (mapped >= 0x20 && mapped != 0x7F)
        {
            return $"'{(char)mapped}'";
        }

        return $"VK_{virtualKey:X2}";
    }

    internal static string DescribeMouse(ushort buttonFlags, ushort buttonData, int lastX, int lastY)
    {
        var parts = new List<string>();

        if ((buttonFlags & RI_MOUSE_WHEEL) != 0)
        {
            parts.Add($"wheel={FormatDelta((short)buttonData)}");
        }

        if ((buttonFlags & RI_MOUSE_HWHEEL) != 0)
        {
            parts.Add($"hwheel={FormatDelta((short)buttonData)}");
        }

        AddButton(parts, buttonFlags, 0x0001, 0x0002, "left");
        AddButton(parts, buttonFlags, 0x0004, 0x0008, "right");
        AddButton(parts, buttonFlags, 0x0010, 0x0020, "middle");
        AddButton(parts, buttonFlags, 0x0040, 0x0080, "button4");
        AddButton(parts, buttonFlags, 0x0100, 0x0200, "button5");

        if (lastX != 0 || lastY != 0)
        {
            parts.Add($"move=({lastX},{lastY})");
        }

        return parts.Count > 0 ? string.Join(' ', parts) : $"flags=0x{buttonFlags:X4}";
    }

    private static void AddButton(List<string> parts, ushort flags, ushort downMask, ushort upMask, string name)
    {
        if ((flags & downMask) != 0)
        {
            parts.Add($"{name} down");
        }

        if ((flags & upMask) != 0)
        {
            parts.Add($"{name} up");
        }
    }

    private static string FormatDelta(short delta)
    {
        // One wheel notch is WHEEL_DELTA (120); show notches so a knob click reads as +1 / -1.
        var notches = delta / 120.0;
        return $"{(delta >= 0 ? "+" : "")}{delta} ({notches:+0.##;-0.##;0} notch)";
    }
}
