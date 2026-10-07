using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class LocalRuntimeEndpoint
{
    private string? _confirmedBackend;
    private Func<string?>? _confirmedBackendReader;
    private Func<bool>? _vulkanDriverUnavailableReader;

    public LocalRuntimeEndpoint(
        Uri baseUri,
        string apiKey,
        string model,
        string? runtimeKey = null,
        string? backend = null,
        string? confirmedBackend = null,
        bool supportsChatCompletionTokenCount = false)
    {
        if (baseUri is null || !baseUri.IsLoopback || baseUri.Scheme != Uri.UriSchemeHttp || baseUri.Port <= 0)
            throw new ArgumentException("本地运行时端点必须是带端口的 loopback HTTP 地址。", nameof(baseUri));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("本地运行时 API Key 不能为空。", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("本地运行时模型不能为空。", nameof(model));
        BaseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/");
        ApiKey = apiKey;
        Model = model;
        RuntimeKey = runtimeKey ?? model;
        BackendCandidate = backend ?? "本地";
        _confirmedBackend = confirmedBackend is "CPU" or "Vulkan" ? confirmedBackend : null;
        SupportsChatCompletionTokenCount = supportsChatCompletionTokenCount;
    }

    public Uri BaseUri { get; }
    public string ApiKey { get; }
    public string Model { get; }
    public string RuntimeKey { get; }
    public bool SupportsChatCompletionTokenCount { get; }
    /// <summary>Runtime package selected by the manager; this alone does not prove device use.</summary>
    public string BackendCandidate { get; }
    /// <summary>Backend confirmed by package policy or a safe startup-log observation.</summary>
    public string? ConfirmedBackend => _confirmedBackendReader?.Invoke() ?? _confirmedBackend;
    public bool VulkanDriverUnavailable => _vulkanDriverUnavailableReader?.Invoke() ?? false;
    /// <summary>Compatibility alias for the historical candidate label.</summary>
    public string DisplayBackend => ConfirmedBackend == "CPU" && BackendCandidate == "Vulkan" && VulkanDriverUnavailable
        ? "CPU（Vulkan 驱动不可用）"
        : ConfirmedBackend ?? (BackendCandidate == "Vulkan"
            ? "Vulkan 候选（实际设备未确认）"
            : BackendCandidate == "CPU" ? "CPU 候选（实际后端未确认）" : BackendCandidate);

    internal void AttachBackendObservation(Func<string?> backendReader, Func<bool> driverUnavailableReader)
    {
        _confirmedBackendReader = backendReader ?? throw new ArgumentNullException(nameof(backendReader));
        _vulkanDriverUnavailableReader = driverUnavailableReader ?? throw new ArgumentNullException(nameof(driverUnavailableReader));
    }
}

public interface ILocalRuntimeHost
{
    Task<LocalRuntimeEndpoint> EnsureStartedAsync(ProviderProfile profile, CancellationToken cancellationToken = default);
    void Stop();
}

public sealed record LocalRuntimeMetrics(
    double? StartupToReadyMilliseconds,
    long? PeakWorkingSetBytes,
    bool IsFirstRequestAfterStart = false);

public interface ILocalRuntimeDiagnostics
{
    LocalRuntimeMetrics? GetMetrics();
}

public interface ILocalRuntimeRequestDiagnostics
{
    LocalRuntimeMetrics? CaptureRequestMetrics();
}

public interface ILocalRuntimeMetricsClient
{
    LocalRuntimeMetrics? GetRuntimeMetrics();
}

internal sealed record LocalRuntimeCandidate(string ExecutablePath, bool UseVulkan);

/// <summary>Retains only the minimum signal needed to distinguish GPU offload from a candidate package.</summary>
internal sealed class LocalRuntimeBackendObservation
{
    private static readonly Regex VulkanDeviceBuffer = new(
        @"\bVulkan\d+\s+model buffer size\s*=\s*(?<mib>\d+(?:\.\d+)?)\s+MiB\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VulkanDriverUnavailableSignal = new(
        @"(?:vkCreateInstance:\s*Found no drivers!|ggml_vulkan:\s*No devices found\.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ModelLoadedSignal = new(
        @"\bmodel loaded\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly bool _useVulkanCandidate;
    private readonly object _gate = new();
    private readonly TaskCompletionSource<string> _confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _vulkanDriverUnavailable;
    private bool _modelLoaded;
    private bool _vulkanDeviceBufferObserved;

    public LocalRuntimeBackendObservation(bool useVulkanCandidate) => _useVulkanCandidate = useVulkanCandidate;

    public void Observe(string? line)
    {
        if (!_useVulkanCandidate || string.IsNullOrEmpty(line)) return;
        string? confirmed;
        lock (_gate)
        {
            if (VulkanDriverUnavailableSignal.IsMatch(line)) _vulkanDriverUnavailable = true;
            if (ModelLoadedSignal.IsMatch(line)) _modelLoaded = true;
            var bufferMatch = VulkanDeviceBuffer.Match(line);
            if (bufferMatch.Success &&
                double.TryParse(bufferMatch.Groups["mib"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var bufferMiB) && bufferMiB > 0)
                _vulkanDeviceBufferObserved = true;
            confirmed = GetConfirmedBackendCore();
        }
        if (confirmed is not null) _confirmation.TrySetResult(confirmed);
    }

    public string? GetConfirmedBackend()
    {
        if (!_useVulkanCandidate) return "CPU";
        lock (_gate) return GetConfirmedBackendCore();
    }

    public bool VulkanDriverUnavailable { get { lock (_gate) return _vulkanDriverUnavailable; } }

    private string? GetConfirmedBackendCore()
    {
        if (_vulkanDriverUnavailable && _vulkanDeviceBufferObserved) return null;
        if (_vulkanDeviceBufferObserved) return "Vulkan";
        return _vulkanDriverUnavailable && _modelLoaded ? "CPU" : null;
    }

    public async Task<string?> WaitForConfirmationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_useVulkanCandidate) return "CPU";
        if (GetConfirmedBackend() is { } confirmed) return confirmed;
        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(_confirmation.Task, delay).ConfigureAwait(false);
        if (completed == _confirmation.Task) return await _confirmation.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return GetConfirmedBackend();
    }
}

public static class LocalRuntimePaths
{
    public static string DefaultInstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huaxiazi", "runtimes", "local");

    public static string LegacyRoot => Path.Combine(AppContext.BaseDirectory, "runtimes", "local");

    public static string GetRuntimeRoot() => ResolveRuntimeRoot(DefaultInstallRoot, LegacyRoot);

    internal static string ResolveRuntimeRoot(string installRoot, string legacyRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        installRoot = Path.GetFullPath(installRoot);
        legacyRoot = Path.GetFullPath(legacyRoot);
        if (HasRuntime(installRoot) || !HasRuntime(legacyRoot)) return installRoot;
        return legacyRoot;
    }

    internal static bool HasRuntime(string root) =>
        LocalRuntimePackageService.ResolveInstalledExecutable(root, LocalRuntimeFlavor.Vulkan) is not null ||
        LocalRuntimePackageService.ResolveInstalledExecutable(root, LocalRuntimeFlavor.Cpu) is not null;
}

public static class LocalRuntimeCommandBuilder
{
    public const string ApiKeyEnvironmentVariable = "LLAMA_API_KEY";

    public static ProcessStartInfo BuildStartInfo(
        string executablePath,
        string modelPath,
        string? adapterPath,
        LocalRuntimeOptions options,
        int port,
        string apiKey,
        bool useVulkan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ArgumentNullException.ThrowIfNull(options);
        options.Normalize();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
        };
        Add(startInfo, "--model", modelPath);
        Add(startInfo, "--host", "127.0.0.1");
        Add(startInfo, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Do not put the ephemeral credential in the command line: same-user processes can
        // read ProcessStartInfo arguments through WMI. The child receives it through its
        // environment instead, which keeps it out of process listings and shell history.
        startInfo.Environment[ApiKeyEnvironmentVariable] = apiKey;
        Add(startInfo, "--no-webui");
        Add(startInfo, "--ctx-size", options.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "--batch-size", options.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "--gpu-layers", useVulkan && options.GpuMode != LocalGpuMode.Off ? "all" : "0");
        if (useVulkan)
        {
            Add(startInfo, "--verbosity", "4");
            var loaderDebug = Environment.GetEnvironmentVariable("VK_LOADER_DEBUG");
            startInfo.Environment["VK_LOADER_DEBUG"] = EnsureVulkanLoaderErrorLogging(loaderDebug);
        }
        if (options.CpuThreads > 0) Add(startInfo, "--threads", options.CpuThreads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(adapterPath))
            Add(startInfo, "--lora-scaled", adapterPath + ":" + options.AdapterScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    internal static string EnsureVulkanLoaderErrorLogging(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing)) return "error";
        var levels = existing.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return levels.Any(level => string.Equals(level, "all", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
            ? existing
            : existing + ",error";
    }

    private static void Add(ProcessStartInfo info, string name, string? value = null)
    {
        info.ArgumentList.Add(name);
        if (value is not null) info.ArgumentList.Add(value);
    }
}

public sealed class LocalTextGenerationClient : ITextGenerationClient, IStructuredTextGenerationClient, ILocalRuntimeMetricsClient, IDisposable
{
    private readonly ILocalRuntimeHost _runtimeHost;
    private readonly ProviderProfile _profile;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Action<ProviderRequestTelemetry>? _telemetryObserver;

    public LocalTextGenerationClient(
        ILocalRuntimeHost runtimeHost,
        ProviderProfile profile,
        HttpClient? httpClient = null,
        Action<ProviderRequestTelemetry>? telemetryObserver = null)
    {
        _runtimeHost = runtimeHost ?? throw new ArgumentNullException(nameof(runtimeHost));
        _profile = profile?.Clone() ?? throw new ArgumentNullException(nameof(profile));
        if (_profile.Platform != ProviderPlatform.ManagedLocal || _profile.Type != ProviderType.Local)
            throw new ArgumentException("本地客户端只能绑定 ManagedLocal 配置。", nameof(profile));
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsClient = httpClient is null;
        _telemetryObserver = telemetryObserver;
    }

    public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) =>
        GenerateCoreAsync(systemPrompt, userInput, null, cancellationToken);

    public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonSchema)) throw new ArgumentException("jsonSchema 不能为空。", nameof(jsonSchema));
        using var _ = JsonDocument.Parse(jsonSchema);
        return GenerateCoreAsync(systemPrompt, userInput, jsonSchema, cancellationToken);
    }

    public LocalRuntimeMetrics? GetRuntimeMetrics() =>
        (_runtimeHost as ILocalRuntimeDiagnostics)?.GetMetrics();

    private async Task<string> GenerateCoreAsync(string systemPrompt, string userInput, string? jsonSchema, CancellationToken cancellationToken)
    {
        LocalRuntimeEndpoint endpoint;
        try
        {
            endpoint = await _runtimeHost.EnsureStartedAsync(_profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FileNotFoundException or TimeoutException or InvalidDataException or InvalidOperationException)
        {
            throw new GenerationFailureException(
                GenerationFailureKind.ProviderUnavailable,
                $"本地模型无法启动：{exception.Message}",
                isTransient: false,
                innerException: exception);
        }
        var localOptions = _profile.LocalRuntimeOptions ?? new LocalRuntimeOptions();
        localOptions.Normalize();
        var maxTokens = Math.Clamp(_profile.MaxTokens, 128, 32768);
        var body = new Dictionary<string, object?>
        {
            ["model"] = endpoint.Model,
            ["messages"] = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userInput } },
            ["temperature"] = Math.Clamp(_profile.Temperature, 0, 2),
            ["top_p"] = Math.Clamp(_profile.TopP, 0, 1),
            ["max_tokens"] = maxTokens,
            ["seed"] = localOptions.Seed,
            ["repeat_penalty"] = localOptions.RepeatPenalty,
            ["stream"] = false
        };
        if (jsonSchema is not null)
        {
            using var schemaDocument = JsonDocument.Parse(jsonSchema);
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "huaxiazi_output", strict = true, schema = schemaDocument.RootElement.Clone() }
            };
        }
        var bodyJson = JsonSerializer.Serialize(body);
        var stopwatch = _telemetryObserver is null ? null : Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        ProviderUsageMetadata? usage = null;
        var telemetryObserved = false;
        int? preflightStatusCode = null;
        var preflightCompleted = !endpoint.SupportsChatCompletionTokenCount;
        string? contextPreflightOutcome = endpoint.SupportsChatCompletionTokenCount ? "unavailable" : null;
        try
        {
            if (endpoint.SupportsChatCompletionTokenCount)
            {
                var inputTokens = await TryCountInputTokensAsync(endpoint, bodyJson, cancellationToken).ConfigureAwait(false);
                preflightCompleted = true;
                preflightStatusCode = inputTokens?.StatusCode;
                if (inputTokens is { } count)
                {
                    contextPreflightOutcome = "counted";
                    if ((long)count.InputTokens + maxTokens > localOptions.ContextSize)
                    {
                        contextPreflightOutcome = "exceeded";
                        ObserveRequest(stopwatch?.Elapsed.TotalMilliseconds ?? 0,
                            new ProviderUsageMetadata(count.InputTokens, null, null), count.StatusCode,
                            "context_limit_exceeded", endpoint.ConfirmedBackend, contextPreflightOutcome);
                        telemetryObserved = true;
                        throw new GenerationFailureException(GenerationFailureKind.ContextLimitExceeded,
                            $"本地请求需要至少 {count.InputTokens} 个输入 token，并预留 {maxTokens} 个输出 token，超过当前 {localOptions.ContextSize} token 上下文窗口。请缩短输入、降低输出上限或选择更大上下文的模型。",
                            isTransient: false);
                    }
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, "v1/chat/completions"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                var kind = statusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GenerationFailureKind.Authentication,
                    HttpStatusCode.NotFound => GenerationFailureKind.ResourceMissing,
                    HttpStatusCode.TooManyRequests => GenerationFailureKind.RateLimited,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => GenerationFailureKind.Timeout,
                    _ when (int)statusCode >= 500 => GenerationFailureKind.ProviderUnavailable,
                    _ => GenerationFailureKind.RequestRejected
                };
                var transient = kind is GenerationFailureKind.RateLimited or GenerationFailureKind.Timeout or GenerationFailureKind.ProviderUnavailable;
                throw new GenerationFailureException(kind, $"本地模型请求失败（HTTP {(int)statusCode}）。", statusCode, transient);
            }
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (_telemetryObserver is not null)
                usage = ProviderUsageMetadataParser.Parse(ProviderProtocol.OpenAICompatible, raw);
            var result = ParseContent(raw);
            ObserveRequest(stopwatch?.Elapsed.TotalMilliseconds ?? 0, usage, (int)response.StatusCode, "success", endpoint.ConfirmedBackend, contextPreflightOutcome);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!preflightCompleted && cancellationToken.IsCancellationRequested) contextPreflightOutcome = "cancelled";
            if (!telemetryObserved)
                ObserveRequest(stopwatch?.Elapsed.TotalMilliseconds ?? 0, usage, response is null ? preflightStatusCode : (int)response.StatusCode, "cancelled", endpoint.ConfirmedBackend, contextPreflightOutcome);
            throw;
        }
        catch
        {
            if (!telemetryObserved)
                ObserveRequest(stopwatch?.Elapsed.TotalMilliseconds ?? 0, usage, response is null ? preflightStatusCode : (int)response.StatusCode, "error", endpoint.ConfirmedBackend, contextPreflightOutcome);
            throw;
        }
        finally
        {
            stopwatch?.Stop();
            response?.Dispose();
            if (!localOptions.KeepLoaded) _runtimeHost.Stop();
        }
    }

    private async Task<(int InputTokens, int StatusCode)?> TryCountInputTokensAsync(
        LocalRuntimeEndpoint endpoint, string bodyJson, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(endpoint.BaseUri, "v1/chat/completions/input_tokens"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > 8_192) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[1_024];
            while (buffer.Length <= 8_192)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0,
                    (int)Math.Min(chunk.Length, 8_193 - buffer.Length)), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length > 8_192) return null;
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (!document.RootElement.TryGetProperty("input_tokens", out var tokenElement) ||
                !tokenElement.TryGetInt32(out var inputTokens) || inputTokens < 0) return null;
            return (inputTokens, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private void ObserveRequest(double latencyMilliseconds, ProviderUsageMetadata? usage, int? statusCode, string outcome,
        string? runtimeBackend, string? contextPreflightOutcome = null)
    {
        LocalRuntimeMetrics? runtimeMetrics = null;
        try
        {
            runtimeMetrics = (_runtimeHost as ILocalRuntimeRequestDiagnostics)?.CaptureRequestMetrics();
        }
        catch
        {
            // Runtime metrics are optional and must never alter local generation behavior.
        }

        if (_telemetryObserver is null) return;
        try
        {
            _telemetryObserver(new ProviderRequestTelemetry(
                usage?.RequestId,
                latencyMilliseconds,
                usage?.InputTokens,
                usage?.OutputTokens,
                statusCode,
                outcome,
                usage?.CacheReadInputTokens,
                usage?.CacheCreationInputTokens,
                runtimeBackend,
                runtimeMetrics?.StartupToReadyMilliseconds,
                runtimeMetrics?.PeakWorkingSetBytes,
                runtimeMetrics?.IsFirstRequestAfterStart,
                usage?.FinishReason,
                contextPreflightOutcome));
        }
        catch
        {
            // Optional telemetry must never alter local model generation behavior.
        }
    }

    private static string ParseContent(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) throw new InvalidOperationException("本地模型返回为空。");
            var message = choices[0].GetProperty("message");
            if (message.TryGetProperty("content", out var content))
            {
                var text = content.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            throw new InvalidOperationException("本地模型返回内容为空。");
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException("本地模型响应格式无效。", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("本地模型响应不是有效 JSON。", exception);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}

public sealed class LocalRuntimeManager : ILocalRuntimeHost, ILocalRuntimeDiagnostics, ILocalRuntimeRequestDiagnostics, IDisposable
{
    private readonly LocalModelStore _store;
    private readonly string _runtimeRoot;
    private readonly ILocalRuntimePrerequisiteService _prerequisites;
    private readonly IDisposable? _ownedPrerequisites;
    private readonly HttpClient _healthClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly object _sync = new();
    private Process? _process;
    private LocalRuntimeEndpoint? _endpoint;
    private double? _startupToReadyMilliseconds;
    private bool _startupRequestObservationPending;

    public LocalRuntimeManager(LocalModelStore store, string? runtimeRoot = null,
        ILocalRuntimePrerequisiteService? prerequisites = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtimeRoot = Path.GetFullPath(runtimeRoot ?? LocalRuntimePaths.GetRuntimeRoot());
        _prerequisites = prerequisites ?? new LocalRuntimePrerequisiteService();
        _ownedPrerequisites = prerequisites is null ? (IDisposable)_prerequisites : null;
    }

    public async Task<LocalRuntimeEndpoint> EnsureStartedAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Platform != ProviderPlatform.ManagedLocal || profile.Type != ProviderType.Local)
            throw new InvalidOperationException("当前配置不是受管本地模型。");
        var model = _store.GetInstalledModels().FirstOrDefault(item => item.InstallationId == profile.LocalModelInstallationId)
            ?? throw new InvalidOperationException("本地模型尚未安装或已被移除。");
        var adapter = string.IsNullOrWhiteSpace(profile.LocalAdapterInstallationId)
            ? null
            : _store.GetInstalledAdapters().FirstOrDefault(item => item.InstallationId == profile.LocalAdapterInstallationId);
        if (!string.IsNullOrWhiteSpace(profile.LocalAdapterInstallationId) && adapter is null)
            throw new InvalidOperationException("所选 LoRA 适配器尚未安装。");
        ValidateAdapterBinding(model, adapter);
        var options = profile.LocalRuntimeOptions ?? new LocalRuntimeOptions();
        options.Normalize();
        var candidates = ResolveRuntimeCandidates(_runtimeRoot, options.GpuMode);
        if (candidates.Count == 0)
            throw new FileNotFoundException("未找到与当前 GPU 设置兼容的本地推理运行时。", Path.Combine(_runtimeRoot, "cpu", "llama-server.exe"));
        lock (_sync)
        {
            if (_process is { HasExited: false } && _endpoint is not null &&
                candidates.Any(candidate => _endpoint.RuntimeKey == BuildRuntimeKey(
                    model.InstallationId, model.FilePath, adapter?.FilePath, options, candidate.UseVulkan, candidate.ExecutablePath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _endpoint;
            }
            StopLocked();
        }

        await _prerequisites.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var startupStopwatch = Stopwatch.StartNew();
        return await StartWithFallbackAsync(candidates, async (candidate, token) =>
        {
            var port = GetFreePort();
            var apiKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var runtimeKey = BuildRuntimeKey(model.InstallationId, model.FilePath, adapter?.FilePath, options, candidate.UseVulkan, candidate.ExecutablePath);
            var backendObservation = new LocalRuntimeBackendObservation(candidate.UseVulkan);
            var startInfo = LocalRuntimeCommandBuilder.BuildStartInfo(
                candidate.ExecutablePath, model.FilePath, adapter?.FilePath, options, port, apiKey, candidate.UseVulkan);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动本地推理运行时。");
            // Drain both pipes so verbose logs cannot block startup. The observer extracts
            // only backend evidence flags and a positive Vulkan buffer measurement, then
            // discards each original line; prompt/output text is never retained in diagnostics.
            process.OutputDataReceived += (_, eventArgs) => backendObservation.Observe(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => backendObservation.Observe(eventArgs.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var endpoint = new LocalRuntimeEndpoint(new Uri($"http://127.0.0.1:{port}"), apiKey,
                model.InstallationId, runtimeKey, candidate.UseVulkan ? "Vulkan" : "CPU",
                supportsChatCompletionTokenCount: LocalRuntimePackageCatalog.SupportsChatCompletionTokenCountEndpoint(candidate.ExecutablePath));
            endpoint.AttachBackendObservation(backendObservation.GetConfirmedBackend, () => backendObservation.VulkanDriverUnavailable);
            lock (_sync)
            {
                _process = process;
                _endpoint = endpoint;
            }

            try
            {
                await WaitForReadyAsync(_healthClient, endpoint, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                startupStopwatch.Stop();
                await backendObservation.WaitForConfirmationAsync(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                lock (_sync)
                {
                    _startupToReadyMilliseconds = startupStopwatch.Elapsed.TotalMilliseconds;
                    _startupRequestObservationPending = true;
                    _endpoint = endpoint;
                }
                return endpoint;
            }
            catch
            {
                lock (_sync) StopLocked();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_sync) StopLocked();
        _healthClient.Dispose();
        _ownedPrerequisites?.Dispose();
    }

    public void Stop() { lock (_sync) StopLocked(); }

    public LocalRuntimeMetrics? GetMetrics()
    {
        lock (_sync)
        {
            long? peakWorkingSetBytes = null;
            if (_process is { HasExited: false })
            {
                try
                {
                    var peak = _process.PeakWorkingSet64;
                    if (peak >= 0) peakWorkingSetBytes = peak;
                }
                catch
                {
                    // Process metrics can be unavailable under OS permissions or process exit races.
                }
            }
            return _startupToReadyMilliseconds.HasValue || peakWorkingSetBytes.HasValue
                ? new LocalRuntimeMetrics(_startupToReadyMilliseconds, peakWorkingSetBytes)
                : null;
        }
    }

    public LocalRuntimeMetrics? CaptureRequestMetrics()
    {
        lock (_sync)
        {
            if (_process is not { HasExited: false }) return null;
            long? peakWorkingSetBytes = null;
            try
            {
                var peak = _process.PeakWorkingSet64;
                if (peak >= 0) peakWorkingSetBytes = peak;
            }
            catch
            {
                // Process metrics can be unavailable under OS permissions or process exit races.
            }

            var startup = _startupRequestObservationPending ? _startupToReadyMilliseconds : null;
            var isFirstRequest = _startupRequestObservationPending;
            _startupRequestObservationPending = false;
            return startup.HasValue || peakWorkingSetBytes.HasValue || isFirstRequest
                ? new LocalRuntimeMetrics(startup, peakWorkingSetBytes, isFirstRequest)
                : null;
        }
    }

    internal static void ValidateAdapterBinding(InstalledLocalModel model, InstalledLocalAdapter? adapter)
    {
        if (adapter is not null && !string.Equals(model.Sha256, adapter.BaseModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选 LoRA 适配器与基础模型 SHA-256 不匹配。");
    }

    internal static IReadOnlyList<LocalRuntimeCandidate> ResolveRuntimeCandidates(string runtimeRoot, LocalGpuMode gpuMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var candidates = new List<LocalRuntimeCandidate>(capacity: 2);
        var vulkanPath = LocalRuntimePackageService.ResolveInstalledExecutable(runtimeRoot, LocalRuntimeFlavor.Vulkan);
        var cpuPath = LocalRuntimePackageService.ResolveInstalledExecutable(runtimeRoot, LocalRuntimeFlavor.Cpu);
        if (gpuMode != LocalGpuMode.Off && vulkanPath is not null)
            candidates.Add(new LocalRuntimeCandidate(vulkanPath, UseVulkan: true));
        if (cpuPath is not null)
            candidates.Add(new LocalRuntimeCandidate(cpuPath, UseVulkan: false));
        return candidates;
    }

    internal static async Task<T> StartWithFallbackAsync<T>(
        IReadOnlyList<LocalRuntimeCandidate> candidates,
        Func<LocalRuntimeCandidate, CancellationToken, Task<T>> startAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(startAsync);
        if (candidates.Count == 0) throw new InvalidOperationException("没有可启动的本地推理运行时。");

        for (var index = 0; index < candidates.Count; index++)
        {
            try
            {
                return await startAsync(candidates[index], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                index + 1 < candidates.Count &&
                candidates[index].UseVulkan &&
                !candidates.Skip(index + 1).Any(candidate => candidate.UseVulkan) &&
                !cancellationToken.IsCancellationRequested &&
                IsRuntimeStartupFailure(exception))
            {
                // Auto mode may fall back from a failed Vulkan startup to a later CPU candidate.
            }
        }

        throw new InvalidOperationException("本地推理运行时候选未能启动。");
    }

    private static bool IsRuntimeStartupFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or
        HttpRequestException or System.ComponentModel.Win32Exception or OperationCanceledException;

    internal static string BuildRuntimeKey(
        string modelInstallationId,
        string modelPath,
        string? adapterPath,
        LocalRuntimeOptions options,
        bool useVulkan,
        string? runtimeExecutablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelInstallationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(options);
        options.Normalize();
        return string.Join("|", [
            modelInstallationId,
            Path.GetFullPath(modelPath),
            adapterPath is null ? "" : Path.GetFullPath(adapterPath),
            useVulkan ? "vulkan" : "cpu",
            runtimeExecutablePath is null ? "" : Path.GetFullPath(runtimeExecutablePath),
            options.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.CpuThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.GpuMode.ToString(),
            options.BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.KeepLoaded ? "1" : "0",
            options.AdapterScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            options.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.RepeatPenalty.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        ]);
    }

    internal static bool IsRuntimeAvailable(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        return LocalRuntimePaths.HasRuntime(runtimeRoot);
    }

    internal static async Task WaitForReadyAsync(
        HttpClient healthClient,
        LocalRuntimeEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(healthClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseUri, "health"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                using var response = await healthClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("本地推理运行时启动超时。");
    }

    private void StopLocked()
    {
        _endpoint = null;
        _startupToReadyMilliseconds = null;
        _startupRequestObservationPending = false;
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch { }
        _process.Dispose();
        _process = null;
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
