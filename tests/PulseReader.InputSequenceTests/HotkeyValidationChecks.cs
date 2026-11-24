using PulseReader.Configuration;
using PulseReader.Hotkeys;

internal static class HotkeyValidationChecks
{
    public static void Run()
    {
        string[] modifierKeys =
        [
            "ShiftKey",
            "LShiftKey",
            "RShiftKey",
            "ControlKey",
            "LControlKey",
            "RControlKey",
            "Menu",
            "LMenu",
            "RMenu",
            "LWin",
            "RWin"
        ];

        foreach (var modifierKey in modifierKeys)
        {
            var configuration = new HotkeyConfiguration
            {
                Modifiers = ["Alt"],
                Key = modifierKey
            };

            if (HotkeyDefinition.TryCreate(configuration, out _))
            {
                throw new InvalidOperationException(
                    $"Modifier key {modifierKey} was accepted as the main hotkey key.");
            }
        }

        var validConfiguration = new HotkeyConfiguration { Modifiers = ["Ctrl"], Key = "F8" };
        if (!HotkeyDefinition.TryCreate(validConfiguration, out _))
        {
            throw new InvalidOperationException("A valid Ctrl + F8 hotkey was rejected.");
        }

        Console.WriteLine("PASS: modifier keys are rejected as the main hotkey key.");
    }
}
