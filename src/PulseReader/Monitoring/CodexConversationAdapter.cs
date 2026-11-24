namespace PulseReader.Monitoring;

internal enum CodexTextPartKind
{
    UserBoundary, AssistantBoundary, UserText, Content, WorkingContent, FinalAnnouncement, ActiveAssistant, CollapsedAssistantHistory
}

internal sealed record CodexTextPart(CodexTextPartKind Kind, string Text, int Order = 0, string ParagraphKey = "",
    string UserMessageKey = "", string AssistantGroupKey = "", bool IsWorkingBoundary = false, string BoundaryKey = "",
    bool IsDirectAnswerHeading = false);

internal sealed class CodexConversationAdapter(string keyPrefix = "codex-message")
{
    private readonly Dictionary<string, string> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenTurns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sealedTurns = new(StringComparer.Ordinal);
    private readonly Queue<string> _turnOrder = new();
    private readonly Dictionary<string, string> _userBubbleTurns = new(StringComparer.Ordinal);
    private readonly Queue<string> _userBubbleOrder = new();
    private string _baseResponseCycleKey = "";
    private (string Group, string PrecedingFinal)? _assistantAnchor;
    private bool _usingAssistantAnchor;
    private string _confirmedHandoffKey = "";
    private int _generation;
    public bool IsRecognized { get; set; }
    public bool? IsResponseActive { get; set; } = false;
    public string ResponseCycleKey { get; private set; } = "";
    public string ViewKey { get; private set; } = "";
    public bool CurrentTurnAccepted { get; private set; }
    public bool CurrentTurnSealed => _sealedTurns.Contains(ResponseCycleKey);

    public void SetView(string key)
    {
        if (ViewKey == key) return;
        ViewKey = key;
        ResponseCycleKey = "";
        _baseResponseCycleKey = "";
        _assistantAnchor = null;
        _usingAssistantAnchor = false;
        _confirmedHandoffKey = "";
        _generation = 0;
        _completed.Clear();
        _seenTurns.Clear();
        _sealedTurns.Clear();
        _turnOrder.Clear();
        _userBubbleTurns.Clear();
        _userBubbleOrder.Clear();
    }

