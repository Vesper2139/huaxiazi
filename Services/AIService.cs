using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class AIService : ITextGenerationClient, IStructuredTextGenerationClient, IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
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
    private readonly TimeSpan _timeout;
    private readonly double _temperature;
    private readonly double _topP;
    private readonly int _maxTokens;
    private readonly InferenceLevel _inferenceLevel;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public AIService(
        ProviderProfile profile,
        string? apiKey,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _apiBase = profile.ApiBase;
        _model = profile.Model;
        _resolvedModel = profile.EnableModelMapping && profile.ModelMapping is { Count: > 0 } map
            && map.TryGetValue(profile.Model, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : profile.Model;
        _apiKey = apiKey ?? string.Empty;
        _providerType = profile.Type;
        _platform = profile.Platform;
        _protocol = profile.Protocol;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 10, 600));
        _temperature = Math.Clamp(profile.Temperature, 0, 2);
        _topP = Math.Clamp(profile.TopP, 0, 1);
        _maxTokens = Math.Clamp(profile.MaxTokens, 128, 32768);
        _inferenceLevel = profile.InferenceLevel;
        _delayAsync = delayAsync ?? Task.Delay;
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
                try
                {
                    using var request = CreateRequest(baseUri, systemPrompt, userInput, forceDisableDeepSeekThinking: disableDeepSeekThinking, jsonSchema: jsonSchema);
                    using var response = await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await ReadResponseBodyLimitedAsync(response.Content, timeoutSource.Token).ConfigureAwait(false);
                        return ParseProtocolContent(responseBody, _protocol);
                    }

                    if (attempt == 0 && IsTransient(response.StatusCode))
                    {
                        await _delayAsync(TimeSpan.FromMilliseconds(250), timeoutSource.Token).ConfigureAwait(false);
                        continue;
                    }

                    // Do not read arbitrary provider error bodies: they may echo the
                    // user's prompt or contain server-side secrets.
                    throw BuildGenerationFailure(response.StatusCode, response.ReasonPhrase);
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
                    await _delayAsync(TimeSpan.FromMilliseconds(250), timeoutSource.Token).ConfigureAwait(false);
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
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GenerationFailureKind.Authentication,
            HttpStatusCode.NotFound => GenerationFailureKind.ResourceMissing,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => GenerationFailureKind.Timeout,
            _ when (int)statusCode >= 500 => GenerationFailureKind.ProviderUnavailable,
            _ => GenerationFailureKind.RequestRejected
        };
        // Provider error bodies are untrusted and may echo user input or server secrets.
        // Keep user-facing/logged failures to a normalized status only.
        return new GenerationFailureException(
            kind,
            $"API 返回错误 {(int)statusCode} {reasonPhrase}。",
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

                        var status = response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ConnectionTestStatus.AuthFailed,
                            HttpStatusCode.NotFound => ConnectionTestStatus.ModelUnavailable,
                            HttpStatusCode.TooManyRequests => ConnectionTestStatus.RateLimited,
                            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ConnectionTestStatus.Timeout,
                            _ => ConnectionTestStatus.ProviderError
                        };
                        var message = status switch
                        {
                            ConnectionTestStatus.AuthFailed => "API Key 无效、已过期或没有访问权限，请重新填写。",
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
                        _ = ParseProtocolContent(body, _protocol);
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
            case ProviderProtocol.AnthropicMessages:
                endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/v1/messages");
                requestBody = new
                {
                    model = _resolvedModel,
                    max_tokens = isConnectionTest ? 8 : _maxTokens,
                    temperature = _temperature,
                    top_p = _topP,
                    system = systemPrompt,
                    messages = new[] { new { role = "user", content = userInput } }
                };
                break;
            case ProviderProtocol.GeminiGenerateContent:
                endpoint = new Uri(baseUri.ToString().TrimEnd('/') + $"/models/{Uri.EscapeDataString(_resolvedModel)}:generateContent");
                requestBody = new
                {
                    system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = userInput } } } },
                    generationConfig = new { temperature = _temperature, topP = _topP, maxOutputTokens = isConnectionTest ? 8 : _maxTokens }
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
        var body = new Dictionary<string, object?>
        {
            ["model"] = _resolvedModel,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userInput }
            },
            ["max_tokens"] = isConnectionTest ? 8 : _maxTokens,
            ["stream"] = false
        };

        if (IsDeepSeekV4)
        {
            var disableThinking = isConnectionTest || forceDisableDeepSeekThinking;
            body["thinking"] = new { type = disableThinking ? "disabled" : "enabled" };
            if (!disableThinking)
            {
                body["reasoning_effort"] = _inferenceLevel switch
                {
                    InferenceLevel.Low => "low",
                    InferenceLevel.High => "max",
                    _ => "high"
                };
            }
            else if (!isConnectionTest)
            {
                body["temperature"] = _temperature;
                body["top_p"] = _topP;
            }
        }
        else if (!isConnectionTest)
        {
            body["temperature"] = _temperature;
            body["top_p"] = _topP;
        }

        if (!string.IsNullOrWhiteSpace(jsonSchema) && !isConnectionTest)
        {
            using var schema = JsonDocument.Parse(jsonSchema);
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "huaxiazi_answer", strict = true, schema = schema.RootElement.Clone() }
            };
        }

        return body;
    }

    private bool IsDeepSeekV4 => _platform == ProviderPlatform.DeepSeek &&
        _resolvedModel.StartsWith("deepseek-v4", StringComparison.OrdinalIgnoreCase);

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
        foreach (var name in new[] { "x-request-id", "request-id", "x-amzn-requestid", "x-goog-request-id" })
        {
            if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        }
        return null;
    }

    private sealed class ResponseTooLargeException : Exception;

    internal static string ParseContent(string responseBody) => ParseProtocolContent(responseBody, ProviderProtocol.OpenAICompatible);

    private static string ParseProtocolContent(string responseBody, ProviderProtocol protocol)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (JsonException) when (protocol == ProviderProtocol.OpenAICompatible && TryParseServerSentEvents(responseBody, out var streamedText))
        {
            return streamedText;
        }
        using (doc)
        {
        var root = doc.RootElement;

        if (protocol == ProviderProtocol.AnthropicMessages)
        {
            if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in blocks.EnumerateArray())
                    if (block.TryGetProperty("text", out var text)) return RequireNonEmptyText(text);
            }
            throw new InvalidOperationException("响应格式异常：未找到 content[].text。");
        }

        if (protocol == ProviderProtocol.GeminiGenerateContent)
        {
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
        if (first.TryGetProperty("message", out var message))
        {
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
