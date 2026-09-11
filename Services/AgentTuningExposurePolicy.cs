using System;
using System.Collections.Generic;
using System.Linq;

namespace Huaxiazi.Services;

public enum AgentTuningExposure
{
    Basic,
    Advanced,
    Internal
}

public sealed record AgentTuningOption(
    string Id,
    string DisplayName,
    AgentTuningExposure Exposure,
    bool UserEditable,
    string Rationale);

/// <summary>Allow-list for settings that may be exposed by a product UI.</summary>
public static class AgentTuningExposurePolicy
{
    public static IReadOnlyList<AgentTuningOption> Options { get; } =
    [
        new("response_style", "回答风格", AgentTuningExposure.Basic, true, "用户可感知且不改变安全边界"),
        new("verbosity", "详细程度", AgentTuningExposure.Basic, true, "只影响表达长度"),
        new("clarification_mode", "澄清偏好", AgentTuningExposure.Basic, true, "控制是否优先询问缺失信息"),
        new("language", "首选语言", AgentTuningExposure.Basic, true, "输出语言偏好"),

        new("preferred_skills", "偏好技能", AgentTuningExposure.Advanced, true, "在兼容性和安全校验后参与路由"),
        new("avoided_skills", "回避技能", AgentTuningExposure.Advanced, true, "仅作为路由软约束"),
        new("memory_consent", "记忆授权", AgentTuningExposure.Advanced, true, "用户控制可保存信息范围"),
        new("model_tier_preference", "模型档位", AgentTuningExposure.Advanced, true, "Fast/Balanced/Reasoning 的产品级选择"),
        new("context_budget", "上下文预算", AgentTuningExposure.Advanced, true, "在系统设定的上下限内调整"),
        new("rag_scope", "知识检索范围", AgentTuningExposure.Advanced, true, "选择已授权的知识源"),

        new("system_prompt", "系统提示词", AgentTuningExposure.Internal, false, "核心策略与安全边界"),
        new("developer_prompt", "开发者提示词", AgentTuningExposure.Internal, false, "产品控制面，不应由终端用户覆盖"),
        new("layer_weights", "提示层权重", AgentTuningExposure.Internal, false, "实验参数，可能破坏优先级"),
        new("safety_thresholds", "安全阈值", AgentTuningExposure.Internal, false, "风险控制参数"),
        new("prompt_injection_rules", "注入检测规则", AgentTuningExposure.Internal, false, "安全实现细节"),
        new("tool_permissions", "工具权限", AgentTuningExposure.Internal, false, "能力授权与最小权限控制"),
        new("retry_policy", "重试策略", AgentTuningExposure.Internal, false, "可靠性和成本控制"),
        new("idempotency", "幂等策略", AgentTuningExposure.Internal, false, "副作用保护"),
        new("stable_prefix", "稳定前缀", AgentTuningExposure.Internal, false, "缓存与评测一致性"),
        new("raw_skill_instructions", "原始技能指令", AgentTuningExposure.Internal, false, "可能绕过技能安全编译"),
        new("test_split", "测试集划分", AgentTuningExposure.Internal, false, "防止评测集污染")
    ];

    public static AgentTuningOption Get(string? id)
    {
        var option = Options.FirstOrDefault(item => string.Equals(item.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
        return option ?? new AgentTuningOption(id?.Trim() ?? string.Empty, "受保护设置", AgentTuningExposure.Internal, false, "未知选项默认拒绝暴露");
    }

    public static IReadOnlyList<AgentTuningOption> ForUi(AgentTuningExposure maximum = AgentTuningExposure.Advanced) =>
        Options.Where(option => option.UserEditable && option.Exposure <= maximum).ToArray();
}
