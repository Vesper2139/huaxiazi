using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Huaxiazi.Services;

public sealed record StructuredOutputContract(
    string Name,
    IReadOnlySet<string> RequiredFields,
    IReadOnlySet<string> AllowedFields,
    int MaxAnswerCharacters = 12_000,
    IReadOnlyDictionary<string, StructuredOutputFieldType>? FieldTypes = null,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? AllowedStringValues = null,
    IReadOnlyDictionary<string, StructuredOutputNumericRange>? NumericRanges = null);

public enum StructuredOutputFieldType
{
    String,
    StringArray,
    Number
}

public sealed record StructuredOutputNumericRange(double Minimum, double Maximum);

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
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) return Invalid("duplicate-field", "JSON 对象包含重复字段，字段值存在歧义。");
            }
            if (contract.RequiredFields.Any(field => !names.Contains(field))) return Invalid("missing-field", "缺少必需字段。");
            if (names.Any(field => !contract.AllowedFields.Contains(field))) return Invalid("extra-field", "包含未允许字段。");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var fieldType = contract.FieldTypes is not null && contract.FieldTypes.TryGetValue(property.Name, out var declaredType)
                    ? declaredType
                    : StructuredOutputFieldType.String;
                var validType = property.Value.ValueKind == JsonValueKind.Null && !contract.RequiredFields.Contains(property.Name) ||
                    fieldType == StructuredOutputFieldType.String && property.Value.ValueKind == JsonValueKind.String ||
                    fieldType == StructuredOutputFieldType.StringArray && property.Value.ValueKind == JsonValueKind.Array &&
                    property.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) ||
                    fieldType == StructuredOutputFieldType.Number && property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetDouble(out _);
                if (!validType) return Invalid("field-type", $"字段 {property.Name} 的类型不符合约定。");
                if (fieldType == StructuredOutputFieldType.Number && property.Value.ValueKind == JsonValueKind.Number &&
                    contract.NumericRanges is not null && contract.NumericRanges.TryGetValue(property.Name, out var range) &&
                    (property.Value.GetDouble() < range.Minimum || property.Value.GetDouble() > range.Maximum))
                    return Invalid("number-range", $"字段 {property.Name} 超出允许范围。");
                if (fieldType == StructuredOutputFieldType.String && property.Value.ValueKind == JsonValueKind.String &&
                    contract.AllowedStringValues is not null &&
                    contract.AllowedStringValues.TryGetValue(property.Name, out var allowedValues) &&
                    !allowedValues.Contains(property.Value.GetString() ?? string.Empty))
                    return Invalid("enum-value", $"字段 {property.Name} 的值不在允许范围内。");
            }

            var text = document.RootElement.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.String
                ? answer.GetString() ?? string.Empty
                : raw;
            if (text.Length > contract.MaxAnswerCharacters) return Invalid("answer-too-long", "输出内容超出长度预算。");
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
