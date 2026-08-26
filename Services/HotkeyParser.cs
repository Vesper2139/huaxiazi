using System;
using System.Windows.Input;

namespace Huaxiazi.Services;

public readonly record struct HotkeyCombination(ModifierKeys Modifiers, Key Key);

public static class HotkeyParser
{
    public static bool TryParse(string? text, out HotkeyCombination combination)
    {
        combination = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "win":
                case "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key.HasValue || !Enum.TryParse<Key>(part, true, out var parsed) || parsed == Key.None)
                        return false;
                    key = parsed;
                    break;
            }
        }

        if (modifiers == ModifierKeys.None || !key.HasValue) return false;
        combination = new HotkeyCombination(modifiers, key.Value);
        return true;
    }
}
