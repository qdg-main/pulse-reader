internal enum LocalEvaluationMode
{
    TargetedModel,
    TextCleanup
}

internal sealed record LocalEvaluationCommand(
    LocalEvaluationMode Mode,
    string OutputDirectory,
    string? CaseId,
    string? ModelFileName,
    int ReasoningBudget);

internal static class LocalEvaluationArguments
{
    private const string TargetedModeOption = "--local-targeted-model-evaluation";
    private const string TextCleanupModeOption = "--local-text-cleanup-evaluation";
    private const string CaseOption = "--local-text-cleanup-case";
    private const string ModelOption = "--local-text-cleanup-model";
    private const string ReasoningBudgetOption = "--local-model-reasoning-budget";

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out LocalEvaluationCommand? command,
        out string? error)
    {
        command = null;
        error = null;

        var modeArguments = arguments
            .Where(IsModeOption)
            .ToArray();
        if (modeArguments.Length == 0)
        {
            return false;
        }

        if (modeArguments.Length > 1)
        {
            var distinctModes = modeArguments
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            error = distinctModes.Length > 1
                ? $"Evaluation modes '{distinctModes[0]}' and '{distinctModes[1]}' cannot be combined."
                : $"Evaluation mode '{modeArguments[0]}' was specified more than once.";
            return true;
        }

        var modeOption = modeArguments[0];
        var mode = modeOption.Equals(TargetedModeOption, StringComparison.OrdinalIgnoreCase)
            ? LocalEvaluationMode.TargetedModel
            : LocalEvaluationMode.TextCleanup;
        var outputDirectory = mode == LocalEvaluationMode.TargetedModel
            ? Path.Combine("artifacts", "local-text-cleanup", "focused")
            : Path.Combine("artifacts", "local-text-cleanup", "review");
        string? caseId = null;
        string? modelFileName = null;
        var reasoningBudget = 0;
        var caseWasSpecified = false;
        var modelWasSpecified = false;
        var reasoningBudgetWasSpecified = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (IsModeOption(argument))
            {
                if (index + 1 < arguments.Count && !IsOption(arguments[index + 1]))
                {
                    outputDirectory = arguments[++index];
                    if (string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        error = $"The output path for evaluation mode '{modeOption}' cannot be empty.";
                        return true;
                    }
                }

                continue;
            }

            if (argument.Equals(CaseOption, StringComparison.OrdinalIgnoreCase))
            {
                if (mode != LocalEvaluationMode.TextCleanup)
                {
                    error = $"Option '{CaseOption}' is not valid with evaluation mode '{modeOption}'.";
                    return true;
                }

                if (caseWasSpecified)
                {
                    error = $"Option '{CaseOption}' was specified more than once.";
                    return true;
                }

                if (!TryReadRequiredValue(arguments, ref index, CaseOption, out caseId, out error))
                {
                    return true;
                }

                caseWasSpecified = true;
                continue;
            }

            if (argument.Equals(ModelOption, StringComparison.OrdinalIgnoreCase))
            {
                if (modelWasSpecified)
                {
                    error = $"Option '{ModelOption}' was specified more than once.";
                    return true;
                }

                if (!TryReadRequiredValue(arguments, ref index, ModelOption, out modelFileName, out error))
                {
                    return true;
                }

                modelWasSpecified = true;
                continue;
            }

            if (argument.Equals(ReasoningBudgetOption, StringComparison.OrdinalIgnoreCase))
            {
                if (mode != LocalEvaluationMode.TargetedModel)
                {
                    error = $"Option '{ReasoningBudgetOption}' is not valid with evaluation mode '{modeOption}'.";
                    return true;
                }

                if (reasoningBudgetWasSpecified)
                {
                    error = $"Option '{ReasoningBudgetOption}' was specified more than once.";
                    return true;
                }

                if (!TryReadRequiredValue(
                        arguments,
                        ref index,
                        ReasoningBudgetOption,
                        out var reasoningBudgetValue,
                        out error,
                        allowNegativeIntegerLiteral: true))
                {
                    return true;
                }

                if (!int.TryParse(
                        reasoningBudgetValue,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out reasoningBudget))
                {
                    error = $"Reasoning budget must be a non-negative integer; received '{reasoningBudgetValue}'.";
                    return true;
                }

                reasoningBudgetWasSpecified = true;
                continue;
            }

            error = IsOption(argument)
                ? $"Unknown option '{argument}' for evaluation mode '{modeOption}'."
                : $"Unexpected argument '{argument}' for evaluation mode '{modeOption}'. "
                  + "The optional output path must immediately follow the evaluation mode option.";
            return true;
        }

        command = new LocalEvaluationCommand(
            mode,
            outputDirectory,
            caseId,
            modelFileName,
            reasoningBudget);
        return true;
    }

    private static bool TryReadRequiredValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string? value,
        out string? error,
        bool allowNegativeIntegerLiteral = false)
    {
        value = null;
        error = null;
        if (index + 1 >= arguments.Count)
        {
            error = $"Option '{option}' requires a value.";
            return false;
        }

        var candidate = arguments[index + 1];
        if (IsOption(candidate)
            && !(allowNegativeIntegerLiteral
                 && candidate.Length > 1
                 && candidate[1..].All(char.IsDigit)))
        {
            error = $"Option '{option}' requires a value; received option '{candidate}' instead.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = $"Option '{option}' requires a non-empty value.";
            return false;
        }

        value = candidate;
        index++;
        return true;
    }

    private static bool IsModeOption(string argument) =>
        argument.Equals(TargetedModeOption, StringComparison.OrdinalIgnoreCase)
        || argument.Equals(TextCleanupModeOption, StringComparison.OrdinalIgnoreCase);

    private static bool IsOption(string argument) =>
        argument.StartsWith("-", StringComparison.Ordinal);
}