    public IReadOnlyList<WindowTextBlock> Assemble(
        IReadOnlyList<CodexTextPart> parts, WindowTextFingerprinter fingerprinter,
        int generationRevision = 0)
    {
        CurrentTurnAccepted = false;
        var turns = new List<List<CodexTextPart>>();
        var userMessageKey = "";
        var hasAssistantParts = false;
        foreach (var part in parts)
        {
            // Codex can virtualize the hidden "You said:" heading while
            // retaining the actual user bubble. Only recognised user text can
            // supply this fallback; unanchored assistant/history stays silent.
            if (part.Kind == CodexTextPartKind.UserBoundary
                || (part.Kind == CodexTextPartKind.UserText
                    && (turns.Count == 0 || hasAssistantParts
                        || (userMessageKey.Length > 0 && part.UserMessageKey.Length > 0
                            && userMessageKey != part.UserMessageKey))))
            {
                turns.Add([]);
                userMessageKey = "";
                hasAssistantParts = false;
            }
            if (turns.Count > 0) turns[^1].Add(part);
            if (part.Kind == CodexTextPartKind.UserText) userMessageKey = part.UserMessageKey;
            else if (part.Kind != CodexTextPartKind.UserBoundary) hasAssistantParts = true;
        }
        var latest = turns.LastOrDefault();
        var activeStatus = IsResponseActive is true
            ? parts.LastOrDefault(part => part.Kind == CodexTextPartKind.ActiveAssistant)
            : null;
        var precedingFinal = activeStatus is null ? null : parts.LastOrDefault(part =>
            part.Order < activeStatus.Order && IsFinalHeading(part, activeStatus.AssistantGroupKey));
        var activeAnchor = activeStatus is null ? ((string Group, string PrecedingFinal)?)null
            : (activeStatus.AssistantGroupKey, precedingFinal?.BoundaryKey ?? "");
        var hasLatestPrompt = latest?.Any(part => part.Kind == CodexTextPartKind.UserText
            && !string.IsNullOrWhiteSpace(part.Text)) is true;
        // A final followed by a live timer within the same prompt's turn is a
        // new continuation, even if that old user bubble is still mounted.
        var continuesAfterFinal = activeStatus is not null && precedingFinal is not null
            && latest?.Contains(activeStatus) is true && latest.Contains(precedingFinal);
        if (!hasLatestPrompt || continuesAfterFinal || (_usingAssistantAnchor && _assistantAnchor is { } anchored
            && latest!.Any(part => part.AssistantGroupKey == anchored.Group
                && part.Kind is CodexTextPartKind.AssistantBoundary or CodexTextPartKind.ActiveAssistant)))
            return AssembleAssistantResponse();

        // Only the current turn is eligible for automatic speech. History can
        // be virtualized and recreated with entirely new UIA runtime IDs.
        // Explicitly selected historical text still uses manual playback.
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var observedKeys = new List<string>();
        string key = "";
        foreach (var turn in turns)
        {
            var userText = string.Join('\n', turn.Where(part => part.Kind == CodexTextPartKind.UserText)
                .Select(part => part.Text));
            if (string.IsNullOrWhiteSpace(userText)) { key = ""; continue; }
            var hash = fingerprinter.Create(userText).Hash;
            occurrences.TryGetValue(hash, out var occurrence);
            occurrences[hash] = ++occurrence;
            key = $"{keyPrefix}:{hash}:{occurrence}";
            // A bubble can lose mounted text fragments during scrolling. Its
            // runtime identity is a useful alias, not the persistent key:
            // complete redraws can also replace that runtime identity.
            var bubble = turn.FirstOrDefault(part => part.Kind == CodexTextPartKind.UserText
                && part.UserMessageKey.Length > 0)?.UserMessageKey ?? "";
            if (bubble.Length > 0)
            {
                var knownBubble = _userBubbleTurns.TryGetValue(bubble, out var remembered);
                if (knownBubble) key = remembered!;
                // Separately mounted identical prompts can initially share a
                // key, just as a complete redraw can retain the same turn.
                // Seeing both bubbles together proves they are distinct, so
                // repair remembered aliases as well as newly assigned ones.
                if (observedKeys.Contains(key))
                {
                    key = $"{keyPrefix}:{hash}:{occurrence}";
                    while (observedKeys.Contains(key)) key = $"{keyPrefix}:{hash}:{++occurrence}";
                }
                occurrences[hash] = occurrence;
                _userBubbleTurns[bubble] = key;
                if (!knownBubble)
                {
                    _userBubbleOrder.Enqueue(bubble);
                }
            }
            observedKeys.Add(key);
        }
        while (_userBubbleOrder.Count > 4096)
            _userBubbleTurns.Remove(_userBubbleOrder.Dequeue());
        if (key.Length == 0) return [];
        if (_baseResponseCycleKey.Length > 0 && key != _baseResponseCycleKey && _seenTurns.Contains(key))
            return []; // A partial redraw or scroll must not move the current turn backwards.
        foreach (var observedKey in observedKeys) RememberTurn(observedKey);
        if (key != _baseResponseCycleKey) _generation = Math.Max(0, generationRevision);
        else if (generationRevision > _generation)
        {
            _sealedTurns.Remove(ResponseCycleKey);
            _completed.Remove(ResponseCycleKey + ":answer");
            _generation = generationRevision;
        }
        if (key != _baseResponseCycleKey) _assistantAnchor = null;
        _baseResponseCycleKey = key;
        if (_generation > 0) key += $":generation:{_generation}";
        ResponseCycleKey = key;
        _usingAssistantAnchor = false;
        if (activeStatus is not null && latest!.Contains(activeStatus)) _assistantAnchor = activeAnchor;
        // The composer can still look busy while Codex streams an answer.
        // Its heading is a direct child of the scoped response container;
        // working headings are nested. The history toggle may not be mounted.
        var latestHeading = latest!.FindLastIndex(part => part.Kind == CodexTextPartKind.AssistantBoundary);
        var hasStructuralFinal = latestHeading >= 0
            && (latest[latestHeading].IsDirectAnswerHeading
                || (latestHeading > 0 && latest[latestHeading - 1].Kind == CodexTextPartKind.CollapsedAssistantHistory
                    && IsFinalHeading(latest[latestHeading], latest[latestHeading - 1].AssistantGroupKey)))
            && !latest.Skip(latestHeading + 1).Any(part => part.Kind == CodexTextPartKind.WorkingContent);
        return BuildResponse(latest, IsResponseActive is not false && !hasStructuralFinal);

        IReadOnlyList<WindowTextBlock> AssembleAssistantResponse()
        {
            if (activeAnchor is { } liveAnchor)
            {
                if (liveAnchor != _assistantAnchor)
                {
                    // A goal can finish a reply and start working on the next
                    // before the same accessibility frame is read. Give that
                    // completed reply to the existing cycle before opening a
                    // new one, which would clear its pending final.
                    if (_assistantAnchor is { } previous && previous.Group == liveAnchor.Group
                        && precedingFinal is not null && _confirmedHandoffKey != ResponseCycleKey
                        && FindFinal(previous) == precedingFinal)
                    {
                        var completed = BuildFinal(precedingFinal, confirmedHandoff: true);
                        if (completed.Count > 0)
                        {
                            _confirmedHandoffKey = ResponseCycleKey;
                            return completed;
                        }
                    }
                    var keyText = $"{liveAnchor.Group}\n{liveAnchor.PrecedingFinal}";
                    var assistantKey = $"{keyPrefix}:assistant:{fingerprinter.Create(keyText).Hash}";
                    if (_seenTurns.Contains(assistantKey)) return [];
                    RememberTurn(assistantKey);
                    _assistantAnchor = liveAnchor;
                    _baseResponseCycleKey = ResponseCycleKey = assistantKey;
                    _generation = 0;
                }
                _usingAssistantAnchor = true;
                // Codex can put many completed replies and the active update
                // in one outer group. Only content after its live timer belongs
                // to the new response.
                // The timer can remain mounted while the final starts streaming.
                // A direct assistant heading after it identifies the final;
                // the final gate still waits for the answer to settle.
                var liveFinal = parts.FirstOrDefault(part =>
                    part.Order > activeStatus!.Order && IsFinalHeading(part, liveAnchor.Group));
                if (liveFinal is not null) return BuildFinal(liveFinal, confirmedHandoff: false);
                return BuildResponse(parts.Where(part => part.AssistantGroupKey == liveAnchor.Group
                    && part.Order > activeStatus!.Order).ToList(), active: true);
            }

            if (_assistantAnchor is not { } anchor || FindFinal(anchor) is not { } final) return [];
            _usingAssistantAnchor = true;
            return BuildFinal(final, confirmedHandoff: false);
        }

        CodexTextPart? FindFinal((string Group, string PrecedingFinal) anchor)
        {
            var priorOrder = -1;
            if (anchor.PrecedingFinal.Length > 0)
            {
                var prior = parts.FirstOrDefault(part => IsFinalHeading(part, anchor.Group)
                    && part.BoundaryKey == anchor.PrecedingFinal);
                if (prior is null) return null;
                priorOrder = prior.Order;
            }
            // A finished reply needs a direct assistant heading after the
            // preceding final. Expanded working headings cannot finish a turn.
            return parts.FirstOrDefault(part => part.Order > priorOrder && IsFinalHeading(part, anchor.Group));
        }

        IReadOnlyList<WindowTextBlock> BuildFinal(CodexTextPart final, bool confirmedHandoff) =>
            BuildResponse(parts.SkipWhile(part => !ReferenceEquals(part, final))
                .TakeWhile(part => part.AssistantGroupKey == final.AssistantGroupKey && (ReferenceEquals(part, final)
                    || part.Kind is not (CodexTextPartKind.AssistantBoundary or CodexTextPartKind.ActiveAssistant)))
                .ToList(), active: false, confirmedHandoff: confirmedHandoff);

        static bool IsFinalHeading(CodexTextPart part, string group) =>
            part.Kind == CodexTextPartKind.AssistantBoundary && part.AssistantGroupKey == group
            && part.BoundaryKey.Length > 0 && !part.IsWorkingBoundary;

        IReadOnlyList<WindowTextBlock> BuildResponse(List<CodexTextPart> reply, bool active, bool confirmedHandoff = false)
        {
            CurrentTurnAccepted = true;
            var responseKey = ResponseCycleKey;
            active &= !_sealedTurns.Contains(responseKey);
            var finalBoundary = reply.FindLastIndex(part => part.Kind == CodexTextPartKind.AssistantBoundary);
            var workingParts = active || finalBoundary < 0 ? reply : reply.Take(finalBoundary).ToList();
            var answerParts = active || finalBoundary < 0 ? [] : reply.Skip(finalBoundary + 1).ToList();
            if (answerParts.Any(part => part.Kind == CodexTextPartKind.Content)) _sealedTurns.Add(responseKey);
            var result = new List<WindowTextBlock>();
            AddBlock(workingParts, responseKey + ":working", false, result);
            AddBlock(answerParts, responseKey + ":answer", true, result, confirmedHandoff);
            while (_completed.Count > 2048) _completed.Remove(_completed.Keys.First());
            return result;
        }

        void AddBlock(IReadOnlyList<CodexTextPart> source, string blockKey, bool answer,
            List<WindowTextBlock> result, bool confirmedHandoff = false)
        {
            var content = source.Where(part => part.Kind == CodexTextPartKind.Content
                || (!answer && part.Kind == CodexTextPartKind.WorkingContent)).ToArray();
            if (content.Length == 0) return;
            var paragraphs = new List<string>();
            string? previousParagraph = null;
            foreach (var part in content)
            {
                if (part.ParagraphKey.Length > 0 && part.ParagraphKey == previousParagraph)
                    paragraphs[^1] = WindowTextChangePolicy.Join(paragraphs[^1], part.Text);
                else paragraphs.Add(part.Text);
                previousParagraph = part.ParagraphKey;
            }
            var text = string.Join('\n', paragraphs);
            var textHash = fingerprinter.Create(text).Hash;
            var completed = answer && (source.Any(part => part.Kind == CodexTextPartKind.FinalAnnouncement)
                || (_completed.TryGetValue(blockKey, out var previousHash) && previousHash == textHash));
            if (completed) _completed[blockKey] = textHash;
            result.Add(new WindowTextBlock(blockKey, text, blockKey, content[0].Order,
                GroupKey: blockKey, IsAggregate: true, IsCompletedAggregate: completed, IsFinalResponse: answer,
                IsConfirmedFinalHandoff: confirmedHandoff));
        }
    }

    private void RememberTurn(string key)
    {
        if (_seenTurns.Add(key)) _turnOrder.Enqueue(key);
        while (_turnOrder.Count > 4096)
        {
            var expired = _turnOrder.Dequeue();
            _seenTurns.Remove(expired);
            _sealedTurns.RemoveWhere(sealedKey => sealedKey == expired
                || sealedKey.StartsWith(expired + ":generation:", StringComparison.Ordinal));
        }
    }
}
