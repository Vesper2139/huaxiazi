using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class PromptOptimizationWorkflowService
{
    private sealed record PromptGenerationOutput(string Text, bool? StructuredOutputValid);

    private static readonly StructuredOutputContract PromptAnswerContract = new(
        "optimized-prompt",
        new HashSet<string>(StringComparer.Ordinal) { "answer" },
        new HashSet<string>(StringComparer.Ordinal) { "answer" });
    private static readonly StructuredOutputContract PromptAnswerEmotionContract = new(
        "optimized-prompt-with-emotion",
        new HashSet<string>(StringComparer.Ordinal) { "answer" },
        new HashSet<string>(StringComparer.Ordinal) { "answer", "companion_emotion", "companion_intensity" },
        FieldTypes: new Dictionary<string, StructuredOutputFieldType>(StringComparer.Ordinal)
        {
            ["companion_intensity"] = StructuredOutputFieldType.Number
        },
        AllowedStringValues: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["companion_emotion"] = Enum.GetNames<AssistantEmotionKind>().ToHashSet(StringComparer.OrdinalIgnoreCase)
        },
        NumericRanges: new Dictionary<string, StructuredOutputNumericRange>(StringComparer.Ordinal)
        {
            ["companion_intensity"] = new(0, 1)
        });

    public static StructuredOutputContract GetOutputContract(CompanionDriverMode companionMode) =>
        companionMode == CompanionDriverMode.EmotionAssistant
            ? PromptAnswerEmotionContract
            : PromptAnswerContract;

    private readonly ITextGenerationClient _client;
    private readonly PromptBuilderService _builder;
    private readonly Func<CompanionDriverMode> _companionModeProvider;

    public PromptOptimizationWorkflowService(
        ITextGenerationClient client,
        PromptBuilderService builder,
        Func<CompanionDriverMode>? companionModeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _companionModeProvider = companionModeProvider ?? (() => App.Settings.CompanionDriverMode);
    }

    public async Task<TransformationResult> ExecuteAsync(PromptRequest request, ProfessionalizationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.NeedsClarification)
        {
            return Result(string.Empty, plan, repaired: false, blocked: true,
                new QualityReport
                {
                    Issues = [new QualityIssue(
                        "missing-information",
                        $"当前信息不足以完成任务。缺少：{MissingInformationSummary.For(plan)}。未向模型发送请求。",
                        QualityIssueSeverity.Unsafe)]
                },
                companionEmotion: null);
        }
        var mode = _companionModeProvider();
        // Reuse one workflow for this task so repairs keep the Provider's schema downgrade.
        var structuredWorkflow = new StructuredGenerationWorkflow(_client);
        var firstPrompt = DecorateSystemPrompt(_builder.BuildAgentContext(request, plan).Text, mode);
        var userMessage = _builder.BuildUserMessage(request);
        PromptGenerationOutput firstOutput;
        try
        {
            firstOutput = await GeneratePromptAsync(firstPrompt, userMessage, mode, structuredWorkflow, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (IsEmptyResponse(exception))
        {
            // 某些兼容网关会以 2xx + 空 content 返回，AIService 会在解析层报告空响应。
            // 只做一次轻量重试，不把空结果交给质量门禁，也不无限重试造成重复计费。
            PromptGenerationOutput? retryOutput = null;
            try
            {
                retryOutput = await GeneratePromptAsync(
                    DecorateSystemPrompt(
                        _builder.BuildAgentContext(request, plan).Text + "\n\n上一份结果为空，只需重新输出完整最终提示词。", mode),
                    userMessage,
                    mode,
                    structuredWorkflow,
                    cancellationToken).ConfigureAwait(false);
                var retry = AssistantEmotionProtocol.ParseContent(retryOutput.Text, mode);
                var retryReport = ProfessionalQualityValidator.Validate(plan, retry.Content);
                if (retryReport.IsValid)
                    return Result(retry.Content, plan, true, false, retryReport, retry.Hint, retryOutput.StructuredOutputValid);
            }
            catch (InvalidOperationException retryException) when (IsEmptyResponse(retryException))
            {
                // 继续走统一的阻断结果，UI 会显示可执行的“模型返回为空”提示。
            }

            return Result(string.Empty, plan, true, true,
                new QualityReport { Issues = [new QualityIssue("empty-output", "模型没有返回可用内容", QualityIssueSeverity.Quality)] }, null,
                retryOutput?.StructuredOutputValid);
        }
        var first = AssistantEmotionProtocol.ParseContent(firstOutput.Text, mode);
        var initialReport = ProfessionalQualityValidator.Validate(plan, first.Content);
        if (initialReport.IsValid) return Result(first.Content, plan, false, false, initialReport, first.Hint, firstOutput.StructuredOutputValid);

        var repairedOutput = await GeneratePromptAsync(
            DecorateSystemPrompt(_builder.BuildAgentContext(request, plan).Text + "\n\n只修复本地质量门禁指出的问题：" + string.Join("；", initialReport.Issues.Select(issue => issue.Message)), mode),
            _builder.BuildUserMessage(request),
            mode,
            structuredWorkflow,
            cancellationToken).ConfigureAwait(false);
        var repaired = AssistantEmotionProtocol.ParseContent(repairedOutput.Text, mode);
        var repairedReport = ProfessionalQualityValidator.Validate(plan, repaired.Content);
        if (repairedReport.IsValid) return Result(repaired.Content, plan, true, false, repairedReport, repaired.Hint, repairedOutput.StructuredOutputValid);
        // 空响应不能走“事实安全则回退”分支；否则 UI 会收到空结果并误以为本次已完成。
        if (initialReport.IsSafe && !string.IsNullOrWhiteSpace(first.Content))
            return Result(first.Content, plan, true, false, initialReport, first.Hint, firstOutput.StructuredOutputValid);
        return Result(string.Empty, plan, true, true, repairedReport, null, repairedOutput.StructuredOutputValid);
    }

    private static bool IsEmptyResponse(InvalidOperationException exception) =>
        exception.Message.Contains("返回为空", StringComparison.Ordinal) ||
        exception.Message.Contains("没有返回可用内容", StringComparison.Ordinal);

    private async Task<PromptGenerationOutput> GeneratePromptAsync(
        string systemPrompt,
        string userInput,
        CompanionDriverMode mode,
        StructuredGenerationWorkflow structuredWorkflow,
        CancellationToken cancellationToken)
    {
        // Text-only clients retain the legacy suffix protocol; structured-capable clients carry
        // emotion metadata as typed fields alongside the prompt answer.
        if (mode == CompanionDriverMode.EmotionAssistant && _client is not IStructuredTextGenerationClient)
            return new(await _client.GenerateAsync(systemPrompt, userInput, cancellationToken).ConfigureAwait(false), null);

        if (_client is not IStructuredTextGenerationClient)
            return new(await _client.GenerateAsync(systemPrompt, userInput, cancellationToken).ConfigureAwait(false), null);

        return await GenerateStructuredPromptAsync(systemPrompt, userInput, mode, structuredWorkflow, cancellationToken).ConfigureAwait(false);
    }

    private string DecorateSystemPrompt(string prompt, CompanionDriverMode mode) =>
        mode == CompanionDriverMode.EmotionAssistant && _client is IStructuredTextGenerationClient
            ? AssistantEmotionProtocol.DecorateStructuredSystemPrompt(prompt, mode)
            : AssistantEmotionProtocol.DecorateSystemPrompt(prompt, mode);

    private async Task<PromptGenerationOutput> GenerateStructuredPromptAsync(
        string systemPrompt,
        string userInput,
        CompanionDriverMode mode,
        StructuredGenerationWorkflow structuredWorkflow,
        CancellationToken cancellationToken)
    {
        var contract = GetOutputContract(mode);
        var result = await structuredWorkflow.ExecuteAsync(
            systemPrompt,
            userInput,
            contract,
            maxAttempts: 1,
            cancellationToken).ConfigureAwait(false);
        return new PromptGenerationOutput(
            result.Succeeded
                ? AssistantEmotionProtocol.AppendStructuredEmotionMarker(result.Answer, result.RawResponse ?? string.Empty, mode)
                : string.Empty,
            result.Succeeded);
    }

    private static TransformationResult Result(
        string content,
        ProfessionalizationPlan plan,
        bool repaired,
        bool blocked,
        QualityReport report,
        AssistantEmotionHint? companionEmotion,
        bool? structuredOutputValid = null) => new()
    {
        Content = content,
        StrategyId = plan.StrategyId,
        StrategyName = plan.StrategyName,
        WasRepaired = repaired,
        IsBlocked = blocked,
        StructuredOutputValid = structuredOutputValid,
        ValidationIssues = report.Issues,
        CompanionEmotion = companionEmotion
    };
}
