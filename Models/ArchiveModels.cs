using System;

namespace PromptFloat.Models;

public sealed class ArchiveDraft
{
    public ApplicationMode Mode { get; init; } = ApplicationMode.Polish;
    public Guid? ItemId { get; init; }
    public string OriginalText { get; init; } = string.Empty;
    public string FinalText { get; init; } = string.Empty;
    public string Scenario { get; init; } = "其他";
    public string Topic { get; init; } = "未命名表达";
    public string ContextJson { get; init; } = "{}";
    public string Style { get; init; } = "自然";
    public string ModelProfileId { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
}

public sealed class ContentRevision
{
    public Guid Id { get; init; }
    public Guid ItemId { get; init; }
    public int Version { get; init; }
    public ApplicationMode Mode { get; init; } = ApplicationMode.Polish;
    public string OriginalText { get; init; } = string.Empty;
    public string FinalText { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Topic { get; init; } = string.Empty;
    public string ContextJson { get; init; } = "{}";
    public string Style { get; init; } = string.Empty;
    public string ModelProfileId { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsArchived { get; init; }
}
