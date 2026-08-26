using System.Collections.Generic;

namespace Huaxiazi.Services;

/// <summary>
/// 皮肤清单。描述一套可完整替换的样式资源字典。
/// </summary>
/// <param name="Id">皮肤唯一标识。</param>
/// <param name="DisplayName">面向用户的显示名称。</param>
/// <param name="ResourcePath">组件内资源路径，如 "Resources/Themes/Light.xaml"。</param>
/// <param name="IsBuiltIn">是否为内置皮肤。</param>
public sealed record SkinManifest(
    string Id,
    string DisplayName,
    string ResourcePath,
    bool IsBuiltIn = true,
    string Version = "1.0.0",
    string? PreviewPath = null,
    string CompanionKind = "vector",
    IReadOnlyDictionary<string, string>? CompanionStates = null,
    string? InstallPath = null,
    string? CompanionVectorPath = null,
    string? CompanionSpriteSheetPath = null,
    int CompanionSpriteSheetColumns = 4,
    int CompanionSpriteSheetRows = 3,
    string? CompanionIdleVariantsPath = null,
    int CompanionIdleVariantsColumns = 4)
{
    /// <summary>由所有窗口共享的类型化角色状态映射；皮肤不拥有业务状态机。</summary>
    public CompanionVisualProfile CompanionProfile { get; } =
        CompanionVisualProfile.Create(CompanionKind, CompanionStates, CompanionVectorPath, CompanionSpriteSheetPath, CompanionSpriteSheetColumns, CompanionSpriteSheetRows, CompanionIdleVariantsPath, CompanionIdleVariantsColumns);
}
