using PulseReader.Monitoring;

internal static class MonitoringDocumentEditabilityChecks
{
    internal static void Run()
    {
        CheckCachedPatterns();
        var rangeCalls = 0;
        bool ExpensiveRange() { rangeCalls++; return false; }
        if (MonitoringEditabilityPolicy.IsEditable(50030, false, true, () => false, ExpensiveRange) || rangeCalls != 0)
            throw new InvalidOperationException("Confirmed chat document queried its entire text range for editability.");
        if (!MonitoringEditabilityPolicy.IsEditable(50030, false, true, () => true,
                () => throw new InvalidOperationException("Writable ValuePattern should short-circuit.")))
            throw new InvalidOperationException("Explicitly writable chat document was not excluded.");
        foreach (var type in new[] { 50004, 50026 })
            if (!MonitoringEditabilityPolicy.IsEditable(type, type != 50004, true,
                    () => throw new InvalidOperationException("Edit/password controls should short-circuit."), ExpensiveRange))
                throw new InvalidOperationException("Editable/password control was not excluded.");
        if (!MonitoringEditabilityPolicy.IsEditable(50030, false, false, () => false, () => true)
            || !MonitoringEditabilityPolicy.IsEditable(50026, false, true, () => false, () => true)
            || MonitoringEditabilityPolicy.IsEditable(50030, false, false, () => false, () => false))
            throw new InvalidOperationException("Generic document or nested-editor editability changed.");
        Console.WriteLine("PASS: cached pattern support avoids unnecessary live lookups; confirmed chat wrappers avoid whole-range scans while editor/password/generic document checks remain active.");
    }

    private static void CheckCachedPatterns()
    {
        bool MustNotQuery() => throw new InvalidOperationException("Cached pattern metadata still requested the live pattern.");
        var unavailable = new CachedMonitoringPatterns(false, false, false);
        if (unavailable.WritableValue(MustNotQuery) || unavailable.WritableText(MustNotQuery))
            throw new InvalidOperationException("Unsupported cached patterns were treated as writable.");
        var writable = new CachedMonitoringPatterns(true, false, true);
        if (!writable.WritableValue(MustNotQuery)) throw new InvalidOperationException("Cached writable control was not excluded.");
        var readOnly = new CachedMonitoringPatterns(true, true, null);
        if (readOnly.WritableValue(MustNotQuery)) throw new InvalidOperationException("Cached read-only value was treated as writable.");
        var calls = 0;
        bool LiveWritable() { calls++; return true; }
        if (!new CachedMonitoringPatterns(null, false, null).WritableValue(LiveWritable)
            || !new CachedMonitoringPatterns(true, null, null).WritableValue(LiveWritable)
            || !writable.WritableText(LiveWritable) || calls != 3)
            throw new InvalidOperationException("Unknown cached metadata or available text patterns did not retain live checks.");
        var unsupported = CachedMonitoringPatterns.FromProperties(new object(), new object(), new object());
        if (unsupported.ValueAvailable is not null || unsupported.ValueReadOnly is not null || unsupported.TextAvailable is not null)
            throw new InvalidOperationException("Unsupported UIA property values were interpreted as false.");
        var native = CachedMonitoringPatterns.FromProperties(1, 0, false);
        if (!native.WritableValue(MustNotQuery) || native.WritableText(MustNotQuery))
            throw new InvalidOperationException("Native Boolean cache values were misinterpreted.");
    }
}
