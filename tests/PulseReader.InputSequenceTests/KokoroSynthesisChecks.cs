using KokoroSharp;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using PulseReader.Speech;
using PulseReader.TextPreparation;
using System.Reflection;

internal static class KokoroSynthesisChecks
{
    public static async Task RunAsync()
    {
        var modelStore = new KokoroModelStore();
        var modelPath = await modelStore.GetModelPathAsync(CancellationToken.None);
        var blendSettings = new KokoroSpeechSettings(
            KokoroSpeechSettings.DefaultVoice,
            KokoroSpeechSettings.DefaultSpeed,
            KokoroSpeechSettings.DefaultVolume,
            true,
            KokoroSpeechSettings.DefaultSecondaryVoice,
            65);
        var voice = KokoroSpeechService.CreateVoice(blendSettings);

        byte[] audio;
        using (var synthesizer = new KokoroWavSynthesizer(modelPath))
        {
            audio = await synthesizer.SynthesizeAsync(
                "Pulse Reader is speaking with a blended Kokoro voice.",
                voice);
        }

        if (audio.Length < 48000)
        {
            throw new InvalidOperationException(
                $"Kokoro returned only {audio.Length} bytes of audio.");
        }

        using (var engine = KokoroTTS.LoadModel(modelPath))
        using (var session = new KokoroPlaybackSession(
                   engine,
                   KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice),
                   [
                       new KokoroPlaybackSegment(
                           Tokenizer.Tokenize("Pulse Reader prepares the next segment."),
                           0f),
                       new KokoroPlaybackSegment(
                           Tokenizer.Tokenize(
                               "The queued segment follows without restarting the model."),
                           0f)
                   ],
                   KokoroSpeechService.CreatePipelineConfig(KokoroSpeechSettings.DefaultSpeed),
                   0f))
        {
            var segmentedPlaybackCompleted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            session.StateChanged += (_, _) =>
            {
                if (session.State == SpeechPlaybackState.Idle)
                {
                    segmentedPlaybackCompleted.TrySetResult();
                }
            };
            session.Start();
            await segmentedPlaybackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        using (var engine = KokoroTTS.LoadModel(modelPath))
        using (var session = new KokoroPlaybackSession(
                   engine,
                   KokoroVoiceManager.GetVoice(KokoroSpeechSettings.DefaultVoice),
                   [
                       new KokoroPlaybackSegment(
                           Tokenizer.Tokenize("The unaffected prefix starts immediately."),
                           0f)
                   ],
                   KokoroSpeechService.CreatePipelineConfig(KokoroSpeechSettings.DefaultSpeed),
                   0f,
                   segmentsComplete: false))
        {
            var prefixStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var progressivePlaybackCompleted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            session.StateChanged += (_, _) =>
            {
                if (session.State == SpeechPlaybackState.Speaking)
                {
                    prefixStarted.TrySetResult();
                }
                else if (session.State == SpeechPlaybackState.Idle)
                {
                    progressivePlaybackCompleted.TrySetResult();
                }
            };
            session.Start();
            await prefixStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (session.State == SpeechPlaybackState.Idle)
            {
                throw new InvalidOperationException(
                    "Progressive playback ended while it was waiting for a contextual tail.");
            }

            if (!session.TryAppendSegments(
                    [
                        new KokoroPlaybackSegment(
                            Tokenizer.Tokenize("The resolved contextual tail follows."),
                            0f)
                    ],
                    markComplete: true))
            {
                throw new InvalidOperationException(
                    "Kokoro rejected the resolved tail of a progressive playback session.");
            }

            await progressivePlaybackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var delayedContextual = new DelayedContextualProcessor();
        var progressiveSettings = blendSettings with { ContextualPronunciationEnabled = true };
        using (var progressiveSpeech = new KokoroSpeechService(
                   modelStore,
                   progressiveSettings,
                   delayedContextual))
        {
            var prefixSpeaking = WaitForNextState(
                progressiveSpeech,
                SpeechPlaybackState.Speaking);
            var progressiveCompleted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            progressiveSpeech.SpeakingStateChanged += (_, _) =>
            {
                if (progressiveSpeech.State == SpeechPlaybackState.Idle)
                {
                    progressiveCompleted.TrySetResult();
                }
            };

            var progressiveStart = progressiveSpeech.TrySpeakAsync(
                "The unaffected opening sentence should begin playing immediately. "
                + "The old pipe contains lead and needs contextual pronunciation.");
            await delayedContextual.Started.WaitAsync(TimeSpan.FromSeconds(10));
            var progressiveResult = await progressiveStart.WaitAsync(TimeSpan.FromSeconds(30));
            if (!progressiveResult.Started || delayedContextual.IsReleased)
            {
                throw new InvalidOperationException(
                    "Kokoro waited for unresolved contextual pronunciation before starting its unaffected prefix.");
            }

            await prefixSpeaking.WaitAsync(TimeSpan.FromSeconds(30));
            if (progressiveSpeech.NavigationState.CanMoveNext)
            {
                throw new InvalidOperationException(
                    "Progressive playback exposed an unresolved contextual reading unit to navigation.");
            }

            var restartedPrefix = WaitForNextState(
                progressiveSpeech,
                SpeechPlaybackState.Speaking);
            if (!progressiveSpeech.TryRestartCurrentUnit())
            {
                throw new InvalidOperationException(
                    "Progressive playback could not restart its resolved prefix while classification was pending.");
            }

            await restartedPrefix.WaitAsync(TimeSpan.FromSeconds(30));
            delayedContextual.Release();
            await progressiveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var unitwiseContextual = new UnitwiseContextualProcessor();
        using (var unitwiseSpeech = new KokoroSpeechService(
                   modelStore,
                   progressiveSettings,
                   unitwiseContextual))
        {
            var prefixSpeaking = WaitForNextState(unitwiseSpeech, SpeechPlaybackState.Speaking);
            var completed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            unitwiseSpeech.SpeakingStateChanged += (_, _) =>
            {
                if (unitwiseSpeech.State == SpeechPlaybackState.Idle)
                {
                    completed.TrySetResult();
                }
            };

            var start = unitwiseSpeech.TrySpeakAsync(
                "The unaffected opening begins while lookahead runs. "
                + "The old pipe contains lead. "
                + "This resolved middle sentence can follow immediately. "
                + "The presenter will go live in five minutes.");
            await unitwiseContextual.Started.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await start.WaitAsync(TimeSpan.FromSeconds(30));
            if (!result.Started)
            {
                throw new InvalidOperationException(
                    result.Error ?? "Unitwise progressive playback did not start.");
            }

            await prefixSpeaking.WaitAsync(TimeSpan.FromSeconds(30));
            var initialUnitCount = unitwiseSpeech.NavigationState.UnitCount;
            unitwiseContextual.ReleaseFirstUnit();
            await unitwiseContextual.SecondUnitStarted.WaitAsync(TimeSpan.FromSeconds(10));
            var unitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (unitwiseSpeech.NavigationState.UnitCount <= initialUnitCount
                   && DateTime.UtcNow < unitDeadline)
            {
                await Task.Delay(20);
            }

            if (unitwiseSpeech.NavigationState.UnitCount <= initialUnitCount)
            {
                throw new InvalidOperationException(
                    "The first resolved contextual unit was not released to playback while the later unit remained pending.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            if (unitwiseSpeech.State == SpeechPlaybackState.Idle)
            {
                throw new InvalidOperationException(
                    "Unitwise progressive playback ended while a later contextual unit was pending.");
            }

            unitwiseContextual.ReleaseSecondUnit();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var canceledContextual = new DelayedContextualProcessor();
        using (var canceledProgressiveSpeech = new KokoroSpeechService(
                   modelStore,
                   progressiveSettings,
                   canceledContextual))
        {
            var prefixSpeaking = WaitForNextState(
                canceledProgressiveSpeech,
                SpeechPlaybackState.Speaking);
            var progressiveStart = canceledProgressiveSpeech.TrySpeakAsync(
                "The resolved opening should start before cancellation. "
                + "The old pipe contains lead and should never be appended.");
            await canceledContextual.Started.WaitAsync(TimeSpan.FromSeconds(10));
            var progressiveResult = await progressiveStart.WaitAsync(TimeSpan.FromSeconds(30));
            if (!progressiveResult.Started)
            {
                throw new InvalidOperationException(
                    progressiveResult.Error ?? "Cancelable progressive playback did not start.");
            }

            await prefixSpeaking.WaitAsync(TimeSpan.FromSeconds(30));
            canceledProgressiveSpeech.Stop();
            await canceledContextual.Canceled.WaitAsync(TimeSpan.FromSeconds(10));
            if (canceledProgressiveSpeech.State != SpeechPlaybackState.Idle)
            {
                throw new InvalidOperationException(
                    "Stopping progressive playback did not clear its speaking state.");
            }
        }

        using (var failedProgressiveSpeech = new KokoroSpeechService(
                   modelStore,
                   progressiveSettings,
                   new FailingContextualProcessor()))
        {
            var failedProgressiveCompleted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            failedProgressiveSpeech.SpeakingStateChanged += (_, _) =>
            {
                if (failedProgressiveSpeech.State == SpeechPlaybackState.Idle)
                {
                    failedProgressiveCompleted.TrySetResult();
                }
            };

            var failedProgressiveResult = await failedProgressiveSpeech.TrySpeakAsync(
                "The resolved opening still starts. The presenter will go live after an optional-model failure.");
            if (!failedProgressiveResult.Started)
            {
                throw new InvalidOperationException(
                    failedProgressiveResult.Error
                    ?? "Kokoro did not start after an unexpected optional-model failure.");
            }

            await failedProgressiveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        using var speech = new KokoroSpeechService(modelStore, blendSettings);
        var playbackCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        speech.SpeakingStateChanged += (_, _) =>
        {
            if (!speech.IsSpeaking)
            {
                playbackCompleted.TrySetResult();
            }
        };

        var playbackResult = await speech.TrySpeakAsync(
            "That was awkward 😅 but Kokoro should keep speaking.");

        if (!playbackResult.Started)
        {
            throw new InvalidOperationException(
                playbackResult.Error ?? "Kokoro playback with an emoji did not start.");
        }

        await playbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var speakingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        speech.SpeakingStateChanged += (_, _) =>
        {
            if (speech.State == SpeechPlaybackState.Speaking)
            {
                speakingStarted.TrySetResult();
            }
        };
        var pauseResult = await speech.TrySpeakAsync(
            "Pulse Reader can pause this Kokoro sentence and continue playing it afterwards.");
        if (!pauseResult.Started)
        {
            throw new InvalidOperationException(
                pauseResult.Error ?? "Kokoro pause playback did not start.");
        }

        await speakingStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (!speech.TryPause() || speech.State != SpeechPlaybackState.Paused)
        {
            throw new InvalidOperationException("Kokoro playback did not enter its paused state.");
        }

        await Task.Delay(300);
        if (speech.State != SpeechPlaybackState.Paused || !speech.TryResume())
        {
            throw new InvalidOperationException("Kokoro playback did not remain paused and resume.");
        }

        speech.Stop();

        var navigationSpeaking = WaitForNextState(speech, SpeechPlaybackState.Speaking);
        var navigationResult = await speech.TrySpeakAsync(
            "The first reading unit is deliberately long enough to begin playback. "
            + "The second reading unit validates the next control. "
            + "The third reading unit remains available afterwards.");
        if (!navigationResult.Started)
        {
            throw new InvalidOperationException(
                navigationResult.Error ?? "Kokoro navigation playback did not start.");
        }

        await navigationSpeaking.WaitAsync(TimeSpan.FromSeconds(30));
        var initialNavigation = speech.NavigationState;
        if (initialNavigation != new PlaybackNavigationState(true, 0, 3))
        {
            throw new InvalidOperationException(
                $"Unexpected initial navigation state: {initialNavigation}.");
        }

        var navigationSession = CurrentSession(speech);

        var nextSpeaking = WaitForNextState(speech, SpeechPlaybackState.Speaking);
        if (!speech.TryMoveToNextUnit())
        {
            throw new InvalidOperationException("Kokoro did not accept navigation to the next unit.");
        }

        await nextSpeaking.WaitAsync(TimeSpan.FromSeconds(30));
        if (!ReferenceEquals(navigationSession, CurrentSession(speech)))
        {
            throw new InvalidOperationException(
                "Kokoro navigation replaced the active audio output session.");
        }

        if (speech.NavigationState.CurrentUnitIndex != 1 || !speech.TryPause())
        {
            throw new InvalidOperationException("Kokoro did not restart and pause on the second unit.");
        }

        var restartedSpeaking = WaitForNextState(speech, SpeechPlaybackState.Speaking);
        if (!speech.TryRestartCurrentUnit())
        {
            throw new InvalidOperationException("Kokoro did not restart the current unit.");
        }

        if (!speech.TryRestartCurrentUnit())
        {
            throw new InvalidOperationException("Kokoro did not accept a rapid second back action.");
        }

        await restartedSpeaking.WaitAsync(TimeSpan.FromSeconds(30));
        if (speech.State != SpeechPlaybackState.Speaking)
        {
            throw new InvalidOperationException(
                "Navigating backward from pause did not resume playback.");
        }

        if (speech.NavigationState.CurrentUnitIndex != 0)
        {
            throw new InvalidOperationException(
                "Rapid back actions did not step from the current unit to the preceding unit.");
        }

        if (!speech.TryPause() || speech.State != SpeechPlaybackState.Paused)
        {
            throw new InvalidOperationException(
                "Kokoro did not pause before the next-from-pause resume check.");
        }

        var nextFromPauseSpeaking = WaitForNextState(speech, SpeechPlaybackState.Speaking);
        if (!speech.TryMoveToNextUnit())
        {
            throw new InvalidOperationException(
                "Kokoro did not accept next navigation while paused.");
        }

        await nextFromPauseSpeaking.WaitAsync(TimeSpan.FromSeconds(30));
        if (speech.State != SpeechPlaybackState.Speaking
            || speech.NavigationState.CurrentUnitIndex != 1)
        {
            throw new InvalidOperationException(
                "Navigating forward from pause did not select the next unit and resume playback.");
        }

        if (!speech.TryPause() || speech.State != SpeechPlaybackState.Paused)
        {
            throw new InvalidOperationException(
                "Kokoro did not pause before the restart-from-beginning resume check.");
        }

        var beginningSpeaking = WaitForNextState(speech, SpeechPlaybackState.Speaking);
        if (!speech.TryRestartFromBeginning())
        {
            throw new InvalidOperationException("Kokoro did not restart from the beginning.");
        }

        await beginningSpeaking.WaitAsync(TimeSpan.FromSeconds(30));
        if (speech.State != SpeechPlaybackState.Speaking)
        {
            throw new InvalidOperationException(
                "Restarting from the beginning while paused did not resume playback.");
        }

        if (speech.NavigationState.CurrentUnitIndex != 0)
        {
            throw new InvalidOperationException("Restarting from the beginning did not select unit zero.");
        }

        speech.Stop();

        var stopResult = await speech.TrySpeakAsync(
            "This sentence should stop before Kokoro can play it.");
        speech.Stop();

        if (!stopResult.Started || speech.IsSpeaking)
        {
            throw new InvalidOperationException("Kokoro stop did not clear speaking state.");
        }

        Console.WriteLine(
            $"PASS: Kokoro synthesized {audio.Length} bytes with a 65/35 voice blend.");
        Console.WriteLine("PASS: bounded Kokoro playback completed two queued segments.");
        Console.WriteLine("PASS: progressive Kokoro playback appended a resolved tail without restarting output.");
        Console.WriteLine("PASS: Kokoro streamed resolved contextual units during lookahead and preserved navigation and cancellation.");
        Console.WriteLine("PASS: Kokoro playback handled an emoji without falling back.");
        Console.WriteLine("PASS: Kokoro navigation resumed paused playback across next, rapid back, and restart-from-beginning actions.");
        Console.WriteLine("PASS: Kokoro playback completed, paused, resumed, and immediate stop cleared speaking state.");
        Console.WriteLine($"MODEL: {modelPath}");
    }

    private static Task WaitForNextState(KokoroSpeechService speech, SpeechPlaybackState expected)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        speech.SpeakingStateChanged += OnStateChanged;
        return reached.Task;

        void OnStateChanged(object? sender, EventArgs eventArgs)
        {
            if (speech.State == expected)
            {
                speech.SpeakingStateChanged -= OnStateChanged;
                reached.TrySetResult();
            }
        }
    }

    private static object? CurrentSession(KokoroSpeechService speech) =>
        typeof(KokoroSpeechService)
            .GetField("_currentSession", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(speech);

    private sealed class DelayedContextualProcessor : IContextualPronunciationProcessor
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _canceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ContextualPronunciationAvailability Availability { get; } =
            new(true, "test", null);

        public Task Started => _started.Task;

        public Task Canceled => _canceled.Task;

        public bool IsReleased => _release.Task.IsCompleted;

        public void SetEnabled(bool enabled)
        {
        }

        public async Task<PreparedDocument> ApplyAsync(
            PreparedDocument document,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return document;
            }
            catch (OperationCanceledException)
            {
                _canceled.TrySetResult();
                throw;
            }
        }

        public async Task ApplyProgressivelyAsync(
            PreparedDocument document,
            Func<ContextualPronunciationProgress, CancellationToken, ValueTask> progressSink,
            CancellationToken cancellationToken)
        {
            var completed = await ApplyAsync(document, cancellationToken);
            var resolvedUnitIndex = ContextualPronunciationRules.FindTargets(document)
                .Select(target => target.UnitIndex)
                .DefaultIfEmpty(-1)
                .Max();
            await progressSink(
                new ContextualPronunciationProgress(completed, resolvedUnitIndex, true),
                cancellationToken);
        }

        public void Release() => _release.TrySetResult();

        public void Dispose() => _release.TrySetCanceled();
    }

    private sealed class UnitwiseContextualProcessor : IContextualPronunciationProcessor
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecond = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ContextualPronunciationAvailability Availability { get; } =
            new(true, "test", null);

        public Task Started => _started.Task;

        public Task SecondUnitStarted => _secondStarted.Task;

        public void SetEnabled(bool enabled)
        {
        }

        public async Task<PreparedDocument> ApplyAsync(
            PreparedDocument document,
            CancellationToken cancellationToken)
        {
            await _releaseFirst.Task.WaitAsync(cancellationToken);
            await _releaseSecond.Task.WaitAsync(cancellationToken);
            var targets = ContextualPronunciationRules.FindTargets(document);
            return ContextualPronunciationRules.ApplyLabels(
                document,
                targets,
                targets.Select(target => target.Rule.PhonemeLabel).ToArray());
        }

        public async Task ApplyProgressivelyAsync(
            PreparedDocument document,
            Func<ContextualPronunciationProgress, CancellationToken, ValueTask> progressSink,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            var targetGroups = ContextualPronunciationRules.FindTargets(document)
                .GroupBy(target => target.UnitIndex)
                .ToArray();
            await _releaseFirst.Task.WaitAsync(cancellationToken);
            var firstTargets = targetGroups[0].ToArray();
            var firstDocument = ContextualPronunciationRules.ApplyLabels(
                document,
                firstTargets,
                firstTargets.Select(target => target.Rule.PhonemeLabel).ToArray());
            await progressSink(
                new ContextualPronunciationProgress(
                    firstDocument,
                    targetGroups[0].Key,
                    false),
                cancellationToken);

            _secondStarted.TrySetResult();
            await _releaseSecond.Task.WaitAsync(cancellationToken);
            var allTargets = targetGroups.SelectMany(group => group).ToArray();
            var finalDocument = ContextualPronunciationRules.ApplyLabels(
                document,
                allTargets,
                allTargets.Select(target => target.Rule.PhonemeLabel).ToArray());
            await progressSink(
                new ContextualPronunciationProgress(
                    finalDocument,
                    targetGroups[^1].Key,
                    true),
                cancellationToken);
        }

        public void ReleaseFirstUnit() => _releaseFirst.TrySetResult();

        public void ReleaseSecondUnit() => _releaseSecond.TrySetResult();

        public void Dispose()
        {
            _releaseFirst.TrySetCanceled();
            _releaseSecond.TrySetCanceled();
        }
    }

    private sealed class FailingContextualProcessor : IContextualPronunciationProcessor
    {
        public ContextualPronunciationAvailability Availability { get; } =
            new(true, "test", null);

        public void SetEnabled(bool enabled)
        {
        }

        public Task<PreparedDocument> ApplyAsync(
            PreparedDocument document,
            CancellationToken cancellationToken) =>
            throw new UnexpectedContextualProcessorException();

        public Task ApplyProgressivelyAsync(
            PreparedDocument document,
            Func<ContextualPronunciationProgress, CancellationToken, ValueTask> progressSink,
            CancellationToken cancellationToken) =>
            throw new UnexpectedContextualProcessorException();

        public void Dispose()
        {
        }
    }

    private sealed class UnexpectedContextualProcessorException : Exception
    {
    }
}
