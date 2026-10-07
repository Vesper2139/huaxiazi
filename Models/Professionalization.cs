using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Huaxiazi.Models;

public sealed class ProfessionalizationRequest
{
    public string Input { get; init; } = string.Empty;
    public ApplicationMode Mode { get; init; }
    public PromptCategory Category { get; init; } = PromptCategory.General;
    public PromptDepth Depth { get; init; } = PromptDepth.Standard;
    public string Recipient { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public bool PurposeIsExplicit { get; init; }
    public string Formality { get; init; } = string.Empty;
    public string ExplicitRequirements { get; init; } = string.Empty;
    public string PreferredStrategyId { get; init; } = string.Empty;
    public string PreferredStrategyName { get; init; } = string.Empty;
    public string StrategyInstructions { get; init; } = string.Empty;
    public string PreferenceInstructions { get; init; } = string.Empty;
    public IReadOnlyList<string> SelectedSkillIds { get; init; } = [];
    public IReadOnlyDictionary<string, double> SkillWeights { get; init; } = new Dictionary<string, double>();
    public bool SkillConflictDetected { get; init; }
}

public sealed class ProfessionalizationPlan
{
    public string OriginalText { get; init; } = string.Empty;
    public ApplicationMode Mode { get; init; }
    public string StrategyId { get; init; } = string.Empty;
    public string StrategyName { get; init; } = string.Empty;
    public string StrategySource { get; init; } = "builtin";
    public string StrategyInstructions { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Recipient { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public bool PurposeIsExplicit { get; init; }
    public string Formality { get; init; } = string.Empty;
    public TextRiskLevel RiskLevel { get; init; }
    public bool ContainsUncertainty { get; init; }
    public IReadOnlyList<string> FidelityAnchors { get; init; } = [];
    public bool NeedsClarification { get; init; }
    public IReadOnlyList<string> ClarificationQuestions { get; init; } = [];
    public IReadOnlyList<string> SelectedSkillIds { get; init; } = [];
    public IReadOnlyDictionary<string, double> SkillWeights { get; init; } = new Dictionary<string, double>();
    public bool SkillConflictDetected { get; init; }
    public string RecommendedModelTier { get; init; } = "Balanced";
    public string ModelSelectionReason { get; init; } = string.Empty;
}

public enum QualityIssueSeverity { Quality, Unsafe }

public sealed record QualityIssue(string Code, string Message, QualityIssueSeverity Severity);

public sealed class QualityReport
{
    public IReadOnlyList<QualityIssue> Issues { get; init; } = [];
    public bool IsSafe => !System.Linq.Enumerable.Any(Issues, issue => issue.Severity == QualityIssueSeverity.Unsafe);
    public bool IsValid => Issues.Count == 0;
}

public sealed class TransformationResult
{
    public string Content { get; init; } = string.Empty;
    public string StrategyId { get; init; } = string.Empty;
    public string StrategyName { get; init; } = string.Empty;
    public bool WasRepaired { get; init; }
    public bool IsBlocked { get; init; }
    /// <summary>Null when the client used an unstructured text path; otherwise reflects local schema validation.</summary>
    public bool? StructuredOutputValid { get; init; }
    public IReadOnlyList<QualityIssue> ValidationIssues { get; init; } = [];
    public AssistantEmotionHint? CompanionEmotion { get; init; }
}

public sealed class ExpressionPreferenceProfile
{
    [JsonPropertyName("editCount")]
    public int EditCount { get; set; }
    [JsonPropertyName("acceptedCount")]
    public int AcceptedCount { get; set; }
    [JsonPropertyName("shorteningEdits")]
    public int ShorteningEdits { get; set; }
    [JsonPropertyName("expansionEdits")]
    public int ExpansionEdits { get; set; }
    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }
    [JsonPropertyName("undoCount")]
    public int UndoCount { get; set; }
    [JsonPropertyName("styleChoiceCount")]
    public int StyleChoiceCount { get; set; }
    [JsonPropertyName("styleChoiceUsage")]
    public Dictionary<string, int> StyleChoiceUsage { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("scenarioUsage")]
    public Dictionary<string, int> ScenarioUsage { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("removedCannedExpressions")]
    public Dictionary<string, int> RemovedCannedExpressions { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("forbiddenExpressions")]
    public List<string> ForbiddenExpressions { get; set; } = [];
    [JsonPropertyName("taskPreferences")]
    public Dictionary<string, ExpressionPreferenceSet> TaskPreferences { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("interactionSignals")]
    public Dictionary<string, ExpressionInteractionSignalSet> InteractionSignals { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("ignoredSuggestionKeys")]
    public List<string> IgnoredSuggestionKeys { get; set; } = [];

    public void Normalize()
    {
        ScenarioUsage ??= new Dictionary<string, int>(StringComparer.Ordinal);
        RemovedCannedExpressions ??= new Dictionary<string, int>(StringComparer.Ordinal);
        ForbiddenExpressions ??= [];
        TaskPreferences ??= new Dictionary<string, ExpressionPreferenceSet>(StringComparer.Ordinal);
        InteractionSignals ??= new Dictionary<string, ExpressionInteractionSignalSet>(StringComparer.Ordinal);
        StyleChoiceUsage = (StyleChoiceUsage ?? new Dictionary<string, int>())
            .Where(pair => OutputStyleCatalog.Values.Contains(pair.Key, StringComparer.Ordinal) && pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        IgnoredSuggestionKeys = (IgnoredSuggestionKeys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key) && key.Length <= 256)
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToList();
        TaskPreferences = TaskPreferences
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var preference in TaskPreferences.Values) preference.Normalize();
        InteractionSignals = InteractionSignals
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 128 && pair.Value is not null)
            .Take(64)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var signals in InteractionSignals.Values) signals.Normalize();
    }
}