internal static class LocalEvaluationArgumentChecks
{
    public static void Run()
    {
        CheckNonEvaluationModesAreIgnored();
        CheckDocumentedCommands();
        CheckOmittedOutputPaths();
        CheckMissingValues();
        CheckInvalidOptionsAndModes();
        Console.WriteLine("PASS: local evaluation arguments reject ambiguous or invalid commands.");
    }

    private static void CheckNonEvaluationModesAreIgnored()
    {
        string[][] commands =
        [
            ["-m", "startup-cleanup-probe.gguf"],
            ["--local-pronunciation-review"],
            ["--print-kokoro-phonemes", "Pulse Reader"],
            ["--text-preparation-identifier-ab"],
            ["--text-preparation-line-boundary-ab"],
            ["--text-preparation-edge-ab"],
            ["--text-preparation-ab"],
            ["--tray-icon-preview"],
            ["--tray-menu-preview"],
            ["--kokoro-smoke"],
            ["--kokoro-cancellation-smoke"],
            ["--contextual-pronunciation-smoke"],
            ["--settings-preview"],
            ["--readme-screenshots"],
            ["--playback-island-preview"],
            ["--windows-smoke"]
        ];

        foreach (var command in commands)
        {
            Assert(
                !LocalEvaluationArguments.TryParse(command, out _, out _),
                $"Non-evaluation mode '{command[0]}' was claimed by the evaluation parser.");
        }
    }

    private static void CheckDocumentedCommands()
    {
        var review = Parse(
            "--local-text-cleanup-evaluation",
            @"artifacts\local-text-cleanup\review");
        Assert(review.Mode == LocalEvaluationMode.TextCleanup, "Review mode was not parsed.");
        Assert(
            review.OutputDirectory == @"artifacts\local-text-cleanup\review",
            "Review output path changed.");

        var targeted = Parse(
            "--local-targeted-model-evaluation",
            @"artifacts\local-text-cleanup\focused\qwen3-4b-instruct-2507",
            "--local-text-cleanup-model",
            "Qwen3-4B-Instruct-2507-Q4_K_M.gguf");
        Assert(targeted.Mode == LocalEvaluationMode.TargetedModel, "Targeted mode was not parsed.");
        Assert(
            targeted.ModelFileName == "Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
            "Targeted model changed.");
    }

