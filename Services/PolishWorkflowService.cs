using System;
using System.Text.Json;
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
        var raw = await _client.GenerateAsync(
            _promptBuilder.BuildSystemPrompt(request, clarificationEnabled),
            _promptBuilder.BuildUserMessage(request),
            cancellationToken).ConfigureAwait(false);
        var response = PolishResponseParser.Parse(raw);
        var wasRepaired = false;
        System.Collections.Generic.IReadOnlyList<string> validationIssues = [];
        if (response.Kind == PolishResponseKind.Final)
        {
            var validation = PolishFidelityValidator.Validate(request.OriginalText, response.Content, request.Intelligence);
            if (!validation.IsValid)
            {
                var repairedRaw = await _client.GenerateAsync(
                    _promptBuilder.BuildRepairSystemPrompt(request, validation.Issues),
                    _promptBuilder.BuildUserMessage(request), cancellationToken).ConfigureAwait(false);
                var repaired = PolishResponseParser.Parse(repairedRaw);
                if (repaired.Kind == PolishResponseKind.Final &&
                    PolishFidelityValidator.Validate(request.OriginalText, repaired.Content, request.Intelligence).IsValid)
                {
                    response = repaired;
                    wasRepaired = true;
                }
                else
                {
                    validationIssues = repaired.Kind == PolishResponseKind.Final
                        ? PolishFidelityValidator.Validate(request.OriginalText, repaired.Content, request.Intelligence).Issues
                        : validation.Issues;
                    response = new PolishResponse { Kind = PolishResponseKind.Invalid, RawText = repairedRaw };
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
            SavedRevision = saved,
            WasRepaired = wasRepaired,
            ValidationIssues = validationIssues
        };
    }
}
