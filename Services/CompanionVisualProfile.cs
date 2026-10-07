using System;
using System.Collections.Generic;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// 与具体猫形象无关的角色呈现配置。业务状态机只使用
/// <see cref="CompanionVisualState"/>；皮肤负责把状态映射到资产。
/// </summary>
public sealed class CompanionVisualProfile
{
    private readonly IReadOnlyDictionary<CompanionVisualState, string> _assets;
    private readonly IReadOnlyDictionary<CompanionIdleBehavior, CompanionVisualState> _idleBehaviorOverrides;
    private readonly IReadOnlyDictionary<CompanionVisualState, CompanionVisualState> _frameStateOverrides;

    private CompanionVisualProfile(
        string kind,
        IReadOnlyDictionary<CompanionVisualState, string> assets,
        IReadOnlyDictionary<CompanionIdleBehavior, CompanionVisualState> idleBehaviorOverrides,
        IReadOnlyDictionary<CompanionVisualState, CompanionVisualState> frameStateOverrides,
        string? vectorPath,
        string? spriteSheetPath,
        int spriteSheetColumns,
        int spriteSheetRows,
        string? idleVariantsPath,
        int idleVariantsColumns)
    {
        Kind = kind;
        _assets = assets;
        _idleBehaviorOverrides = idleBehaviorOverrides;
        _frameStateOverrides = frameStateOverrides;
        VectorPath = vectorPath;
        SpriteSheetPath = spriteSheetPath;
        SpriteSheetColumns = Math.Max(1, spriteSheetColumns);
        SpriteSheetRows = Math.Max(1, spriteSheetRows);
        IdleVariantsPath = idleVariantsPath;
        IdleVariantsColumns = Math.Max(1, idleVariantsColumns);
    }

    public string Kind { get; }
    public bool UsesImages => string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase);
    public bool UsesVectorLayers => string.Equals(Kind, "vector-layered", StringComparison.OrdinalIgnoreCase);
    public bool UsesSpriteSheet => string.Equals(Kind, "spritesheet", StringComparison.OrdinalIgnoreCase);
    public string? VectorPath { get; }
    public string? SpriteSheetPath { get; }
    public int SpriteSheetColumns { get; }
    public int SpriteSheetRows { get; }
    public string? IdleVariantsPath { get; }
    public int IdleVariantsColumns { get; }
    public bool HasCompleteStateSet => UsesSpriteSheet || !UsesImages || _assets.Count == SkinContract.CompanionStates.Count;

    public bool TryGetAsset(CompanionVisualState state, out string path) =>
        _assets.TryGetValue(state, out path!);

    public CompanionVisualState ResolveIdleBehavior(CompanionIdleBehavior behavior, CompanionVisualState fallback) =>
        _idleBehaviorOverrides.TryGetValue(behavior, out var state) ? state : fallback;

    public CompanionVisualState ResolveFrameState(CompanionVisualState state) =>
        _frameStateOverrides.TryGetValue(state, out var mapped) ? mapped : state;

    public static CompanionVisualProfile Create(
        string kind,
        IReadOnlyDictionary<string, string>? stateAssets,
        string? vectorPath = null,
        string? spriteSheetPath = null,
        int spriteSheetColumns = 4,
        int spriteSheetRows = 3,
        string? idleVariantsPath = null,
        int idleVariantsColumns = 4,
        IReadOnlyDictionary<string, string>? idleBehaviorOverrides = null,
        IReadOnlyDictionary<string, string>? frameStateOverrides = null)
    {
        var typed = new Dictionary<CompanionVisualState, string>();
        if (stateAssets is not null)
        {
            foreach (var (name, path) in stateAssets)
                if (Enum.TryParse<CompanionVisualState>(name, ignoreCase: true, out var state) && !string.IsNullOrWhiteSpace(path))
                    typed[state] = path;
        }
        var idle = new Dictionary<CompanionIdleBehavior, CompanionVisualState>();
        if (idleBehaviorOverrides is not null)
        {
            foreach (var (behaviorName, stateName) in idleBehaviorOverrides)
                if (Enum.TryParse<CompanionIdleBehavior>(behaviorName, true, out var behavior) &&
                    Enum.TryParse<CompanionVisualState>(stateName, true, out var state))
                    idle[behavior] = state;
        }
        var frames = new Dictionary<CompanionVisualState, CompanionVisualState>();
        if (frameStateOverrides is not null)
        {
            foreach (var (stateName, mappedName) in frameStateOverrides)
                if (Enum.TryParse<CompanionVisualState>(stateName, true, out var state) &&
                    Enum.TryParse<CompanionVisualState>(mappedName, true, out var mapped))
                    frames[state] = mapped;
        }
        return new CompanionVisualProfile(kind, typed, idle, frames, vectorPath, spriteSheetPath, spriteSheetColumns, spriteSheetRows, idleVariantsPath, idleVariantsColumns);
    }
}
