using System.Collections.Generic;

namespace PromptFloat.Models;

public enum PolishResponseKind
{
    Invalid,
    NeedsClarification,
    Final
}

public sealed class PolishResponse
{
    public PolishResponseKind Kind { get; init; }
    public string Scenario { get; init; } = string.Empty;
    public string Topic { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public IReadOnlyList<string> Questions { get; init; } = [];
    public string RawText { get; init; } = string.Empty;
}
