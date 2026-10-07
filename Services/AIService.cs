using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class AIService : ITextGenerationClient, IStructuredTextGenerationClient, IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumErrorClassificationBytes = 64 * 1024;
    private static readonly HttpClient SharedClient = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        // API 密钥可能位于自定义协议头中；禁止 HttpClient 自动跨主机跟随 30x，
        // 否则重定向目标可能收到原始密钥。调用方会把重定向当作提供商错误处理。
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            // 生成请求仍由每次调用的 linked CancellationToken 按配置超时控制，
            // 但客户端本身不能无限期挂起，避免遗漏令牌时永久占用资源。
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _apiBase;
    private readonly string _model;
    private readonly string _resolvedModel;
    private readonly string _apiKey;
    private readonly ProviderType _providerType;
    private readonly ProviderPlatform _platform;
    private readonly ProviderProtocol _protocol;
    private readonly ProviderCapabilities _capabilities;
    private readonly TimeSpan _timeout;
    private readonly double _temperature;
    private readonly double _topP;
    private readonly int _maxTokens;
    private readonly int? _diagnosticSeed;
    private readonly InferenceLevel _inferenceLevel;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<ProviderRequestTelemetry>? _telemetryObserver;

    /// <summary>Validates a provider profile using the same fail-closed endpoint policy as generation.</summary>
    public static void ValidateProviderProfile(ProviderProfile profile, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!ProviderEndpointPolicy.TryValidate(profile, apiKey, out _, out var error))
            throw new InvalidOperationException(error);
    }

    public AIService(
        ProviderProfile profile,
        string? apiKey,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<ProviderRequestTelemetry>? telemetryObserver = null,
        int? diagnosticSeed = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (diagnosticSeed.HasValue &&
            (profile.Type != ProviderType.Local || profile.Platform != ProviderPlatform.Ollama ||
             profile.Protocol != ProviderProtocol.OpenAICompatible))
            throw new ArgumentException("diagnosticSeed 仅适用于本地 Ollama OpenAI 兼容请求。", nameof(diagnosticSeed));
        _apiBase = profile.ApiBase;
        _model = profile.Model;
        _resolvedModel = ProviderCapabilityResolver.ResolveModelId(profile);
        _apiKey = apiKey ?? string.Empty;
        _providerType = profile.Type;
        _platform = profile.Platform;
        _protocol = profile.Protocol;
        _capabilities = ProviderCapabilityResolver.Resolve(profile, _resolvedModel);
        _timeout = TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 10, 600));
        _temperature = Math.Clamp(profile.Temperature, 0, 2);
        _topP = Math.Clamp(profile.TopP, 0, 1);
        _maxTokens = Math.Clamp(profile.MaxTokens, 128, 32768);
        _diagnosticSeed = diagnosticSeed;
        _inferenceLevel = profile.InferenceLevel;
        _delayAsync = delayAsync ?? Task.Delay;
        _telemetryObserver = telemetryObserver;
        if (handler is null)
        {
            _httpClient = SharedClient;
        }
        else
        {
            _httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(10) };
            _ownsClient = true;
        }
    }

    public async Task<string> GenerateAsync(
        string systemPrompt,
        string userInput,
        CancellationToken cancellationToken = default)
        => await GenerateCoreAsync(systemPrompt, userInput, null, cancellationToken).ConfigureAwait(false);

    public async Task<string> GenerateStructuredAsync(
        string systemPrompt,
        string userInput,
        string jsonSchema,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonSchema)) throw new ArgumentException("jsonSchema 不能为空。", nameof(jsonSchema));
        using var _ = JsonDocument.Parse(jsonSchema);
        return await GenerateCoreAsync(systemPrompt, userInput, jsonSchema, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GenerateCoreAsync(
        string systemPrompt,
        string userInput,
        string? jsonSchema,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration(out var baseUri);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            var disableDeepSeekThinking = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();
                int? httpStatusCode = null;
                string? responseRequestId = null;
                try
                {
                    using var request = CreateRequest(baseUri, systemPrompt, userInput, forceDisableDeepSeekThinking: disableDeepSeekThinking, jsonSchema: jsonSchema);
                    using var response = await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
                    httpStatusCode = (int)response.StatusCode;
                    responseRequestId = ReadRequestId(response);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await ReadResponseBodyLimitedAsync(response.Content, timeoutSource.Token).ConfigureAwait(false);
                        var usage = ProviderUsageMetadataParser.Parse(_protocol, responseBody);
                        string generatedText;
                        try
                        {
                            generatedText = ParseProtocolContent(responseBody, _protocol, IsDeepSeekV4);
                            generatedText = SanitizeProviderText(generatedText);
                        }
                        catch (GenerationFailureException exception)
                        {
                            stopwatch.Stop();
                            TryObserveTelemetry(new ProviderRequestTelemetry(
                                responseRequestId ?? usage.RequestId,
                                stopwatch.Elapsed.TotalMilliseconds,
                                usage.InputTokens,
                                usage.OutputTokens,
                                httpStatusCode,
                                exception.Kind switch
                                {
                                    GenerationFailureKind.Refused => "refused",
                                    GenerationFailureKind.Incomplete => "incomplete",
                                    GenerationFailureKind.ContextLimitExceeded => "context_limit_exceeded",
                                    GenerationFailureKind.ProviderUnavailable => "provider_error",
                                    _ => "invalid_response"
                                },
                                usage.CacheReadInputTokens,
                                usage.CacheCreationInputTokens,
                                FinishReason: usage.FinishReason));
                            throw;
                        }
                        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
                        {
                            // Preserve the existing user-facing parse exception, but retain
                            // content-free metadata for malformed successful HTTP responses.
                            stopwatch.Stop();
                            TryObserveTelemetry(new ProviderRequestTelemetry(
                                responseRequestId ?? usage.RequestId,
                                stopwatch.Elapsed.TotalMilliseconds,
                                usage.InputTokens,
                                usage.OutputTokens,
                                httpStatusCode,
                                "invalid_response",
                                usage.CacheReadInputTokens,
                                usage.CacheCreationInputTokens,
                                FinishReason: usage.FinishReason));
                            throw;
                        }
                        stopwatch.Stop();
                        TryObserveTelemetry(new ProviderRequestTelemetry(
                            responseRequestId ?? usage.RequestId,
                            stopwatch.Elapsed.TotalMilliseconds,
                            usage.InputTokens,
                            usage.OutputTokens,
                            httpStatusCode,
                            "success",
                            usage.CacheReadInputTokens,
                            usage.CacheCreationInputTokens,
                            FinishReason: usage.FinishReason));
                        return generatedText;
                    }

                    var inspectStructuredOutputError = jsonSchema is not null &&
                        _capabilities.StructuredOutput == ProviderStructuredOutputSupport.JsonSchema &&
                        response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;
                    var inspectGeminiCredentialError = _protocol == ProviderProtocol.GeminiGenerateContent &&
                        response.StatusCode == HttpStatusCode.BadRequest;
                    string? boundedErrorBody = inspectStructuredOutputError || inspectGeminiCredentialError
                        ? await ReadErrorBodyForClassificationAsync(response.Content, timeoutSource.Token).ConfigureAwait(false)
                        : null;
                    var structuredOutputUnsupported = inspectStructuredOutputError &&
                        IsStructuredOutputRejection(boundedErrorBody, _protocol);
                    var geminiInvalidApiKey = inspectGeminiCredentialError &&
                        IsGeminiInvalidApiKeyError(boundedErrorBody);
                    var geminiAccountPrerequisite = inspectGeminiCredentialError &&
                        IsGeminiAccountPrerequisiteError(boundedErrorBody);
                    GenerationFailureException failure;
                    if (structuredOutputUnsupported)
                    {
                        failure = new GenerationFailureException(
                            GenerationFailureKind.StructuredOutputUnsupported,
                            "Provider 无法接受当前结构化输出参数，应用将改用提示约束和本地 Schema 校验。",
                            response.StatusCode);
                    }
                    else if (geminiInvalidApiKey)
                    {
                        failure = new GenerationFailureException(
                            GenerationFailureKind.Authentication,
                            "Gemini API Key 无效，请检查密钥是否正确、有效并属于当前项目。",
                            response.StatusCode);
                    }
                    else if (geminiAccountPrerequisite)
                    {
                        failure = new GenerationFailureException(
                            GenerationFailureKind.AccountPrerequisite,
                            "Gemini 项目尚未满足调用前提，请检查项目计费状态及当前地区是否支持 Gemini API。",
                            response.StatusCode);
                    }
                    else
                    {
                        failure = BuildGenerationFailure(response.StatusCode, response.ReasonPhrase);
                    }

                    stopwatch.Stop();
                    TryObserveTelemetry(new ProviderRequestTelemetry(
                        responseRequestId, stopwatch.Elapsed.TotalMilliseconds, null, null,
                        httpStatusCode,
                        structuredOutputUnsupported ? "structured_output_unsupported" :
                        geminiInvalidApiKey ? "authentication" :
                        geminiAccountPrerequisite ? "account_prerequisite" : CategorizeHttpFailure(response.StatusCode)));

                    if (attempt == 0 && IsTransient(response.StatusCode))
                    {
                        await _delayAsync(TimeSpan.FromMilliseconds(250), timeoutSource.Token).ConfigureAwait(false);
                        continue;
                    }

                    // Only bounded, allow-listed error signatures are inspected above;
                    // arbitrary provider messages and bodies are never exposed or logged.
                    throw failure;
                }
                catch (InvalidOperationException exception) when (attempt == 0 && IsDeepSeekV4 && IsEmptyModelResponse(exception))
                {
                    // DeepSeek V4 may spend the whole first budget on reasoning for a
                    // strict structured prompt. Retry once with thinking disabled so
                    // the user receives a final answer instead of an empty bubble.
                    disableDeepSeekThinking = true;
                }
                catch (HttpRequestException) when (attempt == 0)
                {
                    stopwatch.Stop();
                    TryObserveTelemetry(new ProviderRequestTelemetry(null, stopwatch.Elapsed.TotalMilliseconds,
                        null, null, httpStatusCode, "network_error"));
                    await _delayAsync(TimeSpan.FromMilliseconds(250), timeoutSource.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    stopwatch.Stop();
                    TryObserveTelemetry(new ProviderRequestTelemetry(null, stopwatch.Elapsed.TotalMilliseconds,
                        null, null, httpStatusCode, "network_error"));
                    throw;
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    TryObserveTelemetry(new ProviderRequestTelemetry(null, stopwatch.Elapsed.TotalMilliseconds,
                        null, null, httpStatusCode,
                        cancellationToken.IsCancellationRequested ? "cancelled" : "timeout"));
                    throw;
                }
                catch (ResponseTooLargeException)
                {
                    stopwatch.Stop();
                    TryObserveTelemetry(new ProviderRequestTelemetry(responseRequestId, stopwatch.Elapsed.TotalMilliseconds,
                        null, null, httpStatusCode, "invalid_response"));
                    throw;
                }
            }
            throw new InvalidOperationException("网络请求失败，请检查网络后重试。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new GenerationFailureException(
                GenerationFailureKind.Timeout,
                $"请求超时（>{_timeout.TotalSeconds:0} 秒），请检查网络或模型配置。",
                isTransient: true);
        }
        catch (ResponseTooLargeException)
        {
            throw new GenerationFailureException(
                GenerationFailureKind.ProviderUnavailable,
                "模型服务响应过大，已停止读取。",
                isTransient: false);
        }
        catch (HttpRequestException ex)
        {
            throw new GenerationFailureException(
                GenerationFailureKind.Network,
                $"网络请求失败：{ex.Message}",
                isTransient: true,
                innerException: ex);
        }
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var headerName in new[] { "x-amzn-requestid", "x-request-id", "request-id", "openai-request-id" })
            if (response.Headers.TryGetValues(headerName, out var values))
                foreach (var value in values)
                {
                    var normalized = ProviderUsageMetadataParser.NormalizeRequestId(value);
                    if (normalized is not null) return normalized;
                }
        return null;
    }

    private static string CategorizeHttpFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => "authentication",
        HttpStatusCode.Forbidden => "permission_denied",
        HttpStatusCode.PaymentRequired => "billing_issue",
        HttpStatusCode.RequestEntityTooLarge => "request_too_large",
        HttpStatusCode.TooManyRequests => "rate_limited",
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
        _ => "provider_error"
    };

    private void TryObserveTelemetry(ProviderRequestTelemetry telemetry)
    {
        try { _telemetryObserver?.Invoke(telemetry); }
        catch { /* Optional diagnostics must never alter generation behavior. */ }
    }

    private static GenerationFailureException BuildGenerationFailure(
        HttpStatusCode statusCode,
        string? reasonPhrase)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return new GenerationFailureException(
                GenerationFailureKind.RateLimited,
                "请求过于频繁，服务正在限流，请稍后重试。",
                statusCode,
                isTransient: true);
        }

        var kind = statusCode switch
        {
            HttpStatusCode.Unauthorized => GenerationFailureKind.Authentication,
            HttpStatusCode.Forbidden => GenerationFailureKind.PermissionDenied,
            HttpStatusCode.PaymentRequired => GenerationFailureKind.BillingIssue,
            HttpStatusCode.RequestEntityTooLarge => GenerationFailureKind.RequestTooLarge,
            HttpStatusCode.NotFound => GenerationFailureKind.ResourceMissing,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => GenerationFailureKind.Timeout,
            _ when (int)statusCode >= 500 => GenerationFailureKind.ProviderUnavailable,
            _ => GenerationFailureKind.RequestRejected
        };
        // Provider error bodies are untrusted and may echo user input or server secrets.
        // Keep user-facing/logged failures to a normalized status only.
        var message = kind switch
        {
            GenerationFailureKind.Authentication => $"API 认证失败（HTTP {(int)statusCode}），请检查 API Key 是否有效。",
            GenerationFailureKind.PermissionDenied => $"API 权限不足（HTTP {(int)statusCode}），请检查账号对该接口和模型的访问权限。",
            GenerationFailureKind.BillingIssue => $"API 账单或付款状态异常（HTTP {(int)statusCode}），请检查账户余额和付款方式。",
            GenerationFailureKind.RequestTooLarge => "请求体超过模型服务的大小限制，请减少输入文本或附件体积后重试。",
            _ => $"API 返回错误 {(int)statusCode} {reasonPhrase}。"
        };
        return new GenerationFailureException(
            kind,
            message,
            statusCode,
            kind is GenerationFailureKind.Timeout or GenerationFailureKind.ProviderUnavailable);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        Uri baseUri;
        try
        {
            ValidateConfiguration(out baseUri);
        }
        catch (InvalidOperationException exception)
        {
            var missingKey = exception.Message.StartsWith("API Key 未配置", StringComparison.OrdinalIgnoreCase);
            return BuildConnectionResult(
                missingKey ? ConnectionTestStatus.MissingKey : ConnectionTestStatus.InvalidEndpoint,
                exception.Message, null, 0, null, null);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = CreateRequest(baseUri, "只回复 OK。", "连接测试", isConnectionTest: true);
                try
                {
                    using var response = await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
                    var requestId = TryGetRequestId(response);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (attempt == 0 && IsConnectionTransient(response.StatusCode))
                        {
                            await _delayAsync(TimeSpan.FromMilliseconds(250), timeoutSource.Token).ConfigureAwait(false);
                            continue;
                        }

                        var inspectGeminiError = _protocol == ProviderProtocol.GeminiGenerateContent &&
                            response.StatusCode == HttpStatusCode.BadRequest;
                        var geminiErrorBody = inspectGeminiError
                            ? await ReadErrorBodyForClassificationAsync(response.Content, timeoutSource.Token).ConfigureAwait(false)
                            : null;
                        var geminiInvalidApiKey = inspectGeminiError && IsGeminiInvalidApiKeyError(geminiErrorBody);
                        var geminiAccountPrerequisite = inspectGeminiError && IsGeminiAccountPrerequisiteError(geminiErrorBody);
                        var status = geminiInvalidApiKey
                            ? ConnectionTestStatus.AuthFailed
                            : geminiAccountPrerequisite
                                ? ConnectionTestStatus.AccountPrerequisite
                                : response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized => ConnectionTestStatus.AuthFailed,
                            HttpStatusCode.Forbidden => ConnectionTestStatus.PermissionDenied,
                            HttpStatusCode.PaymentRequired => ConnectionTestStatus.BillingIssue,
                            HttpStatusCode.RequestEntityTooLarge => ConnectionTestStatus.RequestTooLarge,
                            HttpStatusCode.NotFound => ConnectionTestStatus.ModelUnavailable,
                            HttpStatusCode.TooManyRequests => ConnectionTestStatus.RateLimited,
                            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ConnectionTestStatus.Timeout,
                            _ => ConnectionTestStatus.ProviderError
                        };
                        var message = status switch
                        {
                            ConnectionTestStatus.AuthFailed => "API Key 无效、已过期或没有访问权限，请重新填写。",
                            ConnectionTestStatus.PermissionDenied => "当前 API Key 或账号无权访问该接口/模型，请检查项目权限、地域和模型授权。",
                            ConnectionTestStatus.BillingIssue => "账户账单或付款状态异常，请检查余额、预付额度和付款方式。",
                            ConnectionTestStatus.RequestTooLarge => "连接测试请求超过服务端大小限制，请检查接口代理或网关的请求体限制。",
                            ConnectionTestStatus.AccountPrerequisite => "Gemini 项目尚未满足调用前提，请检查项目计费状态及当前地区是否支持 Gemini API。",
                            ConnectionTestStatus.ModelUnavailable => "接口或模型不存在，请核对 Model ID。",
                            ConnectionTestStatus.RateLimited => "服务正在限流或额度不足，请稍后重试或检查账户余额。",
                            ConnectionTestStatus.Timeout => "服务端响应超时，请稍后重试。",
                            _ when (int)response.StatusCode >= 500 => "模型服务暂时不可用，请稍后重试。",
                            _ => $"模型服务拒绝了连接测试（HTTP {(int)response.StatusCode}）。"
                        };
                        return BuildConnectionResult(status, message, response.StatusCode,
                            stopwatch.ElapsedMilliseconds, baseUri, requestId);
                    }

                    var body = await ReadResponseBodyLimitedAsync(response.Content, timeoutSource.Token).ConfigureAwait(false);
                    try
                    {
                        _ = ParseProtocolContent(body, _protocol, IsDeepSeekV4);
                    }
                    catch (GenerationFailureException exception)
                    {
                        var message = exception.Kind switch
                        {
                            GenerationFailureKind.Refused => "服务已响应，但模型拒绝了连接测试提示。",
                            GenerationFailureKind.Incomplete => "服务已响应，但未完成连接测试回复。",
                            _ => "服务已响应，但返回了当前客户端无法完成的生成状态。"
                        };
                        return BuildConnectionResult(ConnectionTestStatus.ProviderError,
                            message, response.StatusCode, stopwatch.ElapsedMilliseconds, baseUri, requestId);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                    {
                        return BuildConnectionResult(ConnectionTestStatus.InvalidResponse,
                            "已连接到服务，但返回格式与当前接口协议不匹配。", response.StatusCode,
                            stopwatch.ElapsedMilliseconds, baseUri, requestId);
                    }

                    return BuildConnectionResult(ConnectionTestStatus.Success,
                        $"连接成功，{stopwatch.ElapsedMilliseconds} ms。", response.StatusCode,
                        stopwatch.ElapsedMilliseconds, baseUri, requestId);
                }
                catch (HttpRequestException) when (attempt == 0)
                {
                    await _delayAsync(TimeSpan.FromMilliseconds(250), timeoutSource.Token).ConfigureAwait(false);
                }
            }
            return BuildConnectionResult(ConnectionTestStatus.NetworkError,
                "无法连接模型服务，请检查网络、代理、DNS 或 TLS 设置。", null,
                stopwatch.ElapsedMilliseconds, baseUri, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return BuildConnectionResult(ConnectionTestStatus.Timeout,
                $"连接超时（>{_timeout.TotalSeconds:0} 秒），请检查网络或接口地址。", null,
                stopwatch.ElapsedMilliseconds, baseUri, null);
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            return BuildConnectionResult(ConnectionTestStatus.NetworkError,
                "无法连接模型服务，请检查网络、代理、DNS 或 TLS 设置。", null,
                stopwatch.ElapsedMilliseconds, baseUri, null);
        }
        catch (ResponseTooLargeException)
        {
            return BuildConnectionResult(ConnectionTestStatus.InvalidResponse,
                "服务返回内容过大，已停止读取。", null,
                stopwatch.ElapsedMilliseconds, baseUri, null);
        }
    }

    private static async Task<string> ReadResponseBodyLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new ResponseTooLargeException();

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes) throw new ResponseTooLargeException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static async Task<string?> ReadErrorBodyForClassificationAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumErrorClassificationBytes) return null;

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumErrorClassificationBytes) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static bool IsStructuredOutputRejection(
        string? errorBody,
        ProviderProtocol protocol)
    {
        if (string.IsNullOrWhiteSpace(errorBody)) return false;

        try
        {
            using var document = JsonDocument.Parse(errorBody);
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return false;

            if (protocol == ProviderProtocol.AnthropicMessages)
                return IsAnthropicSchemaCompilationRejection(error);

            if (!error.TryGetProperty("param", out var parameter) || parameter.ValueKind != JsonValueKind.String)
                return false;
            var value = parameter.GetString();
            if (string.IsNullOrWhiteSpace(value)) return false;
            return protocol switch
            {
                ProviderProtocol.OpenAICompatible => IsParameterOrChild(value, "response_format"),
                ProviderProtocol.OpenAIResponses => IsParameterOrChild(value, "text.format"),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsAnthropicSchemaCompilationRejection(JsonElement error)
    {
        // Anthropic's documented error shape has no `param` field. Only classify its
        // documented schema-compilation failure; generic invalid_request_error responses
        // also cover unrelated request problems and must not trigger a fallback.
        return error.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            string.Equals(type.GetString(), "invalid_request_error", StringComparison.Ordinal) &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String &&
            string.Equals(message.GetString()?.Trim(), "Schema is too complex for compilation.", StringComparison.Ordinal);
    }

    private static bool IsGeminiInvalidApiKeyError(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody)) return false;

        try
        {
            using var document = JsonDocument.Parse(errorBody);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("details", out var details) ||
                details.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object ||
                    !detail.TryGetProperty("@type", out var type) ||
                    type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "type.googleapis.com/google.rpc.ErrorInfo", StringComparison.Ordinal) ||
                    !detail.TryGetProperty("reason", out var reason) ||
                    reason.ValueKind != JsonValueKind.String ||
                    !string.Equals(reason.GetString(), "API_KEY_INVALID", StringComparison.Ordinal) ||
                    !detail.TryGetProperty("domain", out var domain) ||
                    domain.ValueKind != JsonValueKind.String ||
                    !string.Equals(domain.GetString(), "googleapis.com", StringComparison.Ordinal) ||
                    !detail.TryGetProperty("metadata", out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object ||
                    !metadata.TryGetProperty("service", out var service) ||
                    service.ValueKind != JsonValueKind.String ||
                    !string.Equals(service.GetString(), "generativelanguage.googleapis.com", StringComparison.Ordinal))
                    continue;

                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsGeminiAccountPrerequisiteError(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody)) return false;

        try
        {
            using var document = JsonDocument.Parse(errorBody);
            return document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                string.Equals(status.GetString(), "FAILED_PRECONDITION", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsParameterOrChild(string parameter, string supportedRoot) =>
        string.Equals(parameter, supportedRoot, StringComparison.OrdinalIgnoreCase) ||
        parameter.StartsWith(supportedRoot + ".", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool IsConnectionTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.GatewayTimeout ||
        (int)statusCode >= 500;

    private void ValidateConfiguration(out Uri baseUri)
    {
        var profile = new ProviderProfile
        {
            Type = _providerType,
            Platform = _platform,
            Protocol = _protocol,
            ApiBase = _apiBase,
            Model = _model
        };
        if (!ProviderEndpointPolicy.TryValidate(profile, _apiKey, out var validatedUri, out var error))
            throw new InvalidOperationException(error);
        baseUri = validatedUri!;
    }

    private HttpRequestMessage CreateRequest(
        Uri baseUri,
        string systemPrompt,
        string userInput,
        bool isConnectionTest = false,
        bool forceDisableDeepSeekThinking = false,
        string? jsonSchema = null)
    {
        Uri endpoint;
        object requestBody;
        switch (_protocol)
        {
            case ProviderProtocol.OpenAIResponses:
                endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/responses");
                var responsesBody = new Dictionary<string, object?>
                {
                    ["model"] = _resolvedModel,
                    ["instructions"] = ShouldUsePromptSchema(jsonSchema, isConnectionTest)
                        ? AppendSchemaInstruction(systemPrompt, jsonSchema!)
                        : systemPrompt,
                    ["input"] = userInput,
                    ["max_output_tokens"] = isConnectionTest
                        ? 8
                        : _capabilities.MaximumOutputTokens is { } responsesOutputMaximum
                            ? Math.Min(_maxTokens, responsesOutputMaximum)
                            : _maxTokens,
                    // Product prompts and user text are not needed for later retrieval.
                    ["store"] = false
                };
                if (!isConnectionTest && _capabilities.Temperature == ProviderCapabilitySupport.Supported)
                    responsesBody["temperature"] = _temperature;
                if (!isConnectionTest && _capabilities.TopP == ProviderCapabilitySupport.Supported)
                    responsesBody["top_p"] = _topP;
                if (!isConnectionTest && _capabilities.ReasoningEffortValues.Count > 0 &&
                    TryResolveReasoningEffort(out var responsesEffort) &&
                    _capabilities.ReasoningEffortValues.Contains(responsesEffort))
                    responsesBody["reasoning"] = new { effort = responsesEffort };
                if (!string.IsNullOrWhiteSpace(jsonSchema) && !isConnectionTest &&
                    _capabilities.StructuredOutput == ProviderStructuredOutputSupport.JsonSchema)
                {
                    using var schema = JsonDocument.Parse(jsonSchema);
                    responsesBody["text"] = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            name = "huaxiazi_answer",
                            strict = true,
                            schema = schema.RootElement.Clone()
                        }
                    };
                }
                requestBody = responsesBody;
                break;
            case ProviderProtocol.AnthropicMessages:
                endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/v1/messages");
                var anthropicBody = new Dictionary<string, object?>
                {
                    ["model"] = _resolvedModel,
                    ["max_tokens"] = isConnectionTest ? 8 : _maxTokens,
                    ["system"] = ShouldUsePromptSchema(jsonSchema, isConnectionTest)
                        ? AppendSchemaInstruction(systemPrompt, jsonSchema!)
                        : systemPrompt,
                    ["messages"] = new[] { new { role = "user", content = userInput } }
                };
                if (_capabilities.Temperature != ProviderCapabilitySupport.Unsupported)
                    anthropicBody["temperature"] = _temperature;
                if (_capabilities.TopP != ProviderCapabilitySupport.Unsupported)
                    anthropicBody["top_p"] = _topP;
                var anthropicOutputConfig = new Dictionary<string, object?>();
                if (!isConnectionTest && _capabilities.ReasoningEffortValues.Count > 0 &&
                    TryResolveReasoningEffort(out var anthropicEffort) &&
                    _capabilities.ReasoningEffortValues.Contains(anthropicEffort))
                    anthropicOutputConfig["effort"] = anthropicEffort;
                if (!string.IsNullOrWhiteSpace(jsonSchema) && !isConnectionTest &&
                    _capabilities.StructuredOutput == ProviderStructuredOutputSupport.JsonSchema)
                {
                    using var schema = JsonDocument.Parse(jsonSchema);
                    anthropicOutputConfig["format"] = new
                    {
                        type = "json_schema",
                        schema = schema.RootElement.Clone()
                    };
                }
                if (anthropicOutputConfig.Count > 0)
                    anthropicBody["output_config"] = anthropicOutputConfig;
                requestBody = anthropicBody;
                break;
            case ProviderProtocol.GeminiGenerateContent:
                endpoint = new Uri(baseUri.ToString().TrimEnd('/') + $"/models/{Uri.EscapeDataString(_resolvedModel)}:generateContent");
                var geminiGenerationConfig = new Dictionary<string, object?>
                {
                    ["maxOutputTokens"] = isConnectionTest ? 8 : _maxTokens
                };
                if (_capabilities.Temperature != ProviderCapabilitySupport.Unsupported)
                    geminiGenerationConfig["temperature"] = _temperature;
                if (_capabilities.TopP != ProviderCapabilitySupport.Unsupported)
                    geminiGenerationConfig["topP"] = _topP;
                if (!isConnectionTest && _capabilities.ReasoningEffortValues.Count > 0 &&
                    TryResolveReasoningEffort(out var geminiThinkingLevel) &&
                    _capabilities.ReasoningEffortValues.Contains(geminiThinkingLevel))
                    geminiGenerationConfig["thinkingConfig"] = new
                    {
                        thinkingLevel = geminiThinkingLevel.ToUpperInvariant()
                    };
                if (!string.IsNullOrWhiteSpace(jsonSchema) && !isConnectionTest &&
                    _capabilities.StructuredOutput == ProviderStructuredOutputSupport.JsonSchema)
                {
                    using var schema = JsonDocument.Parse(jsonSchema);
                    geminiGenerationConfig["responseFormat"] = new
                    {
                        text = new
                        {
                            mimeType = "application/json",
                            schema = schema.RootElement.Clone()
                        }
                    };
                }
                requestBody = new
                {
                    system_instruction = new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = ShouldUsePromptSchema(jsonSchema, isConnectionTest)
                                    ? AppendSchemaInstruction(systemPrompt, jsonSchema!)
                                    : systemPrompt
                            }
                        }
                    },
                    contents = new[] { new { role = "user", parts = new[] { new { text = userInput } } } },
                    generationConfig = geminiGenerationConfig
                };
                break;
                default:
                var compatibleBase = baseUri.ToString().TrimEnd('/');
                // DeepSeek 的 OpenAI 兼容入口固定在 /v1；兼容旧配置中遗漏 /v1
                // 的地址，避免用户必须重新创建配置才能恢复服务。
                if (_platform == ProviderPlatform.DeepSeek &&
                    string.Equals(baseUri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase) &&
                    !baseUri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                {
                    compatibleBase += "/v1";
                }
                endpoint = new Uri(compatibleBase + "/chat/completions");
                requestBody = BuildOpenAiCompatibleBody(systemPrompt, userInput, isConnectionTest, forceDisableDeepSeekThinking, jsonSchema);
                break;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        if (_protocol == ProviderProtocol.AnthropicMessages)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (_protocol == ProviderProtocol.GeminiGenerateContent)
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
        }
        else if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private object BuildOpenAiCompatibleBody(string systemPrompt, string userInput, bool isConnectionTest, bool forceDisableDeepSeekThinking, string? jsonSchema)
    {
        var tokenLimitField = _capabilities.UsesMaxCompletionTokens ? "max_completion_tokens" : "max_tokens";
        var requestTemperature = _capabilities.MaximumTemperature is { } temperatureMaximum
            ? Math.Min(_temperature, temperatureMaximum)
            : _temperature;
        var requestTopP = Math.Max(_topP, _capabilities.MinimumTopP ?? 0);
        if (_capabilities.MinimumTopPExclusive is { } topPMinimumExclusive)
            requestTopP = Math.Max(requestTopP, double.BitIncrement(topPMinimumExclusive));
        if (_capabilities.MaximumTopPExclusive is { } topPMaximumExclusive)
            requestTopP = Math.Min(requestTopP, double.BitDecrement(topPMaximumExclusive));
        var requestMaxTokens = _capabilities.MaximumOutputTokens is { } outputTokenMaximum
            ? Math.Min(_maxTokens, outputTokenMaximum)
            : _maxTokens;
        var structuredSystemPrompt = systemPrompt;
        if (ShouldUsePromptSchema(jsonSchema, isConnectionTest))
            structuredSystemPrompt = AppendSchemaInstruction(systemPrompt, jsonSchema!);

        var body = new Dictionary<string, object?>
        {
            ["model"] = _resolvedModel,
            ["messages"] = new[]
            {
                new { role = _capabilities.UsesDeveloperRole ? "developer" : "system", content = structuredSystemPrompt },
                new { role = "user", content = userInput }
            },
            [tokenLimitField] = isConnectionTest ? 8 : requestMaxTokens,
            ["stream"] = false
        };

        if (!isConnectionTest && _diagnosticSeed is { } diagnosticSeed)
            body["seed"] = diagnosticSeed;

        if (IsDeepSeekV4)
        {
            var disableThinking = isConnectionTest || forceDisableDeepSeekThinking;
            body["thinking"] = new { type = disableThinking ? "disabled" : "enabled" };
            if (!disableThinking)
            {
                if (_inferenceLevel != InferenceLevel.Custom)
                    body["reasoning_effort"] = _inferenceLevel switch
                    {
                        InferenceLevel.Low => "low",
                        InferenceLevel.High => "high",
                        _ => "high"
                    };
                body["top_p"] = Math.Clamp(_topP, 0.95, 1.0);
            }
            else if (!isConnectionTest)
            {
                body["temperature"] = requestTemperature;
            }
        }
        else if (_capabilities.EvidenceId.StartsWith("xiaomi-mimo-v2.", StringComparison.Ordinal))
        {
            var disableThinking = isConnectionTest || _inferenceLevel == InferenceLevel.Low;
            body["thinking"] = new { type = disableThinking ? "disabled" : "enabled" };
            if (disableThinking && !isConnectionTest)
            {
                body["temperature"] = requestTemperature;
                body["top_p"] = requestTopP;
            }
        }
        else
        {
            if (!isConnectionTest && !_capabilities.OmitSamplingParameters && _capabilities.Temperature != ProviderCapabilitySupport.Unsupported)
                body["temperature"] = requestTemperature;
            if (!isConnectionTest && !_capabilities.OmitSamplingParameters && _capabilities.TopP != ProviderCapabilitySupport.Unsupported)
                body["top_p"] = requestTopP;

            if (!isConnectionTest &&
                _capabilities.EvidenceId.StartsWith("siliconflow-v4-glm52-thinking-json-mode", StringComparison.Ordinal))
                body["enable_thinking"] = true;

            if (!isConnectionTest && _capabilities.ReasoningEffortValues.Count > 0 &&
                TryResolveReasoningEffort(out var effort) && _capabilities.ReasoningEffortValues.Contains(effort))
                body["reasoning_effort"] = effort;

            if (!isConnectionTest && _capabilities.SupportsThinkingToggle && _inferenceLevel == InferenceLevel.Low)
                body["thinking"] = new { type = "disabled" };

            switch (_capabilities.ReasoningOutputPolicy)
            {
                case ProviderReasoningOutputPolicy.ExcludeWithIncludeReasoning:
                    body["include_reasoning"] = false;
                    break;
                case ProviderReasoningOutputPolicy.HideWithReasoningFormat:
                    body["reasoning_format"] = "hidden";
                    break;
            }

        if (_capabilities.EvidenceId.StartsWith("minimax-openai-chat", StringComparison.Ordinal) &&
            string.Equals(_resolvedModel, "MiniMax-M3", StringComparison.OrdinalIgnoreCase))
            body["reasoning_split"] = true;
        }

        if (!string.IsNullOrWhiteSpace(jsonSchema) && !isConnectionTest)
        {
            if (_capabilities.StructuredOutput == ProviderStructuredOutputSupport.JsonObjectOnly)
            {
                body["response_format"] = new { type = "json_object" };
            }
            else if (_capabilities.StructuredOutput == ProviderStructuredOutputSupport.JsonSchema)
            {
                using var schema = JsonDocument.Parse(jsonSchema);
                body["response_format"] = new
                {
                    type = "json_schema",
                    json_schema = new { name = "huaxiazi_answer", strict = true, schema = schema.RootElement.Clone() }
                };
                if (_platform == ProviderPlatform.OpenRouter &&
                    _capabilities.EvidenceId == "openrouter-model-catalog-parameters")
                    body["provider"] = new { require_parameters = true };
            }
        }

        return body;
    }

    private string SanitizeProviderText(string generatedText)
    {
        if (!_capabilities.EvidenceId.StartsWith("minimax-openai-chat", StringComparison.Ordinal))
            return generatedText;

        var sanitized = Regex.Replace(generatedText, "<think>.*?</think>", string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        if (sanitized.Contains("<think>", StringComparison.OrdinalIgnoreCase) ||
            sanitized.Contains("</think>", StringComparison.OrdinalIgnoreCase))
            throw new GenerationFailureException(GenerationFailureKind.InvalidResponse,
                "MiniMax 响应中的思考段标记不完整，已阻止将其作为最终成稿展示。");
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new GenerationFailureException(GenerationFailureKind.InvalidResponse,
                "MiniMax 响应只包含思考内容，没有可交付的最终答案。");
        return sanitized;
    }

    private bool ShouldUsePromptSchema(string? jsonSchema, bool isConnectionTest) =>
        !isConnectionTest && !string.IsNullOrWhiteSpace(jsonSchema) &&
        _capabilities.StructuredOutput != ProviderStructuredOutputSupport.JsonSchema;

    private static string AppendSchemaInstruction(string systemPrompt, string jsonSchema) =>
        $"{systemPrompt}\n\n输出必须符合以下 JSON Schema，只返回 JSON 对象，不要添加 Markdown 或解释：\n{jsonSchema}";

    private bool TryResolveReasoningEffort(out string effort)
    {
        if (_capabilities.EvidenceId.StartsWith("zhipu-glm-5.3", StringComparison.Ordinal))
        {
            effort = _inferenceLevel switch
            {
                InferenceLevel.Low => "low",
                InferenceLevel.Medium => "high",
                InferenceLevel.High => "max",
                _ => string.Empty
            };
            return effort.Length > 0 && _capabilities.ReasoningEffortValues.Contains(effort);
        }

        if (_capabilities.EvidenceId.StartsWith("zhipu-glm-5.2", StringComparison.Ordinal))
        {
            effort = _inferenceLevel switch
            {
                InferenceLevel.Low => "none",
                InferenceLevel.Medium => "low",
                InferenceLevel.High => "max",
                _ => string.Empty
            };
            return effort.Length > 0 && _capabilities.ReasoningEffortValues.Contains(effort);
        }

        if (_capabilities.EvidenceId.StartsWith("moonshot-kimi-k3", StringComparison.Ordinal))
        {
            effort = _inferenceLevel switch
            {
                InferenceLevel.Low => "low",
                InferenceLevel.Medium => "high",
                InferenceLevel.High => "max",
                _ => string.Empty
            };
            return effort.Length > 0 && _capabilities.ReasoningEffortValues.Contains(effort);
        }

        if (_capabilities.EvidenceId.StartsWith("aliyun-qwen3.8", StringComparison.Ordinal))
        {
            effort = _inferenceLevel switch
            {
                InferenceLevel.Low => "low",
                InferenceLevel.Medium => "medium",
                InferenceLevel.High => "xhigh",
                _ => string.Empty
            };
            return effort.Length > 0 && _capabilities.ReasoningEffortValues.Contains(effort);
        }

        effort = _inferenceLevel switch
        {
            InferenceLevel.Low => "low",
            InferenceLevel.Medium => "medium",
            InferenceLevel.High => "high",
            _ => string.Empty
        };
        return effort.Length > 0;
    }

    private bool IsDeepSeekV4 => ProviderCapabilityResolver.UsesDeepSeekV4ThinkingControls(_platform, _protocol, _resolvedModel);

    private static bool IsEmptyModelResponse(InvalidOperationException exception) =>
        exception.Message.Contains("模型返回为空", StringComparison.Ordinal) ||
        exception.Message.Contains("只返回了推理过程", StringComparison.Ordinal);

    private ConnectionTestResult BuildConnectionResult(
        ConnectionTestStatus status,
        string message,
        HttpStatusCode? httpStatusCode,
        long elapsedMilliseconds,
        Uri? baseUri,
        string? requestId)
    {
        var diagnostic = new StringBuilder()
            .Append("平台: ").Append(_platform).AppendLine()
            .Append("主机: ").Append(baseUri?.Host ?? "未建立连接").AppendLine()
            .Append("协议: ").Append(_protocol).AppendLine()
            .Append("模型: ").Append(_resolvedModel).AppendLine()
            .Append("状态: ").Append(httpStatusCode.HasValue ? $"HTTP {(int)httpStatusCode.Value}" : status.ToString()).AppendLine()
            .Append("耗时: ").Append(elapsedMilliseconds).Append(" ms");
        if (!string.IsNullOrWhiteSpace(requestId)) diagnostic.AppendLine().Append("Request ID: ").Append(requestId);
        return new ConnectionTestResult(status, message, httpStatusCode, elapsedMilliseconds, diagnostic.ToString());
    }

    private static string? TryGetRequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "x-amzn-requestid", "x-request-id", "request-id", "x-goog-request-id" })
        {
            if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        }
        return null;
    }

    private sealed class ResponseTooLargeException : Exception;

    internal static string ParseContent(string responseBody) => ParseProtocolContent(responseBody, ProviderProtocol.OpenAICompatible);

    private static string ParseProtocolContent(string responseBody, ProviderProtocol protocol, bool isDeepSeekChatModel = false)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (JsonException) when (protocol is ProviderProtocol.OpenAICompatible or ProviderProtocol.OpenAIResponses && TryParseServerSentEvents(responseBody, out var streamedText))
        {
            return streamedText;
        }
        using (doc)
        {
        var root = doc.RootElement;

        if (protocol == ProviderProtocol.OpenAIResponses)
            return ParseResponsesApiContent(root);

        if (protocol == ProviderProtocol.AnthropicMessages)
        {
            if (root.TryGetProperty("stop_reason", out var stopReasonElement) &&
                stopReasonElement.ValueKind == JsonValueKind.String)
            {
                var stopReason = stopReasonElement.GetString();
                if (string.Equals(stopReason, "model_context_window_exceeded", StringComparison.OrdinalIgnoreCase))
                    throw new GenerationFailureException(GenerationFailureKind.ContextLimitExceeded,
                        "模型输出触及上下文窗口上限，可能未能完整处理输入；请缩短输入或选择上下文更大的模型后重试。");
                if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
                    throw new GenerationFailureException(GenerationFailureKind.Incomplete,
                        "模型输出达到长度上限，响应不完整；请缩短输入或提高输出上限后重试。");
                if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
                    throw new GenerationFailureException(GenerationFailureKind.Refused, "模型拒绝了该请求。");
                if (string.Equals(stopReason, "tool_use", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stopReason, "pause_turn", StringComparison.OrdinalIgnoreCase))
                    throw new GenerationFailureException(GenerationFailureKind.InvalidResponse,
                        "模型响应需要工具执行或后续续传，当前客户端无法完成该响应。");
                if (!string.Equals(stopReason, "end_turn", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(stopReason, "stop_sequence", StringComparison.OrdinalIgnoreCase))
                    throw new GenerationFailureException(GenerationFailureKind.InvalidResponse,
                        "Anthropic 返回了当前客户端无法识别的完成状态，已阻止交付可能不完整的文本。");
            }

            if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in blocks.EnumerateArray())
                    if (block.TryGetProperty("text", out var text)) return RequireNonEmptyText(text);
            }
            throw new InvalidOperationException("响应格式异常：未找到 content[].text。");
        }

        if (protocol == ProviderProtocol.GeminiGenerateContent)
        {
            if (root.TryGetProperty("promptFeedback", out var promptFeedback) &&
                promptFeedback.ValueKind == JsonValueKind.Object &&
                promptFeedback.TryGetProperty("blockReason", out var blockReasonElement) &&
                blockReasonElement.ValueKind == JsonValueKind.String)
            {
                var blockReason = blockReasonElement.GetString();
                if (!string.IsNullOrWhiteSpace(blockReason) &&
                    !string.Equals(blockReason, "BLOCK_REASON_UNSPECIFIED", StringComparison.OrdinalIgnoreCase))
                    throw new GenerationFailureException(GenerationFailureKind.Refused,
                        "Gemini 未生成候选内容，提示被服务端内容策略拦截。");
            }

            if (root.TryGetProperty("candidates", out var statusCandidates) &&
                statusCandidates.ValueKind == JsonValueKind.Array && statusCandidates.GetArrayLength() > 0 &&
                statusCandidates[0].TryGetProperty("finishReason", out var candidateFinishReasonElement) &&
                candidateFinishReasonElement.ValueKind == JsonValueKind.String)
            {
                var finishReason = candidateFinishReasonElement.GetString();
                if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                    throw new GenerationFailureException(GenerationFailureKind.Incomplete,
                        "Gemini 输出达到长度上限，响应不完整；请缩短输入或提高输出上限后重试。");
                if (IsGeminiFilteredFinishReason(finishReason))
                    throw new GenerationFailureException(GenerationFailureKind.Refused,
                        "Gemini 未能完成该请求，候选内容被服务端策略拦截。");
                if (IsGeminiUnsupportedFinishReason(finishReason))
                    throw new GenerationFailureException(GenerationFailureKind.InvalidResponse,
                        "Gemini 返回了当前客户端无法完成的工具调用或无效响应。");
            }

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0 &&
                candidates[0].TryGetProperty("content", out var candidateContent) &&
                candidateContent.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0 &&
                parts[0].TryGetProperty("text", out var text))
                return RequireNonEmptyText(text);
            throw new InvalidOperationException("响应格式异常：未找到 candidates[0].content.parts[0].text。");
        }

        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            if (TryExtractResponsesApiText(root, out var responseText)) return responseText;
            throw new InvalidOperationException("响应格式异常：未找到 choices[0]。");
        }

        var first = choices[0];
        if (first.TryGetProperty("finish_reason", out var finishReasonElement) &&
            finishReasonElement.ValueKind == JsonValueKind.String)
        {
            var finishReason = finishReasonElement.GetString();
            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                throw new GenerationFailureException(GenerationFailureKind.Incomplete,
                    "模型输出达到上限，响应不完整；请提高输出上限或缩短输入后重试。");
            if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
                throw new GenerationFailureException(GenerationFailureKind.Refused,
                    "模型因内容安全策略未完成该请求。");
            if (isDeepSeekChatModel && string.Equals(finishReason, "insufficient_system_resource", StringComparison.OrdinalIgnoreCase))
                throw new GenerationFailureException(GenerationFailureKind.ProviderUnavailable,
                    "DeepSeek 推理资源不足，未能完成本次请求。", isTransient: true);
            if (isDeepSeekChatModel && string.Equals(finishReason, "aborted", StringComparison.OrdinalIgnoreCase))
                throw new GenerationFailureException(GenerationFailureKind.Incomplete,
                    "DeepSeek 已中断本次生成，部分文本不作为完整结果交付。", isTransient: true);
        }

        if (first.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("refusal", out var refusal) &&
                refusal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(refusal.GetString()))
                throw new GenerationFailureException(GenerationFailureKind.Refused, "模型拒绝了该请求。");

            if (message.TryGetProperty("content", out var content))
            {
                if (TryExtractText(content, out var messageText) && !string.IsNullOrWhiteSpace(messageText))
                {
                    var normalizedMessage = NormalizeModelText(messageText);
                    if (!string.IsNullOrWhiteSpace(normalizedMessage)) return normalizedMessage;
                }
                // 部分兼容网关会同时返回空 content 和 Responses API 风格的 output_text；
                // 仅在存在明确的最终文本字段时回退，绝不把 reasoning_content 当成答案。
                if (TryExtractResponsesApiText(root, out var responseText)) return responseText;
                throw new InvalidOperationException("模型返回为空，请重试。");
            }

            if (message.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(reasoning.GetString()))
            {
                throw new InvalidOperationException("模型只返回了推理过程，未返回最终答案。请重试或更换模型。");
            }

            throw new InvalidOperationException("响应格式异常：未找到 choices[0].message.content。");
        }

        // 兼容仍返回增量块或旧版 completion 的 OpenAI 兼容网关；不改变请求协议。
        if (first.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent))
            return RequireNonEmptyText(deltaContent);
        if (first.TryGetProperty("text", out var legacyText))
            return RequireNonEmptyText(legacyText);

        throw new InvalidOperationException("响应格式异常：未找到 choices[0].message.content。");
        }
    }

    private static bool IsGeminiFilteredFinishReason(string? finishReason) => finishReason?.ToUpperInvariant() is
        "SAFETY" or "RECITATION" or "LANGUAGE" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or
        "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" or "IMAGE_OTHER" or "NO_IMAGE" or "IMAGE_RECITATION" or
        "ESCALATION" or "PUP_LIMITED_DISABLED";

    private static bool IsGeminiUnsupportedFinishReason(string? finishReason) => finishReason?.ToUpperInvariant() is
        "OTHER" or "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" or "TOO_MANY_TOOL_CALLS" or
        "MISSING_THOUGHT_SIGNATURE" or "MALFORMED_RESPONSE";

    private static string ParseResponsesApiContent(JsonElement root)
    {
        var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString()
            : null;
        if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
        {
            var reason = root.TryGetProperty("incomplete_details", out var details) &&
                         details.ValueKind == JsonValueKind.Object &&
                         details.TryGetProperty("reason", out var reasonElement) &&
                         reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
            throw new GenerationFailureException(
                reason == "content_filter" ? GenerationFailureKind.Refused : GenerationFailureKind.Incomplete,
                reason == "content_filter" ? "模型因内容安全策略未完成该请求。" : "模型响应未完整，可能达到输出长度上限；请缩短输入或提高输出上限。");
        }

        if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase))
            throw new GenerationFailureException(
                GenerationFailureKind.Incomplete,
                "模型响应仍在处理中，尚未形成最终成稿。");

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            throw new GenerationFailureException(GenerationFailureKind.ProviderUnavailable, "Responses API 未能完成生成请求。", isTransient: true);

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType) || itemType.GetString() != "message" ||
                    !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var partType) && partType.GetString() == "refusal")
                        throw new GenerationFailureException(GenerationFailureKind.Refused, "模型拒绝了该请求。");
                    if (part.TryGetProperty("type", out partType) && partType.GetString() == "output_text" &&
                        part.TryGetProperty("text", out var text))
                        return RequireNonEmptyText(text);
                }
            }
        }

        if (TryExtractResponsesApiText(root, out var responseText)) return responseText;
        throw new GenerationFailureException(GenerationFailureKind.InvalidResponse, "Responses API 响应中没有可用的最终文本。");
    }

    private static bool TryParseServerSentEvents(string responseBody, out string result)
    {
        var fragments = new StringBuilder();
        foreach (var line in responseBody.Split('\n'))
        {
            var payload = line.Trim();
            if (!payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            payload = payload[5..].Trim();
            if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var eventDocument = JsonDocument.Parse(payload);
                var root = eventDocument.RootElement;
                if (root.TryGetProperty("output_text", out var outputText) &&
                    outputText.ValueKind == JsonValueKind.String)
                {
                    fragments.Append(outputText.GetString());
                    continue;
                }

                if (root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var content) && TryExtractText(content, out var text))
                            fragments.Append(text);
                        continue;
                    }
                    if (choice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var messageContent) &&
                        TryExtractText(messageContent, out var messageText))
                        fragments.Append(messageText);
                }
            }
            catch (JsonException)
            {
                // Ignore keep-alive or malformed individual events; the final
                // result is accepted only when at least one usable text fragment exists.
            }
        }

        result = NormalizeModelText(fragments.ToString());
        return !string.IsNullOrWhiteSpace(result);
    }

    private static string RequireNonEmptyText(JsonElement content)
    {
        if (TryExtractText(content, out var result) && !string.IsNullOrWhiteSpace(result))
        {
            var normalized = NormalizeModelText(result);
            if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
        }
        throw new InvalidOperationException("模型返回为空，请重试。");
    }

    /// <summary>
    /// Removes a transport-only Markdown fence that some providers wrap around
    /// the complete answer. Inline code and partial fences remain untouched.
    /// </summary>
    internal static string NormalizeModelText(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Trim();

        // Some gateways append an internal chain-of-thought reminder to the
        // user-visible answer. Remove it only when it is the final sentence.
        const string thinkingSuffix = "Take time to think through this carefully before responding.";
        while (true)
        {
            var suffixCandidate = normalized.EndsWith('”') || normalized.EndsWith('"')
                ? normalized[..^1].TrimEnd()
                : normalized;
            if (!suffixCandidate.EndsWith(thinkingSuffix, StringComparison.OrdinalIgnoreCase)) break;
            normalized = suffixCandidate[..^thinkingSuffix.Length]
                .TrimEnd(' ', '\t', '\n', '\r', '”', '"');
        }

        if (!normalized.StartsWith("```", StringComparison.Ordinal) ||
            !normalized.EndsWith("```", StringComparison.Ordinal) ||
            normalized.Length <= 6)
            return normalized;

        var firstLineEnd = normalized.IndexOf('\n');
        if (firstLineEnd < 0) return normalized;
        var opening = normalized[..firstLineEnd].Trim();
        if (opening.Length < 3 || opening.Any(char.IsWhiteSpace) && opening != "```")
        {
            // A language label (for example ```markdown) is allowed, but a
            // prose line beginning with backticks is not treated as a wrapper.
            if (!opening.StartsWith("```", StringComparison.Ordinal) || opening.Length > 32)
                return normalized;
        }

        var bodyStart = firstLineEnd + 1;
        var body = normalized[bodyStart..^3].Trim();
        return body;
    }

    private static bool TryExtractText(JsonElement value, out string? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.String)
        {
            result = value.GetString();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("text", out var objectText) && objectText.ValueKind == JsonValueKind.String)
            {
                if (value.TryGetProperty("type", out var textType) && textType.ValueKind == JsonValueKind.String &&
                    textType.GetString() is { } objectType &&
                    !string.Equals(objectType, "text", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(objectType, "output_text", StringComparison.OrdinalIgnoreCase))
                    return false;
                result = objectText.GetString();
                return true;
            }
            if (value.TryGetProperty("content", out var objectContent))
                return TryExtractText(objectContent, out result);
            return false;
        }

        if (value.ValueKind != JsonValueKind.Array) return false;

        var fragments = new StringBuilder();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                fragments.Append(item.GetString());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                if (!item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                    type.GetString() is not { } blockType ||
                    string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(blockType, "output_text", StringComparison.OrdinalIgnoreCase))
                    fragments.Append(text.GetString());
            }
            else if (item.TryGetProperty("content", out var nested) && TryExtractText(nested, out var nestedText))
                fragments.Append(nestedText);
        }

        result = fragments.ToString();
        return true;
    }

    private static bool TryExtractResponsesApiText(JsonElement root, out string result)
    {
        result = string.Empty;
        if (root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(outputText.GetString()))
        {
            result = NormalizeModelText(outputText.GetString()!);
            return !string.IsNullOrWhiteSpace(result);
        }

        if (root.TryGetProperty("output", out var output) && TryExtractText(output, out var nested) &&
            !string.IsNullOrWhiteSpace(nested))
        {
            result = NormalizeModelText(nested!);
            return !string.IsNullOrWhiteSpace(result);
        }
        return false;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
