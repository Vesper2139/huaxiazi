using System;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class PromptOptimizationWorkflowService
{
    private readonly ITextGenerationClient _client;
    private readonly PromptBuilderService _builder;

    public PromptOptimizationWorkflowService(ITextGenerationClient client, PromptBuilderService builder)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public async Task<TransformationResult> ExecuteAsync(PromptRequest request, ProfessionalizationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        var mode = App.Settings.CompanionDriverMode;
        var firstRaw = await _client.GenerateAsync(
            AssistantEmotionProtocol.DecorateSystemPrompt(_builder.Build(request, plan), mode),
            _builder.BuildUserMessage(request),
            cancellationToken).ConfigureAwait(false);
        var first = AssistantEmotionProtocol.ParseContent(firstRaw, mode);
        var initialReport = ProfessionalQualityValidator.Validate(plan, first.Content);
        if (initialReport.IsValid) return Result(first.Content, plan, false, false, initialReport, first.Hint);

        var repairedRaw = await _client.GenerateAsync(
            AssistantEmotionProtocol.DecorateSystemPrompt(_builder.BuildRepairPrompt(request, plan, initialReport.Issues), mode),
            _builder.BuildUserMessage(request),
            cancellationToken).ConfigureAwait(false);
        var repaired = AssistantEmotionProtocol.ParseContent(repairedRaw, mode);
        var repairedReport = ProfessionalQualityValidator.Validate(plan, repaired.Content);
        if (repairedReport.IsValid) return Result(repaired.Content, plan, true, false, repairedReport, repaired.Hint);
        // 空响应不能走“事实安全则回退”分支；否则 UI 会收到空结果并误以为本次已完成。
        if (initialReport.IsSafe && !string.IsNullOrWhiteSpace(first.Content))
            return Result(first.Content, plan, true, false, initialReport, first.Hint);
        return Result(string.Empty, plan, true, true, repairedReport, null);
    }

    private static TransformationResult Result(
        string content,
        ProfessionalizationPlan plan,
        bool repaired,
        bool blocked,
        QualityReport report,
        AssistantEmotionHint? companionEmotion) => new()
    {
        Content = content,
        StrategyId = plan.StrategyId,
        StrategyName = plan.StrategyName,
        WasRepaired = repaired,
        IsBlocked = blocked,
        ValidationIssues = report.Issues,
        CompanionEmotion = companionEmotion
    };
}
