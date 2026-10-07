using System;
using System.Collections.Generic;
using System.Text.Json;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed record ProviderUsageMetadata(
    int? InputTokens,
    int? OutputTokens,
    string? RequestId,
    int? CacheReadInputTokens = null,
    int? CacheCreationInputTokens = null,
    string? FinishReason = null);

/// <summary>Content-free metadata for one completed HTTP attempt.</summary>
public sealed record ProviderRequestTelemetry(
    string? RequestId,
    double LatencyMilliseconds,
    int? InputTokens,
    int? OutputTokens,
    int? HttpStatusCode,
    string Outcome,
    int? CacheReadInputTokens = null,
    int? CacheCreationInputTokens = null,
    string? RuntimeBackend = null,
    double? RuntimeStartupToReadyMilliseconds = null,
    long? RuntimePeakWorkingSetBytes = null,
    bool? IsFirstRequestAfterRuntimeStart = null,
    string? FinishReason = null,
    string? ContextPreflightOutcome = null);

/// <summary>
/// Extracts non-content usage fields from already-bounded provider responses.
/// Missing or malformed usage is represented as unknown rather than estimated.
/// </summary>
public static class ProviderUsageMetadataParser
{
    private static readonly HashSet<string> OpenAiFinishReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "length", "content_filter", "tool_calls", "function_call", "error", "other",
        "aborted", "insufficient_system_resource"
    };
    private static readonly HashSet<string> AnthropicFinishReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "end_turn", "max_tokens", "stop_sequence", "tool_use", "pause_turn", "refusal"
    };
    private static readonly HashSet<string> GeminiFinishReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "FINISH_REASON_UNSPECIFIED", "STOP", "MAX_TOKENS", "SAFETY", "RECITATION", "LANGUAGE",
        "OTHER", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII", "MALFORMED_FUNCTION_CALL", "IMAGE_SAFETY",
        "IMAGE_PROHIBITED_CONTENT", "IMAGE_OTHER", "NO_IMAGE", "IMAGE_RECITATION", "UNEXPECTED_TOOL_CALL",
        "TOO_MANY_TOOL_CALLS", "MISSING_THOUGHT_SIGNATURE", "MALFORMED_RESPONSE", "ESCALATION"
    };
    private static readonly HashSet<string> ResponsesFinishReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed", "incomplete", "failed", "cancelled", "queued", "in_progress", "max_output_tokens",
        "content_filter", "max_messages", "steered"
    };

    public static ProviderUsageMetadata Parse(ProviderProtocol protocol, string responseBody)
    {
        ArgumentNullException.ThrowIfNull(responseBody);
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, null, null);
            var requestId = NormalizeRequestId(ReadString(root, protocol == ProviderProtocol.GeminiGenerateContent ? "responseId" : "id"));
            var finishReason = ReadFinishReason(root, protocol);
            var usageName = protocol == ProviderProtocol.GeminiGenerateContent ? "usageMetadata" : "usage";
            if (!root.TryGetProperty(usageName, out var usage) || usage.ValueKind != JsonValueKind.Object)
                return new(null, null, requestId, FinishReason: finishReason);

            int? input;
            int? output;
            int? cacheRead;
            int? cacheCreation;
            if (protocol == ProviderProtocol.AnthropicMessages)
            {
                input = SumNonNegative(usage, "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens");
                output = ReadNonNegative(usage, "output_tokens");
                cacheRead = ReadNonNegative(usage, "cache_read_input_tokens");
                cacheCreation = ReadNonNegative(usage, "cache_creation_input_tokens");
            }
            else if (protocol == ProviderProtocol.GeminiGenerateContent)
            {
                input = ReadNonNegative(usage, "promptTokenCount");
                output = ReadNonNegative(usage, "candidatesTokenCount");
                cacheRead = ReadNonNegative(usage, "cachedContentTokenCount");
                cacheCreation = null;
            }
            else
            {
                input = ReadNonNegative(usage, "input_tokens") ?? ReadNonNegative(usage, "prompt_tokens");
                output = ReadNonNegative(usage, "output_tokens") ?? ReadNonNegative(usage, "completion_tokens");
                var detailsName = usage.TryGetProperty("input_tokens_details", out _)
                    ? "input_tokens_details"
                    : "prompt_tokens_details";
                cacheRead = ReadNestedNonNegative(usage, detailsName, "cached_tokens");
                cacheCreation = ReadNestedNonNegative(usage, detailsName, "cache_write_tokens");
            }
            return new(input, output, requestId, cacheRead, cacheCreation, finishReason);
        }
        catch (JsonException)
        {
            return new(null, null, null);
        }
    }

    private static int? SumNonNegative(JsonElement element, params string[] names)
    {
        var total = 0;
        var found = false;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            found = true;
            if (!value.TryGetInt32(out var count) || count < 0) return null;
            try { total = checked(total + count); }
            catch (OverflowException) { return null; }
        }
        return found ? total : null;
    }

    private static int? ReadNonNegative(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) && count >= 0
            ? count
            : null;

    private static int? ReadNestedNonNegative(JsonElement element, string objectName, string valueName) =>
        element.TryGetProperty(objectName, out var details) && details.ValueKind == JsonValueKind.Object
            ? ReadNonNegative(details, valueName)
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? NormalizeRequestId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > 200) return null;
        foreach (var character in normalized)
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':'))
                return null;
        return normalized;
    }

    private static string? ReadFinishReason(JsonElement root, ProviderProtocol protocol)
    {
        string? value = null;
        if (protocol == ProviderProtocol.AnthropicMessages)
        {
            value = ReadString(root, "stop_reason");
        }
        else if (protocol == ProviderProtocol.GeminiGenerateContent)
        {
            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0 && candidates[0].ValueKind == JsonValueKind.Object)
                value = ReadString(candidates[0], "finishReason");
        }
        else if (protocol == ProviderProtocol.OpenAIResponses)
        {
            if (root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object)
                value = ReadString(details, "reason");
            value ??= ReadString(root, "status");
        }
        else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
                 choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object)
        {
            value = ReadString(choices[0], "finish_reason");
        }

        return NormalizeFinishReason(protocol, value);
    }

    private static string? NormalizeFinishReason(ProviderProtocol protocol, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        var allowed = protocol switch
        {
            ProviderProtocol.OpenAICompatible => OpenAiFinishReasons,
            ProviderProtocol.AnthropicMessages => AnthropicFinishReasons,
            ProviderProtocol.GeminiGenerateContent => GeminiFinishReasons,
            ProviderProtocol.OpenAIResponses => ResponsesFinishReasons,
            _ => null
        };
        return allowed is not null && allowed.Contains(normalized) ? normalized.ToLowerInvariant() : null;
    }
}
