namespace PulseReader.Monitoring;

internal sealed record ConversationContext(string ViewKey);
internal enum ConversationContextAction { Continue, Hold, Baseline }

// Unknown views remain silent. A changed view or thread must settle before
// its history is baselined.
internal sealed class ConversationContextGuard
{
    private string? _active;
    private string? _candidate;
    private DateTimeOffset _candidateSince;
    private bool _recognized;
    public bool IsSuspended { get; private set; }
    public void EstablishBaseline(ConversationContext? context)
    {
        _recognized = context is not null;
        _active = context?.ViewKey;
        IsSuspended = _recognized && string.IsNullOrEmpty(_active);
    }
    public ConversationContextAction Observe(ConversationContext? context, DateTimeOffset now)
    {
        if (!_recognized && context is null) return ConversationContextAction.Continue;
        _recognized = true;
        var key = context?.ViewKey ?? "";
        if (!IsSuspended && key.Length > 0 && key == _active)
            return ConversationContextAction.Continue;
        IsSuspended = true;
        if (key.Length == 0)
        {
            _candidate = null;
            return ConversationContextAction.Hold;
        }
        if (key != _candidate)
        {
            _candidate = key;
            _candidateSince = now;
            return ConversationContextAction.Hold;
        }
        if (now - _candidateSince < TimeSpan.FromSeconds(1)) return ConversationContextAction.Hold;
        _active = key;
        _candidate = null;
        IsSuspended = false;
        return ConversationContextAction.Baseline;
    }
}
