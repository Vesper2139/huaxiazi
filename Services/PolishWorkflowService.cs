using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class PolishWorkflowService
{
    private static readonly StructuredOutputContract PolishResponseContract = new(
        "polish-response",
        new HashSet<string>(StringComparer.Ordinal) { "kind" },
        new HashSet<string>(StringComparer.Ordinal) { "kind", "scenario", "topic", "content", "questions" },
        FieldTypes: new Dictionary<string, StructuredOutputFieldType>(StringComparer.Ordinal)
        {
            ["questions"] = StructuredOutputFieldType.StringArray
        },
        AllowedStringValues: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["kind"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "final", "needs_clarification" }
        });
    private static readonly StructuredOutputContract PolishEmotionResponseContract = new(
        "polish-response-with-emotion",
        new HashSet<string>(StringComparer.Ordinal) { "kind" },
        new HashSet<string>(StringComparer.Ordinal) { "kind", "scenario", "topic", "content", "questions", "companion_emotion", "companion_intensity" },
        FieldTypes: new Dictionary<string, StructuredOutputFieldType>(StringComparer.Ordinal)
        {
            ["questions"] = StructuredOutputFieldType.StringArray,
            ["companion_intensity"] = StructuredOutputFieldType.Number
        },
        AllowedStringValues: new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["kind"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "final", "needs_clarification" },
            ["companion_emotion"] = Enum.GetNames<AssistantEmotionKind>().ToHashSet(StringComparer.OrdinalIgnoreCase)
        },
        NumericRanges: new Dictionary<string, StructuredOutputNumericRange>(StringComparer.Ordinal)
        {
            ["companion_intensity"] = new(0, 1)
        });

    public static StructuredOutputContract GetOutputContract(CompanionDriverMode companionMode) =>
        companionMode == CompanionDriverMode.EmotionAssistant
            ? PolishEmotionResponseContract
            : PolishResponseContract;

    private readonly ITextGenerationClient _client;
    private readonly PolishPromptBuilderService _promptBuilder;
    private readonly ArchiveService? _archive;
    private readonly Func<CompanionDriverMode> _companionModeProvider;

    public PolishWorkflowService(
        ITextGenerationClient client,
        PolishPromptBuilderService promptBuilder,
        ArchiveService? archive,
        Func<CompanionDriverMode>? companionModeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _archive = archive;
        _companionModeProvider = companionModeProvider ?? (() => App.Settings.CompanionDriverMode);
    }

    public async Task<PolishWorkflowResult> ExecuteAsync(
        PolishRequest request,
        bool clarificationEnabled,
        bool autoArchive,
        Guid? existingItemId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = request.Professionalization ?? new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = request.OriginalText,
            Mode = ApplicationMode.Polish,
            Recipient = request.Recipient,
            Scenario = request.Scenario,
            Purpose = request.Purpose,
            PurposeIsExplicit = !string.IsNullOrWhiteSpace(request.Purpose),
            Formality = request.Formality
        });
        if (plan.NeedsClarification)
        {
            if (!clarificationEnabled)
            {
                var purpose = string.IsNullOrWhiteSpace(plan.Purpose) ? "当前任务" : $"“{plan.Purpose}”";
                return new PolishWorkflowResult
                {
                    Response = new PolishResponse { Kind = PolishResponseKind.Invalid },
                    ValidationIssues = [$"当前信息不足以完成{purpose}。缺少：{MissingInformationSummary.For(plan)}。未向模型发送请求。"]
                };
            }

            return new PolishWorkflowResult
            {
                Response = new PolishResponse
                {
                    Kind = PolishResponseKind.NeedsClarification,
                    Questions = plan.ClarificationQuestions
                }
            };
        }
        if (autoArchive && _archive is null)
            throw new InvalidOperationException("启用自动归档时必须提供 ArchiveService。");
        var companionMode = _companionModeProvider();
        // Share schema-fallback state across the initial draft and this task's repairs.
        var structuredWorkflow = new StructuredGenerationWorkflow(_client);
        var raw = await GenerateResponseAsync(
            DecorateSystemPrompt(
                _promptBuilder.BuildSystemPrompt(request, clarificationEnabled), companionMode),
            _promptBuilder.BuildUserMessage(request),
            companionMode,
            structuredWorkflow,
            cancellationToken).ConfigureAwait(false);
        var annotated = AssistantEmotionProtocol.ParseContent(raw, companionMode);
        var response = PolishResponseParser.Parse(annotated.Content);
        var companionEmotion = annotated.Hint;
        var wasRepaired = false;
        System.Collections.Generic.IReadOnlyList<string> validationIssues = [];
        if (response.Kind == PolishResponseKind.Invalid)
        {
            var contractRepairRaw = await GenerateResponseAsync(
                DecorateSystemPrompt(
                    _promptBuilder.BuildContractRepairSystemPrompt(request, clarificationEnabled), companionMode),
                _promptBuilder.BuildUserMessage(request),
                companionMode,
                structuredWorkflow,
                cancellationToken).ConfigureAwait(false);
            var contractRepair = AssistantEmotionProtocol.ParseContent(contractRepairRaw, companionMode);
            var repairedResponse = PolishResponseParser.Parse(contractRepair.Content);
            if (repairedResponse.Kind != PolishResponseKind.Invalid)
            {
                response = repairedResponse;
                companionEmotion = contractRepair.Hint;
                wasRepaired = true;
            }
            else
            {
                validationIssues = ["润色响应不符合字段契约；已尝试一次结构修复，但结果仍无法解析。"];
            }
        }
        if (response.Kind == PolishResponseKind.Final)
        {
            var validation = ProfessionalQualityValidator.Validate(plan, response.Content);
            if (!validation.IsValid)
            {
                var repairedRaw = await GenerateResponseAsync(
                    DecorateSystemPrompt(
                        _promptBuilder.BuildRepairSystemPrompt(request, validation.Issues.Select(issue => issue.Message).ToArray()), companionMode),
                    _promptBuilder.BuildUserMessage(request), companionMode, structuredWorkflow, cancellationToken).ConfigureAwait(false);
                var repairedAnnotated = AssistantEmotionProtocol.ParseContent(repairedRaw, companionMode);
                var repaired = PolishResponseParser.Parse(repairedAnnotated.Content);
                if (repaired.Kind == PolishResponseKind.Final &&
                    ProfessionalQualityValidator.Validate(plan, repaired.Content).IsValid)
                {
                    response = repaired;
                    companionEmotion = repairedAnnotated.Hint;
                    wasRepaired = true;
                }
                else
                {
                    validationIssues = repaired.Kind == PolishResponseKind.Final
                        ? ProfessionalQualityValidator.Validate(plan, repaired.Content).Issues.Select(issue => issue.Message).ToArray()
                        : validation.Issues.Select(issue => issue.Message).ToArray();
                    if (validation.IsSafe)
                    {
                        // The initial draft is fact-safe; keep it when the one allowed quality repair is worse.
                        wasRepaired = true;
                    }
                    else
                    {
                        response = new PolishResponse { Kind = PolishResponseKind.Invalid, RawText = repairedAnnotated.Content };
                        companionEmotion = null;
                    }
                }
            }
        }

        ContentRevision? saved = null;
        if (response.Kind == PolishResponseKind.Final && autoArchive)
        {
            saved = _archive!.SavePolishRevision(new ArchiveDraft
            {
                ItemId = existingItemId,
                OriginalText = request.SaveOriginalText ? request.OriginalText : string.Empty,
                FinalText = request.SaveOptimizedText ? response.Content : string.Empty,
                Scenario = response.Scenario,
                Topic = response.Topic,
                ContextJson = JsonSerializer.Serialize(new
                {
                    request.Recipient,
                    request.Channel,
                    request.Purpose,
                    request.Formality,
                    request.Scenario,
                    request.Persona,
                    request.CustomStyleInstructions,
                    selectedSkillIds = request.Professionalization?.SelectedSkillIds ?? [],
                    skillWeights = request.Professionalization?.SkillWeights ?? new Dictionary<string, double>(),
                    skillConflictDetected = request.Professionalization?.SkillConflictDetected ?? false
                }),
                Style = request.OutputStyle,
                ModelProfileId = request.ModelProfileId,
                ModelName = request.ModelName
            }, createdAt);
        }

        return new PolishWorkflowResult
        {
            Response = response,
            CompanionEmotion = response.Kind == PolishResponseKind.Final ? companionEmotion : null,
            SavedRevision = saved,
            WasRepaired = wasRepaired,
            ValidationIssues = validationIssues
        };
    }

    private Task<string> GenerateResponseAsync(
        string systemPrompt,
        string userInput,
        CompanionDriverMode companionMode,
        StructuredGenerationWorkflow structuredWorkflow,
        CancellationToken cancellationToken)
    {
        // Text-only clients retain the legacy suffix protocol; structured-capable clients carry
        // emotion metadata inside the same schema as the business response.
        if (companionMode == CompanionDriverMode.EmotionAssistant && _client is not IStructuredTextGenerationClient)
            return _client.GenerateAsync(systemPrompt, userInput, cancellationToken);

        return GenerateStructuredResponseAsync(systemPrompt, userInput, companionMode, structuredWorkflow, cancellationToken);
    }

    private string DecorateSystemPrompt(string prompt, CompanionDriverMode companionMode) =>
        companionMode == CompanionDriverMode.EmotionAssistant && _client is IStructuredTextGenerationClient
            ? AssistantEmotionProtocol.DecorateStructuredSystemPrompt(prompt, companionMode)
            : AssistantEmotionProtocol.DecorateSystemPrompt(prompt, companionMode);

    private async Task<string> GenerateStructuredResponseAsync(
        string systemPrompt,
        string userInput,
        CompanionDriverMode companionMode,
        StructuredGenerationWorkflow structuredWorkflow,
        CancellationToken cancellationToken)
    {
        var contract = GetOutputContract(companionMode);
        var result = await structuredWorkflow.ExecuteAsync(
            systemPrompt,
            userInput,
            contract,
            maxAttempts: 1,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? AssistantEmotionProtocol.AppendStructuredEmotionMarker(result.Answer, result.RawResponse ?? result.Answer, companionMode)
            : string.Empty;
    }
}
