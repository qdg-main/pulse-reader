using PulseReader.Configuration;

namespace PulseReader.Hotkeys;

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

internal sealed record HotkeyDefinition(HotkeyModifiers Modifiers, Keys Key)
{
    public static HotkeyDefinition Default { get; } =
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.Space);

    public string DisplayName
    {
        get
        {
            var parts = new List<string>();

            AddModifier(parts, HotkeyModifiers.Control, "Ctrl");
            AddModifier(parts, HotkeyModifiers.Alt, "Alt");
            AddModifier(parts, HotkeyModifiers.Shift, "Shift");
            AddModifier(parts, HotkeyModifiers.Windows, "Win");
            parts.Add(Key.ToString());

            return string.Join(" + ", parts);
        }
    }

    public static bool TryCreate(HotkeyConfiguration? configuration, out HotkeyDefinition definition)
    {
        definition = Default;

        if (configuration is null
            || configuration.Modifiers is null
            || string.IsNullOrWhiteSpace(configuration.Key))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;

        foreach (var modifier in configuration.Modifiers)
        {
            if (modifier is null)
            {
                return false;
            }

            if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || modifier.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Control;
            }
            else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
            }
            else if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
            }
            else if (modifier.Equals("Win", StringComparison.OrdinalIgnoreCase)
                     || modifier.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Windows;
            }
            else
            {
                return false;
            }
        }

        if (modifiers == HotkeyModifiers.None
            || !Enum.TryParse<Keys>(configuration.Key, true, out var key))
        {
            return false;
        }

        var keyCode = (int)(key & Keys.KeyCode);
        if (key == Keys.None || IsModifierKey(key)
            || (key & Keys.KeyCode) != key
            || keyCode > 0xFE)
        {
            return false;
        }

        definition = new HotkeyDefinition(modifiers, key);
        return true;
    }

    public HotkeyConfiguration ToConfiguration()
    {
        var modifiers = new List<string>();
        AddModifier(modifiers, HotkeyModifiers.Control, "Ctrl");
        AddModifier(modifiers, HotkeyModifiers.Alt, "Alt");
        AddModifier(modifiers, HotkeyModifiers.Shift, "Shift");
        AddModifier(modifiers, HotkeyModifiers.Windows, "Win");

        return new HotkeyConfiguration
        {
            Modifiers = modifiers,
            Key = Key.ToString()
        };
    }

    private static bool IsModifierKey(Keys key) =>
        key is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin;

    private void AddModifier(List<string> parts, HotkeyModifiers modifier, string displayName)
    {
        if (Modifiers.HasFlag(modifier))
        {
            parts.Add(displayName);
        }
    }
}
