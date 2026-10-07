using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.DatasetBuilder;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.BlindEvaluationRunner;

/// <summary>
/// Runs the product's complete workflow and quality gate for one admitted sample.
/// Provider selection and credential handling stay outside this executor.
/// </summary>
public static class BlindWorkflowExecutor
{
    public static async Task<BlindWorkflowPrediction> ExecuteConversationAsync(
        BlindEvaluationRecord record,
        ITextGenerationClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(client);
        if (record.Task != "polish" || string.IsNullOrWhiteSpace(record.ConversationId) || record.Turns is not { Count: >= 2 })
            throw new ArgumentException("顺序对话回放必须提供带 conversation_id 的多轮 polish 记录。", nameof(record));

        var history = new System.Collections.Generic.List<PolishConversationMessage>();
        BlindWorkflowPrediction? lastPrediction = null;
        foreach (var turn in record.Turns)
        {
            if (turn is null || string.IsNullOrWhiteSpace(turn.UserInput))
                return new(record.Id, "invalid", string.Empty, [], ["conversation_turn_invalid"], false);
            var turnRecord = record with { Input = turn.UserInput, ConversationId = null, Turns = null };
            var workflow = BlindWorkflowRequestFactory.Create(turnRecord, history);
            lastPrediction = await ExecuteAsync(workflow, client, cancellationToken).ConfigureAwait(false);
            if (lastPrediction.Status is not ("completed" or "needs_clarification")) return lastPrediction;

            history.Add(new("user", turn.UserInput));
            var assistantResponse = lastPrediction.Status == "needs_clarification"
                ? string.Join(Environment.NewLine, lastPrediction.ClarificationQuestions)
                : lastPrediction.Output;
            history.Add(new("assistant", assistantResponse));
        }
        return lastPrediction ?? new(record.Id, "invalid", string.Empty, [], ["conversation_no_output"], false);
    }

    public static async Task<BlindWorkflowPrediction> ExecuteAsync(
        BlindWorkflowRequest request,
        ITextGenerationClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);

        if (request.PolishRequest is { } polish)
        {
            var result = await new PolishWorkflowService(
                    client,
                    new PolishPromptBuilderService(),
                    archive: null,
                    companionModeProvider: () => request.CompanionMode)
                .ExecuteAsync(polish, request.ClarificationEnabled, autoArchive: false,
                    existingItemId: null, createdAt: DateTimeOffset.UtcNow, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return result.Response.Kind switch
            {
                PolishResponseKind.Final => new(request.RecordId, "completed", result.Response.Content,
                    [], result.ValidationIssues, result.WasRepaired),
                PolishResponseKind.NeedsClarification => new(request.RecordId, "needs_clarification", string.Empty,
                    result.Response.Questions, [], false),
                _ => new(request.RecordId, "invalid", string.Empty, [], result.ValidationIssues, result.WasRepaired)
            };
        }

        if (request.PromptRequest is { } prompt)
        {
            if (request.Plan.NeedsClarification)
            {
                return request.ClarificationEnabled
                    ? new(request.RecordId, "needs_clarification", string.Empty,
                        request.Plan.ClarificationQuestions, [], false)
                    : new(request.RecordId, "invalid", string.Empty, [], ["missing-information"], false);
            }

            var result = await new PromptOptimizationWorkflowService(
                    client,
                    new PromptBuilderService(),
                    () => request.CompanionMode)
                .ExecuteAsync(prompt, request.Plan, cancellationToken)
                .ConfigureAwait(false);
            var status = result.IsBlocked ? "blocked" : string.IsNullOrWhiteSpace(result.Content) ? "invalid" : "completed";
            return new(request.RecordId, status, result.Content, [],
                result.ValidationIssues.Select(issue => issue.Code).ToArray(), result.WasRepaired);
        }

        throw new InvalidOperationException("盲评任务没有可执行的产品请求。");
    }
}

/// <summary>
/// Reviewer-facing prediction data. Candidate/model identity and runtime telemetry
/// must be stored separately by the caller to preserve blinding.
/// </summary>
public sealed record BlindWorkflowPrediction(
    string Id,
    string Status,
    string Output,
    IReadOnlyList<string> ClarificationQuestions,
    IReadOnlyList<string> QualityIssueCodes,
    bool WasRepaired);
