using System;
using System.Collections.Generic;
using System.Linq;
using PromptFloat.Models;

namespace PromptFloat.Services;

public sealed class FidelityValidationResult
{
    public bool IsValid => Issues.Count == 0;
    public IReadOnlyList<string> Issues { get; init; } = [];
}

public static class PolishFidelityValidator
{
    private static readonly string[] StrongTerms = ["保证", "一定", "肯定", "绝对", "务必", "必定"];
    private static readonly string[] UncertainTerms = ["可能", "大概", "预计", "或许", "尽量", "争取", "暂定", "不一定"];

    public static FidelityValidationResult Validate(string? original, string? output, TextIntelligence? intelligence)
    {
        var source = original ?? "";
        var result = output ?? "";
        var issues = new List<string>();
        foreach (var anchor in intelligence?.FidelityAnchors ?? [])
            if (!result.Contains(anchor, StringComparison.Ordinal)) issues.Add($"成稿遗漏了必须保留的信息：{anchor}");

        if (intelligence?.ContainsUncertainty == true && UncertainTerms.Any(source.Contains) &&
            !UncertainTerms.Any(result.Contains) && StrongTerms.Any(result.Contains))
            issues.Add("成稿把原文的不确定表述强化成了确定承诺");

        return new FidelityValidationResult { Issues = issues };
    }
}
