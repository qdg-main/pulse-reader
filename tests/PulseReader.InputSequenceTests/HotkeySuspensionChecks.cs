using System.ComponentModel;
using PulseReader.Hotkeys;
using System.Windows.Forms;

internal static class HotkeySuspensionChecks
{
    public static void Run()
    {
        using var owner = new GlobalHotkeyManager();
        var contender = new GlobalHotkeyManager();
        HotkeyDefinition? hotkey = null;

        try
        {
            foreach (var key in Enumerable.Range((int)Keys.F13, 12).Reverse().Select(value => (Keys)value))
            {
                var candidate = new HotkeyDefinition(
                    HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
                    key);
                try
                {
                    owner.Register(candidate);
                    hotkey = candidate;
                    break;
                }
                catch (Win32Exception)
                {
                    // Try another uncommon function key if this one is already registered.
                }
            }

            Assert(hotkey is not null, "No isolated hotkey was available for the suspension check.");
            var selectedHotkey = hotkey
                ?? throw new InvalidOperationException("No isolated hotkey was available.");

            var registrationBlocked = false;
            try
            {
                contender.Register(selectedHotkey);
            }
            catch (Win32Exception)
            {
                registrationBlocked = true;
            }

            Assert(registrationBlocked, "The test hotkey was not registered before suspension.");
            Assert(owner.TrySuspend(out var suspendError), suspendError ?? "Hotkey suspension failed.");

            contender.Register(selectedHotkey);
            contender.Dispose();

            Assert(owner.TryResume(out var resumeError), resumeError ?? "Hotkey restoration failed.");
        }
        finally
        {
            contender.Dispose();
        }

        Console.WriteLine("PASS: global hotkey registration is released during shortcut capture and restored afterwards.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