public sealed class ExpressionInteractionSignalSet
{
    [JsonPropertyName("acceptedCount")]
    public int AcceptedCount { get; set; }
    [JsonPropertyName("editCount")]
    public int EditCount { get; set; }
    [JsonPropertyName("shorteningEdits")]
    public int ShorteningEdits { get; set; }
    [JsonPropertyName("expansionEdits")]
    public int ExpansionEdits { get; set; }
    [JsonPropertyName("acceptedShortenedOutputs")]
    public int AcceptedShortenedOutputs { get; set; }
    [JsonPropertyName("acceptedExpandedOutputs")]
    public int AcceptedExpandedOutputs { get; set; }
    [JsonPropertyName("rejectedShortenedOutputs")]
    public int RejectedShortenedOutputs { get; set; }
    [JsonPropertyName("rejectedExpandedOutputs")]
    public int RejectedExpandedOutputs { get; set; }
    [JsonPropertyName("acceptedOutputStyles")]
    public Dictionary<string, int> AcceptedOutputStyles { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("rejectedOutputStyles")]
    public Dictionary<string, int> RejectedOutputStyles { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("acceptedRemovedCannedExpressions")]
    public Dictionary<string, int> AcceptedRemovedCannedExpressions { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("rejectedRemovedCannedExpressions")]
    public Dictionary<string, int> RejectedRemovedCannedExpressions { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }
    [JsonPropertyName("undoCount")]
    public int UndoCount { get; set; }
    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public void Normalize()
    {
        AcceptedCount = Math.Max(0, AcceptedCount);
        EditCount = Math.Max(0, EditCount);
        ShorteningEdits = Math.Max(0, ShorteningEdits);
        ExpansionEdits = Math.Max(0, ExpansionEdits);
        AcceptedShortenedOutputs = Math.Max(0, AcceptedShortenedOutputs);
        AcceptedExpandedOutputs = Math.Max(0, AcceptedExpandedOutputs);
        RejectedShortenedOutputs = Math.Max(0, RejectedShortenedOutputs);
        RejectedExpandedOutputs = Math.Max(0, RejectedExpandedOutputs);
        AcceptedOutputStyles = NormalizeStyleCounts(AcceptedOutputStyles);
        RejectedOutputStyles = NormalizeStyleCounts(RejectedOutputStyles);
        AcceptedRemovedCannedExpressions = NormalizeCannedExpressionCounts(AcceptedRemovedCannedExpressions);
        RejectedRemovedCannedExpressions = NormalizeCannedExpressionCounts(RejectedRemovedCannedExpressions);
        RetryCount = Math.Max(0, RetryCount);
        UndoCount = Math.Max(0, UndoCount);
    }

    private static Dictionary<string, int> NormalizeStyleCounts(Dictionary<string, int>? counts) =>
        (counts ?? new Dictionary<string, int>())
        .Where(pair => OutputStyleCatalog.Values.Contains(pair.Key, StringComparer.Ordinal) && pair.Value > 0)
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static Dictionary<string, int> NormalizeCannedExpressionCounts(Dictionary<string, int>? counts) =>
        (counts ?? new Dictionary<string, int>())
        .Where(pair => CannedExpressionCatalog.Values.Contains(pair.Key, StringComparer.Ordinal) && pair.Value > 0)
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

public static class CannedExpressionCatalog
{
    public static IReadOnlyList<string> Values { get; } = ["首先", "其次", "最后", "感谢您的理解与支持", "希望以上内容对您有所帮助", "后续我会及时"];
}

public static class OutputStyleCatalog
{
    public static IReadOnlyList<string> Values { get; } = ["自然", "克制", "亲切", "专业", "正式", "简洁"];

    public static string Normalize(string? value) =>
        Values.Contains(value, StringComparer.Ordinal) ? value! : "自然";

    public static string Describe(string? value) => Normalize(value) switch
    {
        "克制" => "克制表达，不过度热情或夸张",
        "亲切" => "温和、亲切，同时保持信息准确",
        "专业" => "专业、清晰、用词准确",
        "正式" => "正式书面表达",
        "简洁" => "简洁直接，避免冗余",
        _ => "自然清晰，避免机械套话"
    };
}

public sealed class ExpressionPreferenceSet
{
    [JsonPropertyName("preferredLength")]
    public string PreferredLength { get; set; } = "balanced";
    [JsonPropertyName("preferredTone")]
    public string PreferredTone { get; set; } = "natural";
    [JsonPropertyName("preserveOriginalWording")]
    public bool PreserveOriginalWording { get; set; } = true;
    [JsonPropertyName("forbiddenExpressions")]
    public List<string> ForbiddenExpressions { get; set; } = [];
    [JsonPropertyName("userConfirmed")]
    public bool UserConfirmed { get; set; }
    [JsonPropertyName("source")]
    public string Source { get; set; } = "none";
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public void Normalize()
    {
        PreferredLength = PreferredLength is "concise" or "detailed" ? PreferredLength : "balanced";
        PreferredTone = PreferredTone is "professional" or "warm" ? PreferredTone : "natural";
        ForbiddenExpressions ??= [];
        Confidence = double.IsFinite(Confidence) ? Math.Clamp(Confidence, 0, 1) : 0;
        Source = string.IsNullOrWhiteSpace(Source) ? "none" : Source.Trim();
    }
}
