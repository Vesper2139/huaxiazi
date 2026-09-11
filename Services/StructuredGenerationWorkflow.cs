using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Huaxiazi.Services;

public sealed record StructuredGenerationResult(
    bool Succeeded,
    string Answer,
    int Attempts,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Runs a bounded generate/validate/repair loop for structured responses.</summary>
public sealed class StructuredGenerationWorkflow
{
    private readonly ITextGenerationClient _client;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

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
        var prompt = systemPrompt ?? string.Empty;
        var last = new StructuredOutputValidationResult(false, string.Empty, "empty", "未执行。");
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var raw = _client is IStructuredTextGenerationClient structured
                ? await structured.GenerateStructuredAsync(prompt, userInput ?? string.Empty, BuildSchema(contract), cancellationToken).ConfigureAwait(false)
                : await _client.GenerateAsync(prompt, userInput ?? string.Empty, cancellationToken).ConfigureAwait(false);
            last = StructuredOutputValidator.Validate(raw, contract);
            if (last.IsValid) return new(true, last.Answer, attempt, null, null);
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
        var schema = new
        {
            type = "object",
            properties = new { answer = new { type = "string" } },
            required = new[] { "answer" },
            additionalProperties = false
        };
        return JsonSerializer.Serialize(schema);
    }
}
