using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>从 OpenRouter 官方目录读取当前支持文本输入/输出的模型；目录字段不作为端点能力证明。</summary>
public sealed class OpenRouterModelCatalogService
{
    private const int MaxCatalogBytes = 16 * 1024 * 1024;
    private const int MaxModelCount = 20_000;
    private static readonly Uri CatalogUri = new("https://openrouter.ai/api/v1/models", UriKind.Absolute);
    private static readonly IReadOnlySet<string> SupportedRequestParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "response_format", "structured_outputs", "max_tokens", "temperature", "top_p"
    };
    private static readonly HttpClient SharedClient = new(
        new HttpClientHandler { AllowAutoRedirect = false },
        disposeHandler: true)
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly HttpClient _httpClient;

    public OpenRouterModelCatalogService(HttpClient? httpClient = null) => _httpClient = httpClient ?? SharedClient;

    public async Task<IReadOnlyList<ModelDefinition>> FetchAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(char.IsControl))
            throw new ArgumentException("OpenRouter API Key is missing or invalid.", nameof(apiKey));

        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter model catalog request failed (HTTP {(int)response.StatusCode}).", null, response.StatusCode);

        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("OpenRouter model catalog response has no data array.");
            if (data.GetArrayLength() > MaxModelCount)
                throw new InvalidDataException("OpenRouter model catalog contains too many entries.");

            var modelsById = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryReadSafeString(item, "id", out var id) ||
                    !TryReadArchitecture(item, out var architecture) ||
                    !SupportsTextInputAndOutput(architecture))
                    continue;

                var name = TryReadSafeString(item, "name", out var displayName) ? displayName : id;
                modelsById.TryAdd(id, new ModelDefinition(name, id)
                {
                    SupportedParameters = TryReadSupportedParameters(item)
                });
            }

            return modelsById.Values
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("OpenRouter model catalog response is not valid JSON.", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxCatalogBytes)
            throw new InvalidDataException("OpenRouter model catalog response exceeds the size limit.");

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxCatalogBytes)
                throw new InvalidDataException("OpenRouter model catalog response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool TryReadSafeString(JsonElement parent, string property, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= 256 && !value.Any(char.IsControl);
    }

    private static bool TryReadArchitecture(JsonElement item, out JsonElement architecture)
    {
        architecture = default;
        return item.TryGetProperty("architecture", out architecture) && architecture.ValueKind == JsonValueKind.Object;
    }

    private static IReadOnlySet<string>? TryReadSupportedParameters(JsonElement item)
    {
        if (!item.TryGetProperty("supported_parameters", out var values) || values.ValueKind != JsonValueKind.Array)
            return null;

        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            var parameter = value.GetString()?.Trim();
            if (parameter is not null && SupportedRequestParameters.Contains(parameter))
                supported.Add(parameter.ToLowerInvariant());
        }
        return supported;
    }

    private static bool SupportsTextInputAndOutput(JsonElement architecture) =>
        ContainsText(architecture, "input_modalities") && ContainsText(architecture, "output_modalities");

    private static bool ContainsText(JsonElement architecture, string property) =>
        architecture.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array &&
        values.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), "text", StringComparison.OrdinalIgnoreCase));
}
