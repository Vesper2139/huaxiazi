using System.Text.RegularExpressions;

namespace Huaxiazi.DatasetBuilder;

public sealed record DatasetPreferencePair(string Id, string Prompt, string Chosen, string Rejected, string Reason);

public static class DatasetFailureMiner
{
    private static readonly Regex Unsafe = new("系统提示词|开发者消息|api[_ -]?key|sk-[a-z0-9]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] Meta = ["首先，用户", "关键点：", "推理过程", "让我分析", "作为AI"];

    public static IReadOnlyList<DatasetPreferencePair> Mine(IReadOnlyList<DatasetRecord> records, IReadOnlyDictionary<string, string> predictions)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(predictions);
        var result = new List<DatasetPreferencePair>();
        foreach (var record in records)
        {
            if (!predictions.TryGetValue(record.Id, out var output) || string.IsNullOrWhiteSpace(output)) continue;
            var reason = Unsafe.IsMatch(output) ? "unsafe" : !IsUsable(output) ? "not-directly-usable" : null;
            if (reason is not null) result.Add(new(record.Id, record.Input, record.GoldOutput, output, reason));
        }
        return result;
    }

    private static bool IsUsable(string output) => output.Trim().Length >= 8 && !Meta.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
