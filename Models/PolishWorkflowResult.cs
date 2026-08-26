using System.Collections.Generic;

namespace Huaxiazi.Models;

public sealed class PolishWorkflowResult
{
    public required PolishResponse Response { get; init; }
    public AssistantEmotionHint? CompanionEmotion { get; init; }
    public ContentRevision? SavedRevision { get; init; }
    public bool WasRepaired { get; init; }
    public IReadOnlyList<string> ValidationIssues { get; init; } = [];
}
