using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed record StructuredGenerationResult(
    bool Succeeded,
    string Answer,
    int Attempts,
    string? ErrorCode,
    string? ErrorMessage,
    string? RawResponse = null);

/// <summary>
/// Runs the structured generation attempts for one user task. Keep one instance across
/// that task's initial generation and repair calls so a rejected native schema stays disabled.
/// </summary>
public sealed class StructuredGenerationWorkflow
{
    private readonly ITextGenerationClient _client;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private bool _nativeSchemaRejected;

    public StructuredGenerationWorkflow(ITextGenerationClient client, Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<StructuredGenerationResult> ExecuteAsync(
        string systemPrompt,
        string userInput,
        StructuredOutputContract contract,
        int maxAttempts = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (maxAttempts is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (contract.RequiredFields.Any(field => !contract.AllowedFields.Contains(field)))
            throw new ArgumentException("Every required field must be allowed.", nameof(contract));
        if (contract.FieldTypes is not null && contract.FieldTypes.Keys.Any(field => !contract.AllowedFields.Contains(field)))
            throw new ArgumentException("Every typed field must be allowed.", nameof(contract));
        if (contract.AllowedStringValues is not null && contract.AllowedStringValues.Keys.Any(field =>
                !contract.AllowedFields.Contains(field) ||
                contract.FieldTypes is not null && contract.FieldTypes.TryGetValue(field, out var type) && type != StructuredOutputFieldType.String))
            throw new ArgumentException("Every enum field must be an allowed string field.", nameof(contract));
        if (contract.NumericRanges is not null && contract.NumericRanges.Any(pair =>
                !contract.AllowedFields.Contains(pair.Key) ||
                contract.FieldTypes is null || !contract.FieldTypes.TryGetValue(pair.Key, out var type) || type != StructuredOutputFieldType.Number ||
                !double.IsFinite(pair.Value.Minimum) || !double.IsFinite(pair.Value.Maximum) || pair.Value.Minimum > pair.Value.Maximum))
            throw new ArgumentException("Every numeric range must bind to a valid numeric field.", nameof(contract));
        var prompt = systemPrompt ?? string.Empty;
        var schema = BuildSchema(contract);
        var structuredClient = _client as IStructuredTextGenerationClient;
        var last = new StructuredOutputValidationResult(false, string.Empty, "empty", "未执行。");
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string raw;
            if (structuredClient is not null && !_nativeSchemaRejected)
            {
                try
                {
                    raw = await structuredClient.GenerateStructuredAsync(prompt, userInput ?? string.Empty, schema, cancellationToken).ConfigureAwait(false);
                }
                catch (GenerationFailureException exception) when (
                    exception.Kind == GenerationFailureKind.StructuredOutputUnsupported)
                {
                    _nativeSchemaRejected = true;
                    raw = await _client.GenerateAsync(BuildFallbackPrompt(prompt, schema), userInput ?? string.Empty, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                raw = await _client.GenerateAsync(BuildFallbackPrompt(prompt, schema), userInput ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }
            last = StructuredOutputValidator.Validate(raw, contract);
            if (last.IsValid) return new(true, last.Answer, attempt, null, null, raw);
            if (attempt < maxAttempts)
            {
                prompt += $"\n\n<output_repair>上一次输出未通过结构化门禁（{last.ErrorCode}）。只返回符合约定 schema 的最终 JSON，不解释修复过程。</output_repair>";
                await _delayAsync(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        return new(false, string.Empty, maxAttempts, last.ErrorCode, last.ErrorMessage);
    }

    private static string BuildSchema(StructuredOutputContract contract)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in contract.AllowedFields.OrderBy(field => field, StringComparer.Ordinal))
        {
            var fieldType = contract.FieldTypes is not null && contract.FieldTypes.TryGetValue(field, out var declaredType)
                ? declaredType
                : StructuredOutputFieldType.String;
            var nullable = !contract.RequiredFields.Contains(field);
            var type = fieldType switch
            {
                StructuredOutputFieldType.String => "string",
                StructuredOutputFieldType.StringArray => "array",
                StructuredOutputFieldType.Number => "number",
                _ => throw new ArgumentOutOfRangeException(nameof(contract), $"Unsupported field type for {field}.")
            };
            var fieldSchema = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = nullable ? new[] { type, "null" } : type
            };
            if (fieldType == StructuredOutputFieldType.StringArray) fieldSchema["items"] = new { type = "string" };
            if (contract.AllowedStringValues is not null && contract.AllowedStringValues.TryGetValue(field, out var allowedValues))
                fieldSchema["enum"] = nullable
                    ? allowedValues.OrderBy(value => value, StringComparer.Ordinal).Cast<object?>().Append(null).ToArray()
                    : allowedValues.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            // Keep numeric bounds in the provider-independent local validator. Anthropic's raw
            // structured-output API rejects minimum/maximum even though some other providers accept them.
            properties[field] = fieldSchema;
        }

        var schema = new
        {
            type = "object",
            properties,
            // OpenAI strict JSON Schema requires every property in required. Optional semantic fields
            // are represented as nullable and remain optional in the local validator.
            required = contract.AllowedFields.OrderBy(field => field, StringComparer.Ordinal).ToArray(),
            additionalProperties = false
        };
        return JsonSerializer.Serialize(schema);
    }

    private static string BuildFallbackPrompt(string prompt, string schema) =>
        prompt + "\n\n<output_contract>只返回一个 JSON 对象，严格遵守以下 JSON Schema；不要使用 Markdown 代码围栏或附加说明：\n" + schema + "\n</output_contract>";
}
