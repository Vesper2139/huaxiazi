using System;
using System.Linq;
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
        var firstPrompt = AssistantEmotionProtocol.DecorateSystemPrompt(_builder.BuildAgentContext(request, plan).Text, mode);
        var userMessage = _builder.BuildUserMessage(request);
        string firstRaw;
        try
        {
            firstRaw = await _client.GenerateAsync(firstPrompt, userMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (IsEmptyResponse(exception))
        {
            // 某些兼容网关会以 2xx + 空 content 返回，AIService 会在解析层报告空响应。
            // 只做一次轻量重试，不把空结果交给质量门禁，也不无限重试造成重复计费。
            try
            {
                var retryRaw = await _client.GenerateAsync(
                    AssistantEmotionProtocol.DecorateSystemPrompt(
                        _builder.BuildAgentContext(request, plan).Text + "\n\n上一份结果为空，只需重新输出完整最终提示词。", mode),
                    userMessage,
                    cancellationToken).ConfigureAwait(false);
                var retry = AssistantEmotionProtocol.ParseContent(retryRaw, mode);
                var retryReport = ProfessionalQualityValidator.Validate(plan, retry.Content);
                if (retryReport.IsValid)
                    return Result(retry.Content, plan, true, false, retryReport, retry.Hint);
            }
            catch (InvalidOperationException retryException) when (IsEmptyResponse(retryException))
            {
                // 继续走统一的阻断结果，UI 会显示可执行的“模型返回为空”提示。
            }

            return Result(string.Empty, plan, true, true,
                new QualityReport { Issues = [new QualityIssue("empty-output", "模型没有返回可用内容", QualityIssueSeverity.Quality)] }, null);
        }
        var first = AssistantEmotionProtocol.ParseContent(firstRaw, mode);
        var initialReport = ProfessionalQualityValidator.Validate(plan, first.Content);
        if (initialReport.IsValid) return Result(first.Content, plan, false, false, initialReport, first.Hint);

        var repairedRaw = await _client.GenerateAsync(
            AssistantEmotionProtocol.DecorateSystemPrompt(_builder.BuildAgentContext(request, plan).Text + "\n\n只修复本地质量门禁指出的问题：" + string.Join("；", initialReport.Issues.Select(issue => issue.Message)), mode),
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

    private static bool IsEmptyResponse(InvalidOperationException exception) =>
        exception.Message.Contains("返回为空", StringComparison.Ordinal) ||
        exception.Message.Contains("没有返回可用内容", StringComparison.Ordinal);

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
