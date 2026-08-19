using System.Collections.Generic;

namespace PromptFloat.Models;

/// <summary>
/// 提示词深度，控制结构复杂度（不只是字数）。
/// </summary>
public enum PromptDepth
{
    Concise,    // 简洁
    Standard,   // 标准（默认）
    Detailed    // 详细
}

/// <summary>
/// PromptDepth 的扩展信息：中文名称 + 结构模板要点。
/// 结构模板以代码常量形式固化，供 PromptBuilderService 替换 {{Depth}} 占位符使用。
/// </summary>
public static class PromptDepthMetadata
{
    /// <summary>
    /// 获取深度的中文显示名称。
    /// </summary>
    public static string GetDisplayName(this PromptDepth depth) => depth switch
    {
        PromptDepth.Concise => "简洁",
        PromptDepth.Standard => "标准",
        PromptDepth.Detailed => "详细",
        _ => "标准"
    };

    /// <summary>
    /// 获取深度的结构模板要点。
    /// 这些要点会被注入到 System Prompt 的 {{Depth}} 占位符中，指导模型如何组织输出结构。
    /// </summary>
    public static string GetStructureTemplate(this PromptDepth depth) => depth switch
    {
        PromptDepth.Concise =>
            "结构模板：目标 / 核心要求 / 关键限制 / 输出格式",

        PromptDepth.Standard =>
            "结构模板：角色 / 任务目标 / 背景 / 具体要求 / 约束条件 / 执行步骤 / 输出格式",

        PromptDepth.Detailed =>
            "结构模板：角色定位 / 任务背景 / 核心目标 / 输入信息 / 详细任务 / 执行流程 / " +
            "约束条件 / 判断标准 / 异常情况 / 输出结构 / 质量要求 / 禁止事项 / 最终交付要求",

        _ =>
            "结构模板：角色 / 任务目标 / 背景 / 具体要求 / 约束条件 / 执行步骤 / 输出格式"
    };

    /// <summary>
    /// 列出所有深度（用于 UI 绑定）。
    /// </summary>
    public static IReadOnlyList<PromptDepth> AllDepths { get; } =
        new[] { PromptDepth.Concise, PromptDepth.Standard, PromptDepth.Detailed };
}
