using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PromptFloat.Models;

/// <summary>
/// 提示词方向（任务类别）。共 6 类，固定不变。
/// 每个类别带中文名称、优化重点（用于注入 System Prompt 的 {{Category}} 占位符）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptCategory
{
    General,
    Coding,
    Writing,
    Analysis,
    Research,
    Creative
}

/// <summary>
/// PromptCategory 的扩展信息：中文名 + 优化重点描述。
/// 以代码常量形式固化，供 PromptBuilderService 替换占位符使用。
/// </summary>
public static class PromptCategoryMetadata
{
    /// <summary>
    /// 获取类别的中文显示名称。
    /// </summary>
    public static string GetDisplayName(this PromptCategory category) => category switch
    {
        PromptCategory.General => "通用任务",
        PromptCategory.Coding => "编程开发",
        PromptCategory.Writing => "文案写作",
        PromptCategory.Analysis => "数据分析",
        PromptCategory.Research => "学术研究",
        PromptCategory.Creative => "创意设计",
        _ => "通用任务"
    };

    /// <summary>
    /// 获取类别的优化重点（每行 = 优化重点）。
    /// </summary>
    public static string GetOptimizationFocus(this PromptCategory category) => category switch
    {
        PromptCategory.General => "目标、背景、约束、输出",
        PromptCategory.Coding => "技术栈、架构、功能、边界、代码规范",
        PromptCategory.Writing => "受众、语气、结构、长度、表达目标",
        PromptCategory.Analysis => "数据来源、指标、分析方法、输出格式",
        PromptCategory.Research => "研究问题、方法、证据、引用、严谨性",
        PromptCategory.Creative => "风格、构图、视觉元素、限制条件",
        _ => "目标、背景、约束、输出"
    };

    /// <summary>
    /// 列出所有类别（用于 UI 绑定）。
    /// </summary>
    public static IReadOnlyList<PromptCategory> AllCategories { get; } =
        new[]
        {
            PromptCategory.General,
            PromptCategory.Coding,
            PromptCategory.Writing,
            PromptCategory.Analysis,
            PromptCategory.Research,
            PromptCategory.Creative
        };
}
