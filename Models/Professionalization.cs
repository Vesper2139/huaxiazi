using System;
using System.Collections.Generic;
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
    [JsonPropertyName("scenarioUsage")]
    public Dictionary<string, int> ScenarioUsage { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("removedCannedExpressions")]
    public Dictionary<string, int> RemovedCannedExpressions { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("forbiddenExpressions")]
    public List<string> ForbiddenExpressions { get; set; } = [];
}
