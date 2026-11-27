using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Windows.Input;

namespace Snappo.Hotkeys;

internal enum SideMouseButton
{
    None,
    Button4,
    Button5,
}

internal sealed record HotkeyTrigger(ModifierKeys Modifiers, Key Key, SideMouseButton MouseButton)
{
    private const string MouseButton4Name = "Mouse Button 4";
    private const string MouseButton5Name = "Mouse Button 5";

    private static readonly Dictionary<Key, string> FriendlyKeyNames = BuildFriendlyKeyNames();

    private static readonly Dictionary<string, Key> KeysByFriendlyName =
        FriendlyKeyNames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    public bool UsesMouse => MouseButton != SideMouseButton.None;

    public bool MayBreakNormalUse => !UsesMouse && Modifiers == ModifierKeys.None && !IsSafeOnItsOwn(Key);

    private static bool IsSafeOnItsOwn(Key key) =>
        key is Key.Snapshot or Key.Scroll or Key.Pause || (key >= Key.F1 && key <= Key.F24);

    public static bool IsModifierKey(Key key) => ModifierFor(key) != ModifierKeys.None;

    public static ModifierKeys ModifierFor(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        Key.LWin or Key.RWin => ModifierKeys.Windows,
        _ => ModifierKeys.None,
    };

    public static HotkeyTrigger ForKey(Key key, ModifierKeys modifiers) =>
        new(modifiers, key, SideMouseButton.None);

    public static HotkeyTrigger ForMouse(SideMouseButton button, ModifierKeys modifiers = ModifierKeys.None) =>
        new(modifiers, Key.None, button);

    public override string ToString()
    {
        var parts = new List<string>();

        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        parts.Add(MouseButton switch
        {
            SideMouseButton.Button4 => MouseButton4Name,
            SideMouseButton.Button5 => MouseButton5Name,
            _ => FriendlyKeyNames.GetValueOrDefault(Key, Key.ToString()),
        });

        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyTrigger? trigger)
    {
        trigger = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            switch (parts[index].ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win":
                case "windows": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        string mainPart = parts[^1];

        if (mainPart.Equals(MouseButton4Name, StringComparison.OrdinalIgnoreCase))
        {
            trigger = ForMouse(SideMouseButton.Button4, modifiers);
            return true;
        }

        if (mainPart.Equals(MouseButton5Name, StringComparison.OrdinalIgnoreCase))
        {
            trigger = ForMouse(SideMouseButton.Button5, modifiers);
            return true;
        }

        if (KeysByFriendlyName.TryGetValue(mainPart, out Key knownKey))
        {
            trigger = ForKey(knownKey, modifiers);
            return true;
        }

        if (!int.TryParse(mainPart, out _) && Enum.TryParse(mainPart, ignoreCase: true, out Key parsedKey)
            && Enum.IsDefined(parsedKey) && KeyInterop.VirtualKeyFromKey(parsedKey) != 0)
        {
            trigger = ForKey(parsedKey, modifiers);
            return true;
        }

        return false;
    }

    private static Dictionary<Key, string> BuildFriendlyKeyNames()
    {
        var names = new Dictionary<Key, string>
        {
            [Key.Snapshot] = "PrintScreen",
            [Key.Return] = "Enter",
            [Key.Escape] = "Esc",
            [Key.Back] = "Backspace",
            [Key.Prior] = "PageUp",
            [Key.Next] = "PageDown",
            [Key.Capital] = "CapsLock",
            [Key.Scroll] = "ScrollLock",
            [Key.LeftCtrl] = "Left Ctrl",
            [Key.RightCtrl] = "Right Ctrl",
            [Key.LeftAlt] = "Left Alt",
            [Key.RightAlt] = "Right Alt",
            [Key.LeftShift] = "Left Shift",
            [Key.RightShift] = "Right Shift",
            [Key.LWin] = "Left Win",
            [Key.RWin] = "Right Win",
        };

        for (int digit = 0; digit <= 9; digit++)
        {
            names[Key.D0 + digit] = digit.ToString();
        }

        return names;
    }
}
