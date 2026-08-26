using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class AIService : ITextGenerationClient, IDisposable
{
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
    {
        ValidateConfiguration(out var baseUri);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var request = CreateRequest(baseUri, systemPrompt, userInput);
                    using var response = await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                        return ParseProtocolContent(responseBody, _protocol);
                    }

                    if (attempt == 0 && IsTransient(response.StatusCode))
                    {
                        await _delayAsync(TimeSpan.FromMilliseconds(250), timeoutSource.Token).ConfigureAwait(false);
                        continue;
                    }

                    var errorBody = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                    var providerMessage = TryReadProviderErrorMessage(errorBody);
                    throw BuildGenerationFailure(response.StatusCode, response.ReasonPhrase, providerMessage);
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
        string? reasonPhrase,
        string? providerMessage)
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
        var detail = string.IsNullOrWhiteSpace(providerMessage) ? string.Empty : $"：{providerMessage}";
        return new GenerationFailureException(
            kind,
            $"API 返回错误 {(int)statusCode} {reasonPhrase}{detail}。",
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

        using var request = CreateRequest(baseUri, "只回复 OK。", "连接测试", isConnectionTest: true);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var requestId = TryGetRequestId(response);
            if (!response.IsSuccessStatusCode)
            {
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

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
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
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private void ValidateConfiguration(out Uri baseUri)
    {
        if (_providerType == ProviderType.Cloud && string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("API Key 未配置。请到“设置 → 模型配置”中填写密钥。");
        if (!Uri.TryCreate(_apiBase, UriKind.Absolute, out baseUri!) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("API 地址无效，请填写完整的 http 或 https 地址。");
        if (_providerType == ProviderType.Cloud && baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("云端模型必须使用 HTTPS 地址，已阻止通过明文 HTTP 发送 API Key。");
        if (_protocol == ProviderProtocol.OpenAICompatible && baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
            throw new InvalidOperationException("OpenAI 兼容接口使用远程 HTTP 地址不安全，请改用 HTTPS 或仅限本地 localhost 测试。");
        if (string.IsNullOrWhiteSpace(_model)) throw new InvalidOperationException("模型名称不能为空。");
    }

    private HttpRequestMessage CreateRequest(Uri baseUri, string systemPrompt, string userInput, bool isConnectionTest = false)
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
                requestBody = isConnectionTest
                    ? new
                    {
                        model = _resolvedModel,
                        messages = new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userInput }
                        },
                        max_tokens = 8,
                        stream = false
                    }
                    : new
                    {
                        model = _resolvedModel,
                        messages = new[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userInput }
                        },
                        temperature = _temperature,
                        top_p = _topP,
                        max_tokens = _maxTokens,
                        stream = false
                    };
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

    private static string? TryReadProviderErrorMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("error", out var error)) return null;
            string? message = error.ValueKind switch
            {
                JsonValueKind.String => error.GetString(),
                JsonValueKind.Object when error.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String => value.GetString(),
                _ => null
            };
            message = message?.Trim();
            if (string.IsNullOrWhiteSpace(message)) return null;
            return message.Length <= 300 ? message : message[..300] + "…";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string ParseContent(string responseBody) => ParseProtocolContent(responseBody, ProviderProtocol.OpenAICompatible);

    private static string ParseProtocolContent(string responseBody, ProviderProtocol protocol)
    {
        using var doc = JsonDocument.Parse(responseBody);
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
            throw new InvalidOperationException("响应格式异常：未找到 choices[0]。");
        }

        var first = choices[0];
        if (first.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("content", out var content))
                return RequireNonEmptyText(content);

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

    private static string RequireNonEmptyText(JsonElement content)
    {
        if (TryExtractText(content, out var result) && !string.IsNullOrWhiteSpace(result))
            return result.Trim();
        throw new InvalidOperationException("模型返回为空，请重试。");
    }

    private static bool TryExtractText(JsonElement value, out string? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.String)
        {
            result = value.GetString();
            return true;
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
            if (item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() is { } blockType &&
                !string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(blockType, "output_text", StringComparison.OrdinalIgnoreCase))
                continue;
            if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                fragments.Append(text.GetString());
            else if (item.TryGetProperty("content", out var nested) && TryExtractText(nested, out var nestedText))
                fragments.Append(nestedText);
        }

        result = fragments.ToString();
        return true;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
