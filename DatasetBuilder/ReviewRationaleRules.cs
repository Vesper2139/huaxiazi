namespace Huaxiazi.DatasetBuilder;

internal static class ReviewRationaleRules
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "无非空理由", "无理由", "无需理由", "暂无", "待补充", "待填写", "待定", "占位", "占位符",
        "na", "none", "null", "todo", "tbd", "placeholder", "noreason", "notapplicable",
    };

    public static bool IsSubstantive(string? rationale)
    {
        if (string.IsNullOrWhiteSpace(rationale)) return false;

        var normalized = new string(rationale
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized.Length > 0 && !PlaceholderValues.Contains(normalized);
    }
}
