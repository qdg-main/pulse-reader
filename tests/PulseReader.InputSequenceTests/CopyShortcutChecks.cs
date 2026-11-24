using PulseReader.Selection;

internal static class CopyShortcutChecks
{
    private static readonly KeyboardKey[] Modifiers = [KeyboardKey.LeftShift, KeyboardKey.RightShift,
        KeyboardKey.LeftControl, KeyboardKey.RightControl, KeyboardKey.LeftAlt, KeyboardKey.RightAlt,
        KeyboardKey.LeftWindows, KeyboardKey.RightWindows];

    public static void Run()
    {
        var scenarios = 0;
        for (var mask = 0; mask < 1 << Modifiers.Length; mask++)
        {
            var sampled = Modifiers.Where((_, i) => (mask & (1 << i)) != 0).ToHashSet();
            var sequence = NativeInput.BuildCopyShortcutSequence(sampled);
            // Every sampled key may still be physically held, or have been
            // released since the snapshot. Never inject actual desktop input.
            for (var released = mask; ; released = (released - 1) & mask)
            {
                var logical = sampled.Where(k => (released & (1 << Array.IndexOf(Modifiers, k))) == 0).ToHashSet();
                foreach (var step in sequence)
                {
                    if (step.Key is KeyboardKey.LeftAlt or KeyboardKey.RightAlt
                        or KeyboardKey.LeftWindows or KeyboardKey.RightWindows && step.IsKeyUp)
                        Assert(logical.Contains(KeyboardKey.LeftControl) || logical.Contains(KeyboardKey.RightControl),
                            "Copy released an access-key modifier without a Control guard.");
                    if (step.Key == KeyboardKey.C && !step.IsKeyUp)
                        Assert(logical.SetEquals([KeyboardKey.LeftControl]), "Copy included an unintended modifier.");
                    Apply(logical, step);
                }
                Assert(logical.Count == 0,
                    "Copy left keys logically held after capture: " + string.Join(", ", logical));
                scenarios++;
                if (released == 0) break;
            }
        }
        Console.WriteLine($"PASS: {scenarios} modifier/release-timing combinations send clean Ctrl+C and finish released.");
        CheckPartialInjection();
        CheckCleanupRetry();
    }

    private static void CheckPartialInjection()
    {
        var cases = 0;
        for (var mask = 0; mask < 1 << Modifiers.Length; mask++)
        {
            var sampled = Modifiers.Where((_, i) => (mask & (1 << i)) != 0).ToHashSet();
            var original = NativeInput.BuildCopyShortcutSequence(sampled);
            for (var cut = 0; cut <= original.Count; cut++)
            {
                var calls = 0;
                var logical = new HashSet<KeyboardKey>(sampled) { (KeyboardKey)0x42 }; // unrelated B
                var injectedDown = new HashSet<KeyboardKey>();
                var success = NativeInput.SendCopyShortcut(sampled, steps =>
                {
                    var first = calls++ == 0;
                    var count = first ? cut : steps.Count;
                    if (!first) Assert(steps.All(s => s.IsKeyUp && injectedDown.Contains(s.Key)),
                        "Cleanup touched keys not pressed by the copy sequence.");
                    foreach (var step in steps.Take(count))
                    {
                        if (first && !step.IsKeyUp) injectedDown.Add(step.Key);
                        Apply(logical, step);
                    }
                    return (uint)count;
                });
                Assert(success == (cut == original.Count), "Incomplete copy input was reported as success.");
                Assert(!injectedDown.Any(logical.Contains), "A partial copy left an injected key down.");
                Assert(logical.Contains((KeyboardKey)0x42), "Cleanup released an unrelated key.");
                if (cut == 0 || cut == original.Count) Assert(calls == 1, "A blocked or complete copy caused unnecessary input.");
                cases++;
            }
        }
        Console.WriteLine($"PASS: {cases} input-prefix outcomes clean up only injected keys and preserve copy failures.");
    }

    private static void Apply(HashSet<KeyboardKey> logical, KeyboardInputStep step)
    {
        if (step.IsKeyUp) logical.Remove(step.Key);
        else logical.Add(step.Key);
    }

    private static void CheckCleanupRetry()
    {
        foreach (var blockedForever in new[] { false, true })
        {
            var logical = new HashSet<KeyboardKey>();
            var calls = 0;
            var success = NativeInput.SendCopyShortcut(new HashSet<KeyboardKey>(), steps =>
            {
                calls++;
                var count = calls == 1 ? 2 : blockedForever || calls == 2 ? 0 : 1;
                if (calls > 1) Assert(steps.All(s => s.IsKeyUp), "Cleanup reissued copy key-downs.");
                foreach (var step in steps.Take(count)) Apply(logical, step);
                return (uint)count;
            });
            Assert(!success && calls == 4, "Cleanup did not remain bounded or concealed a failed copy.");
            if (!blockedForever) Assert(logical.Count == 0, "Partially accepted cleanup left injected keys down.");
        }
        Console.WriteLine("PASS: blocked/partial cleanup is bounded, key-up-only, and never turns a failed copy into success.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
