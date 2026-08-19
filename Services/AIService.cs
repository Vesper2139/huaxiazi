using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFloat.Models;

namespace PromptFloat.Services;

public sealed class AIService : ITextGenerationClient, IDisposable
{
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _apiBase;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly ProviderType _providerType;
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
        _apiKey = apiKey ?? string.Empty;
        _providerType = profile.Type;
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
            _httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
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

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        throw new InvalidOperationException("请求过于频繁，服务正在限流，请稍后重试。");

                    var errorBody = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                    var providerMessage = TryReadProviderErrorMessage(errorBody);
                    var detail = string.IsNullOrWhiteSpace(providerMessage) ? string.Empty : $"：{providerMessage}";
                    throw new InvalidOperationException($"API 返回错误 {(int)response.StatusCode} {response.ReasonPhrase}{detail}。");
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
            throw new InvalidOperationException($"请求超时（>{_timeout.TotalSeconds:0} 秒），请检查网络或模型配置。");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"网络请求失败：{ex.Message}");
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    public async Task<string> DiagnoseAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(out var baseUri);
        using var request = CreateRequest(baseUri, systemPrompt, userInput);
        var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var report = new StringBuilder()
            .AppendLine($"{request.Method} {request.RequestUri}")
            .AppendLine($"Protocol: {_protocol}")
            .AppendLine("Authorization: [REDACTED]")
            .AppendLine("Request body:")
            .AppendLine(requestBody)
            .AppendLine();
        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            report.AppendLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}")
                .AppendLine("Response headers:");
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                report.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
            report.AppendLine("Response body:").Append(responseBody);
            return report.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return report.AppendLine($"请求超时（>{_timeout.TotalSeconds:0} 秒）。").ToString();
        }
        catch (Exception exception)
        {
            return report.AppendLine($"请求异常：{exception.GetType().Name}: {exception.Message}").ToString();
        }
    }

    private void ValidateConfiguration(out Uri baseUri)
    {
        if (_providerType == ProviderType.Cloud && string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("API Key 未配置。请到“设置 → 模型配置”中填写密钥。");
        if (!Uri.TryCreate(_apiBase, UriKind.Absolute, out baseUri!) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("API 地址无效，请填写完整的 http 或 https 地址。");
        if (_providerType == ProviderType.Cloud && baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("云端模型必须使用 HTTPS 地址，已阻止通过明文 HTTP 发送 API Key。");
        if (string.IsNullOrWhiteSpace(_model)) throw new InvalidOperationException("模型名称不能为空。");
    }

    private HttpRequestMessage CreateRequest(Uri baseUri, string systemPrompt, string userInput)
    {
        Uri endpoint;
        object requestBody;
        switch (_protocol)
        {
            case ProviderProtocol.AnthropicMessages:
                endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/v1/messages");
                requestBody = new
                {
                    model = _model,
                    max_tokens = _maxTokens,
                    temperature = _temperature,
                    top_p = _topP,
                    system = systemPrompt,
                    messages = new[] { new { role = "user", content = userInput } }
                };
                break;
            case ProviderProtocol.GeminiGenerateContent:
                endpoint = new Uri(baseUri.ToString().TrimEnd('/') + $"/models/{Uri.EscapeDataString(_model)}:generateContent");
                requestBody = new
                {
                    system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = userInput } } } },
                    generationConfig = new { temperature = _temperature, topP = _topP, maxOutputTokens = _maxTokens }
                };
                break;
            default:
                endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/chat/completions");
                requestBody = new
                {
                    model = _model,
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
        if (!first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("响应格式异常：未找到 choices[0].message.content。");
        }

        return RequireNonEmptyText(content);
    }

    private static string RequireNonEmptyText(JsonElement content)
    {
        var result = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("模型返回为空，请重试。");
        }
        return result;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
