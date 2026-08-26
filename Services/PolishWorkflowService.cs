using System;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFloat.Models;

namespace PromptFloat.Services;

public sealed class PolishWorkflowService
{
    private readonly ITextGenerationClient _client;
    private readonly PolishPromptBuilderService _promptBuilder;
    private readonly ArchiveService _archive;

    public PolishWorkflowService(
        ITextGenerationClient client,
        PolishPromptBuilderService promptBuilder,
        ArchiveService archive)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
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
        var companionMode = App.Settings.CompanionDriverMode;
        var raw = await _client.GenerateAsync(
            AssistantEmotionProtocol.DecorateSystemPrompt(
                _promptBuilder.BuildSystemPrompt(request, clarificationEnabled), companionMode),
            _promptBuilder.BuildUserMessage(request),
            cancellationToken).ConfigureAwait(false);
        var annotated = AssistantEmotionProtocol.ParseContent(raw, companionMode);
        var response = PolishResponseParser.Parse(annotated.Content);
        var companionEmotion = annotated.Hint;
        var wasRepaired = false;
        System.Collections.Generic.IReadOnlyList<string> validationIssues = [];
        if (response.Kind == PolishResponseKind.Final)
        {
            var plan = request.Professionalization ?? new ProfessionalizationPlanner().Create(new ProfessionalizationRequest { Input = request.OriginalText, Mode = ApplicationMode.Polish });
            var validation = ProfessionalQualityValidator.Validate(plan, response.Content);
            if (!validation.IsValid)
            {
                var repairedRaw = await _client.GenerateAsync(
                    AssistantEmotionProtocol.DecorateSystemPrompt(
                        _promptBuilder.BuildRepairSystemPrompt(request, validation.Issues.Select(issue => issue.Message).ToArray()), companionMode),
                    _promptBuilder.BuildUserMessage(request), cancellationToken).ConfigureAwait(false);
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
            saved = _archive.SavePolishRevision(new ArchiveDraft
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
                    request.CustomStyleInstructions
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
}
