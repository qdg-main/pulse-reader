using PulseReader.Monitoring;

internal static class EditableDuplicateChecks
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 19, 38, 42, TimeSpan.Zero);

    public static void Run()
    {
        CheckDuplicateFocusedObservations();
        CheckDuplicateSnapshotObservations();
        CheckDraftChangesAndSubmission();
        CheckIndependentEditableValues();
        CheckEmptyEditableValues();
        CheckCandidateExpiration();
        Console.WriteLine("PASS: duplicate editable observations preserve monitoring, submission echoes, draft changes, and empty-field behavior.");
    }

    private static void CheckDuplicateFocusedObservations()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        const string prompt = "Please explain why the monitor stopped reading.";
        var fingerprint = fingerprinter.Create(prompt);
        policy.EstablishBaseline(Snapshot(fingerprint, fingerprint), Now);

        // The capture worker reads focused editables separately from the snapshot.
        policy.ObserveEditableFingerprints([fingerprint, fingerprinter.Create(prompt.ToUpperInvariant())], Now.AddMilliseconds(10));
        policy.Process(Snapshot(fingerprint, fingerprint), [], Now.AddMilliseconds(20));
        Assert(!policy.ResponseCycleStarted, "Repeated focused observations were mistaken for a submission.");

        policy.ObserveEditableFingerprints([fingerprint], Now.AddMilliseconds(30));
        AssertTexts(policy.Process(Snapshot(fingerprint), [Change(prompt)], Now.AddMilliseconds(40)), prompt);
        Assert(!policy.ResponseCycleStarted, "Removing one duplicate submitted an editable value that was still present.");

        policy.ObserveEditableFingerprints([], Now.AddMilliseconds(50));
        AssertTexts(policy.Process(Snapshot(), [Change(prompt), Change("The assistant response remains readable.")], Now.AddMilliseconds(60)),
            "The assistant response remains readable.");
        Assert(policy.ResponseCycleStarted, "Clearing duplicate focused draft observations lost the submission.");
        AssertTexts(policy.Process(Snapshot(), [Change(prompt)], Now.AddMilliseconds(70)), prompt);
        Assert(!policy.ResponseCycleStarted, "An already consumed prompt echo suppressed later real content.");
    }

    private static void CheckDuplicateSnapshotObservations()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        const string prompt = "Please describe this new monitoring response.";
        var fingerprint = fingerprinter.Create(prompt);
        policy.EstablishBaseline(Snapshot(), Now);
        AssertTexts(policy.Process(Snapshot(fingerprint, fingerprint), [Change("A separate visible message remains readable.")], Now),
            "A separate visible message remains readable.");
        Assert(!policy.ResponseCycleStarted, "Adding duplicate draft observations started a response cycle.");
        AssertTexts(policy.Process(Snapshot(), [Change(prompt), Change("The requested answer arrives normally.")], Now.AddSeconds(1)),
            "The requested answer arrives normally.");
        Assert(policy.ResponseCycleStarted, "Duplicate snapshot observations lost submitted-echo detection.");
    }

    private static void CheckDraftChangesAndSubmission()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        var original = fingerprinter.Create("Please summarize this monitoring behavior.");
        const string editedPrompt = "Please explain this monitoring behavior in detail.";
        var edited = fingerprinter.Create(editedPrompt);
        policy.EstablishBaseline(Snapshot(original, original), Now);
        policy.ObserveEditableFingerprints([edited, edited], Now.AddMilliseconds(10));
        policy.Process(Snapshot(edited, edited), [], Now.AddMilliseconds(20));
        Assert(!policy.ResponseCycleStarted, "Editing duplicate draft observations was mistaken for a submission.");
        policy.ObserveEditableFingerprints([], Now.AddMilliseconds(30));
        AssertTexts(policy.Process(Snapshot(),
            [Change("Please explain this monitoring behavior"), Change("in detail."), Change("The edited prompt has a readable answer.")],
            Now.AddMilliseconds(40)), "The edited prompt has a readable answer.");
        Assert(policy.ResponseCycleStarted, "Editing then clearing duplicate observations lost the split prompt echo.");
    }

    private static void CheckIndependentEditableValues()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        const string prompt = "Please answer the first editable prompt.";
        var first = fingerprinter.Create(prompt);
        var second = fingerprinter.Create("A separate editable value remains present.");
        policy.EstablishBaseline(Snapshot(first, first, second, second), Now);
        policy.ObserveEditableFingerprints([second, second], Now.AddMilliseconds(10));
        AssertTexts(policy.Process(Snapshot(second, second), [], Now.AddMilliseconds(20)));
        Assert(!policy.ResponseCycleStarted, "One vanished editable value reset the cycle while another remained.");
        AssertTexts(policy.Process(Snapshot(second, second), [Change(prompt), Change("The assistant answer still reaches the reader.")],
            Now.AddMilliseconds(30)), "The assistant answer still reaches the reader.");
        Assert(policy.ResponseCycleStarted, "A submitted echo was not detected while another editable value remained.");
    }

    private static void CheckEmptyEditableValues()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        var empty = fingerprinter.Create("");
        var whitespace = fingerprinter.Create(" \t\r\n ");
        policy.EstablishBaseline(Snapshot(empty, whitespace), Now);
        policy.ObserveEditableFingerprints([empty, whitespace], Now.AddMilliseconds(10));
        AssertTexts(policy.Process(Snapshot(empty, whitespace), [Change("Empty fields leave real responses readable.")], Now.AddMilliseconds(20)),
            "Empty fields leave real responses readable.");
        Assert(!policy.ResponseCycleStarted, "Duplicate empty editable observations started a response cycle.");
        policy.ObserveEditableFingerprints([], Now.AddMilliseconds(30));
        policy.Process(Snapshot(), [], Now.AddMilliseconds(40));
        Assert(!policy.ResponseCycleStarted, "Clearing empty fields was mistaken for a submission.");

        const string prompt = "A real draft clears beside empty fields.";
        var draft = fingerprinter.Create(prompt);
        policy.ObserveEditableFingerprints([draft, draft, empty, whitespace], Now.AddMilliseconds(50));
        policy.ObserveEditableFingerprints([empty, whitespace], Now.AddMilliseconds(60));
        policy.Process(Snapshot(empty, whitespace), [], Now.AddMilliseconds(70));
        Assert(!policy.ResponseCycleStarted, "Empty fields changed the existing partial-clear submission behavior.");
        AssertTexts(policy.Process(Snapshot(empty, whitespace), [Change(prompt)], Now.AddMilliseconds(80)));
        Assert(policy.ResponseCycleStarted, "Empty duplicates prevented a real submitted echo from being suppressed.");
    }

    private static void CheckCandidateExpiration()
    {
        using var fingerprinter = new WindowTextFingerprinter();
        var policy = new WindowTextChangePolicy(fingerprinter);
        const string prompt = "The expired prompt becomes readable again.";
        var fingerprint = fingerprinter.Create(prompt);
        policy.EstablishBaseline(Snapshot(fingerprint, fingerprint), Now);
        policy.ObserveEditableFingerprints([], Now);
        policy.Process(Snapshot(), [], Now);
        AssertTexts(policy.Process(Snapshot(), [Change(prompt)], Now + SubmittedTextEchoFilter.CandidateLifetime), prompt);
        Assert(!policy.ResponseCycleStarted, "An expired duplicate submission candidate started another response cycle.");
    }

    private static WindowTextSnapshot Snapshot(params WindowTextFingerprint[] fingerprints) =>
        new([], EditableFingerprints: fingerprints);

    private static WindowTextChange Change(string text) => new(text, true, true);

    private static void AssertTexts(IReadOnlyList<WindowTextChange> changes, params string[] expected) =>
        Assert(changes.Select(change => change.Text).SequenceEqual(expected),
            $"Editable duplicate check expected {expected.Length} readable changes, received {changes.Count}.");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
