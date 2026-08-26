using System;

namespace Huaxiazi.Models;

/// <summary>主工作区的可恢复快照。它不包含 API Key 等机密信息。</summary>
public sealed record WorkspaceDraft
{
    public string UserInput { get; init; } = string.Empty;
    public string OptimizedResult { get; init; } = string.Empty;
    public ApplicationMode CurrentMode { get; init; } = ApplicationMode.Polish;
    public ViewMode ViewMode { get; init; } = ViewMode.Original;
    public PromptCategory SelectedCategory { get; init; } = PromptCategory.General;
    public PromptDepth SelectedDepth { get; init; } = PromptDepth.Standard;
    public string ActiveProviderProfileId { get; init; } = string.Empty;
    public string Recipient { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string Formality { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
