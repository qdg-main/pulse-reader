using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PulseReader.TextPreparation;

internal sealed class LocalLlamaServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _executablePath;
    private readonly string _modelPath;
    private readonly string _outputDirectory;
    private readonly int _threads;
    private readonly int _reasoningBudget;
    private readonly string _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _modelAlias = $"pulse-reader-{Guid.NewGuid():N}";
    private readonly HttpClient _httpClient = new();
    private Process? _process;
    private bool _processStarted;
    private Uri? _baseUri;

    public LocalLlamaServer(
        string executablePath,
        string modelPath,
        string outputDirectory,
        int threads,
        int reasoningBudget = 0)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _modelPath = Path.GetFullPath(modelPath);
        _outputDirectory = Path.GetFullPath(outputDirectory);
        _threads = threads;
        _reasoningBudget = Math.Max(0, reasoningBudget);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public TimeSpan StartupTime { get; private set; }

    public int ProcessId => _processStarted ? _process?.Id ?? 0 : 0;

    public long WorkingSetBytes
    {
        get
        {
            _process?.Refresh();
            return _process is { HasExited: false } ? _process.WorkingSet64 : 0;
        }
    }

    public long PrivateMemoryBytes
    {
        get
        {
            _process?.Refresh();
            return _process is { HasExited: false } ? _process.PrivateMemorySize64 : 0;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException(
                "The local model server is already started or still requires cleanup. "
                + "Call DisposeAsync before retrying startup.");
        }

        Directory.CreateDirectory(_outputDirectory);
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        _baseUri = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-m", _modelPath,
                     "--alias", _modelAlias,
                     "--host", "127.0.0.1",
                     "--port", port.ToString(),
                     "-c", "2048",
                     "-t", _threads.ToString(),
                     "-tb", _threads.ToString(),
                     "-np", "1",
                     "-ngl", "0",
                     "--reasoning", _reasoningBudget > 0 ? "on" : "off",
                     "--reasoning-budget", _reasoningBudget.ToString(),
                     "--api-key", _apiKey,
                     "--no-webui"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        _process.OutputDataReceived += (_, eventArgs) => AppendLine(standardOutput, eventArgs.Data);
        _process.ErrorDataReceived += (_, eventArgs) => AppendLine(standardError, eventArgs.Data);

        var stopwatch = Stopwatch.StartNew();
        Exception? startupFailure = null;
        try
        {
            // Keep the selected port bound until the last possible moment before launch.
            portReservation.Stop();
            if (!_process.Start())
            {
                throw new InvalidOperationException("The local model server could not be started.");
            }

            _processStarted = true;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (!await IsHealthyAsync(startupTimeout.Token))
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        "The local model server exited during startup."
                        + Environment.NewLine
                        + standardError);
                }

                await Task.Delay(100, startupTimeout.Token);
            }

            StartupTime = stopwatch.Elapsed;
        }
        catch (Exception exception)
        {
            startupFailure = exception;
        }
        finally
        {
            portReservation.Stop();
            stopwatch.Stop();
        }

        if (startupFailure is not null)
        {
            var failures = new List<Exception> { startupFailure };
            await CaptureFailureAsync(StopProcessAsync, failures);
            await CaptureFailureAsync(
                () => WriteStartupLogsAsync(standardOutput, standardError),
                failures);
            ThrowStartupFailures(failures);
            return;
        }

        try
        {
            await WriteStartupLogsAsync(standardOutput, standardError);
        }
        catch (Exception logFailure)
        {
            var failures = new List<Exception> { logFailure };
            await CaptureFailureAsync(StopProcessAsync, failures);
            ThrowStartupFailures(failures);
        }
    }

    public async Task<LocalModelDecision> EvaluateAsync(
        LocalTextCleanupCase example,
        ReadingUnit unit,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        var instruction = string.IsNullOrWhiteSpace(example.Instruction)
            ? "No additional case instruction."
            : example.Instruction;
        var missingFromUnit = LocalProtectedTermMatcher.FindMissingTerms(
            unit.OriginalText,
            example.RequiredTerms);
        var requiredForUnit = example.RequiredTerms
            .Where(term => !missingFromUnit.Contains(term, StringComparer.Ordinal))
            .ToArray();
        var userPrompt = $"""
                          Unit kind: {unit.Kind}
                          Case instruction: {instruction}
                          Required content in this unit: {(requiredForUnit.Length == 0 ? "None." : string.Join(", ", requiredForUnit))}

                          Original unit:
                          <original>
                          {unit.OriginalText}
                          </original>

                          Deterministic spoken unit:
                          <deterministic>
                          {unit.SpokenText}
                          </deterministic>
                          """;
        return await SendDecisionRequestAsync(userPrompt, cancellationToken);
    }

    public async Task<LocalSenseClassification> ClassifySenseAsync(
        LocalSenseCase example,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        var markedContext = MarkTargetOccurrence(
            example.Context,
            example.TargetWord,
            example.TargetOccurrence);
        var userPrompt = $"""
                          Target word: {example.TargetWord}
                          Allowed labels:
                          {string.Join(Environment.NewLine, example.Labels.Select(label => $"- {label.Name}: {label.Definition}"))}

                          Context (the exact target is marked):
                          <context>
                          {markedContext}
                          </context>

                          """;
        var request = new
        {
            temperature = 0,
            seed = 42,
            max_tokens = Math.Max(24, _reasoningBudget + 24),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = SenseClassificationPrompt
                },
                new
                {
                    role = "user",
                    content = userPrompt
                }
            }
        };
        var response = await SendRequestAsync(request, cancellationToken);
        var candidateLabel = response.MessageContent.Trim();
        var matchingLabels = example.Labels
            .Where(candidate => Regex.IsMatch(
                candidateLabel,
                $@"(?<![A-Z0-9_]){Regex.Escape(candidate.Name)}(?![A-Z0-9_])",
                RegexOptions.CultureInvariant))
            .Select(candidate => candidate.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var label = matchingLabels.Length == 1 ? matchingLabels[0] : null;

        return new LocalSenseClassification(
            label,
            response.ElapsedMilliseconds,
            response.PromptTokens,
            response.CompletionTokens,
            response.TokensPerSecond,
            response.MessageContent);
    }

    internal static string MarkTargetOccurrence(
        string context,
        string targetWord,
        int occurrence)
    {
        var matches = Regex.Matches(
            context,
            $@"\b{Regex.Escape(targetWord)}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (occurrence < 1 || occurrence > matches.Count)
        {
            throw new InvalidOperationException(
                $"Target occurrence {occurrence} of '{targetWord}' was not found in '{context}'.");
        }

        var marked = new StringBuilder(context.Length + 32);
        var sourceIndex = 0;
        for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
        {
            var match = matches[matchIndex];
            marked.Append(context, sourceIndex, match.Index - sourceIndex);
            if (matchIndex == occurrence - 1)
            {
                marked.Append("<target>").Append(match.Value).Append("</target>");
            }
            else
            {
                marked.Append("<other-occurrence/>");
            }

            sourceIndex = match.Index + match.Length;
        }

        marked.Append(context, sourceIndex, context.Length - sourceIndex);
        return marked.ToString();
    }

    public async Task<LocalTargetedTransformation> TransformAsync(
        LocalTransformationCase example,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        var userPrompt = $"""
                          Task: {example.Instruction}

                          Input:
                          <input>
                          {example.Input}
                          </input>
                          """;
        var request = new
        {
            temperature = 0,
            seed = 42,
            max_tokens = 256,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "targeted_tts_transformation",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            spokenText = new { type = "string" }
                        },
                        required = new[] { "spokenText" },
                        additionalProperties = false
                    }
                }
            },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = TargetedTransformationPrompt
                },
                new
                {
                    role = "user",
                    content = userPrompt
                }
            }
        };
        var response = await SendRequestAsync(request, cancellationToken);
        string? spokenText = null;
        try
        {
            using var decision = JsonDocument.Parse(response.MessageContent);
            spokenText = decision.RootElement.GetProperty("spokenText").GetString();
        }
        catch (JsonException)
        {
            // The report retains the raw output for diagnosis.
        }

        return new LocalTargetedTransformation(
            spokenText,
            response.ElapsedMilliseconds,
            response.PromptTokens,
            response.CompletionTokens,
            response.TokensPerSecond,
            response.MessageContent);
    }

    public async Task<LocalCancellationProbe> RunCancellationProbeAsync(
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        var longPrompt = """
                         Unit kind: StructuredBlock
                         Case instruction: Replace this synthetic benchmark with a complete spoken version. Preserve every numbered item.

                         Original unit:
                         <original>
                         """ + string.Join(
                             Environment.NewLine,
                             Enumerable.Range(1, 40).Select(
                                 index => $"Item {index}: preserve benchmark value {index * 17}."))
                         + """

                           </original>

                           Deterministic spoken unit:
                           <deterministic>
                           Code block.
                           </deterministic>
                           """;

        var stopwatch = Stopwatch.StartNew();
        var canceled = false;
        long cancellationSignaledTimestamp = 0;
        long requestCompletedTimestamp = 0;
        using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            using var cancellationRegistration = requestCancellation.Token.Register(() =>
            {
                Interlocked.CompareExchange(
                    ref cancellationSignaledTimestamp,
                    Stopwatch.GetTimestamp(),
                    0);
            });
            requestCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            try
            {
                await SendDecisionRequestAsync(longPrompt, requestCancellation.Token, maxTokens: 512);
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                canceled = true;
            }
            finally
            {
                requestCompletedTimestamp = Stopwatch.GetTimestamp();
                stopwatch.Stop();
            }
        }

        var cancellationAttemptMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var signaledTimestamp = Interlocked.Read(ref cancellationSignaledTimestamp);
        double? cancellationResponseMilliseconds = canceled && signaledTimestamp != 0
            ? Stopwatch.GetElapsedTime(
                    signaledTimestamp,
                    requestCompletedTimestamp)
                .TotalMilliseconds
            : null;

        var readinessStopwatch = Stopwatch.StartNew();
        await Task.Delay(150, cancellationToken);
        var healthyAfterCancellation = await IsHealthyAsync(cancellationToken);
        readinessStopwatch.Stop();
        var recovery = await SendDecisionRequestAsync(
            """
            Unit kind: Sentence
            Case instruction: No additional case instruction.

            Original unit:
            <original>
            Pulse Reader remains responsive.
            </original>

            Deterministic spoken unit:
            <deterministic>
            Pulse Reader remains responsive.
            </deterministic>
            """,
            cancellationToken);
        return new LocalCancellationProbe(
            canceled,
            cancellationAttemptMilliseconds,
            cancellationResponseMilliseconds,
            readinessStopwatch.Elapsed.TotalMilliseconds,
            healthyAfterCancellation,
            recovery.ElapsedMilliseconds,
            recovery.Action);
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessAsync();
        _httpClient.Dispose();
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        if (!_processStarted)
        {
            ReleaseProcess(process);
            return;
        }

        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the state check and the kill request.
            }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Local model server process {process.Id} did not exit within five seconds. "
                + "The process handle was retained so cleanup can be retried.",
                exception);
        }

        if (!process.HasExited)
        {
            throw new InvalidOperationException(
                $"Local model server process {process.Id} remained active after shutdown. "
                + "The process handle was retained so cleanup can be retried.");
        }

        ReleaseProcess(process);
    }

    private void ReleaseProcess(Process process)
    {
        process.Dispose();
        if (ReferenceEquals(_process, process))
        {
            _process = null;
            _processStarted = false;
            _baseUri = null;
        }
    }

    private async Task WriteStartupLogsAsync(
        StringBuilder standardOutput,
        StringBuilder standardError)
    {
        var failures = new List<Exception>();
        await CaptureFailureAsync(
            () => File.WriteAllTextAsync(
                Path.Combine(_outputDirectory, "llama-server.stdout.log"),
                standardOutput.ToString(),
                CancellationToken.None),
            failures);
        await CaptureFailureAsync(
            () => File.WriteAllTextAsync(
                Path.Combine(_outputDirectory, "llama-server.stderr.log"),
                standardError.ToString(),
                CancellationToken.None),
            failures);

        if (failures.Count > 0)
        {
            ThrowStartupFailures(failures);
        }
    }

    private static async Task CaptureFailureAsync(
        Func<Task> operation,
        ICollection<Exception> failures)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowStartupFailures(IReadOnlyCollection<Exception> failures)
    {
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures.Single()).Throw();
        }

        throw new AggregateException(
            "Local model server startup failed, and one or more cleanup or log operations "
            + "also failed. Any retained process handle can be retried through DisposeAsync.",
            failures);
    }

    private async Task<LocalModelDecision> SendDecisionRequestAsync(
        string userPrompt,
        CancellationToken cancellationToken,
        int maxTokens = 512)
    {
        var request = new
        {
            temperature = 0,
            seed = 42,
            max_tokens = maxTokens,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "tts_cleanup",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", @enum = new[] { "keep", "replace" } },
                            spokenText = new { type = new[] { "string", "null" } },
                            reason = new { type = "string" }
                        },
                        required = new[] { "action", "spokenText", "reason" },
                        additionalProperties = false
                    }
                }
            },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = SystemPrompt
                },
                new
                {
                    role = "user",
                    content = userPrompt
                }
            }
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");
        var stopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.PostAsync(
            new Uri(_baseUri!, "v1/chat/completions"),
            content,
            cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The local model server returned {(int)response.StatusCode}: {responseJson}");
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var messageContent = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?? throw new InvalidOperationException("The local model returned an empty decision.");
        var usage = root.GetProperty("usage");
        var timings = root.TryGetProperty("timings", out var timingElement)
            ? timingElement
            : default;
        LocalModelWireDecision wireDecision;
        try
        {
            wireDecision = JsonSerializer.Deserialize<LocalModelWireDecision>(
                               messageContent,
                               JsonOptions)
                           ?? throw new JsonException("The decision was empty.");
        }
        catch (JsonException exception)
        {
            wireDecision = new LocalModelWireDecision(
                "invalid",
                null,
                "The model returned malformed structured output: " + exception.Message);
        }

        return new LocalModelDecision(
            wireDecision.Action,
            wireDecision.SpokenText,
            wireDecision.Reason,
            stopwatch.Elapsed.TotalMilliseconds,
            usage.GetProperty("prompt_tokens").GetInt32(),
            usage.GetProperty("completion_tokens").GetInt32(),
            timings.ValueKind == JsonValueKind.Object
            && timings.TryGetProperty("predicted_per_second", out var tokensPerSecond)
                ? tokensPerSecond.GetDouble()
                : 0,
            messageContent);
    }

    private async Task<LocalStructuredResponse> SendRequestAsync(
        object request,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");
        var stopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.PostAsync(
            new Uri(_baseUri!, "v1/chat/completions"),
            content,
            cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The local model server returned {(int)response.StatusCode}: {responseJson}");
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var messageContent = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?? throw new InvalidOperationException("The local model returned an empty response.");
        var usage = root.GetProperty("usage");
        var timings = root.TryGetProperty("timings", out var timingElement)
            ? timingElement
            : default;
        return new LocalStructuredResponse(
            messageContent,
            stopwatch.Elapsed.TotalMilliseconds,
            usage.GetProperty("prompt_tokens").GetInt32(),
            usage.GetProperty("completion_tokens").GetInt32(),
            timings.ValueKind == JsonValueKind.Object
            && timings.TryGetProperty("predicted_per_second", out var tokensPerSecond)
                ? tokensPerSecond.GetDouble()
                : 0);
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        var baseUri = _baseUri;
        if (process is null
            || !_processStarted
            || process.HasExited
            || baseUri is null)
        {
            return false;
        }

        try
        {
            using var healthResponse = await _httpClient.GetAsync(
                new Uri(baseUri, "health"),
                cancellationToken);
            if (healthResponse.StatusCode != HttpStatusCode.OK || process.HasExited)
            {
                return false;
            }

            // /health may be unauthenticated. Require this launch's random API key and
            // model alias before accepting the responder as our child.
            using var identityResponse = await _httpClient.GetAsync(
                new Uri(baseUri, "v1/models"),
                cancellationToken);
            if (identityResponse.StatusCode != HttpStatusCode.OK || process.HasExited)
            {
                return false;
            }

            var identityJson = await identityResponse.Content.ReadAsStringAsync(
                cancellationToken);
            using var identity = JsonDocument.Parse(identityJson);
            return identity.RootElement.TryGetProperty("data", out var models)
                   && models.ValueKind == JsonValueKind.Array
                   && models.GetArrayLength() > 0
                   && models.EnumerateArray().Any(model =>
                       model.TryGetProperty("id", out var id)
                       && id.ValueKind == JsonValueKind.String
                       && string.Equals(
                           id.GetString(),
                           _modelAlias,
                           StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return false;
        }
    }

    private void EnsureStarted()
    {
        if (_process is null
            || !_processStarted
            || _process.HasExited
            || _baseUri is null)
        {
            throw new InvalidOperationException("The local model server is not running.");
        }
    }

    private static void AppendLine(StringBuilder buffer, string? line)
    {
        if (line is not null)
        {
            buffer.AppendLine(line);
        }
    }

    private const string SystemPrompt = """
        You are a conservative text-to-speech cleanup stage. You receive one already segmented reading unit and Pulse Reader's deterministic spoken version.

        Return action "keep" when the deterministic version is already faithful and natural. Return action "replace" only when a complete replacement would make speech materially clearer. For "keep", spokenText must be null. For "replace", spokenText must contain the complete replacement for this unit.

        Rules:
        - Produce spoken text, not commentary about the text.
        - Preserve meaning, order, names, numbers, units, addresses, paths, URLs, identifiers, and factual details.
        - Judge fidelity against the original unit, not only whether the deterministic version sounds fluent.
        - Replace when the deterministic version omits meaningful source content that the case instruction requires.
        - A case-specific pronunciation instruction describes an observed speech defect. Apply the smallest familiar phonetic respelling that fixes it.
        - If a pronunciation instruction reports the wrong sense, returning the original ambiguous spelling does not fix it.
        - Never summarize ordinary prose, add explanations, translate, censor, or invent content.
        - For a real code block only, a short literal description is allowed when the case instruction explicitly permits it.
        - A code replacement must be speakable words. Do not copy code syntax or punctuation as spokenText.
        - Preserve useful deterministic pronunciations and pauses.
        - Do not announce visual markup unless it provides meaning that a listener needs.
        - Make the smallest useful change.
        - Keep the reason to one short sentence.
        """;

    private const string SenseClassificationPrompt = """
        Classify the intended meaning of one target word from its context.

        Return exactly one supplied label as plain text, with no JSON, punctuation, copied definition, explanation, or additional words. If a label were named LABEL_NAME, the entire valid response would be LABEL_NAME; "LABEL_NAME: its definition" would be invalid. Do not rewrite the sentence or suggest a pronunciation. Classify only the word enclosed by the target tags and base the choice on its grammatical role and governing words. Other copies of the same spelling are replaced by <other-occurrence/> so they cannot be mistaken for the target.
        """;

    private const string TargetedTransformationPrompt = """
        Convert the supplied technical structure into faithful spoken text by following the task exactly.

        The caller has already decided that transformation is required. Do not decline, classify, summarize beyond the requested form, or comment on the task. Preserve every fact, value, identifier, relationship, and order that the task requires. Output only the complete spoken text.
        """;

    private sealed record LocalModelWireDecision(
        string Action,
        string? SpokenText,
        string Reason);
}

internal sealed record LocalModelDecision(
    string Action,
    string? SpokenText,
    string Reason,
    double ElapsedMilliseconds,
    int PromptTokens,
    int CompletionTokens,
    double TokensPerSecond,
    string RawJson);

internal sealed record LocalCancellationProbe(
    bool RequestCanceled,
    double CancellationAttemptMilliseconds,
    double? CancellationResponseMilliseconds,
    double RecoveryReadinessMilliseconds,
    bool ServerHealthyAfterCancellation,
    double RecoveryRequestMilliseconds,
    string RecoveryAction)
{
    // Keep the existing report readers source-compatible while exposing precise timing fields.
    [JsonIgnore]
    public double CancellationMilliseconds =>
        CancellationResponseMilliseconds ?? CancellationAttemptMilliseconds;

    [JsonIgnore]
    public bool ServerHealthy => ServerHealthyAfterCancellation;

    [JsonIgnore]
    public double RecoveryMilliseconds => RecoveryRequestMilliseconds;
}

internal sealed record LocalSenseClassification(
    string? Label,
    double ElapsedMilliseconds,
    int PromptTokens,
    int CompletionTokens,
    double TokensPerSecond,
    string RawJson);

internal sealed record LocalTargetedTransformation(
    string? SpokenText,
    double ElapsedMilliseconds,
    int PromptTokens,
    int CompletionTokens,
    double TokensPerSecond,
    string RawJson);

internal sealed record LocalStructuredResponse(
    string MessageContent,
    double ElapsedMilliseconds,
    int PromptTokens,
    int CompletionTokens,
    double TokensPerSecond);
