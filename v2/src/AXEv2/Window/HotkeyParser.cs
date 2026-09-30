using System.Windows.Input;
using AxeV2.Native;

namespace AxeV2.Window;

/// <summary>Parses shortcuts such as "Ctrl+Alt+X" into RegisterHotKey arguments.</summary>
public static class HotkeyParser
{
    public readonly record struct Hotkey(uint Modifiers, uint VirtualKey);

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "WIN":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;
                default:
                    if (key is not null)
                    {
                        return false; // two non-modifier keys
                    }

                    var name = part.Length == 1 && char.IsAsciiDigit(part[0]) ? "D" + part : part;
                    if (!Enum.TryParse<Key>(name, ignoreCase: true, out var parsed) || parsed == Key.None)
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        // Require at least one modifier so a plain key is never grabbed system-wide.
        if (key is null || modifiers == 0)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers | NativeMethods.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key.Value));
        return true;
    }
}
