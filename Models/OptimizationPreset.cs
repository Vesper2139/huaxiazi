using System;
using System.Collections.Generic;

namespace Huaxiazi.Models;

public sealed class OptimizationPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新预设";
    public ApplicationMode Mode { get; set; } = ApplicationMode.PromptOptimize;
    public PromptCategory Category { get; set; } = PromptCategory.General;
    public PromptDepth Depth { get; set; } = PromptDepth.Standard;
    public string OutputStyle { get; set; } = "自然";
    public string Instructions { get; set; } = string.Empty;
    public string CustomSystemPrompt { get; set; } = string.Empty;

    public OptimizationPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Mode = Mode,
        Category = Category,
        Depth = Depth,
        OutputStyle = OutputStyle,
        Instructions = Instructions,
        CustomSystemPrompt = CustomSystemPrompt
    };

    public static List<OptimizationPreset> CreateDefaults() =>
    [
        new() { Id = "builtin-coding", Name = "编程", Category = PromptCategory.Coding, Depth = PromptDepth.Detailed, Instructions = "明确技术栈、边界条件、验收标准和可运行交付物。" },
        new() { Id = "builtin-copywriting", Name = "文案", Category = PromptCategory.Writing, Depth = PromptDepth.Standard, Instructions = "明确受众、渠道、语气、长度和行动目标。" },
        new() { Id = "builtin-academic", Name = "学术", Category = PromptCategory.Research, Depth = PromptDepth.Detailed, Instructions = "区分事实与推断，要求证据、引用和方法说明。" },
        new() { Id = "builtin-report", Name = "汇报", Mode = ApplicationMode.Polish, Category = PromptCategory.General, Depth = PromptDepth.Standard, OutputStyle = "专业", Instructions = "结论先行，保留事实，不夸大承诺。" }
    ];
}
