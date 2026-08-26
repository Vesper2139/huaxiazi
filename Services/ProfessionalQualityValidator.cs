using System;
using System.Collections.Generic;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public static class ProfessionalQualityValidator
{
    private static readonly string[] Negations = ["不", "不能", "不会", "无法", "未", "没有", "并非"];
    private static readonly string[] Uncertain = ["可能", "大概", "预计", "或许", "尽量", "争取", "暂定", "不一定", "拟"];
    private static readonly string[] Strong = ["保证", "一定", "肯定", "绝对", "必定", "必然", "确保"];
    private static readonly string[] Canned = ["希望以上内容对您有所帮助", "如果您还有其他问题", "综上所述", "总而言之"];
    private static readonly string[] Conditions = ["如果", "若", "前提", "条件", "除非", "只要"];

    public static QualityReport Validate(ProfessionalizationPlan plan, string? output)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var source = plan.OriginalText ?? string.Empty;
        var result = output?.Trim() ?? string.Empty;
        var issues = new List<QualityIssue>();
        if (string.IsNullOrWhiteSpace(result))
            issues.Add(new("empty-output", "模型没有返回可用内容", QualityIssueSeverity.Quality));

        foreach (var anchor in plan.FidelityAnchors)
            if (!result.Contains(anchor, StringComparison.Ordinal))
                issues.Add(new("fact-anchor-missing", $"遗漏必须保留的信息：{anchor}", QualityIssueSeverity.Unsafe));

        if (Negations.Any(term => source.Contains(term, StringComparison.Ordinal)) &&
            !Negations.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("negation-lost", "原文的否定关系被删除", QualityIssueSeverity.Unsafe));

        if ((plan.ContainsUncertainty || Uncertain.Any(term => source.Contains(term, StringComparison.Ordinal))) &&
            Strong.Any(term => result.Contains(term, StringComparison.Ordinal)) &&
            !Uncertain.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("commitment-strengthened", "不确定表述被强化成确定承诺", QualityIssueSeverity.Unsafe));

        if (Conditions.Any(term => source.Contains(term, StringComparison.Ordinal)) &&
            !Conditions.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("condition-lost", "原文的条件或前提被删除", QualityIssueSeverity.Unsafe));

        if (source.Contains("负责", StringComparison.Ordinal) && !result.Contains("负责", StringComparison.Ordinal))
            issues.Add(new("responsibility-lost", "原文的责任关系被删除", QualityIssueSeverity.Unsafe));

        if (Canned.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("canned-expression", "包含无助于交付的模板套话", QualityIssueSeverity.Quality));
        return new QualityReport { Issues = issues };
    }
}
