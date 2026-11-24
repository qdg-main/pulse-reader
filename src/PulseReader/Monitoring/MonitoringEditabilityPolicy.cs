namespace PulseReader.Monitoring;

internal sealed record CachedMonitoringPatterns(bool? ValueAvailable, bool? ValueReadOnly, bool? TextAvailable)
{
    internal bool WritableValue(Func<bool> liveCheck)
    {
        if (ValueAvailable is false) return false;
        if (ValueAvailable is true && ValueReadOnly is { } readOnly) return !readOnly;
        return liveCheck();
    }
    internal bool WritableText(Func<bool> liveCheck) => TextAvailable is not false && liveCheck();

    internal static CachedMonitoringPatterns FromProperties(object? valueAvailable, object? valueReadOnly, object? textAvailable)
        => new(Boolean(valueAvailable), Boolean(valueReadOnly), Boolean(textAvailable));

    private static bool? Boolean(object? value) => value switch
    {
        bool boolean => boolean,
        int number when number is 0 or 1 => number != 0,
        _ => null
    };
}

internal static class MonitoringEditabilityPolicy
{
    internal static bool IsEditable(int controlType, bool password, bool confirmedChatDocument,
        Func<bool> writableValue, Func<bool> writableTextRange)
    {
        if (controlType == 50004 || password) return true;
        if (writableValue()) return true;
        // The chat document is a wrapper around history and the separately
        // exposed composer. Do not ask its entire TextPattern range to inspect
        // every descendant. Explicit writable controls still win above.
        if (controlType == 50030 && confirmedChatDocument) return false;
        return writableTextRange();
    }
}
