using PulseReader.Selection;

Environment.ExitCode = await TestProcessBoundary.RunAsync(
    async () =>
    {
        if (await UpdateMachineElevationQualification.TryRunAsync(args) is { } machineExit)
        {
            if (machineExit != 0) throw new InvalidOperationException($"Machine elevation fixture failed with code {machineExit}.");
            return;
        }
        if (await UpdateInteractiveQualification.TryRunAsync(args) is { } interactiveExit)
        {
            if (interactiveExit != 0)
                throw new InvalidOperationException($"The disposable interactive update fixture exited with code {interactiveExit}.");
            return;
        }
        if (await UpdateSandboxQualification.TryRunAsync(args) is { } qualificationExit)
        {
            if (qualificationExit != 0)
                throw new InvalidOperationException($"The disposable update qualification helper exited with code {qualificationExit}.");
            return;
        }
        if (args.Length == 1 && args[0] == "--update-live-network-check")
        {
            await UpdateLiveNetworkCheck.RunAsync();
            return;
        }
        if (args.Length == 2 && args[0] == "--update-built-catalog")
        {
            await UpdatePackageCatalogChecks.RunAsync(args[1]);
            return;
        }
        if (args.Length == 1 && args[0] == "--update-qualification-payload-checks")
        {
            UpdateQualificationPayloadChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--update-handoff-checks")
        {
            await UpdateHandoffChecks.RunAsync();
            return;
        }
        if (args.Length == 1 && args[0] == "--update-scheduling-checks")
        {
            UpdateSchedulingChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--update-catalog-checks")
        {
            await UpdateCatalogChecks.RunAsync();
            return;
        }
        if (args.Length == 1 && args[0] == "--portable-update-checks")
        {
            await PortableUpdateChecks.RunAsync();
            return;
        }
        if (args.Length == 1 && args[0] == "--update-ui-checks")
        {
            UpdateUiChecks.Run();
            TrayMenuChecks.Run();
            SettingsInteractionChecks.Run();
            TrayIconChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--startup-failure-checks")
        {
            StartupFailureChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--speech-validation-failure-checks")
        {
            await SpeechValidationFailureChecks.RunAsync();
            return;
        }
        if (args.Length == 1 && args[0] == "--readonly-voice-checks")
        {
            ReadOnlyVoiceChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--readonly-voice-file-checks")
        {
            ReadOnlyVoiceChecks.RunIsolatedFiles();
            return;
        }
        if (args.Length == 1 && args[0] == "--optional-model-startup-checks")
        {
            OptionalModelStartupChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--shared-model-setup-checks")
        {
            await SharedModelSetupChecks.RunAsync();
            return;
        }
        if (args.Length == 1 && args[0] == "--uninstall-data-checks")
        {
            UninstallDataCleanupChecks.Run();
            return;
        }
        if (args.Length == 2 && args[0] == "--release-optional-model-smoke")
        {
            await ReleaseOptionalModelSmokeChecks.RunAsync(args[1]);
            return;
        }
        if (args.Length == 1 && args[0] == "--distribution-terms-checks")
        {
            DistributionTermsChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--release-model-smoke")
        {
            await ReleaseModelSmokeChecks.RunAsync();
            return;
        }
        if (args.Length == 1 && args[0] == "--model-setup-checks")
        {
            await ModelSetupChecks.RunAsync();
            await ModelDownloadTimeoutChecks.RunAsync();
            ModelSetupFlowChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--thought-settlement-checks")
        {
            CodexThoughtSettlementChecks.Run();
            CodexThoughtSettlementIntegrationChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--monitoring-feedback-checks")
        {
            MonitoringLatchSoundChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--completed-revision-checks")
        {
            await CompletedResponseRevisionChecks.RunAsync();
            return;
        }
        if (args.Length == 1 && args[0] == "--opening-revision-checks")
        {
            await OpeningRevisionChecks.RunAsync();
            return;
        }
        if (args.Length == 1 && args[0] == "--inline-hyphen-checks")
        {
            InlineHyphenChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--manual-monitoring-checks")
        {
            await ManualPlaybackMonitoringChecks.RunAsync();
            PlaybackIslandChecks.Run();
            return;
        }
        if (args.Length == 2 && args[0] == "--t3-scope-check")
        {
            T3LiveCaptureChecks.Inspect(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 2 && args[0] == "--codex-scope-check")
        {
            CodexLiveCaptureChecks.RunScope(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 3 && args[0] == "--codex-capture-check")
        {
            CodexLiveCaptureChecks.Run(new IntPtr(long.Parse(args[1])), args[2]);
            return;
        }
        if (args.Length == 1 && args[0] == "--codex-latch-checks")
        {
            await CodexLatchChecks.RunAsync();
            return;
        }
        if (args.Length == 2 && args[0] == "--feet-inches-ab")
        {
            await TextPreparationAbGenerator.RunFeetAndInchesAsync(args[1]);
            return;
        }
        if (args.Length == 1 && args[0] == "--spoken-text-checks")
        {
            TextPreparationChecks.Run();
            SpokenTextFormattingChecks.Run();
            SpokenTextRegressionChecks.Run();
            PronunciationDictionaryChecks.Run();
            MeasurementUnitFormattingChecks.Run();
            TitleFormattingChecks.Run();
            FourDigitNumberFormattingChecks.Run();
            OpaqueIdentifierFormattingChecks.Run();
            KokoroSegmentationChecks.Run();
            PronunciationEdgeChecks.Run();
            return;
        }
        if (args.Length == 2 && args[0] == "--pronunciation-edge-audio")
        {
            await TextPreparationAbGenerator.RunPronunciationEdgesAsync(args[1]);
            return;
        }
        if (args.Length == 1 && args[0] == "--pronunciation-edge-checks")
        {
            PronunciationEdgeChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--copy-shortcut-checks")
        {
            CopyShortcutChecks.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--document-fallback-checks")
        {
            DocumentMonitoringFallbackChecks.Run();
            return;
        }
        if (args.Length == 2 && args[0] == "--discord-composer-check")
        {
            DiscordMonitoringChecks.CheckLiveComposer(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 2 && args[0] == "--discord-mention-check")
        {
            DiscordMonitoringChecks.CheckLiveMentions(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 2 && args[0] == "--discord-newcomer-check")
        {
            DiscordMonitoringChecks.CheckLiveNewcomer(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 1 && args[0] == "--discord-checks")
        {
            DiscordMonitoringChecks.Run();
            return;
        }
        if (args.Length == 2 && args[0] == "--discord-live-check")
        {
            await DiscordMonitoringChecks.LiveAsync(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 2 && args[0] == "--discord-structure-check")
        {
            DiscordMonitoringChecks.Inspect(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 3 && args[0] == "--lmstudio-thinking-probe")
        {
            await MonitoringLiveCaptureChecks.RunThinkingPanelAsync(new IntPtr(long.Parse(args[1])), args[2]);
            return;
        }
        if (args.Length == 3 && args[0] is "--lmstudio-live-probe" or "--lmstudio-rapid-probe")
        {
            await MonitoringLiveCaptureChecks.RunAsync(new IntPtr(long.Parse(args[1])), args[2], corpus: args[0] == "--lmstudio-rapid-probe" ? ["Ready."] : [
                "Fresh copper bells mark the return to this conversation.",
                "The reader should speak this new message without repeating the earlier garden story.",
                "Quiet violet lanterns mark the end of the test."
            ], allowStreamedContinuation: true);
            return;
        }
        if (args.Length == 2 && args[0] == "--lmstudio-empty-capture-check")
        {
            LmStudioMonitoringChecks.CheckEmptyThread(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 2 && args[0] == "--lmstudio-recovery-capture-check")
        {
            LmStudioMonitoringChecks.CheckVisibleRecovery(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 2 && args[0] == "--lmstudio-table-capture-check")
        {
            LmStudioMonitoringChecks.CheckVisibleTable(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 2 && args[0] == "--lmstudio-policy-check")
        {
            switch (args[1])
            {
                case "stream": LmStudioMonitoringChecks.CheckContinuousConversationFragments(); break;
                case "collapsed": LmStudioMonitoringChecks.CheckCollapsedPreview(); break;
                case "table": LmStudioMonitoringChecks.CheckTableCells(); break;
                case "paragraph": LmStudioMonitoringChecks.CheckDirectParagraphAnswer(); break;
                case "regeneration": LmStudioMonitoringChecks.CheckRegeneration(); break;
                case "rapid": LmStudioMonitoringChecks.CheckRapidRegeneration(); break;
                default: throw new ArgumentException("Unknown LM Studio policy check.");
            }
            return;
        }
        if (args.Length == 2 && args[0] == "--lmstudio-visible-fixture-check")
        {
            LmStudioMonitoringChecks.CheckVisibleFixture(new IntPtr(long.Parse(args[1])));
            return;
        }
        if (args.Length == 3 && args[0] == "--window-monitoring-handoff-probe")
        {
            await MonitoringLiveCaptureChecks.RunAsync(new IntPtr(long.Parse(args[1])), args[2],
                corpus: MonitoringHandoffCorpus.Sentences, allowStreamedContinuation: true);
            return;
        }
        if (args.Length == 3 && args[0] is "--window-monitoring-resumption-probe" or "--window-monitoring-settings-probe")
        {
            string[] corpus = args[0] == "--window-monitoring-settings-probe" ? [
                "The reader has returned from settings.",
                "This fresh response should play without repeating the earlier river passage.",
                "The settings recovery test is complete."
            ] : [
                "Fresh copper bells mark the return to this conversation.",
                "The reader should speak this new message without repeating the earlier garden story.",
                "Quiet violet lanterns mark the end of the test."
            ];
            await MonitoringLiveCaptureChecks.RunAsync(new IntPtr(long.Parse(args[1])), args[2], corpus: corpus);
            return;
        }
        if (args.Length == 4 && args[0] == "--window-context-probe")
        {
            WindowContextIdentityProbe.Run(new IntPtr(long.Parse(args[1])), args[2], int.Parse(args[3]));
            return;
        }
        if (args.Length == 3 && args[0] == "--window-monitoring-live-probe")
        {
            await MonitoringLiveCaptureChecks.RunAsync(new IntPtr(long.Parse(args[1])), args[2]);
            return;
        }
        if (args.Length == 3 && args[0] == "--window-monitoring-idle-probe")
        {
            await MonitoringLiveCaptureChecks.RunAsync(new IntPtr(long.Parse(args[1])), args[2], idleOnly: true);
            return;
        }
        if (args.Length >= 2 && args[0] == "--window-monitoring-startup-probe")
        {
            using var diagnostics = PulseReader.Monitoring.WindowMonitoringDiagnosticLog.CreateFromArguments(args);
            using var probe = new PulseReader.Monitoring.WindowTextMonitoringSession(new IntPtr(long.Parse(args[1])), diagnostics);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await probe.StartAsync(CancellationToken.None);
            Console.WriteLine($"Monitoring startup: {watch.ElapsedMilliseconds} ms; started={result.Started}");
            if (!result.Started) throw new InvalidOperationException(result.Error);
            return;
        }
        if (args is ["--monitoring-document-checks"])
        {
            CachedCodexTranscriptChecks.Run();
            CodexCaptureScopeChecks.Run();
            MonitoringDocumentEditabilityChecks.Run();
            DocumentMonitoringFallbackChecks.Run();
            return;
        }
        if (args is ["--monitoring-performance-checks"])
        {
            MonitoringWaitSchedulingChecks.Run();
            MonitoringPerformanceDiagnosticChecks.Run();
            MonitorExitDiagnosticChecks.Run();
            await MonitorStartupCancellationChecks.RunAsync();
            return;
        }
        if (args is ["--t3-monitoring-checks"])
        {
            T3MonitoringChecks.Run();
            return;
        }
        if (args.Contains("--window-monitoring-checks", StringComparer.OrdinalIgnoreCase))
        {
            await WindowMonitoringChecks.RunAsync();
            return;
        }
        if (args.Contains("--failure-reporting-probe", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Intentional test-harness failure probe.",
                new InvalidOperationException("Intentional assertion detail."));
        }

        if (args.Length >= 2
            && args[0].Equals("-m", StringComparison.Ordinal)
            && Path.GetFileName(args[1]).Equals(
                "startup-cleanup-probe.gguf",
                StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        if (args.Contains("--clipboard-checks", StringComparer.OrdinalIgnoreCase))
        {
            await ClipboardCaptureChecks.RunAsync();
            return;
        }

        if (args.Contains("--ui-automation-smoke", StringComparer.OrdinalIgnoreCase))
        {
            await UiAutomationSmokeChecks.RunAsync();
            return;
        }

        if (args.Contains("--ui-automation-manual-provider", StringComparer.OrdinalIgnoreCase))
        {
            await UiAutomationSmokeChecks.RunManualProviderAsync();
            return;
        }

        if (args.Contains("--window-monitoring-smoke", StringComparer.OrdinalIgnoreCase))
        {
            await WindowMonitoringSmokeChecks.RunAsync();
            return;
        }

        if (args.Contains("--window-monitoring-manual-provider", StringComparer.OrdinalIgnoreCase))
        {
            await WindowMonitoringSmokeChecks.RunManualProviderAsync();
            return;
        }

        if (LocalEvaluationArguments.TryParse(args, out var evaluationCommand, out var evaluationError))
        {
            if (evaluationError is not null)
            {
                Console.Error.WriteLine($"FAIL: {evaluationError}");
                Environment.ExitCode = 2;
                return;
            }

            if (evaluationCommand is null)
            {
                throw new InvalidOperationException("Evaluation arguments produced no command.");
            }

            if (evaluationCommand.Mode == LocalEvaluationMode.TargetedModel)
            {
                await LocalTargetedModelEvaluation.RunAsync(
                    evaluationCommand.OutputDirectory,
                    evaluationCommand.ModelFileName,
                    evaluationCommand.ReasoningBudget);
            }
            else
            {
                await LocalTextCleanupEvaluation.RunAsync(
                    evaluationCommand.OutputDirectory,
                    evaluationCommand.CaseId,
                    evaluationCommand.ModelFileName);
            }

            return;
        }

        if (args.Contains("--local-pronunciation-review", StringComparer.OrdinalIgnoreCase))
        {
            var outputArgument = args
                .SkipWhile(argument => !argument.Equals("--local-pronunciation-review", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            await LocalPronunciationReviewGenerator.RunAsync(
                outputArgument ?? Path.Combine("artifacts", "local-text-cleanup", "pronunciation-review"));
            return;
        }

        if (args.Contains("--print-kokoro-phonemes", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var value in args.SkipWhile(argument => !argument.Equals(
                             "--print-kokoro-phonemes",
                             StringComparison.OrdinalIgnoreCase))
                         .Skip(1))
            {
                Console.WriteLine($"{value} => {KokoroSharp.Processing.Tokenizer.Phonemize(value)}");
            }

            return;
        }

        if (args.Contains("--text-preparation-identifier-ab", StringComparer.OrdinalIgnoreCase))
        {
            var outputArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-identifier-ab", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var caseArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-ab-case", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            await TextPreparationAbGenerator.RunIdentifiersAsync(
                outputArgument ?? Path.Combine("artifacts", "text-preparation-identifier-ab"),
                caseArgument);
            return;
        }

        if (args.Contains("--measurement-unit-ab", StringComparer.OrdinalIgnoreCase))
        {
            var outputArgument = args
                .SkipWhile(argument => !argument.Equals("--measurement-unit-ab", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var caseArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-ab-case", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            await TextPreparationAbGenerator.RunMeasurementUnitsAsync(
                outputArgument ?? Path.Combine("artifacts", "measurement-unit-ab"),
                caseArgument);
            return;
        }

        if (args.Contains("--four-digit-number-ab", StringComparer.OrdinalIgnoreCase))
        {
            var outputArgument = args
                .SkipWhile(argument => !argument.Equals("--four-digit-number-ab", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var caseArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-ab-case", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            await TextPreparationAbGenerator.RunFourDigitNumbersAsync(
                outputArgument ?? Path.Combine("artifacts", "four-digit-number-ab"),
                caseArgument);
            return;
        }

        if (args.Contains("--text-preparation-line-boundary-ab", StringComparer.OrdinalIgnoreCase))
        {
            var outputArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-line-boundary-ab", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var caseArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-ab-case", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            await TextPreparationAbGenerator.RunLineBoundariesAsync(
                outputArgument ?? Path.Combine("artifacts", "text-preparation-line-boundary-ab"),
                caseArgument);
            return;
        }

        if (args.Contains("--text-preparation-edge-ab", StringComparer.OrdinalIgnoreCase))
        {
            var outputArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-edge-ab", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var caseArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-ab-case", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            await TextPreparationAbGenerator.RunEdgeCasesAsync(
                outputArgument ?? Path.Combine("artifacts", "text-preparation-edge-ab"),
                caseArgument);
            return;
        }

        if (args.Contains("--text-preparation-ab", StringComparer.OrdinalIgnoreCase))
        {
            var outputArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-ab", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var caseArgument = args
                .SkipWhile(argument => !argument.Equals("--text-preparation-ab-case", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            await TextPreparationAbGenerator.RunAsync(
                outputArgument ?? Path.Combine("artifacts", "text-preparation-ab"),
                caseArgument);
            return;
        }

        if (args.Contains("--tray-icon-preview", StringComparer.OrdinalIgnoreCase))
        {
            TrayIconChecks.SaveFrames(Path.Combine("artifacts", "tray-icon-preview"));
            return;
        }

        if (args.Contains("--tray-menu-preview", StringComparer.OrdinalIgnoreCase))
        {
            TrayMenuChecks.SavePreview(Path.Combine("artifacts", "tray-menu-preview.png"));
            return;
        }

        if (args.Contains("--kokoro-smoke", StringComparer.OrdinalIgnoreCase))
        {
            await KokoroSynthesisChecks.RunAsync();
            return;
        }

        if (args.Contains("--kokoro-playback-lifecycle", StringComparer.OrdinalIgnoreCase))
        {
            await KokoroPlaybackLifecycleChecks.RunAsync();
            return;
        }

        if (args.Contains("--kokoro-cancellation-smoke", StringComparer.OrdinalIgnoreCase))
        {
            await KokoroCancellationChecks.RunSmokeAsync();
            return;
        }

        if (args.Contains("--contextual-pronunciation-smoke", StringComparer.OrdinalIgnoreCase))
        {
            await ContextualPronunciationSmokeChecks.RunAsync();
            return;
        }

        if (args.Contains("--settings-preview", StringComparer.OrdinalIgnoreCase))
        {
            SettingsInteractionChecks.ShowPreview();
            return;
        }

        if (args.Contains("--readme-screenshots", StringComparer.OrdinalIgnoreCase))
        {
            var outputArgument = args
                .SkipWhile(argument => !argument.Equals("--readme-screenshots", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault();
            var outputDirectory = outputArgument ?? Path.Combine("docs", "assets");
            ReadmeScreenshotChecks.SaveAll(outputDirectory);
            return;
        }

        if (args.Contains("--playback-island-preview", StringComparer.OrdinalIgnoreCase))
        {
            PlaybackIslandChecks.SaveFrames(Path.Combine("artifacts", "playback-island-preview"));
            return;
        }

        if (args.Contains("--windows-smoke", StringComparer.OrdinalIgnoreCase))
        {
            await WindowsSpeechChecks.RunSmokeAsync();
            return;
        }

        var scenarios = new[]
        {
    Array.Empty<KeyboardKey>(),
    [KeyboardKey.LeftControl, KeyboardKey.LeftAlt],
    [KeyboardKey.LeftControl, KeyboardKey.RightAlt],
    [KeyboardKey.RightControl, KeyboardKey.RightAlt],
    [
        KeyboardKey.LeftControl,
        KeyboardKey.LeftAlt,
        KeyboardKey.LeftShift,
        KeyboardKey.LeftWindows
    ],
    [KeyboardKey.LeftAlt],
    [KeyboardKey.RightShift]
};

        foreach (var scenario in scenarios)
        {
            CheckScenario(scenario);
        }

        Console.WriteLine($"PASS: {scenarios.Length} modifier scenarios send clean Ctrl+C and leave capture keys released.");
        CopyShortcutChecks.Run();
        await ClipboardCaptureChecks.RunAsync();
        await WindowMonitoringChecks.RunAsync();
        await FailureReportingChecks.RunAsync();
        LocalEvaluationArgumentChecks.Run();
        HotkeyValidationChecks.Run();
        await ManualPlaybackMonitoringChecks.RunAsync();
        HotkeySuspensionChecks.Run();
        StartupRegistrationChecks.Run();
        ConfigurationChecks.Run();
        OptionalModelStartupChecks.Run();
        await SharedModelSetupChecks.RunAsync();
        UninstallDataCleanupChecks.Run();
        DistributionTermsChecks.Run();
        StartupFailureChecks.Run();
        await UpdateCatalogChecks.RunAsync();
        await PortableUpdateChecks.RunAsync();
        UpdateUiChecks.Run();
        UpdateSchedulingChecks.Run();
        await UpdateHandoffChecks.RunAsync();
        UpdateQualificationPayloadChecks.Run();
        await ModelSetupChecks.RunAsync();
        await ModelDownloadTimeoutChecks.RunAsync();
        ModelSetupFlowChecks.Run();
        KokoroVoiceBlendChecks.Run();
        ReadOnlyVoiceChecks.RunIsolatedFiles();
        KokoroSegmentationChecks.Run();
        TextPreparationChecks.Run();
        SpokenTextFormattingChecks.Run();
        PronunciationDictionaryChecks.Run();
        await ContextualPronunciationChecks.RunAsync();
        MeasurementUnitFormattingChecks.Run();
        TitleFormattingChecks.Run();
        FourDigitNumberFormattingChecks.Run();
        OpaqueIdentifierFormattingChecks.Run();
        SpokenTextRegressionChecks.Run();
        PronunciationEdgeChecks.Run();
        SettingsInteractionChecks.Run();
        TrayIconChecks.Run();
        TrayMenuChecks.Run();
        PlaybackIslandChecks.Run();
        await LocalTextCleanupEvaluationChecks.RunAsync();
        await LocalLlamaServerChecks.RunAsync();
        await SpeechServiceChecks.RunAsync();
        await SpeechValidationFailureChecks.RunAsync();
        await KokoroPlaybackLifecycleChecks.RunAsync();
        return;

        static void CheckScenario(IEnumerable<KeyboardKey> initialKeys)
        {
            var heldModifiers = initialKeys.ToHashSet();
            var pressedKeys = initialKeys.ToHashSet();
            var sequence = NativeInput.BuildCopyShortcutSequence(heldModifiers);

            foreach (var step in sequence)
            {
                if (IsAlt(step.Key)
                    && step.IsKeyUp
                    && !pressedKeys.Any(IsControl))
                {
                    Fail("Alt was released without Ctrl down, which can activate application menu access keys.");
                }

                if (step.Key == KeyboardKey.C && !step.IsKeyUp)
                {
                    var expectedKeys = new HashSet<KeyboardKey> { KeyboardKey.LeftControl };
                    if (!pressedKeys.SetEquals(expectedKeys))
                    {
                        Fail($"Copy was sent with the wrong modifiers: {FormatKeys(pressedKeys)}.");
                    }
                }

                if (step.IsKeyUp)
                {
                    pressedKeys.Remove(step.Key);
                }
                else
                {
                    pressedKeys.Add(step.Key);
                }
            }

            if (pressedKeys.Count != 0)
            {
                Fail(
                    $"Copy left keys logically pressed: {FormatKeys(pressedKeys)}.");
            }
        }

        static bool IsAlt(KeyboardKey key) =>
            key is KeyboardKey.LeftAlt or KeyboardKey.RightAlt;

        static bool IsControl(KeyboardKey key) =>
            key is KeyboardKey.LeftControl or KeyboardKey.RightControl;

        static string FormatKeys(IEnumerable<KeyboardKey> keys)
        {
            var keyNames = keys.Select(key => key.ToString()).ToArray();
            return keyNames.Length == 0 ? "(none)" : string.Join(", ", keyNames);
        }

        static void Fail(string message)
        {
            throw new InvalidOperationException(message);
        }
    },
    Console.Error);
