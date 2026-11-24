using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulseReader.TextPreparation;

namespace PulseReader.Speech;

internal interface IContextualSenseClassifier : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task<string?> ClassifyAsync(
        ContextualPronunciationTarget target,
        CancellationToken cancellationToken);
}

internal sealed class ContextualPronunciationServer : IContextualSenseClassifier
{
    private const string ClassificationPrompt = """
        Classify the intended meaning of one target word from its context.

        Return exactly one supplied label as plain text, with no JSON, punctuation, copied definition, explanation, or additional words. Do not rewrite the sentence or suggest a pronunciation. Classify only the word enclosed by the target tags and base the choice on its grammatical role and governing words. Other copies of the same spelling are replaced by <other-occurrence/> so they cannot be mistaken for the target.
        """;

    private readonly string _executablePath;
    private readonly string _modelPath;
    private readonly string _logDirectory;
    private readonly int _threads;
    private readonly string _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _modelAlias = $"pulse-reader-{Guid.NewGuid():N}";
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly StringBuilder _standardOutput = new();
    private readonly StringBuilder _standardError = new();
    private Process? _process;
    private Uri? _baseUri;
    private bool _started;
    private bool _disposed;

    public ContextualPronunciationServer(
        string executablePath,
        string modelPath,
        string logDirectory,
        int? threads = null)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _modelPath = Path.GetFullPath(modelPath);
        _logDirectory = Path.GetFullPath(logDirectory);
        _threads = Math.Clamp(threads ?? Environment.ProcessorCount, 1, 8);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public int ProcessId => _started ? _process?.Id ?? 0 : 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("The contextual-pronunciation server is already started.");
        }

        Directory.CreateDirectory(_logDirectory);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
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
        // Published builds carry the VC runtime beside the application. Let the
        // optional child find it without installing a machine-wide prerequisite.
        startInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        startInfo.Environment["PATH"] = AppContext.BaseDirectory
            + Path.PathSeparator + inheritedPath;
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
                     "--reasoning", "off",
                     "--reasoning-budget", "0",
                     "--api-key", _apiKey,
                     "--no-webui"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutputData;
        _process.ErrorDataReceived += OnErrorData;

        try
        {
            reservation.Stop();
            if (!_process.Start())
            {
                throw new InvalidOperationException(
                    "The contextual-pronunciation server could not be started.");
            }

            _started = true;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (!await IsHealthyAsync(startupTimeout.Token))
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        "The contextual-pronunciation server exited during startup.");
                }

                await Task.Delay(100, startupTimeout.Token);
            }

            await WriteLogsAsync();
        }
        catch
        {
            StopProcess();
            await TryWriteLogsAsync();
            throw;
        }
        finally
        {
            reservation.Stop();
        }
    }

    public async Task<string?> ClassifyAsync(
        ContextualPronunciationTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureStarted();

        var markedContext = ContextualPronunciationRules.MarkTargetOccurrence(
            target.Context,
            target.Rule.Word,
            target.Occurrence);
        var userPrompt = $"""
                         Target word: {target.Rule.Word}
                         Allowed labels:
                         {string.Join(Environment.NewLine, target.Rule.Labels.Select(label => $"- {label.Name}: {label.Definition}"))}

                         Context (the exact target is marked):
                         <context>
                         {markedContext}
                         </context>
                         """;
        var request = new
        {
            temperature = 0,
            seed = 42,
            max_tokens = 24,
            messages = new object[]
            {
                new { role = "system", content = ClassificationPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.PostAsync(
            new Uri(_baseUri!, "v1/chat/completions"),
            content,
            cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The contextual-pronunciation server returned {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(responseJson);
        var label = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim();
        return target.Rule.Labels.Any(candidate =>
            string.Equals(candidate.Name, label, StringComparison.Ordinal))
            ? label
            : null;
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        var baseUri = _baseUri;
        if (process is null || !_started || process.HasExited || baseUri is null)
        {
            return false;
        }

        try
        {
            using var health = await _httpClient.GetAsync(
                new Uri(baseUri, "health"),
                cancellationToken);
            if (health.StatusCode != HttpStatusCode.OK || process.HasExited)
            {
                return false;
            }

            using var identityResponse = await _httpClient.GetAsync(
                new Uri(baseUri, "v1/models"),
                cancellationToken);
            if (identityResponse.StatusCode != HttpStatusCode.OK || process.HasExited)
            {
                return false;
            }

            var identityJson = await identityResponse.Content.ReadAsStringAsync(cancellationToken);
            using var identity = JsonDocument.Parse(identityJson);
            return identity.RootElement.TryGetProperty("data", out var models)
                   && models.ValueKind == JsonValueKind.Array
                   && models.EnumerateArray().Any(model =>
                       model.TryGetProperty("id", out var id)
                       && string.Equals(id.GetString(), _modelAlias, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return false;
        }
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is null || !_started || _process.HasExited || _baseUri is null)
        {
            throw new InvalidOperationException(
                "The contextual-pronunciation server is not running.");
        }
    }

    private void StopProcess()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (_started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and shutdown.
        }
        finally
        {
            process.OutputDataReceived -= OnOutputData;
            process.ErrorDataReceived -= OnErrorData;
            process.Dispose();
            _process = null;
            _baseUri = null;
            _started = false;
        }
    }

    private void OnOutputData(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data is not null)
        {
            lock (_standardOutput)
            {
                _standardOutput.AppendLine(eventArgs.Data);
            }
        }
    }

    private void OnErrorData(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data is not null)
        {
            lock (_standardError)
            {
                _standardError.AppendLine(eventArgs.Data);
            }
        }
    }

    private async Task WriteLogsAsync()
    {
        Directory.CreateDirectory(_logDirectory);
        string output;
        string error;
        lock (_standardOutput)
        {
            output = _standardOutput.ToString();
        }

        lock (_standardError)
        {
            error = _standardError.ToString();
        }

        await File.WriteAllTextAsync(
            Path.Combine(_logDirectory, "llama-server.stdout.log"),
            output);
        await File.WriteAllTextAsync(
            Path.Combine(_logDirectory, "llama-server.stderr.log"),
            error);
    }

    private async Task TryWriteLogsAsync()
    {
        try
        {
            await WriteLogsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must not turn an optional pronunciation failure into a speech failure.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess();
        _httpClient.Dispose();
    }
}
