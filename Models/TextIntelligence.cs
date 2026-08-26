using System.Collections.Generic;

namespace Huaxiazi.Models;

public enum TextRiskLevel { Low, Medium, High }

public sealed class TextIntelligence
{
    public string Recipient { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string Formality { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public TextRiskLevel RiskLevel { get; init; }
    public bool ContainsUncertainty { get; init; }
    public IReadOnlyList<string> FidelityAnchors { get; init; } = [];
    public IReadOnlyList<string> Signals { get; init; } = [];
}

public sealed record ResolvedPolishContext(string Recipient, string Channel, string Purpose, string Formality, string Scenario);
