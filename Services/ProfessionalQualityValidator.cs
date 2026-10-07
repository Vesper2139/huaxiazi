using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public static class ProfessionalQualityValidator
{
    private static readonly string[] Negations = ["不", "不能", "不会", "无法", "未", "没有", "并非"];
    private static readonly string[] Uncertain = ["可能", "大概", "预计", "或许", "尽量", "争取", "暂定", "不一定", "拟"];
    private static readonly string[] Strong = ["保证", "一定", "肯定", "绝对", "必定", "必然", "确保"];
    private static readonly string[] Canned = ["希望以上内容对您有所帮助", "如果您还有其他问题", "综上所述", "总而言之"];
    private static readonly string[] Conditions = ["如果", "若", "前提", "条件", "除非", "只要"];
    private static readonly string[] Hostile = ["属猪", "猪", "蠢", "笨", "傻", "滚", "闭嘴", "恶心", "讨厌", "混蛋", "废物", "垃圾", "打死", "杀了", "弄死", "揍", "砍", "捅", "报复"];
    private static readonly string[] Praise = ["可爱", "优秀", "真棒", "厉害", "喜欢", "欣赏", "温柔", "亲切"];
    private static readonly string[] CriticalStance = ["不满", "不舒服", "不合适", "不妥", "不能接受", "无法接受", "请停止", "请注意", "不认同", "不同意", "不尊重", "冒犯", "失望", "生气", "反感", "不成熟", "有问题", "不赞同"];
    private static readonly IReadOnlyDictionary<string, string[]> ExplicitPurposeSignals = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["问题分析"] = ["故障", "异常", "失败", "原因", "分析", "排查", "影响", "问题表现", "现象"],
        ["说明延期"] = ["延期", "延误", "推迟", "顺延", "延后", "晚"]
    };
    private static readonly Regex PlaceholderOutput = new(@"(?:\.{3,}|…+|（[^）]*(?:此处|待补|补充|填写|替换)[^）]*）)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ConditionalClause = new(@"(?:如果|若)(?<condition>[^，。；！？!?]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string QuantityUnits = "个月|星期|分钟|公里|小时|天|日|周|秒|人|个|次|份|项|元|块|米";
    private static readonly Regex NumericQuantityAnchor = new($"^(?<number>\\d+)(?<unit>{QuantityUnits})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuantityOccurrence = new($"(?<!\\d)(?<number>\\d+|[零〇一二两三四五六七八九十]+)(?<unit>{QuantityUnits})(?!\\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static QualityReport Validate(ProfessionalizationPlan plan, string? output)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var source = plan.OriginalText ?? string.Empty;
        var result = output?.Trim() ?? string.Empty;
        var issues = new List<QualityIssue>();
        if (string.IsNullOrWhiteSpace(result))
            issues.Add(new("empty-output", "模型没有返回可用内容", QualityIssueSeverity.Quality));

        foreach (var anchor in plan.FidelityAnchors)
            if (!ContainsFidelityAnchor(result, anchor))
                issues.Add(new("fact-anchor-missing", $"遗漏必须保留的信息：{anchor}", QualityIssueSeverity.Unsafe));

        if (Negations.Any(term => source.Contains(term, StringComparison.Ordinal)) &&
            !Negations.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("negation-lost", "原文的否定关系被删除", QualityIssueSeverity.Unsafe));

        if ((plan.ContainsUncertainty || Uncertain.Any(term => source.Contains(term, StringComparison.Ordinal))) &&
            Strong.Any(term => result.Contains(term, StringComparison.Ordinal)) &&
            !Uncertain.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("commitment-strengthened", "不确定表述被强化成确定承诺", QualityIssueSeverity.Unsafe));

        if (Conditions.Any(term => source.Contains(term, StringComparison.Ordinal)) &&
            !Conditions.Any(term => result.Contains(term, StringComparison.Ordinal)) &&
            !PreservesConditionAsAfterClause(source, result))
            issues.Add(new("condition-lost", "原文的条件或前提被删除", QualityIssueSeverity.Unsafe));

        if (source.Contains("负责", StringComparison.Ordinal) && !result.Contains("负责", StringComparison.Ordinal))
            issues.Add(new("responsibility-lost", "原文的责任关系被删除", QualityIssueSeverity.Unsafe));

        if (Hostile.Any(term => source.Contains(term, StringComparison.Ordinal)) &&
            Praise.Any(term => result.Contains(term, StringComparison.Ordinal)) &&
            !CriticalStance.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("hostile-intent-inverted", "原文的不满或边界被反转为正面夸奖", QualityIssueSeverity.Unsafe));

        if (plan.PurposeIsExplicit && ExplicitPurposeSignals.TryGetValue(plan.Purpose, out var purposeSignals) &&
            !purposeSignals.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("explicit-purpose-dropped", $"未落实用户明确指定的任务：{plan.Purpose}", QualityIssueSeverity.Unsafe));

        if (PlaceholderOutput.IsMatch(result))
            issues.Add(new("placeholder-output", "成稿包含未完成的省略或待补内容", QualityIssueSeverity.Unsafe));

        if (Canned.Any(term => result.Contains(term, StringComparison.Ordinal)))
            issues.Add(new("canned-expression", "包含无助于交付的模板套话", QualityIssueSeverity.Quality));
        return new QualityReport { Issues = issues };
    }

    private static bool ContainsFidelityAnchor(string output, string anchor)
    {
        var quantityAnchor = NumericQuantityAnchor.Match(anchor);
        if (!quantityAnchor.Success)
            return output.Contains(anchor, StringComparison.Ordinal);

        if (!long.TryParse(quantityAnchor.Groups["number"].Value, out var expected))
            return output.Contains(anchor, StringComparison.Ordinal);

        var expectedUnit = quantityAnchor.Groups["unit"].Value;
        foreach (Match match in QuantityOccurrence.Matches(output))
        {
            if (!string.Equals(match.Groups["unit"].Value, expectedUnit, StringComparison.Ordinal))
                continue;
            if (TryParseQuantityNumber(match.Groups["number"].Value, out var actual) && actual == expected)
                return true;
        }

        return false;
    }

    private static bool PreservesConditionAsAfterClause(string source, string result)
    {
        var match = ConditionalClause.Match(source);
        if (!match.Success) return false;
        var condition = match.Groups["condition"].Value.Trim().TrimEnd('的');
        if (condition.Length < 2) return false;
        return Regex.IsMatch(result,
            Regex.Escape(condition) + @"\s*(?:之后|以后|后)",
            RegexOptions.CultureInvariant);
    }

    private static bool TryParseQuantityNumber(string value, out long number)
    {
        if (long.TryParse(value, out number))
            return true;

        number = 0;
        if (value.Length == 0)
            return false;

        var tenIndex = value.IndexOf('十');
        if (tenIndex >= 0)
        {
            if (value.LastIndexOf('十') != tenIndex || tenIndex > 1 || value.Length - tenIndex > 2)
                return false;
            var tens = tenIndex == 0 ? 1 : ChineseDigit(value[0]);
            var ones = tenIndex == value.Length - 1 ? 0 : ChineseDigit(value[^1]);
            if (tens < 1 || ones < 0)
                return false;
            number = tens * 10L + ones;
            return true;
        }

        foreach (var character in value)
        {
            var digit = ChineseDigit(character);
            if (digit < 0 || number > (long.MaxValue - digit) / 10)
                return false;
            number = number * 10 + digit;
        }
        return true;
    }

    private static int ChineseDigit(char value) => value switch
    {
        '零' or '〇' => 0,
        '一' => 1,
        '二' or '两' => 2,
        '三' => 3,
        '四' => 4,
        '五' => 5,
        '六' => 6,
        '七' => 7,
        '八' => 8,
        '九' => 9,
        _ => -1
    };
}