    private static void CheckOmittedOutputPaths()
    {
        var targeted = Parse(
            "--local-targeted-model-evaluation",
            "--local-text-cleanup-model",
            "model.gguf",
            "--local-model-reasoning-budget",
            "64");
        Assert(
            targeted.OutputDirectory == Path.Combine("artifacts", "local-text-cleanup", "focused"),
            "Targeted mode did not use its default output path when omitted.");
        Assert(targeted.ModelFileName == "model.gguf", "A switch was consumed as the targeted output path.");
        Assert(targeted.ReasoningBudget == 64, "The reasoning budget was not parsed.");

        var review = Parse(
            "--local-text-cleanup-evaluation",
            "--local-text-cleanup-case",
            "case-1");
        Assert(
            review.OutputDirectory == Path.Combine("artifacts", "local-text-cleanup", "review"),
            "Review mode did not use its default output path when omitted.");
        Assert(review.CaseId == "case-1", "A switch was consumed as the review output path.");
    }

    private static void CheckMissingValues()
    {
        ExpectError(
            ["--local-text-cleanup-evaluation", "--local-text-cleanup-model"],
            "requires a value");
        ExpectError(
            [
                "--local-text-cleanup-evaluation",
                "--local-text-cleanup-model",
                "--local-text-cleanup-case",
                "case-1"
            ],
            "received option");
        ExpectError(
            ["--local-text-cleanup-evaluation", "--local-text-cleanup-case"],
            "requires a value");
        ExpectError(
            ["--local-targeted-model-evaluation", "--local-model-reasoning-budget"],
            "requires a value");
        ExpectError(
            ["--local-targeted-model-evaluation", "--local-model-reasoning-budget", "many"],
            "non-negative integer");
        ExpectError(
            ["--local-targeted-model-evaluation", "--local-model-reasoning-budget", "-1"],
            "non-negative integer");
        ExpectError(
            ["--local-targeted-model-evaluation", "--local-model-reasoning-budget", "2147483648"],
            "non-negative integer");
    }

    private static void CheckInvalidOptionsAndModes()
    {
        ExpectError(
            ["--local-text-cleanup-evaluation", "--unknown-evaluation-option"],
            "Unknown option");
        ExpectError(
            [
                "--local-text-cleanup-evaluation",
                "--local-targeted-model-evaluation"
            ],
            "cannot be combined");
        ExpectError(
            ["--local-text-cleanup-evaluation", "--local-model-reasoning-budget", "64"],
            "not valid");
        ExpectError(
            ["--local-targeted-model-evaluation", "--local-text-cleanup-case", "case-1"],
            "not valid");
    }

    private static LocalEvaluationCommand Parse(params string[] arguments)
    {
        var wasEvaluation = LocalEvaluationArguments.TryParse(arguments, out var command, out var error);
        Assert(wasEvaluation, "Evaluation command was not claimed by the parser.");
        Assert(error is null, $"Valid evaluation command failed: {error}");
        return command ?? throw new InvalidOperationException("Valid evaluation command returned no result.");
    }

    private static void ExpectError(string[] arguments, string expectedText)
    {
        var wasEvaluation = LocalEvaluationArguments.TryParse(arguments, out var command, out var error);
        Assert(wasEvaluation, "Invalid evaluation command was not claimed by the parser.");
        Assert(command is null, "Invalid evaluation command returned a runnable result.");
        Assert(
            error?.Contains(expectedText, StringComparison.OrdinalIgnoreCase) == true,
            $"Expected an error containing '{expectedText}', received '{error}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
