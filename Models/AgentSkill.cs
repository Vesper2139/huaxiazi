using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Huaxiazi.Models;

public enum SkillCompatibilityStatus { Ready, NeedsMapping, RequiresTools, ReviewRequired, Invalid }

public enum AgentSkillSource { Preset, User }

public sealed class SkillPresetCatalog
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("skills")] public IReadOnlyList<SkillPresetManifest> Skills { get; init; } = [];
}

public sealed class SkillPresetManifest
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("packagePath")] public string PackagePath { get; init; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("displayDescription")] public string DisplayDescription { get; init; } = string.Empty;
    [JsonPropertyName("mode")] public ApplicationMode Mode { get; init; }
    [JsonPropertyName("defaultEnabled")] public bool DefaultEnabled { get; init; } = true;
    [JsonPropertyName("sourceRepository")] public string SourceRepository { get; init; } = string.Empty;
    [JsonPropertyName("sourceRevision")] public string SourceRevision { get; init; } = string.Empty;
    [JsonPropertyName("license")] public string License { get; init; } = string.Empty;
    [JsonPropertyName("routingTags")] public IReadOnlyList<string> RoutingTags { get; init; } = [];
    [JsonPropertyName("excludedSections")] public IReadOnlyList<string> ExcludedSections { get; init; } = [];
}

public sealed class ExpressionSkillRoutingContext
{
    public ApplicationMode Mode { get; init; }
    public string Input { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public PromptCategory Category { get; init; } = PromptCategory.General;
}

public sealed class ExpressionSkillRouteResult
{
    public string SkillId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = "话匣子默认表达";
    public string Instructions { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool UsedFallback { get; init; }
}

public interface IExpressionSkillRouter
{
    ExpressionSkillRouteResult Route(ExpressionSkillRoutingContext context);
}

public sealed class AgentSkillCandidate
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string PackageRoot { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public SkillCompatibilityStatus Status { get; init; }
    public IReadOnlyList<ApplicationMode> Modes { get; init; } = [];
    public ApplicationMode SuggestedMode { get; init; } = ApplicationMode.Polish;
    public bool CanEnable => Status is SkillCompatibilityStatus.Ready or SkillCompatibilityStatus.NeedsMapping;
}

public sealed class AgentSkillInstallation
{
    public string Name { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public ApplicationMode Mode { get; init; }
}

public sealed class AgentSkillRecord
{
    public required AgentSkillCandidate Candidate { get; init; }
    public AgentSkillSource Source { get; init; }
    public ApplicationMode Mode { get; init; }
    public bool IsEnabled { get; init; }
    public string PackageSha256 { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string DisplayDescription { get; init; } = string.Empty;
    public string SourceRepository { get; init; } = string.Empty;
    public string SourceRevision { get; init; } = string.Empty;
    public string ManifestLicense { get; init; } = string.Empty;
    public IReadOnlyList<string> RoutingTags { get; init; } = [];
    public IReadOnlyList<string> ExcludedSections { get; init; } = [];
    public string Name => Candidate.Name;
    public string UpstreamName => Candidate.Name;
    public string Description => Candidate.Description;
    public string Instructions => Candidate.Instructions;
    public SkillCompatibilityStatus Status => Candidate.Status;
    public bool CanEdit => Source == AgentSkillSource.User;
    public bool CanDelete => Source == AgentSkillSource.User;
    public string License => string.IsNullOrWhiteSpace(ManifestLicense) ? Candidate.License : ManifestLicense;
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Candidate.Name : DisplayName;
    public string EffectiveDescription => string.IsNullOrWhiteSpace(DisplayDescription) ? Candidate.Description : DisplayDescription;
    public string SourceDisplay => Source == AgentSkillSource.Preset ? "网络预置" : "自定义";
    public string ModeDisplay => Mode == ApplicationMode.Polish ? "表达润色" : "提示词优化";
    public string StateDisplay => IsEnabled ? "已启用" : "已停用";
    public string ToggleActionDisplay => IsEnabled ? "停用" : "启用";
}
