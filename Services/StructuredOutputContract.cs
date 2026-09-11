using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Huaxiazi.Services;

public sealed record StructuredOutputContract(
    string Name,
    IReadOnlySet<string> RequiredFields,
    IReadOnlySet<string> AllowedFields,
    int MaxAnswerCharacters = 12_000);

public sealed record StructuredOutputValidationResult(
    bool IsValid,
    string Answer,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Provider-independent semantic guard for structured model responses.</summary>
public static class StructuredOutputValidator
{
    public static StructuredOutputValidationResult Validate(string? raw, StructuredOutputContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (string.IsNullOrWhiteSpace(raw)) return Invalid("empty", "结构化输出为空。");
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid("root-type", "根节点必须是 JSON 对象。");
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            if (contract.RequiredFields.Any(field => !names.Contains(field))) return Invalid("missing-field", "缺少必需字段。");
            if (names.Any(field => !contract.AllowedFields.Contains(field))) return Invalid("extra-field", "包含未允许字段。");
            if (!document.RootElement.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.String)
                return Invalid("answer-type", "answer 必须是字符串。");
            var text = answer.GetString() ?? string.Empty;
            if (text.Length > contract.MaxAnswerCharacters) return Invalid("answer-too-long", "answer 超出长度预算。");
            if (ContainsMetaEcho(text)) return Invalid("meta-echo", "answer 回显了协议或内部分析。");
            return new(true, text, null, null);
        }
        catch (JsonException) { return Invalid("invalid-json", "输出不是合法 JSON。"); }
    }

    private static StructuredOutputValidationResult Invalid(string code, string message) => new(false, string.Empty, code, message);

    private static bool ContainsMetaEcho(string text) =>
        text.Contains("architecture_weights", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("只返回一个合法 JSON", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("只包含 answer 字段", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("首先，用户要求", StringComparison.Ordinal);
}
