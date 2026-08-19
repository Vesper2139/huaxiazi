namespace PromptFloat.Models;

public sealed class PolishRequest
{
    public string OriginalText { get; init; } = string.Empty;
    public string Recipient { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string Formality { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string OutputStyle { get; init; } = "自然";
    public string CustomStyleInstructions { get; init; } = string.Empty;
    public string Persona { get; init; } = string.Empty;
    public string CustomSystemPrompt { get; init; } = string.Empty;
    public string PreferenceInstructions { get; init; } = string.Empty;
    public TextIntelligence? Intelligence { get; init; }
    public string ModelProfileId { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public bool SaveOriginalText { get; init; } = true;
    public bool SaveOptimizedText { get; init; } = true;
}
